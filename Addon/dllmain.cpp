#include <imgui.h>

#ifndef ImGuiDockNodeFlags
typedef int ImGuiDockNodeFlags;
#endif
#ifndef ImGuiWindowClass
struct ImGuiWindowClass { int ClassFlags; };
#endif

#include <reshade.hpp>
#include <string>
#include <thread>
#include <atomic>
#include <chrono>
#include <filesystem>
#include <vector>
#include <sstream>
#include <mutex>
#include <algorithm>
#include <queue>
#include <functional>
#include <unordered_map>

static std::atomic<bool> g_paused{false};
static std::atomic<bool> g_should_pause{true};
static std::atomic<bool> g_exiting{false};
static bool g_active = false;
static std::string g_pause_file_path;
static std::string g_preset_file_path;
static std::string g_anim_file_path;
static std::string g_state_file_path;
static std::string g_cmd_file_path;
static std::string g_toggle_file_path;
static std::string g_techlist_file_path;
static std::atomic<bool> g_refresh_techlist{true};
static std::string g_last_preset;
static uint64_t g_last_preset_counter = 0;
static reshade::api::effect_runtime *g_runtime = nullptr;
static std::mutex g_anim_mutex;
static std::mutex g_cmd_mutex;

struct AnimUniform {
	std::string effect_name;
	std::string uniform_name;
	std::string type;
	std::vector<float> float_values;
	std::vector<int32_t> int_values;
};

static std::vector<AnimUniform> g_anim_uniforms;

// True from the moment a preset switch is executed until the effect set is
// calm again. Technique/uniform handles are unstable across a reload (old
// objects freed, new ones still compiling), so anim application waits while
// this is set. NOTE: reshade_reloaded_effects does NOT fire for preset
// switches in ReShade 6.8 (only full effect reloads), so the guard clears by
// deadline (fast tier) or enumeration calm (full tier) — never by waiting on
// the event. Toggles/commands still execute immediately (overlay parity).
static std::atomic<bool> g_reload_pending{false};
static std::atomic<long long> g_reload_deadline_ms{0};
static std::atomic<bool> g_guard_full{false};
static std::atomic<size_t> g_calmSig{0};
static std::atomic<int> g_calmFrames{0};
static std::string g_last_exec_preset;
static std::string g_pending_preset;
static bool g_has_pending_preset = false;

static long long steady_ms_now()
{
	return std::chrono::duration_cast<std::chrono::milliseconds>(
		std::chrono::steady_clock::now().time_since_epoch()).count();
}

// Tolerant name matching: ReShade may report effect names as bare filenames
// ("Vignette.fx") or full paths, with varying case. Never trust one format.
static std::string base_name(const std::string &p)
{
	size_t i = p.find_last_of("/\\");
	return (i == std::string::npos) ? p : p.substr(i + 1);
}
static std::string lower_str(std::string s)
{
	for (auto &c : s) c = (char)tolower((unsigned char)c);
	return s;
}
static bool effect_matches(const char *reported, const std::string &wanted)
{
	std::string r = lower_str(base_name(reported));
	std::string w = lower_str(base_name(wanted));
	if (r == w) return true;
	// also try stem without extension ("Vignette" vs "Vignette.fx")
	auto stem = [](const std::string &s) {
		size_t d = s.find_last_of('.');
		return (d == std::string::npos) ? s : s.substr(0, d);
	};
	return stem(r) == stem(w);
}

static bool iequals_path(const std::string &a, const std::string &b)
{
	if (a.size() != b.size()) return false;
	for (size_t i = 0; i < a.size(); i++)
		if (tolower((unsigned char)a[i]) != tolower((unsigned char)b[i])) return false;
	return true;
}

struct RenderCommand {
	std::function<void(reshade::api::effect_runtime *)> fn;
	// Preset switches always run (they START a reload). Everything else
	// waits for reload completion, with a timeout fallback.
	bool is_preset_switch = false;
	std::chrono::steady_clock::time_point enqueue_time;
	// State-sets collapse: only the latest toggle/uniform per target
	// matters (kind 1 = technique toggle, 2 = uniform). Ordered ops
	// (preset/reorder/save) always run in full.
	int collapse_kind = 0;
	std::string collapse_key;
};

static std::queue<RenderCommand> g_render_commands;

static void queue_command(std::function<void(reshade::api::effect_runtime *)> fn, bool is_preset_switch = false, int collapse_kind = 0, const std::string &collapse_key = "")
{
	std::lock_guard<std::mutex> lock(g_cmd_mutex);
	RenderCommand cmd;
	cmd.fn = std::move(fn);
	cmd.is_preset_switch = is_preset_switch;
	cmd.enqueue_time = std::chrono::steady_clock::now();
	cmd.collapse_kind = collapse_kind;
	cmd.collapse_key = collapse_key;
	g_render_commands.push(std::move(cmd));
}

static std::string get_exe_dir()
{
	char buf[MAX_PATH] = {};
	GetModuleFileNameA(NULL, buf, MAX_PATH);
	std::filesystem::path p(buf);
	return p.parent_path().string();
}

static std::string read_file(const std::string &path)
{
	HANDLE hFile = CreateFileA(path.c_str(), GENERIC_READ, FILE_SHARE_READ,
		NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
	if (hFile == INVALID_HANDLE_VALUE) return "";

	DWORD size = GetFileSize(hFile, NULL);
	if (size == 0 || size > 131072) { CloseHandle(hFile); return ""; }

	std::string content(size, '\0');
	DWORD read = 0;
	BOOL ok = ReadFile(hFile, &content[0], size, &read, NULL);
	CloseHandle(hFile);

	if (!ok || read == 0) return "";
	content.resize(read);
	return content;
}

static void write_file(const std::string &path, const std::string &content)
{
	HANDLE hFile = CreateFileA(path.c_str(), GENERIC_WRITE, 0,
		NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
	if (hFile == INVALID_HANDLE_VALUE) return;

	DWORD written = 0;
	WriteFile(hFile, content.c_str(), (DWORD)content.size(), &written, NULL);
	CloseHandle(hFile);
}

static void delete_file(const std::string &path)
{
	DeleteFileA(path.c_str());
}

static bool atomic_write_file(const std::string &path, const std::string &content)
{
	std::string tmp = path + ".tmp";
	HANDLE hFile = CreateFileA(tmp.c_str(), GENERIC_WRITE, 0,
		NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
	if (hFile == INVALID_HANDLE_VALUE) return false;
	DWORD written = 0;
	WriteFile(hFile, content.c_str(), (DWORD)content.size(), &written, NULL);
	CloseHandle(hFile);
	if (written != content.size()) { DeleteFileA(tmp.c_str()); return false; }
	if (!MoveFileExA(tmp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
	{
		DeleteFileA(tmp.c_str());
		return false;
	}
	return true;
}

// Rewrite only the TechniqueSorting= line of a preset file (atomic:
// readers always see the complete old or new file). Entries for
// techniques that no longer resolve are preserved verbatim at the end.
static void persist_technique_sorting(const std::string &presetPath,
	const std::vector<std::pair<std::string, std::string>> &ordered)
{
	if (presetPath.empty() || ordered.empty()) return;
	std::string content = read_file(presetPath);
	if (content.empty() || content.size() > 1048576) return;

	std::istringstream iss(content);
	std::vector<std::string> lines;
	std::string line;
	while (std::getline(iss, line))
	{
		while (!line.empty() && line.back() == '\r') line.pop_back();
		lines.push_back(line);
	}

	auto tolerant_match = [](const std::string &entry, const std::string &tech, const std::string &eff) {
		size_t at = entry.find_last_of('@');
		if (at == std::string::npos) return false;
		std::string et = entry.substr(0, at), ee = entry.substr(at + 1);
		if (et.size() != tech.size()) return false;
		for (size_t i = 0; i < et.size(); i++)
			if (tolower((unsigned char)et[i]) != tolower((unsigned char)tech[i])) return false;
		std::string be = base_name(ee), bf = base_name(eff);
		if (be.size() != bf.size()) return false;
		for (size_t i = 0; i < be.size(); i++)
			if (tolower((unsigned char)be[i]) != tolower((unsigned char)bf[i])) return false;
		return true;
	};

	std::vector<std::string> oldEntries;
	int sortIdx = -1, techIdx = -1;
	for (size_t i = 0; i < lines.size(); i++)
	{
		std::string t = lines[i];
		size_t s = t.find_first_not_of(" \t");
		if (s != std::string::npos) t = t.substr(s);
		if (t.size() >= 17 && _strnicmp(t.c_str(), "TechniqueSorting=", 17) == 0)
		{
			sortIdx = (int)i;
			std::string rest = t.substr(17);
			std::istringstream ess(rest);
			std::string e;
			while (std::getline(ess, e, ',')) {
				while (!e.empty() && (e.front() == ' ' || e.front() == '\t')) e.erase(e.begin());
				while (!e.empty() && (e.back() == ' ' || e.back() == '\t')) e.pop_back();
				if (!e.empty()) oldEntries.push_back(e);
			}
		}
		else if (t.size() >= 11 && _strnicmp(t.c_str(), "Techniques=", 11) == 0)
		{
			techIdx = (int)i;
		}
	}

	std::ostringstream out;
	out << "TechniqueSorting=";
	bool firstOut = true;
	std::vector<bool> oldUsed(oldEntries.size(), false);
	for (auto &p : ordered)
	{
		// Prefer the preset's own spelling when it names the same entry.
		const std::string *spelling = nullptr;
		for (size_t i = 0; i < oldEntries.size(); i++)
		{
			if (!oldUsed[i] && tolerant_match(oldEntries[i], p.first, p.second)) {
				spelling = &oldEntries[i];
				oldUsed[i] = true;
				break;
			}
		}
		std::string entry = spelling ? *spelling : (p.first + "@" + p.second);
		if (!firstOut) out << ",";
		firstOut = false;
		out << entry;
	}
	for (size_t i = 0; i < oldEntries.size(); i++)
	{
		if (oldUsed[i]) continue;
		if (!firstOut) out << ",";
		firstOut = false;
		out << oldEntries[i];
	}

	std::string newLine = out.str();
	if (sortIdx >= 0)
	{
		lines[sortIdx] = newLine;
	}
	else if (techIdx >= 0)
	{
		lines.insert(lines.begin() + techIdx + 1, newLine);
	}
	else
	{
		return; // not recognizably a preset; don't invent one
	}

	std::ostringstream full;
	for (size_t i = 0; i < lines.size(); i++)
	{
		if (i > 0) full << "\n";
		full << lines[i];
	}
	full << "\n";
	atomic_write_file(presetPath, full.str());
	reshade::log::message(reshade::log::level::info, "reshade_controller: persisted TechniqueSorting");
}

static std::vector<std::string> scan_presets()
{
	std::vector<std::string> presets;
	std::string gameDir = get_exe_dir();

	for (auto &entry : std::filesystem::directory_iterator(gameDir))
	{
		if (entry.is_regular_file() && entry.path().extension() == ".ini")
			presets.push_back(entry.path().filename().string());
	}

	std::sort(presets.begin(), presets.end());
	return presets;
}

static std::string get_type_string(reshade::api::format base_type, uint32_t rows, uint32_t columns, uint32_t array_length)
{
	std::string base;
	switch (base_type)
	{
	case reshade::api::format::r32_float:  base = "float"; break;
	case reshade::api::format::r32_sint:  base = "int"; break;
	case reshade::api::format::r32_uint:  base = "uint"; break;
	case reshade::api::format::r32_typeless: base = "bool"; break;
	default: base = "float"; break;
	}

	std::string result = base;
	if (columns > 0 && rows > 0)
		result += std::to_string(rows) + "x" + std::to_string(columns);
	else if (rows > 1)
		result += std::to_string(rows);

	if (array_length > 0)
		result += "[" + std::to_string(array_length) + "]";

	return result;
}

static std::string json_escape(const std::string &s)
{
	std::string out;
	out.reserve(s.size() + 8);
	for (char c : s)
	{
		switch (c)
		{
		case '"':  out += "\\\""; break;
		case '\\': out += "\\\\"; break;
		case '\n': out += "\\n"; break;
		case '\r': out += "\\r"; break;
		case '\t': out += "\\t"; break;
		default:   out += c; break;
		}
	}
	return out;
}

static void write_state_file()
{
	if (!g_runtime || g_state_file_path.empty()) return;

	auto presets = scan_presets();

	char presetPath[512] = {};
	g_runtime->get_current_preset_path(presetPath);
	std::string currentPreset(presetPath);
	std::string currentPresetFilename = std::filesystem::path(currentPreset).filename().string();

	std::ostringstream json;
	json << "{\n";
	json << "  \"preset\": \"" << json_escape(currentPresetFilename) << "\",\n";
	json << "  \"presets\": [";
	for (size_t i = 0; i < presets.size(); i++)
	{
		if (i > 0) json << ", ";
		json << "\"" << json_escape(presets[i]) << "\"";
	}
	json << "],\n";

	json << "  \"effects\": [\n";

	bool firstEffect = true;
	g_runtime->enumerate_techniques(nullptr, [&](reshade::api::effect_runtime *rt, auto technique) {
		char effectName[256] = {};
		rt->get_technique_effect_name(technique, effectName);

		if (firstEffect)
			firstEffect = false;
		else
			json << ",\n";

		json << "    {\n";
		json << "      \"effect\": \"" << json_escape(effectName) << "\",\n";

		json << "      \"techniques\": [";
		char techName[256] = {};
		rt->get_technique_name(technique, techName);
		bool enabled = rt->get_technique_state(technique);
		json << "{\"name\": \"" << json_escape(techName) << "\", \"enabled\": " << (enabled ? "true" : "false") << "}";
		json << "],\n";

		json << "      \"uniforms\": [";
		bool firstUniform = true;
		rt->enumerate_uniform_variables(effectName, [&](reshade::api::effect_runtime *rt2, auto variable) {
			char uname[256] = {};
			rt2->get_uniform_variable_name(variable, uname);

			reshade::api::format baseType;
			uint32_t rows = 0, columns = 0, arrayLen = 0;
			rt2->get_uniform_variable_type(variable, &baseType, &rows, &columns, &arrayLen);

			std::string typeStr = get_type_string(baseType, rows, columns, arrayLen);

			if (firstUniform)
				firstUniform = false;
			else
				json << ", ";

			json << "{\"name\": \"" << json_escape(uname) << "\", \"type\": \"" << typeStr << "\", \"values\": ";

			json << "[";
			if (baseType == reshade::api::format::r32_float || baseType == reshade::api::format::r32_typeless)
			{
				float values[16] = {};
				uint32_t count = rows > 0 ? rows : 1;
				if (count > 16) count = 16;
				rt2->get_uniform_value_float(variable, values, count);
				for (uint32_t i = 0; i < count; i++)
				{
					if (i > 0) json << ", ";
					json << values[i];
				}
			}
			else if (baseType == reshade::api::format::r32_sint)
			{
				int32_t values[16] = {};
				uint32_t count = rows > 0 ? rows : 1;
				if (count > 16) count = 16;
				rt2->get_uniform_value_int(variable, values, count);
				for (uint32_t i = 0; i < count; i++)
				{
					if (i > 0) json << ", ";
					json << values[i];
				}
			}
			else if (baseType == reshade::api::format::r32_uint)
			{
				uint32_t values[16] = {};
				uint32_t count = rows > 0 ? rows : 1;
				if (count > 16) count = 16;
				rt2->get_uniform_value_uint(variable, values, count);
				for (uint32_t i = 0; i < count; i++)
				{
					if (i > 0) json << ", ";
					json << values[i];
				}
			}
			json << "]";

			char label[256] = {};
			size_t labelSize = sizeof(label);
			if (rt2->get_annotation_string_from_uniform_variable(variable, "ui_label", label, &labelSize) && labelSize > 1)
				json << ", \"label\": \"" << json_escape(label) << "\"";

			char tooltip[512] = {};
			size_t tooltipSize = sizeof(tooltip);
			if (rt2->get_annotation_string_from_uniform_variable(variable, "ui_tooltip", tooltip, &tooltipSize) && tooltipSize > 1)
				json << ", \"tooltip\": \"" << json_escape(tooltip) << "\"";

			char uiType[64] = {};
			size_t uiTypeSize = sizeof(uiType);
			if (rt2->get_annotation_string_from_uniform_variable(variable, "ui_type", uiType, &uiTypeSize) && uiTypeSize > 1)
				json << ", \"ui_type\": \"" << json_escape(uiType) << "\"";

			float uiMin = 0, uiMax = 1;
			if (rt2->get_annotation_float_from_uniform_variable(variable, "ui_min", &uiMin, 1))
				json << ", \"ui_min\": " << uiMin;
			if (rt2->get_annotation_float_from_uniform_variable(variable, "ui_max", &uiMax, 1))
				json << ", \"ui_max\": " << uiMax;

			float uiStep = 0;
			if (rt2->get_annotation_float_from_uniform_variable(variable, "ui_step", &uiStep, 1) && uiStep > 0)
				json << ", \"ui_step\": " << uiStep;

			json << "}";
		});
		json << "]\n";

		json << "    }";
		return true;
	});

	json << "\n  ]\n}\n";

	write_file(g_state_file_path, json.str());
}

static void check_anim_signal()
{
	if (g_anim_file_path.empty()) return;

	std::string content = read_file(g_anim_file_path);
	if (content.empty())
	{
		std::lock_guard<std::mutex> lock(g_anim_mutex);
		g_anim_uniforms.clear();
		return;
	}

	std::vector<AnimUniform> new_uniforms;
	std::istringstream iss(content);
	std::string line;

	while (std::getline(iss, line))
	{
		if (line.empty()) continue;

		size_t p1 = line.find('|');
		if (p1 == std::string::npos) continue;
		size_t p2 = line.find('|', p1 + 1);
		if (p2 == std::string::npos) continue;
		size_t p3 = line.find('|', p2 + 1);
		if (p3 == std::string::npos) continue;

		AnimUniform u;
		u.effect_name = line.substr(0, p1);
		u.uniform_name = line.substr(p1 + 1, p2 - p1 - 1);
		u.type = line.substr(p2 + 1, p3 - p2 - 1);
		std::string vals = line.substr(p3 + 1);

		try
		{
			std::istringstream vss(vals);
			std::string token;
			if (u.type.substr(0, 5) == "float" || u.type.substr(0, 4) == "half")
			{
				while (std::getline(vss, token, ','))
					u.float_values.push_back(static_cast<float>(std::stof(token)));
			}
			else if (u.type.substr(0, 3) == "int" || u.type.substr(0, 4) == "bool")
			{
				while (std::getline(vss, token, ','))
					u.int_values.push_back(static_cast<int32_t>(std::stoi(token)));
			}
			else if (u.type.substr(0, 4) == "uint")
			{
				while (std::getline(vss, token, ','))
					u.int_values.push_back(static_cast<int32_t>(std::stoul(token)));
			}
			else
			{
				continue;
			}
		}
		catch (...)
		{
			continue;
		}

		if (u.float_values.empty() && u.int_values.empty()) continue;
		new_uniforms.push_back(std::move(u));
	}

	std::lock_guard<std::mutex> lock(g_anim_mutex);
	g_anim_uniforms = std::move(new_uniforms);
}

static void check_command_file()
{
	if (g_cmd_file_path.empty()) return;

	std::string content = read_file(g_cmd_file_path);
	if (content.empty()) return;

	delete_file(g_cmd_file_path);

	std::istringstream iss(content);
	std::string line;

	while (std::getline(iss, line))
	{
		if (line.empty()) continue;

		while (!line.empty() && (line.back() == '\r' || line.back() == '\n'))
			line.pop_back();

		size_t sep = line.find('|');
		if (sep == std::string::npos) continue;

		std::string cmd = line.substr(0, sep);
		std::string args = line.substr(sep + 1);

		if (cmd == "ACTIVATE")
		{
			g_active = true;
			g_should_pause = false;
			reshade::log::message(reshade::log::level::info, "reshade_controller: activated by plugin");
		}
		else if (cmd == "SET_PRESET")
		{
			while (!args.empty() && (args.back() == '\r' || args.back() == '\n' || args.back() == ' '))
				args.pop_back();
			std::string presetPath = args;
			{
				std::lock_guard<std::mutex> lock(g_cmd_mutex);
				g_pending_preset = presetPath;
				g_has_pending_preset = true;
			}
			reshade::log::message(reshade::log::level::info,
				("reshade_controller: preset target " + presetPath).c_str());
			reshade::log::message(reshade::log::level::info,
				("reshade_controller: queued set preset " + presetPath).c_str());
		}
		else if (cmd == "SET_TECHNIQUE")
		{
			size_t p1 = args.find('|');
			if (p1 == std::string::npos) continue;
			size_t p2 = args.find('|', p1 + 1);
			if (p2 == std::string::npos) continue;

			std::string effectName = args.substr(0, p1);
			std::string techName = args.substr(p1 + 1, p2 - p1 - 1);
			bool enabled = args.substr(p2 + 1) == "1";

			queue_command([effectName, techName, enabled](reshade::api::effect_runtime *rt) {
				if (rt == nullptr) return;
				try
				{
					rt->enumerate_techniques(nullptr, [&](reshade::api::effect_runtime *rt2, auto technique) {
						char effName[256] = {};
						rt2->get_technique_effect_name(technique, effName);
						if (!effect_matches(effName, effectName)) return true;
						char techN[256] = {};
						rt2->get_technique_name(technique, techN);
						if (lower_str(techName) != lower_str(techN)) return true;
						rt2->set_technique_state(technique, enabled);
						return true;
					});
				}
				catch (...) {}
			}, false, 1, lower_str(effectName) + "|" + lower_str(techName));
		}
		else if (cmd == "REORDER")
		{
			// REORDER|fromEffect|fromTech|beforeEffect|beforeTech
			// (empty before* = move to end)
			size_t p1 = args.find('|');
			if (p1 == std::string::npos) continue;
			size_t p2 = args.find('|', p1 + 1);
			if (p2 == std::string::npos) continue;
			size_t p3 = args.find('|', p2 + 1);
			if (p3 == std::string::npos) continue;

			std::string fromEffect = args.substr(0, p1);
			std::string fromTech = args.substr(p1 + 1, p2 - p1 - 1);
			std::string beforeEffect = args.substr(p2 + 1, p3 - p2 - 1);
			std::string beforeTech = args.substr(p3 + 1);

			queue_command([fromEffect, fromTech, beforeEffect, beforeTech](reshade::api::effect_runtime *rt) {
				if (rt == nullptr) return;
				try
				{
					struct Entry { reshade::api::effect_technique handle; std::string eff; std::string tech; };
					std::vector<Entry> current;
					rt->enumerate_techniques(nullptr, [&](reshade::api::effect_runtime *rt2, auto technique) {
						char effName[256] = {};
						rt2->get_technique_effect_name(technique, effName);
						char techN[256] = {};
						rt2->get_technique_name(technique, techN);
						current.push_back({ technique, effName, techN });
						return true;
					});
					auto matches = [](const Entry &e, const std::string &f, const std::string &t) {
						if (!effect_matches(e.eff.c_str(), f)) return false;
						if (t.empty()) return true;
						return lower_str(e.tech) == lower_str(t);
					};
					int from = -1;
					for (size_t i = 0; i < current.size(); i++)
						if (matches(current[i], fromEffect, fromTech)) { from = (int)i; break; }
					if (from < 0)
					{
						reshade::log::message(reshade::log::level::info,
							("reshade_controller: reorder NOT FOUND " + fromEffect + "|" + fromTech).c_str());
						return;
					}
					Entry moving = current[(size_t)from];
					current.erase(current.begin() + from);
					size_t at = current.size();
					if (!beforeEffect.empty() || !beforeTech.empty())
					{
						for (size_t i = 0; i < current.size(); i++)
							if (matches(current[i], beforeEffect, beforeTech)) { at = i; break; }
					}
					current.insert(current.begin() + at, moving);
					std::vector<reshade::api::effect_technique> arr;
					arr.reserve(current.size());
					for (auto &e : current) arr.push_back(e.handle);
					rt->reorder_techniques(arr.size(), arr.data());
					reshade::log::message(reshade::log::level::info,
						("reshade_controller: reordered " + fromTech + "@" + fromEffect).c_str());
					// Persist so restarts/reselects keep the order. In-memory
					// order is already correct, so any later ReShade save
					// stays consistent (no save-then-load clobber).
					char presetPath[512] = {};
					try { rt->get_current_preset_path(presetPath); } catch (...) { presetPath[0] = '\0'; }
					if (presetPath[0] != '\0')
					{
						std::vector<std::pair<std::string, std::string>> ordered;
						ordered.reserve(current.size());
						for (auto &e : current) ordered.emplace_back(e.tech, e.eff);
						persist_technique_sorting(presetPath, ordered);
					}
				}
				catch (...) {}
			});
		}
		else if (cmd == "SET_UNIFORM")
		{
			size_t p1 = args.find('|');
			if (p1 == std::string::npos) continue;
			size_t p2 = args.find('|', p1 + 1);
			if (p2 == std::string::npos) continue;
			size_t p3 = args.find('|', p2 + 1);
			if (p3 == std::string::npos) continue;

			std::string effectName = args.substr(0, p1);
			std::string uniformName = args.substr(p1 + 1, p2 - p1 - 1);
			std::string claimedType = args.substr(p2 + 1, p3 - p2 - 1);
			(void)claimedType; // actual type is queried from ReShade on the render thread
			std::string valStr = args.substr(p3 + 1);

			// Split raw tokens on poll thread (no throwing conversions here).
			// All numeric conversion happens on the render thread against the
			// ACTUAL uniform type queried from ReShade, so a wrong BaseType
			// string from the C# .fx parser can never crash or corrupt.
			std::vector<std::string> tokens;
			try
			{
				std::istringstream vss(valStr);
				std::string token;
				while (std::getline(vss, token, ','))
				{
					while (!token.empty() && (token.front() == ' ' || token.front() == '\t')) token.erase(token.begin());
					while (!token.empty() && (token.back() == ' ' || token.back() == '\t' || token.back() == '\r' || token.back() == '\n')) token.pop_back();
					if (token.size() > 64) continue; // garbage guard (torn file write)
					tokens.push_back(token);
					if (tokens.size() >= 16) break;
				}
			}
			catch (...) { continue; }
			if (tokens.empty()) continue;

			queue_command([effectName, uniformName, tokens](reshade::api::effect_runtime *rt) {
				if (rt == nullptr) return;
				try
				{
					{
						std::string tstr;
						for (auto &t : tokens) { if (!tstr.empty()) tstr += ","; tstr += t; }
						reshade::log::message(reshade::log::level::info,
							("reshade_controller: uniform-exec " + effectName + "|" + uniformName + "=[" + tstr + "]").c_str());
					}
					// Resolve the variable through enumeration (never
					// find_uniform_variable: same unstable-handle suspicion
					// as find_technique). Use ReShade's own reported effect
					// name for the nested enumeration so the format always
					// matches what the API expects.
					bool resolved = false;
					reshade::api::effect_uniform_variable variable{ 0 };
					rt->enumerate_techniques(nullptr, [&](reshade::api::effect_runtime *rt2, auto technique) {
						if (resolved) return true;
						char effName[256] = {};
						rt2->get_technique_effect_name(technique, effName);
						if (!effect_matches(effName, effectName)) return true;
						reshade::log::message(reshade::log::level::info,
							("reshade_controller: uniform-effect-match '" + std::string(effName) + "'").c_str());
						rt2->enumerate_uniform_variables(effName, [&](reshade::api::effect_runtime *rt3, auto var) {
							if (resolved) return;
							char uname[256] = {};
							rt3->get_uniform_variable_name(var, uname);
							if (uniformName == uname || lower_str(uniformName) == lower_str(uname))
							{
								variable = var;
								resolved = true;
							}
						});
						return true;
					});
					if (!resolved || variable.handle == 0)
					{
						reshade::log::message(reshade::log::level::warning, "reshade_controller: uniform NOT FOUND");
						return;
					}

					reshade::api::format baseType = reshade::api::format::r32_float;
					uint32_t rows = 0, columns = 0, arrayLen = 0;
					rt->get_uniform_variable_type(variable, &baseType, &rows, &columns, &arrayLen);

					uint32_t expected = rows > 0 ? rows : 1;
					if (columns > 1) expected *= columns;
					if (arrayLen > 0) expected *= arrayLen;
					if (expected == 0) expected = 1;
					if (expected > 16) expected = 16;
					reshade::log::message(reshade::log::level::info,
						("reshade_controller: uniform-type base=" + std::to_string((int)baseType) + " rows=" + std::to_string(rows) + " cols=" + std::to_string(columns) + " arr=" + std::to_string(arrayLen) + " have=" + std::to_string((unsigned long long)tokens.size())).c_str());

					if (baseType == reshade::api::format::r32_float)
					{
						std::vector<float> values;
						values.reserve(expected);
						for (size_t i = 0; i < tokens.size() && values.size() < expected; i++)
						{
							try { values.push_back(std::stof(tokens[i])); }
							catch (...) { return; } // malformed number: drop whole command
						}
						if (!values.empty())
							rt->set_uniform_value_float(variable, values.data(), values.size());
					}
					else if (baseType == reshade::api::format::r32_sint)
					{
						std::vector<int32_t> values;
						values.reserve(expected);
						for (size_t i = 0; i < tokens.size() && values.size() < expected; i++)
						{
							try
							{
								size_t pos = 0;
								float f = std::stof(tokens[i], &pos);
								if (pos != tokens[i].size()) return; // e.g. "0.5" for int: drop
								values.push_back(static_cast<int32_t>(f));
							}
							catch (...) { return; }
						}
						if (!values.empty())
							rt->set_uniform_value_int(variable, values.data(), values.size());
					}
					else if (baseType == reshade::api::format::r32_uint)
					{
						std::vector<uint32_t> values;
						values.reserve(expected);
						for (size_t i = 0; i < tokens.size() && values.size() < expected; i++)
						{
							try
							{
								size_t pos = 0;
								float f = std::stof(tokens[i], &pos);
								if (pos != tokens[i].size() || f < 0) return;
								values.push_back(static_cast<uint32_t>(f));
							}
							catch (...) { return; }
						}
						if (!values.empty())
							rt->set_uniform_value_uint(variable, values.data(), values.size());
					}
					else // bool / typeless: 1 byte per element
					{
						std::vector<uint8_t> values;
						values.reserve(expected);
						for (size_t i = 0; i < tokens.size() && values.size() < expected; i++)
						{
							const std::string &t = tokens[i];
							if (t == "1" || t == "true" || t == "True" || t == "TRUE") values.push_back(1);
							else if (t == "0" || t == "false" || t == "False" || t == "FALSE") values.push_back(0);
							else
							{
								try
								{
									float f = std::stof(t);
									values.push_back(f > 0.5f ? 1 : 0);
								}
								catch (...) { return; }
							}
						}
						if (!values.empty())
							rt->set_uniform_value_bool(variable, reinterpret_cast<const bool*>(values.data()), values.size());
					}
				}
				catch (...) { /* never let a uniform update take down the render thread */ }
			}, false, 2, lower_str(effectName) + "|" + lower_str(uniformName));
		}
		else if (cmd == "SAVE")
		queue_command([](reshade::api::effect_runtime *rt) {
			if (rt == nullptr) return;
			try { rt->save_current_preset(); } catch (...) {}
		});
	}
}

// Parse the Techniques= line of a preset file into "Tech@File" entries.
// Returns false when the file can't be read (caller assumes full guard).
static bool parse_preset_techniques(const std::string &presetPath, std::vector<std::string> &out)
{
	out.clear();
	std::string content = read_file(presetPath);
	if (content.empty()) return false;
	std::istringstream iss(content);
	std::string line;
	while (std::getline(iss, line))
	{
		while (!line.empty() && (line.back() == '\r' || line.back() == '\n')) line.pop_back();
		size_t s = line.find_first_not_of(" \t");
		if (s == std::string::npos) continue;
		std::string t = line.substr(s);
		if (t.size() > 11 && (t[0] == 'T' || t[0] == 't') && _strnicmp(t.c_str(), "Techniques=", 11) == 0)
		{
			std::string rest = t.substr(11);
			std::istringstream ess(rest);
			std::string e;
			while (std::getline(ess, e, ','))
			{
				while (!e.empty() && (e.front() == ' ' || e.front() == '\t')) e.erase(e.begin());
				while (!e.empty() && (e.back() == ' ' || e.back() == '\t')) e.pop_back();
				if (!e.empty()) out.push_back(e);
			}
			return true;
		}
	}
	return true; // readable file, just no Techniques= line (zero enabled)
}

static void check_preset_signal()
{
	if (g_preset_file_path.empty()) return;

	std::string raw = read_file(g_preset_file_path);
	if (raw.empty()) return;

	while (!raw.empty() && (raw.back() == '\r' || raw.back() == '\n' || raw.back() == ' '))
		raw.pop_back();

	if (raw.empty()) return;

	uint64_t counter = 0;
	std::string presetPath = raw;
	auto pipe = raw.find('|');
	if (pipe != std::string::npos)
	{
		presetPath = raw.substr(0, pipe);
		try { counter = std::stoull(raw.substr(pipe + 1)); } catch (...) {}
	}

	if (presetPath.empty()) return;

	if (presetPath == g_last_preset && counter == g_last_preset_counter) return;

	bool samePath = (presetPath == g_last_preset);
	g_last_preset = presetPath;
	g_last_preset_counter = counter;



	{
		std::lock_guard<std::mutex> lock(g_cmd_mutex);
		g_pending_preset = presetPath;
		g_has_pending_preset = true;
	}
	reshade::log::message(reshade::log::level::info,
		("reshade_controller: preset target " + presetPath).c_str());
}

static void check_toggle_signal()
{
	if (g_toggle_file_path.empty()) return;

	std::string content = read_file(g_toggle_file_path);
	if (content.empty()) return;

	delete_file(g_toggle_file_path);

	std::istringstream iss(content);
	std::string line;

	while (std::getline(iss, line))
	{
		if (line.empty()) continue;

		while (!line.empty() && (line.back() == '\r' || line.back() == '\n'))
			line.pop_back();

		size_t p1 = line.find('|');
		if (p1 == std::string::npos) continue;
		size_t p2 = line.find('|', p1 + 1);
		if (p2 == std::string::npos) continue;

		std::string effectName = line.substr(0, p1);
		std::string techName = line.substr(p1 + 1, p2 - p1 - 1);
		bool enabled = line.substr(p2 + 1) == "1";

		queue_command([effectName, techName, enabled](reshade::api::effect_runtime *rt) {
				if (rt == nullptr) return;
				try
				{
					reshade::log::message(reshade::log::level::info,
						("reshade_controller: toggle-exec " + effectName + "|" + techName).c_str());
					// NOTE: deliberately NOT using find_technique(): its
					// returned handles proved unstable across calls and
					// set_technique_state on them corrupted the heap
					// (silent delayed deaths). Only handles handed out by
					// enumerate_techniques are used.
					bool found = false;
					rt->enumerate_techniques(nullptr, [&](reshade::api::effect_runtime *rt2, auto technique) {
						char effName[256] = {};
						rt2->get_technique_effect_name(technique, effName);
						if (!effect_matches(effName, effectName)) return true;
					if (!techName.empty())
					{
						char techN[256] = {};
						rt2->get_technique_name(technique, techN);
						if (lower_str(techName) != lower_str(techN)) return true;
					}
					rt2->set_technique_state(technique, enabled);
					found = true;
					return true;
					});
					// NOTE: no final log line here on purpose. The previous
					// build died with the ("toggle "+effect+"|"+tech...)
					// message half-written, so logging itself is a suspect
					// until proven otherwise.
				}
				catch (...) { /* never take down the render thread */ }
			}, false, 1, lower_str(effectName) + "|" + lower_str(techName));
		reshade::log::message(reshade::log::level::info,
			("reshade_controller: queued toggle '" + effectName + "' | '" + techName + "' -> " + (enabled ? "on" : "off")).c_str());
	}
}

// Ground truth technique list, read-only. Static .fx parsing cannot see
// macro-generated techniques (Censor, LayerCake, ...); ReShade can, so we
// publish what it reports: effect file, technique name, enabled state.
// Called only on the present thread (enumerate_* reads are safe there;
// WRITES via set_* are what historically corrupted the heap).
static void write_technique_list(reshade::api::effect_runtime *runtime)
{
	if (runtime == nullptr || g_techlist_file_path.empty()) return;
	try
	{
		char curPreset[512] = {};
		try { runtime->get_current_preset_path(curPreset); } catch (...) { curPreset[0] = '\0'; }
		std::ostringstream json;
		json << "{\"preset\":\"" << json_escape(curPreset) << "\",\"effects\":[";
		bool first = true;
		runtime->enumerate_techniques(nullptr, [&](reshade::api::effect_runtime *rt, auto technique) {
			// Mirror the ReShade UI: techniques annotated hidden=true are
			// compiled but never listed.
			try
			{
				bool hidden = false;
				rt->get_annotation_bool_from_technique(technique, "hidden", &hidden, 1);
				if (hidden) return true;
			}
			catch (...) {}
			char effName[256] = {};
			rt->get_technique_effect_name(technique, effName);
			char techN[256] = {};
			rt->get_technique_name(technique, techN);
			bool enabled = false;
			try { enabled = rt->get_technique_state(technique); } catch (...) {}
			if (!first) json << ",";
			first = false;
			json << "{\"effect\":\"" << json_escape(effName) << "\",\"tech\":\"" << json_escape(techN) << "\",\"enabled\":" << (enabled ? "true" : "false") << "}";
			return true;
		});
		json << "]}";
		write_file(g_techlist_file_path, json.str());
	}
	catch (...) {}
}

// Last applied animation values (effect|uniform -> signature). Skips
// redundant sets so static values cost a map lookup instead of API calls.
// Cleared lazily on the present thread after every reload (new objects
// need re-applying; also bounds the map).
static std::unordered_map<std::string, std::string> g_anim_applied;
static std::atomic<bool> g_anim_applied_stale{ false };

// Fully type-checked uniform apply: queries the LIVE variable type and
// uses the matching setter with a correctly-sized buffer and clamped
// count. Never trusts the file's claimed type.
static void apply_anim_values(reshade::api::effect_runtime *rt,
	reshade::api::effect_uniform_variable variable,
	const std::vector<float> &floatVals, const std::vector<int32_t> &intVals)
{
	reshade::api::format baseType = reshade::api::format::r32_float;
	uint32_t rows = 0, columns = 0, arrayLen = 0;
	rt->get_uniform_variable_type(variable, &baseType, &rows, &columns, &arrayLen);
	uint32_t expected = rows > 0 ? rows : 1;
	if (columns > 1) expected *= columns;
	if (arrayLen > 0) expected *= arrayLen;
	if (expected == 0) expected = 1;
	if (expected > 16) expected = 16;

	if (baseType == reshade::api::format::r32_float)
	{
		std::vector<float> v;
		for (size_t i = 0; i < floatVals.size() && v.size() < expected; i++) v.push_back(floatVals[i]);
		if (v.empty())
			for (size_t i = 0; i < intVals.size() && v.size() < expected; i++) v.push_back((float)intVals[i]);
		if (!v.empty()) rt->set_uniform_value_float(variable, v.data(), v.size());
	}
	else if (baseType == reshade::api::format::r32_sint)
	{
		std::vector<int32_t> v;
		for (size_t i = 0; i < intVals.size() && v.size() < expected; i++) v.push_back(intVals[i]);
		if (v.empty())
			for (size_t i = 0; i < floatVals.size() && v.size() < expected; i++) v.push_back((int32_t)floatVals[i]);
		if (!v.empty()) rt->set_uniform_value_int(variable, v.data(), v.size());
	}
	else if (baseType == reshade::api::format::r32_uint)
	{
		std::vector<uint32_t> v;
		for (size_t i = 0; i < intVals.size() && v.size() < expected; i++) v.push_back(intVals[i] < 0 ? 0u : (uint32_t)intVals[i]);
		if (v.empty())
			for (size_t i = 0; i < floatVals.size() && v.size() < expected; i++) v.push_back(floatVals[i] < 0 ? 0u : (uint32_t)floatVals[i]);
		if (!v.empty()) rt->set_uniform_value_uint(variable, v.data(), v.size());
	}
	else
	{
		// bool / typeless: 1 byte per element — never int-sized.
		std::vector<uint8_t> v;
		for (size_t i = 0; i < intVals.size() && v.size() < expected; i++) v.push_back(intVals[i] ? 1 : 0);
		if (v.empty())
			for (size_t i = 0; i < floatVals.size() && v.size() < expected; i++) v.push_back(floatVals[i] > 0.5f ? 1 : 0);
		if (!v.empty()) rt->set_uniform_value_bool(variable, reinterpret_cast<const bool*>(v.data()), v.size());
	}
}

static void poll_thread()
{
	while (!g_exiting.load())
	{
		std::this_thread::sleep_for(std::chrono::milliseconds(16));
		if (g_exiting.load()) break;

		if (g_pause_file_path.empty()) continue;

		// Check pause signal (atomics only, no ReShade API)
		DWORD attr = GetFileAttributesA(g_pause_file_path.c_str());
		bool file_exists = (attr != INVALID_FILE_ATTRIBUTES && !(attr & FILE_ATTRIBUTE_DIRECTORY));
		g_should_pause.store(file_exists);

		// Read signal files and queue commands (file I/O only, no ReShade API)
		check_command_file();

		if (!g_active) continue;

		check_preset_signal();
		check_toggle_signal();
		check_anim_signal();

		// Write state file (every 5th tick = ~1 second) - DISABLED for crash debugging
		// static int tick = 0;
		// if (++tick >= 5)
		// {
		// 	tick = 0;
		// 	{
		// 		std::lock_guard<std::mutex> lock(g_cmd_mutex);
		// 		g_render_commands.push([](reshade::api::effect_runtime *rt) {
		// 			write_state_file();
		// 		});
		// 	}
		// }
	}
}

static void on_init(reshade::api::effect_runtime *runtime)
{
	g_runtime = runtime;
	std::string dir = get_exe_dir();
	g_pause_file_path = dir + "\\ffxiv_reshade_pause";
	g_preset_file_path = dir + "\\ffxiv_reshade_preset";
	g_anim_file_path = dir + "\\ffxiv_reshade_anim";
	g_state_file_path = dir + "\\ffxiv_reshade_state.json";
	g_cmd_file_path = dir + "\\ffxiv_reshade_cmd.txt";
	g_toggle_file_path = dir + "\\ffxiv_reshade_toggle";
	g_techlist_file_path = dir + "\\ffxiv_reshade_techniques.json";

	try
	{
		char cur[512] = {};
		runtime->get_current_preset_path(cur);
		if (cur[0] != '\0') g_last_exec_preset = cur;
	}
	catch (...) {}
	delete_file(g_preset_file_path);
	delete_file(g_anim_file_path);
	delete_file(g_state_file_path);
	delete_file(g_toggle_file_path);
	delete_file(g_techlist_file_path);

	reshade::log::message(reshade::log::level::info, "reshade_controller: initialized v4-present-step-log");

	std::thread(poll_thread).detach();
}

static void on_destroy(reshade::api::effect_runtime *runtime)
{
	if (g_runtime == runtime)
		g_runtime = nullptr;
}

static void on_reloaded_effects(reshade::api::effect_runtime *runtime)
{
	// Reload finished: handles are fresh again.
	g_reload_pending.store(false);
	g_refresh_techlist.store(true);
	g_anim_applied_stale.store(true);
	reshade::log::message(reshade::log::level::info, "reshade_controller: reload-done");
	if (runtime == nullptr) return;
	try
	{
		// Handle-free pause: the global effects switch needs no technique
		// handles, so it stays correct across reloads by construction.
		if (g_should_pause.load()) { runtime->set_effects_state(false); g_paused.store(true); }
		else { g_paused.store(!runtime->get_effects_state()); }
	}
	catch (...) {}
}

static void on_begin_effects(reshade::api::effect_runtime *, reshade::api::command_list *, reshade::api::resource_view, reshade::api::resource_view)
{
	// Intentionally empty. Mutating technique/uniform state inside
	// reshade_begin_effects races ReShade's own render iteration over the
	// enabled technique list (use-after-free -> 0x12345679 AV). All queued
	// work runs in on_present instead, same place ReShade's own overlay
	// applies UI changes.
}

static void on_present(reshade::api::effect_runtime *runtime)
{
	if (runtime == nullptr) return;
	// One-shot diagnostic: log exactly what effect/technique name strings
	// ReShade reports, so our matching code never has to guess formats.
	static bool g_names_dumped = false;
	if (!g_names_dumped)
	{
		g_names_dumped = true;
		try
		{
			runtime->enumerate_techniques(nullptr, [](reshade::api::effect_runtime *rt, auto technique) {
				char effName[256] = {};
				rt->get_technique_effect_name(technique, effName);
				char techN[256] = {};
				rt->get_technique_name(technique, techN);
				bool en = rt->get_technique_state(technique);
				reshade::log::message(reshade::log::level::info,
					("reshade_controller: known effect='" + std::string(effName) + "' tech='" + std::string(techN) + "' " + (en ? "on" : "off")).c_str());
				return true;
			});
		}
		catch (...) {}
	}
	// Pause/resume via the global effects switch: no technique handles, so
	// it applies instantly even mid-reload (per-technique commands below
	// still wait for reload-done). Technique states are untouched, so a
	// resume restores exactly what was there.
	if (g_should_pause.load() != g_paused.load())
	{
		try
		{
			bool wantPause = g_should_pause.load();
			runtime->set_effects_state(!wantPause);
			g_paused.store(wantPause);
			reshade::log::message(reshade::log::level::info,
				wantPause ? "reshade_controller: paused" : "reshade_controller: resumed");
		}
		catch (...) {}
	}

	// Coalesced preset switch: rapid bursts (clicking through presets,
	// zone flapping) collapse to the latest target. Executes immediately
	// even mid-reload — like ReShade's own overlay, which aborts the
	// in-flight reload and starts the new one. Waiting for quiet first
	// serialized storms back-to-back and made fast switching far slower
	// than the overlay. Toggles/uniforms below still wait for calm.
	{
		std::string switchTarget;
		{
			std::lock_guard<std::mutex> lock(g_cmd_mutex);
			if (g_has_pending_preset)
			{
				switchTarget = g_pending_preset;
				g_has_pending_preset = false;
			}
		}
		if (!switchTarget.empty())
		{
			try
			{
				if (!g_last_exec_preset.empty() && iequals_path(switchTarget, g_last_exec_preset))
				{
					reshade::log::message(reshade::log::level::info, "reshade_controller: preset unchanged, skipping");
				}
				else
				{
					// Tier 0: byte-identical content needs no reload guard.
					// Verified by experiment: switching identical presets
					// neither hitches nor compiles; the call just updates
					// ReShade's displayed path and future-save destination.
					bool identical = false;
					if (!g_last_exec_preset.empty())
					{
						std::string cur = read_file(g_last_exec_preset);
						std::string tgt = read_file(switchTarget);
						identical = !cur.empty() && cur.size() == tgt.size() && cur == tgt;
					}
					// Tier 1: every newly-enabled technique is already
					// compiled (present in the live enumeration), so no
					// compile storm can follow — short guard only.
					bool subset = false;
					if (!identical)
					{
						std::vector<std::string> wanted;
						if (parse_preset_techniques(switchTarget, wanted))
						{
							subset = true;
							for (auto &entry : wanted)
							{
								size_t at = entry.find_last_of('@');
								std::string wt = (at == std::string::npos) ? entry : entry.substr(0, at);
								std::string wf = (at == std::string::npos) ? "" : entry.substr(at + 1);
								bool hit = false;
								runtime->enumerate_techniques(nullptr, [&](reshade::api::effect_runtime *rt2, auto technique) {
									if (hit) return true;
									char effName[256] = {};
									rt2->get_technique_effect_name(technique, effName);
									if (!effect_matches(effName, wf)) return true;
									char techN[256] = {};
									rt2->get_technique_name(technique, techN);
									if (lower_str(techN) == lower_str(wt)) hit = true;
									return true;
								});
								if (!hit) { subset = false; break; }
							}
						}
					}
					reshade::log::message(reshade::log::level::info,
						("reshade_controller: preset-exec " + switchTarget + (identical ? " [identical]" : (subset ? " [fast]" : ""))).c_str());
					g_last_exec_preset = switchTarget;
				if (!identical)
				{
					// Handles may churn, so the applied-value cache must go
					// (else post-switch values equal to cached ones get
					// skipped even though the preset load reset them live).
					g_anim_applied_stale.store(true);
					g_refresh_techlist.store(true);
					if (subset)
					{
						// Fast tier: every wanted technique is already live,
						// so no compile can follow. Short guard covers the
						// handle churn only; self-clears, event-independent.
						g_guard_full.store(false);
						g_reload_deadline_ms.store(steady_ms_now() + 750);
						g_reload_pending.store(true);
					}
					else
					{
						// Full tier: new effects compile now. Guard clears
						// by enumeration calm (stable 30 presents) with a
						// 60s backstop — never by the reload event, which
						// preset switches don't fire.
						g_guard_full.store(true);
						g_calmSig.store(0);
						g_calmFrames.store(0);
						g_reload_deadline_ms.store(steady_ms_now() + 60000);
						g_reload_pending.store(true);
					}
				}
					runtime->set_current_preset_path(switchTarget.c_str());
					reshade::log::message(reshade::log::level::info, "reshade_controller: preset-exec done");
				}
			}
			catch (...) {}
		}
	}

	// Execute queued commands on present thread.
	// Drain under lock, execute OUTSIDE the lock: a command may trigger a
	// preset reload/compile that takes a while, and holding the mutex across
	// ReShade API calls risks stalling the poll thread or re-entering.
	std::vector<RenderCommand> pending;
	{
		std::lock_guard<std::mutex> lock(g_cmd_mutex);
		while (!g_render_commands.empty())
		{
			pending.push_back(std::move(g_render_commands.front()));
			g_render_commands.pop();
		}
	}
	{
		auto now = std::chrono::steady_clock::now();
		// Collapse redundant state-sets first: 20 rapid on/off flips of one
		// technique become a single set to its final state, so a released
		// backlog applies instantly instead of replaying every click.
		{
			std::unordered_map<std::string, size_t> lastIdx;
			std::vector<bool> drop(pending.size(), false);
			for (size_t i = 0; i < pending.size(); i++)
			{
				if (pending[i].collapse_kind == 0) continue;
				std::string k = std::to_string(pending[i].collapse_kind) + '\0' + pending[i].collapse_key;
				auto it = lastIdx.find(k);
				if (it != lastIdx.end()) drop[it->second] = true;
				lastIdx[k] = i;
			}
			if (!lastIdx.empty())
			{
				std::vector<RenderCommand> kept;
				kept.reserve(pending.size());
				for (size_t i = 0; i < pending.size(); i++)
					if (!drop[i]) kept.push_back(std::move(pending[i]));
				pending.swap(kept);
			}
		}
	// Guard clearing keeps the flag honest for the read paths below
	// (tech list + anim uniforms stay out of reloads).
	bool reloading = g_reload_pending.load();
	if (reloading)
	{
		if (g_guard_full.load())
		{
			// Full tier: clear once the live enumeration is stable across
			// 30 presents (compiles done), or at the backstop.
			try
			{
				size_t sig = 1469598103934665603ull;
				runtime->enumerate_techniques(nullptr, [&](reshade::api::effect_runtime *rt2, auto technique) {
					char effName[256] = {};
					rt2->get_technique_effect_name(technique, effName);
					char techN[256] = {};
					rt2->get_technique_name(technique, techN);
					for (char *c = effName; *c; ++c) { sig ^= (size_t)(unsigned char)*c; sig *= 1099511628211ull; }
					sig ^= (size_t)0xff; sig *= 1099511628211ull;
					for (char *c = techN; *c; ++c) { sig ^= (size_t)(unsigned char)*c; sig *= 1099511628211ull; }
					return true;
				});
				if (sig == g_calmSig.load())
				{
					if (g_calmFrames.fetch_add(1) + 1 >= 30)
					{
						reloading = false;
						g_reload_pending.store(false);
					}
				}
				else
				{
					g_calmSig.store(sig);
					g_calmFrames.store(0);
				}
			}
			catch (...) {}
		}
		if (reloading && steady_ms_now() >= g_reload_deadline_ms.load())
		{
			reloading = false;
			g_reload_pending.store(false);
		}
	}
		// Commands execute immediately, storm or not: every proven crash
		// predates enumerate-only resolution, and mid-storm executions have
		// run clean many times over (plus ReShade's own overlay never
		// guards these same calls). The old deferral turned every switch
		// burst into a minute of dead toggles.
		for (auto &cmd : pending)
		{
			try { cmd.fn(runtime); } catch (...) {
				reshade::log::message(reshade::log::level::error, "reshade_controller: render command threw exception");
			}
		}
	}

	// Publish the ground-truth technique list once per reload (and once at
	// startup via the initial true). Present thread only.
	if (g_refresh_techlist.load() && !g_reload_pending.load())
	{
		g_refresh_techlist.store(false);
		write_technique_list(runtime);
	}

	// Apply animation uniforms (also deferred across reloads for the same
	// handle-stability reason). Resolution is enumerate-only (never
	// find_uniform_variable), values are re-resolved every present so no
	// handle survives a reload, and already-applied values are skipped.
	if (!g_paused.load() && !g_reload_pending.load())
	{
		try {
			if (g_anim_applied_stale.exchange(false)) g_anim_applied.clear();
			std::vector<AnimUniform> uniforms;
			{
				std::lock_guard<std::mutex> lock(g_anim_mutex);
				uniforms = g_anim_uniforms;
			}
			if (!uniforms.empty())
			{
				std::vector<std::string> reportedEffs;
				runtime->enumerate_techniques(nullptr, [&](reshade::api::effect_runtime *rt2, auto technique) {
					char effName[256] = {};
					rt2->get_technique_effect_name(technique, effName);
					std::string e = effName;
					bool dup = false;
					for (auto &x : reportedEffs) if (x == e) { dup = true; break; }
					if (!dup) reportedEffs.push_back(e);
					return true;
				});
				for (auto &u : uniforms)
				{
					std::string sig = "f:";
					for (auto v : u.float_values) { sig += std::to_string(v); sig += ","; }
					sig += ";i:";
					for (auto v : u.int_values) { sig += std::to_string(v); sig += ","; }
					std::string akey = lower_str(u.effect_name) + '\0' + lower_str(u.uniform_name);
					auto it = g_anim_applied.find(akey);
					if (it != g_anim_applied.end() && it->second == sig) continue;
					bool done = false;
					for (auto &rep : reportedEffs)
					{
						if (!effect_matches(rep.c_str(), u.effect_name)) continue;
						runtime->enumerate_uniform_variables(rep.c_str(), [&](reshade::api::effect_runtime *rt3, auto variable) {
							if (done) return;
							char uname[256] = {};
							rt3->get_uniform_variable_name(variable, uname);
							if (u.uniform_name != uname && lower_str(u.uniform_name) != lower_str(uname)) return;
							apply_anim_values(rt3, variable, u.float_values, u.int_values);
							done = true;
						});
						if (done) break;
					}
					if (done) g_anim_applied[akey] = sig;
				}
			}
		} catch (...) {
			reshade::log::message(reshade::log::level::error, "reshade_controller: animation uniforms threw exception");
		}
	}
}

static void overlay_draw(reshade::api::effect_runtime *)
{
	ImGui::Text("Reshade Controller");
	ImGui::Separator();

	if (g_paused.load())
		ImGui::TextColored(ImVec4(1.0f, 0.4f, 0.4f, 1.0f), "PAUSED (Dalamud)");
	else
		ImGui::TextColored(ImVec4(0.4f, 1.0f, 0.4f, 1.0f), "Active");

	if (!g_last_preset.empty())
	{
		auto filename = std::filesystem::path(g_last_preset).filename().string();
		ImGui::Text("Preset: %s", filename.c_str());
	}
}

extern "C" __declspec(dllexport) const char *NAME = "Reshade Controller";
extern "C" __declspec(dllexport) const char *DESCRIPTION = "Pause/resume and zone-based preset switching for ReShade via Dalamud.";

BOOL WINAPI DllMain(HINSTANCE hinstDLL, DWORD fdwReason, LPVOID)
{
	switch (fdwReason)
	{
	case DLL_PROCESS_ATTACH:
		if (!reshade::register_addon(hinstDLL))
			return FALSE;
		reshade::register_event<reshade::addon_event::init_effect_runtime>(&on_init);
		reshade::register_event<reshade::addon_event::destroy_effect_runtime>(&on_destroy);
		reshade::register_event<reshade::addon_event::reshade_reloaded_effects>(&on_reloaded_effects);
		reshade::register_event<reshade::addon_event::reshade_begin_effects>(&on_begin_effects);
		reshade::register_event<reshade::addon_event::reshade_present>(&on_present);
		reshade::register_overlay("Reshade Controller", &overlay_draw);
		break;
	case DLL_PROCESS_DETACH:
		g_exiting.store(true);
		reshade::unregister_addon(hinstDLL);
		break;
	}
	return TRUE;
}
