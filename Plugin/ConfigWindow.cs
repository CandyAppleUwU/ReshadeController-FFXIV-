using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Dalamud.Interface.Windowing;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Bindings.ImGui;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace ReshadeController;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string filter = string.Empty;
    private bool showOnlyModified = false;
    private List<string> presetFiles = new();
    private string presetSearch = string.Empty;
    private int recordingTarget = 0; // 0 none, 1 shader toggle, 2 menu toggle, 3 animator toggle
    private string? pendingTab;

    // Shaders tab
    private string selectedPresetPath = "";
    private List<string> shaderFiles = new();
    private HashSet<string> shaderFileSet = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> shaderFileCanonical = new(StringComparer.OrdinalIgnoreCase); // any case -> disk case
    private Dictionary<string, bool> effectEnabledState = new(StringComparer.OrdinalIgnoreCase); // keyed "TechName@Effect.fx"
    private Dictionary<string, List<string>> effectTechniques = new(StringComparer.OrdinalIgnoreCase); // file -> all parsed technique names
    private Dictionary<string, List<string>> effectHiddenTechniques = new(StringComparer.OrdinalIgnoreCase); // file -> hidden=true technique names
    private Dictionary<string, string> effectTechTips = new(StringComparer.OrdinalIgnoreCase); // "Tech@File" -> ui_tooltip
    private List<string> techniqueSorting = new();
    private Dictionary<string, Dictionary<string, string>> effectSettings = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<ShaderUniformInfo>> effectUniforms = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> shaderFileFullPaths = new(StringComparer.OrdinalIgnoreCase); // filename -> relative path with subdirs
    private Dictionary<string, List<string>> shaderFileAllPaths = new(StringComparer.OrdinalIgnoreCase); // filename -> EVERY relative path (dup names!)
    private string shaderSearch = string.Empty;
    private string settingsSearch = string.Empty;
    private HashSet<string> expandedEffects = new();
    private Dictionary<string, bool> fxParsed = new(StringComparer.OrdinalIgnoreCase);
    private string? focusedEffect;
    private HashSet<string> pendingExpand = new(StringComparer.OrdinalIgnoreCase);
    private string? draggedTechKey;
    private bool wasSplitActive;
    private string? nicknameEditingKey;
    private string nicknameDraft = "";
    private bool focusNickInput;
    private string? iconPressedKey;
    private string? starPressedKey;
    private Vector2 _lastMousePos;
    private bool showOnlyStarred;
    private DynamicPresetData? activeDynData;
    private string activeDynPath = "";
    private DateTime lastSidecarSave = DateTime.MinValue;
    private bool sidecarDirty;
    private HashSet<string> pendingCollapse = new(StringComparer.OrdinalIgnoreCase);
    private bool focusWasCollapsed;

    private void SetFocus(string? effectName)
    {
        if (focusedEffect != null && focusWasCollapsed)
        {
            expandedEffects.Remove(focusedEffect);
            pendingCollapse.Add(focusedEffect);
        }
        focusedEffect = effectName;
        pendingExpand.Clear();
        if (effectName != null)
        {
            focusWasCollapsed = !expandedEffects.Contains(effectName);
            expandedEffects.Add(effectName);
            pendingExpand.Add(effectName);
        }
    }

    public ConfigWindow(Plugin plugin) : base("Reshade Controller")
    {
        this.plugin = plugin;
        this.Size = new Vector2(750, 600);
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void OnOpen()
    {
        plugin.RefreshConditionSets();
        ScanZonePresets();
        ScanShaderFiles();
    }

    private string GetGameDir()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath)) return "";
        return Path.GetDirectoryName(processPath) ?? "";
    }

    // ==================== ZONE PRESETS TAB ====================

    private void ScanZonePresets()
    {
        presetFiles.Clear();
        try
        {
            var gameDir = GetGameDir();
            if (string.IsNullOrEmpty(gameDir)) return;
            var presetsDir = Path.Combine(gameDir, "reshade-presets");
            if (!Directory.Exists(presetsDir)) return;
            foreach (var file in Directory.EnumerateFiles(presetsDir, "*.ini", SearchOption.AllDirectories))
                presetFiles.Add(file);
            presetFiles.Sort(StringComparer.OrdinalIgnoreCase);
        }
        catch { }
    }

    private string GetPresetDisplayName(string fullPath)
    {
        var presetsDir = Path.Combine(GetGameDir(), "reshade-presets");
        if (fullPath.StartsWith(presetsDir, StringComparison.OrdinalIgnoreCase))
            return fullPath[(presetsDir.Length + 1)..];
        return Path.GetFileName(fullPath);
    }

    public override void PreOpenCheck()
    {
        BgAlpha = Math.Clamp(plugin.Config.WindowBgOpacity, 0.3f, 1f);
    }

    private static readonly ImGuiCol[] FadedContentCols = new[]
    {
        ImGuiCol.Text, ImGuiCol.TextDisabled,
        ImGuiCol.Tab, ImGuiCol.TabActive,
        ImGuiCol.Header,
        ImGuiCol.CheckMark, ImGuiCol.Button,
        ImGuiCol.ScrollbarGrab,
        ImGuiCol.TableHeaderBg, ImGuiCol.TableBorderStrong, ImGuiCol.TableBorderLight,
        ImGuiCol.TableRowBg, ImGuiCol.TableRowBgAlt,
        ImGuiCol.ChildBg, ImGuiCol.FrameBg,
    };

    public override void Draw()
    {
        MergeRuntimeTechniques();
        // Background technique indexing (~10ms/frame) so window open
        // stays instant; rows stream in as files parse.
        PumpTechParseQueue();
        // WindowBg itself rides BgAlpha (see PreOpenCheck); everything else
        // listed fades inline since it's drawn after these pushes.
        float bgOp = Math.Clamp(plugin.Config.WindowBgOpacity, 0.3f, 1f);
        bool fadeContent = bgOp < 0.999f;
        if (fadeContent)
        {
            foreach (var col in FadedContentCols)
            {
                var c = ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(col));
                c.W *= bgOp;
                ImGui.PushStyleColor(col, c);
            }
        }
        if (ImGui.BeginTabBar("##reshadeTabs"))
        {
            if (ImGui.BeginTabItem("Main"))
            {
                // Ko-Fi + Discord buttons, top-right of Main.
                {
                    const string kofiLabel = "Ko-Fi";
                    const string discordLabel = "Discord";
                    float bw = ImGui.CalcTextSize(kofiLabel).X + ImGui.GetStyle().FramePadding.X * 2;
                    float dw = ImGui.CalcTextSize(discordLabel).X + ImGui.GetStyle().FramePadding.X * 2;
                    float gap = ImGui.GetStyle().ItemSpacing.X;
                    ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMax().X - dw - gap - bw);
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.35f, 0.40f, 0.95f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.42f, 0.47f, 1f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.28f, 0.33f, 0.80f, 1f));
                    if (ImGui.Button(discordLabel, new Vector2(dw, 0)))
                    {
                        try { Dalamud.Utility.Util.OpenLink("https://discord.gg/bKswgraKvh"); } catch { }
                    }
                    ImGui.PopStyleColor(3);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Join the Discord");
                    ImGui.SameLine(0, 0);
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.72f, 0.38f, 0.05f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.82f, 0.45f, 0.08f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.60f, 0.30f, 0.03f, 1f));
                    if (ImGui.Button(kofiLabel, new Vector2(bw, 0)))
                    {
                        try { Dalamud.Utility.Util.OpenLink("https://ko-fi.com/candyappleffxiv"); } catch { }
                    }
                    ImGui.PopStyleColor(3);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Support on Ko-Fi");
                }
                DrawCurrentZone();
                ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
                DrawHotkeyConfig();
                ImGui.Spacing();
                DrawWindowOpacity();
                ImGui.Spacing();
                DrawDlssToggle("main");
                bool dlssPresent = false;
                try { dlssPresent = IsDlssAddonPresent(); } catch { }
                if (!dlssPresent) ImGui.BeginDisabled();
                bool dlssCs = plugin.Config.DlssInCutscenes;
                if (ImGui.Checkbox("DLSS 5 In Cutscenes##dlsscs", ref dlssCs))
                {
                    plugin.Config.DlssInCutscenes = dlssCs;
                    plugin.Config.Save();
                }
                if (!dlssPresent) ImGui.EndDisabled();
                if (!dlssPresent)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "(Not Detected)");
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Automation: forces Neural Rendering ON inside cutscenes\n(OccupiedInCutSceneEvent) and back OFF outside.\nWhile on, it owns NR — manual toggles get overridden.");
                ImGui.Separator(); ImGui.Spacing();
                DrawDefaultPreset();
                ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
                DrawZoneList();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Shaders", pendingTab == "Shaders" ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
            {
                DrawShadersTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Weather"))
            {
                DrawWeatherTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Settings"))
            {
                DrawSettingsTab();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
            pendingTab = null;
        }
        if (fadeContent) ImGui.PopStyleColor(FadedContentCols.Length);
    }

    public void ToggleShadersTab()
    {
        if (IsOpen) IsOpen = false;
        else { pendingTab = "Shaders"; IsOpen = true; }
    }

    // True while the Settings tab drew this frame (consumed each frame by
    // the outline overlay, so it only renders with the tab open).
    internal bool SettingsTabDrawn;

    private void DrawSettingsTab()
    {
        SettingsTabDrawn = true;
        ImGui.TextWrapped("Some dynamic shaders work based on what you're looking at adjust this if u have random menus covering a whole side of ur screen or something :)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The coords Visible gate only counts what falls inside this rect.\nUse it to ignore screen areas covered by side menus etc.\nMeasured in fractions of the game window (0-1).");
        float l = plugin.Config.ViewAreaLeft, t = plugin.Config.ViewAreaTop;
        float r = plugin.Config.ViewAreaRight, b = plugin.Config.ViewAreaBottom;
        bool changed = false;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputFloat("Left##viewarea", ref l, 0.01f, 0.05f, "%.3f")) { plugin.Config.ViewAreaLeft = Math.Clamp(l, 0f, 1f); changed = true; }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputFloat("Top##viewarea", ref t, 0.01f, 0.05f, "%.3f")) { plugin.Config.ViewAreaTop = Math.Clamp(t, 0f, 1f); changed = true; }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputFloat("Right##viewarea", ref r, 0.01f, 0.05f, "%.3f")) { plugin.Config.ViewAreaRight = Math.Clamp(r, 0f, 1f); changed = true; }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputFloat("Bottom##viewarea", ref b, 0.01f, 0.05f, "%.3f")) { plugin.Config.ViewAreaBottom = Math.Clamp(b, 0f, 1f); changed = true; }
        if (changed) plugin.Config.Save();
        if (ImGui.Button("Reset##viewarea"))
        {
            plugin.Config.ViewAreaLeft = 0f;
            plugin.Config.ViewAreaTop = 0f;
            plugin.Config.ViewAreaRight = 1f;
            plugin.Config.ViewAreaBottom = 1f;
            plugin.Config.Save();
        }
        ImGui.SameLine();
        if (Plugin.TryGetGameClientSize(out int w, out int h))
        {
            int px = (int)(plugin.Config.ViewAreaLeft * w);
            int py = (int)(plugin.Config.ViewAreaTop * h);
            int pw = (int)((plugin.Config.ViewAreaRight - plugin.Config.ViewAreaLeft) * w);
            int ph = (int)((plugin.Config.ViewAreaBottom - plugin.Config.ViewAreaTop) * h);
            ImGui.TextDisabled($"Window {w}x{h} -> area ({px},{py}) {pw}x{ph}");
        }
        else ImGui.TextDisabled("Game window size unknown");
        ImGui.SameLine();
        bool outline = plugin.Config.ViewAreaOutline;
        if (ImGui.Checkbox("Outline##viewarea", ref outline))
        {
            plugin.Config.ViewAreaOutline = outline;
            plugin.Config.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Draw the play-area rectangle on screen.");
        ImGui.Separator();
        bool dev = plugin.Config.DeveloperMode;
        if (ImGui.Checkbox("Developer mode##devmode", ref dev))
        {
            plugin.Config.DeveloperMode = dev;
            plugin.Config.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Shows developer settings (daylight recording etc.).");
    }

    // Weather tab state (cached per territory; the .lvb read is not free).
    private uint weatherTabTerritory = uint.MaxValue;
    private List<byte> weatherTabZoneWeathers = new();

    private static readonly string[] WxMoonPhases = new[]
    {
        "New Moon", "Waxing Crescent", "Waxing Half Moon", "Waxing Gibbous",
        "Full Moon", "Waning Gibbous", "Waning Half Moon", "Waning Crescent",
    };

    private static int WxClamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

    // Normal (naturally occurring) weathers for a territory, via WeatherRate.
    private bool WxIsWeatherNormal(byte id, ushort terr)
    {
        try
        {
            var t = Service.DataManager.GetExcelSheet<TerritoryType>().GetRow(terr);
            var r = Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.WeatherRate>().GetRow(t.WeatherRate.RowId);
            foreach (var w in r.Weather)
                if (w.RowId != 0 && w.RowId == id) return true;
        }
        catch { }
        return false;
    }

    private void WxRefreshZoneWeathers()
    {
        weatherTabTerritory = plugin.CurrentTerritoryId;
        try { weatherTabZoneWeathers = plugin.Weather.GetZoneWeathers((ushort)plugin.CurrentTerritoryId); }
        catch { weatherTabZoneWeathers = new List<byte>(); }
    }

    private void WxRefuse(string what)
    {
        try { Service.ChatGui.Print($"[ReshadeController] Built-in {what} refused: external Weatherman override is active — clear it there first."); } catch { }
    }

    private void WxFailed(string what)
    {
        try { Service.ChatGui.Print($"[ReshadeController] Built-in {what} failed to engage. See status above."); } catch { }
    }

    // Built-in weather/time override, laid out like Weatherman's quick
    // control tab. Everything is gated by the master switch: while it is
    // off no patches are applied and the override manager never scans
    // signatures. Weather resets on zone change.
    private void DrawWeatherTab()
    {
        var cfg = plugin.Config;
        // External Weatherman owns its patches; never fight it. Detected
        // up-front so even the master switch can lock while it is live.
        bool extWeather = false, extTime = false;
        try { extWeather = plugin.IsExternalWeathermanWeatherCustom(); } catch { }
        try { extTime = plugin.IsExternalWeathermanTimeCustom(); } catch { }
        bool masterLocked = !cfg.WeatherControlEnabled && (extWeather || extTime);
        if (masterLocked) ImGui.BeginDisabled();
        bool master = cfg.WeatherControlEnabled;
        if (ImGui.Checkbox("Enable Weather Control##wx", ref master))
        {
            cfg.WeatherControlEnabled = master;
            if (!master)
            {
                try { plugin.StopWeatherTimePlay(false); } catch { }
                try { plugin.Weather.DisableAll(); } catch { }
                cfg.WeatherCustomOn = false;
                cfg.TimeCustomOn = false;
                cfg.DayCustomOn = false;
            }
            cfg.Save();
        }
        if (masterLocked) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Master switch. While OFF nothing is patched and no signatures are scanned.\nNo Weatherman plugin required.\nLocked while an external Weatherman override is live — clear it there first.");

        if (!cfg.WeatherControlEnabled)
        {
            ImGui.TextWrapped("Control Weather and Time to see changes of dynamic shader presets.");
            try
            {
                if (plugin.IsExternalWeathermanWeatherCustom())
                    ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "(Weatherman Detected! Disable it if you want to use this, hopefully we can get weatherman support in the near future)");
            }
            catch { }
            return;
        }

        try
        {
            var st = plugin.Weather.Status;
            if (st != "Disabled" && st != "Ready")
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), st);
        }
        catch { }

        // External Weatherman owns its patches; never fight it.
        // (Detected above so the master switch can lock on it too.)
        if (extWeather && !cfg.WeatherCustomOn)
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), "External Weatherman weather override is active — clear it there before forcing weather here.");
        if (extTime && !cfg.TimeCustomOn && !cfg.DayCustomOn)
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), "External Weatherman time override is active — clear it there before freezing here.");

        ImGui.TextWrapped("These controls temporarily adjust weather and time. Weather is reset on zone change.");
        ImGui.Text($"Zone: {plugin.CurrentTerritoryName} ({plugin.CurrentTerritoryId})");

        if (weatherTabTerritory != plugin.CurrentTerritoryId)
            WxRefreshZoneWeathers();

        // ---- time row: [Time: x] [slider 0-24 HH:MM:SS] [Date: ] [day slider] ----
        // Locked (unclickable) while an external override holds the patches.
        bool timeLocked = extTime && !cfg.TimeCustomOn;
        if (timeLocked) ImGui.BeginDisabled();
        bool tOn = cfg.TimeCustomOn;
        if (ImGui.Checkbox("Time: ##wx", ref tOn))
        {
            if (tOn)
            {
                if (extTime) WxRefuse("time freeze");
                else
                {
                    int live = cfg.ForcedTimeSeconds;
                    try { if (!plugin.Weather.IsTimeCustom()) live = plugin.GetEorzeaSecondsPublic(); } catch { }
                    live = ((live % 86400) + 86400) % 86400;
                    if (plugin.Weather.EnableTime((uint)live))
                    {
                        cfg.ForcedTimeSeconds = live;
                        cfg.TimeCustomOn = true;
                    }
                    else WxFailed("time freeze");
                    cfg.Save();
                }
            }
            else
            {
                try { plugin.Weather.DisableTime(); } catch { }
                try { plugin.Weather.DisableDay(); } catch { }
                try { plugin.StopWeatherTimePlay(false); } catch { }
                cfg.TimeCustomOn = false;
                cfg.DayCustomOn = false;
                cfg.Save();
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Holds time-of-day. The time timeline follows the frozen clock.\nUnchecking also releases the date and stops playback.");
        // Play speed (seconds per full 24h cycle) + play/stop, left of
        // the slider.
        ImGui.SameLine();
        ImGui.SetNextItemWidth(56f);
        {
            int secs = Math.Clamp(cfg.WeatherPlayCycleSeconds, 5, 600);
            if (ImGui.InputInt("s##wxCycle", ref secs, 0, 0))
            {
                cfg.WeatherPlayCycleSeconds = Math.Clamp(secs, 5, 600);
                cfg.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Seconds per full 24h cycle (5–600).");
        }
        ImGui.SameLine();
        {
            bool playing = false;
            try { playing = plugin.WeatherTimePlaying; } catch { }
            if (ImGui.Button(playing ? "■##wxPlay" : "▶##wxPlay"))
            {
                if (playing)
                {
                    try { plugin.StopWeatherTimePlay(); } catch { }
                }
                else if (extTime && !cfg.TimeCustomOn) WxRefuse("time freeze");
                else
                {
                    int start = ((cfg.ForcedTimeSeconds % 86400) + 86400) % 86400;
                    if (!cfg.TimeCustomOn)
                    {
                        try { if (!plugin.Weather.IsTimeCustom()) start = plugin.GetEorzeaSecondsPublic(); } catch { }
                        start = ((start % 86400) + 86400) % 86400;
                    }
                    if (plugin.Weather.EnableTime((uint)start))
                    {
                        cfg.ForcedTimeSeconds = start;
                        cfg.TimeCustomOn = true;
                        cfg.Save();
                        try { plugin.WeatherTimePlaying = true; } catch { }
                    }
                    else WxFailed("time playback");
                }
            }
            if (ImGui.IsItemHovered())
            {
                int secs = Math.Clamp(cfg.WeatherPlayCycleSeconds, 5, 600);
                ImGui.SetTooltip(playing ? "Stop playback (clock stays where it is)" : $"Play the full 24h cycle smoothly over {secs}s");
            }
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150f);
        {
            var span = TimeSpan.FromSeconds(cfg.ForcedTimeSeconds);
            int position = (int)MathF.Ceiling(cfg.ForcedTimeSeconds / 3600f);
            if (ImGui.SliderInt("##wxTime", ref position, 0, 24, $"{span.Hours:D2}:{span.Minutes:D2}:{span.Seconds:D2}"))
            {
                int v = position * 3600;
                if (v == 86400) v -= 1;
                if (extTime && !cfg.TimeCustomOn) WxRefuse("time freeze");
                else if (plugin.Weather.EnableTime((uint)v))
                {
                    cfg.ForcedTimeSeconds = v;
                    cfg.TimeCustomOn = true;
                    cfg.Save();
                }
                else WxFailed("time freeze");
            }
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                ImGui.OpenPopup("##wxPreciseTime");
        }
        if (ImGui.BeginPopup("##wxPreciseTime"))
        {
            var oSpan = TimeSpan.FromSeconds(cfg.ForcedTimeSeconds);
            int ph = oSpan.Hours, pm = oSpan.Minutes, ps = oSpan.Seconds;
            ImGui.Text("Precise editing");
            ImGui.Text("Hours:");
            ImGui.SameLine(75f);
            ImGui.SetNextItemWidth(100f);
            ImGui.InputInt("##wxPH", ref ph);
            ph = WxClamp(ph, 0, 23);
            ImGui.Text("Minutes:");
            ImGui.SameLine(75f);
            ImGui.SetNextItemWidth(100f);
            ImGui.InputInt("##wxPM", ref pm);
            pm = WxClamp(pm, 0, 59);
            ImGui.Text("Seconds:");
            ImGui.SameLine(75f);
            ImGui.SetNextItemWidth(100f);
            ImGui.InputInt("##wxPS", ref ps);
            ps = WxClamp(ps, 0, 59);
            int v = ph * 3600 + pm * 60 + ps;
            if (v != cfg.ForcedTimeSeconds)
            {
                if (extTime && !cfg.TimeCustomOn) WxRefuse("time freeze");
                else if (plugin.Weather.EnableTime((uint)v))
                {
                    cfg.ForcedTimeSeconds = v;
                    cfg.TimeCustomOn = true;
                    cfg.Save();
                }
                else WxFailed("time freeze");
            }
            ImGui.EndPopup();
        }
        if (timeLocked) ImGui.EndDisabled();
        cfg.ForcedTimeSeconds = WxClamp(cfg.ForcedTimeSeconds, 0, 86399);

        ImGui.SameLine();
        ImGui.Text("Date: ");
        ImGui.SameLine();
        bool dayLocked = extTime && !cfg.TimeCustomOn && !cfg.DayCustomOn;
        if (dayLocked) ImGui.BeginDisabled();
        ImGui.SetNextItemWidth(150f);
        {
            int day = WxClamp(cfg.ForcedDay, 1, 32);
            if (ImGui.SliderInt("##wxDay", ref day, 1, 32, $"Day {day} - {WxMoonPhases[(day - 1) / 4]}"))
            {
                if (extTime && !cfg.TimeCustomOn && !cfg.DayCustomOn) WxRefuse("date");
                else
                {
                    // A set date implies a frozen clock, like upstream.
                    bool ok = true;
                    if (!cfg.TimeCustomOn)
                    {
                        int live = cfg.ForcedTimeSeconds;
                        try { if (!plugin.Weather.IsTimeCustom()) live = plugin.GetEorzeaSecondsPublic(); } catch { }
                        live = ((live % 86400) + 86400) % 86400;
                        ok = plugin.Weather.EnableTime((uint)live);
                        if (ok) { cfg.ForcedTimeSeconds = live; cfg.TimeCustomOn = true; }
                    }
                    if (ok && plugin.Weather.EnableDay((uint)day))
                    {
                        cfg.ForcedDay = day;
                        cfg.DayCustomOn = true;
                    }
                    else WxFailed("date");
                    cfg.Save();
                }
            }
        }
        if (dayLocked) ImGui.EndDisabled();

        // ---- weather radio list (zone weathers, normals green) ----
        // Locked (unclickable) while an external override holds the patches.
        bool wxLocked = extWeather && !cfg.WeatherCustomOn;
        if (wxLocked) ImGui.BeginDisabled();
        ushort terr = (ushort)plugin.CurrentTerritoryId;
        var names = new Dictionary<byte, string>();
        try
        {
            foreach (var w in GetWeatherList())
                names[(byte)w.Id] = w.Name;
        }
        catch { }
        if (weatherTabZoneWeathers.Count == 0)
        {
            ImGui.TextDisabled("No zone weather list for this territory.");
        }
        else
        {
            foreach (var i in weatherTabZoneWeathers)
            {
                ImGui.PushID(i.ToString());
                string label = names.TryGetValue(i, out var n) && !string.IsNullOrWhiteSpace(n) ? n : i.ToString();
                bool colored = false;
                if (WxIsWeatherNormal(i, terr))
                {
                    try { ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.ParsedGreen); colored = true; } catch { }
                }
                if (ImGui.RadioButton(label, cfg.WeatherCustomOn && cfg.ForcedWeatherId == i))
                {
                    if (extWeather && !cfg.WeatherCustomOn) WxRefuse("weather");
                    else if (plugin.Weather.EnableWeather(i, terr))
                    {
                        cfg.ForcedWeatherId = i;
                        cfg.WeatherCustomOn = true;
                        cfg.Save();
                    }
                    else WxFailed("weather");
                }
                if (colored) ImGui.PopStyleColor(1);
                ImGui.PopID();
            }
        }
        if (cfg.WeatherCustomOn && ImGui.Button("Reset weather##wx"))
        {
            try { plugin.Weather.DisableWeather(); } catch { }
            cfg.WeatherCustomOn = false;
            cfg.Save();
        }
        if (ImGui.Button("Re-apply##wx"))
        {
            WxRefreshZoneWeathers();
            try
            {
                if (cfg.WeatherCustomOn) plugin.Weather.SetWeather(cfg.ForcedWeatherId);
                if (cfg.TimeCustomOn) plugin.Weather.SetTime((uint)cfg.ForcedTimeSeconds);
                if (cfg.DayCustomOn) plugin.Weather.SetDay((uint)WxClamp(cfg.ForcedDay, 1, 32));
            }
            catch { }
        }
        if (wxLocked) ImGui.EndDisabled();

        // ---- daylight recording (developer-only) ----
        if (plugin.Config.DeveloperMode)
        {
        ImGui.Separator();
        ImGui.Text("Daylight curve (global, per-node Curve enables it):");
        {
            var dl = ActiveDaylight();
            if (dl != null && dl.Values.Count > 0)
                ImGui.TextDisabled($"Recorded: {dl.Samples} samples, range {dl.Min:F0}–{dl.Max:F0}, {dl.RecordedUtc}");
            else
                ImGui.TextDisabled("No daylight data recorded yet.");
        }
        bool recording = false;
        try { recording = plugin.DaylightRecordArmed; } catch { }
        if (recording)
        {
            if (ImGui.Button("Stop recording##wxDl"))
            {
                try { plugin.StopDaylightRecord(); } catch { }
            }
            ImGui.SameLine();
            try
            {
                bool playing = plugin.WeatherTimePlaying;
                if (!playing)
                    ImGui.TextDisabled("Armed — run the recorder; playback starts on region confirm.");
                else
                    ImGui.TextDisabled($"Recording… loop {plugin.DaylightLoopProgress() * 100f:F0}% (clock @ {plugin.GetEorzeaSecondsPublic()}s)");
            }
            catch { }
        }
        else
        {
            if (ImGui.Button("Record daylight cycle##wxDl"))
            {
                // Arms 00:00 + clock publisher. The recorder triggers
                // playback via daylight_go.txt on region confirm, so no
                // alt-tab race. Ends itself at one full loop.
                bool ok = false;
                try { ok = plugin.StartDaylightRecord(); } catch { }
                try
                {
                    daylightStatus = ok
                        ? "Armed at 00:00. Run: record --fps 10 --duration 70 -o daylight.csv --region … --eorzea-file <gamedir>\\daylight_clock.txt --go-file <gamedir>\\daylight_go.txt"
                        : "Record refused (external time override?).";
                }
                catch { }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Freezes at midnight and arms. Playback starts when the recorder confirms its region; stops itself after one full day loop.");
        }
        ImGui.SetNextItemWidth(280);
        try
        {
            if (string.IsNullOrEmpty(daylightCsvPath))
            {
                var gd = GetGameDir();
                daylightCsvPath = string.IsNullOrEmpty(gd) ? "daylight.csv" : Path.Combine(gd, "daylight.csv");
            }
        }
        catch { }
        ImGui.InputTextWithHint("##wxDlCsv", "daylight.csv path", ref daylightCsvPath, 260);
        ImGui.SameLine();
        if (ImGui.Button("Import##wxDl"))
        {
            try { ImportDaylightCsv(); } catch (Exception ex) { try { daylightStatus = "Import failed: " + ex.Message; } catch { } }
        }
        if (!string.IsNullOrEmpty(daylightStatus))
            ImGui.TextDisabled(daylightStatus);
        if (ImGui.Button("Snap chain to day/night##wxDlSnap"))
        {
            try { SnapChainToDaylight(); } catch (Exception ex) { try { daylightStatus = "Snap failed: " + ex.Message; } catch { } }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Sets the winning chain's earliest keyframe to the measured darkest second and latest to the brightest. Engine unchanged.");
        } // DeveloperMode
    }

    private string daylightCsvPath = "";
    private string daylightStatus = "";
    private string trimStatus = "";

    // Merge a recorder CSV (needs the eorzea_s column from --eorzea-file)
    // into per-Eorzea-time bins, smooth, and store in the GLOBAL daylight
    // file (shared by every preset). Refuses thin coverage (<80% of day).
    private void ImportDaylightCsv()
    {
        daylightStatus = "";
        try
        {
            if (string.IsNullOrEmpty(daylightCsvPath) || !File.Exists(daylightCsvPath))
            {
                daylightStatus = "CSV not found. Re-record with --eorzea-file <gamedir>\\daylight_clock.txt.";
                return;
            }
            var pts = new List<(float Eorzea, float Bright)>();
            foreach (var line in File.ReadLines(daylightCsvPath))
            {
                var row = line.Split(',');
                if (row.Length < 4) continue;
                if (!float.TryParse(row[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) continue;
                if (!float.TryParse(row[2].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float b)) continue;
                if (!float.TryParse(row[3].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float e)) continue;
                e = ((e % 86400f) + 86400f) % 86400f;
                if (b < 0 || b > 1000) continue;
                pts.Add((e, b));
            }
            if (pts.Count == 0)
            {
                daylightStatus = "No usable rows (need eorzea_s column — re-record with --eorzea-file).";
                return;
            }
            const int Bins = DynamicDaylight.Bins;
            var sum = new double[Bins];
            var cnt = new int[Bins];
            foreach (var (e, b) in pts)
            {
                int bi = Math.Clamp((int)(e / 86400f * Bins), 0, Bins - 1);
                sum[bi] += b;
                cnt[bi]++;
            }
            int hit = cnt.Count(c => c > 0);
            double coverage = (double)hit / Bins;
            if (coverage < 0.8)
            {
                daylightStatus = $"Only {coverage:P0} of the day covered — record a fuller cycle.";
                return;
            }
            // Fill gaps by circular linear interpolation between hit bins.
            var mean = new double[Bins];
            for (int i = 0; i < Bins; i++) mean[i] = cnt[i] > 0 ? sum[i] / cnt[i] : double.NaN;
            for (int i = 0; i < Bins; i++)
            {
                if (!double.IsNaN(mean[i])) continue;
                int prev = -1, next = -1;
                for (int k = 1; k <= Bins; k++)
                {
                    int pi = (((i - k) % Bins) + Bins) % Bins;
                    int ni = (i + k) % Bins;
                    if (prev < 0 && !double.IsNaN(mean[pi])) prev = pi;
                    if (next < 0 && !double.IsNaN(mean[ni])) next = ni;
                    if (prev >= 0 && next >= 0) break;
                }
                if (prev < 0 && next < 0) { mean[i] = 0; continue; }
                if (prev < 0) prev = next;
                if (next < 0) next = prev;
                int back = (i - prev + Bins) % Bins;
                int span = (next - prev + Bins) % Bins;
                if (span <= 0) span = Bins;
                mean[i] = mean[prev] + (mean[next] - mean[prev]) * back / (double)span;
            }
            // Box smooth (radius 2, circular).
            var sm = new double[Bins];
            for (int i = 0; i < Bins; i++)
            {
                double acc = 0;
                for (int k = -2; k <= 2; k++) acc += mean[(i + k + Bins * 10) % Bins];
                sm[i] = acc / 5.0;
            }
            float mn = float.MaxValue, mx = float.MinValue;
            var vals = new List<float>(Bins);
            for (int i = 0; i < Bins; i++)
            {
                float v = (float)sm[i];
                vals.Add(v);
                if (v < mn) mn = v;
                if (v > mx) mx = v;
            }
            EnsureDynCache();
            var gd = GetGameDir();
            if (string.IsNullOrEmpty(gd))
            {
                daylightStatus = "Game dir unknown.";
                return;
            }
            var newDl = new DynamicDaylight
            {
                Values = vals,
                Samples = pts.Count,
                RecordedUtc = DateTime.UtcNow.ToString("o"),
                Min = mn,
                Max = mx,
            };
            try { DynamicDaylightStore.Save(gd, newDl); } catch (Exception ex) { daylightStatus = "Save failed: " + ex.Message; return; }
            daylightStatus = $"Imported {pts.Count} samples, {coverage:P0} coverage, range {mn:F0}–{mx:F0} (global).";
        }
        catch (Exception ex) { try { daylightStatus = "Import failed: " + ex.Message; } catch { } }
    }

    // Snap the winning chain's time-extreme endpoints to the measured
    // daylight extrema: earliest keyframe -> darkest second, latest ->
    // brightest. Direction-preserving (never inverts), middles untouched,
    // engine reads the same objects either way.
    internal void SnapChainToDaylight()
    {
        daylightStatus = "";
        try
        {
            if (string.IsNullOrEmpty(selectedPresetPath) || !IsDynamicPreset(selectedPresetPath))
            {
                daylightStatus = "Select a dynamic preset first.";
                return;
            }
            EnsureDynCache();
            var dd = activeDynData;
            if (dd == null || !string.Equals(activeDynPath, selectedPresetPath, StringComparison.OrdinalIgnoreCase))
            {
                dd = DynamicPresetStore.Load(selectedPresetPath) ?? new DynamicPresetData();
                dd.Normalize();
            }
            var snapDl = ActiveDaylight(dd);
            if (snapDl == null || snapDl.Values == null || snapDl.Values.Count == 0)
            {
                daylightStatus = "No daylight data — Import a CSV first.";
                return;
            }
            var frames = DynamicTimeline.ResolveChainFrames(dd, null);
            if (frames == null || frames.Count < 2)
            {
                daylightStatus = "Need a chain of 2+ keyframes (link two time nodes first).";
                return;
            }
            var (minSec, maxSec) = DynamicTimeline.DaylightExtrema(snapDl.Values);
            var earliest = frames.OrderBy(f => f.TimeSeconds).First();
            var latest = frames.OrderBy(f => f.TimeSeconds).Last();
            if (ReferenceEquals(earliest, latest))
            {
                daylightStatus = "Chain resolves to one keyframe — bind two different keyframes.";
                return;
            }
            earliest.TimeSeconds = minSec;
            latest.TimeSeconds = maxSec;
            try
            {
                var eNode = dd.Nodes.FirstOrDefault(n => n.KeyframeId == earliest.Id);
                var lNode = dd.Nodes.FirstOrDefault(n => n.KeyframeId == latest.Id);
                if (eNode != null) eNode.DayNightRole = 0;
                if (lNode != null && !ReferenceEquals(lNode, eNode)) lNode.DayNightRole = 1;
            }
            catch { }
            try { DynamicPresetStore.Save(selectedPresetPath, dd); } catch (Exception ex) { daylightStatus = "Save failed: " + ex.Message; return; }
            activeDynData = dd;
            activeDynPath = selectedPresetPath;
            sidecarDirty = false;
            lastSidecarSave = DateTime.UtcNow;
            string eName = string.IsNullOrEmpty(earliest.Name) ? "unnamed" : earliest.Name;
            string lName = string.IsNullOrEmpty(latest.Name) ? "unnamed" : latest.Name;
            daylightStatus = $"Snapped: night {EorzeaFormat.SecondsToTimeString(minSec)} <- '{eName}', day {EorzeaFormat.SecondsToTimeString(maxSec)} <- '{lName}'.";
            try
            {
                var start = DynamicTimeline.ResolveChainStartNode(dd, null);
                if (start == null || start.CurveMode != 2)
                    daylightStatus += " Set chain-start Curve to 24H Brightness Curve for wrap-free blending (night look on the earlier node).";
            }
            catch { }
        }
        catch (Exception ex) { try { daylightStatus = "Snap failed: " + ex.Message; } catch { } }
    }

    private void DrawCurrentZone()
    {
        if (plugin.IsPaused)
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "Shaders: PAUSED");
        else
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "Shaders: Running");
        ImGui.SameLine();
        ImGui.Text($"Current Zone: {plugin.CurrentTerritoryName} ({plugin.CurrentTerritoryId})");
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);
    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);
    private const int IDC_SIZEWE = 32644;
    private static IntPtr sizeWeCursor = IntPtr.Zero;

    private void DrawHotkeyConfig()
    {
        DrawHotkeyRow("Shader Toggle:", 1);
        DrawHotkeyRow("Shader Menu Toggle:", 2);
        DrawHotkeyRow("Animator Menu Toggle:", 3);
    }

    private void DrawWindowOpacity()
    {
        float op = Math.Clamp(plugin.Config.WindowBgOpacity, 0.3f, 1f);
        ImGui.Text("Window Background Opacity:"); ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("##bgopacity", ref op, 0.3f, 1f, "%.2f"))
        {
            plugin.Config.WindowBgOpacity = op;
            plugin.Config.Save();
        }
    }

    private void DrawHotkeyRow(string label, int id)
    {
        int key = id == 1 ? plugin.Config.ToggleHotkeyKey : id == 2 ? plugin.Config.MenuHotkeyKey : plugin.Config.AnimatorHotkeyKey;
        int mod = id == 1 ? plugin.Config.ToggleHotkeyMod : id == 2 ? plugin.Config.MenuHotkeyMod : plugin.Config.AnimatorHotkeyMod;
        ImGui.Text(label); ImGui.SameLine();
        string keyName = GetHotkeyDisplayName(key, mod);
        if (recordingTarget == id)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.2f, 0.2f, 1f));
            if (ImGui.Button($"Press a key...##hotkey{id}")) recordingTarget = 0;
            ImGui.PopStyleColor();
            for (int vk = 1; vk < 256; vk++)
            {
                if (vk == 0x10 || vk == 0x11 || vk == 0x12) continue;
                if ((GetAsyncKeyState(vk) & 0x8000) != 0)
                {
                    int mods = 0;
                    if ((GetAsyncKeyState(0x10) & 0x8000) != 0) mods |= 1;
                    if ((GetAsyncKeyState(0x11) & 0x8000) != 0) mods |= 2;
                    if ((GetAsyncKeyState(0x12) & 0x8000) != 0) mods |= 4;
                    if (id == 1) { plugin.Config.ToggleHotkeyKey = vk; plugin.Config.ToggleHotkeyMod = mods; }
                    else if (id == 2) { plugin.Config.MenuHotkeyKey = vk; plugin.Config.MenuHotkeyMod = mods; }
                    else { plugin.Config.AnimatorHotkeyKey = vk; plugin.Config.AnimatorHotkeyMod = mods; }
                    plugin.Config.Save();
                    recordingTarget = 0;
                    break;
                }
            }
        }
        else
        {
            if (ImGui.Button($"{keyName}##hotkey{id}", new Vector2(200, 0))) recordingTarget = id;
            ImGui.SameLine();
            if (key != 0 && ImGui.Button($"X##hotkeyx{id}", new Vector2(25, 0)))
            {
                if (id == 1) { plugin.Config.ToggleHotkeyKey = 0; plugin.Config.ToggleHotkeyMod = 0; }
                else if (id == 2) { plugin.Config.MenuHotkeyKey = 0; plugin.Config.MenuHotkeyMod = 0; }
                else { plugin.Config.AnimatorHotkeyKey = 0; plugin.Config.AnimatorHotkeyMod = 0; }
                plugin.Config.Save();
            }
        }
    }

    private static string GetHotkeyDisplayName(int key, int mod)
    {
        if (key == 0) return "None";
        string modStr = "";
        if ((mod & 1) != 0) modStr += "Shift + ";
        if ((mod & 2) != 0) modStr += "Ctrl + ";
        if ((mod & 4) != 0) modStr += "Alt + ";
        string keyName = key switch
        {
            0x70 => "F1", 0x71 => "F2", 0x72 => "F3", 0x73 => "F4",
            0x74 => "F5", 0x75 => "F6", 0x76 => "F7", 0x77 => "F8",
            0x78 => "F9", 0x79 => "F10", 0x7A => "F11", 0x7B => "F12",
            0x30 => "0", 0x31 => "1", 0x32 => "2", 0x33 => "3", 0x34 => "4",
            0x35 => "5", 0x36 => "6", 0x37 => "7", 0x38 => "8", 0x39 => "9",
            0x41 => "A", 0x42 => "B", 0x43 => "C", 0x44 => "D", 0x45 => "E",
            0x46 => "F", 0x47 => "G", 0x48 => "H", 0x49 => "I", 0x4A => "J",
            0x4B => "K", 0x4C => "L", 0x4D => "M", 0x4E => "N", 0x4F => "O",
            0x50 => "P", 0x51 => "Q", 0x52 => "R", 0x53 => "S", 0x54 => "T",
            0x55 => "U", 0x56 => "V", 0x57 => "W", 0x58 => "X", 0x59 => "Y",
            0x5A => "Z", 0x20 => "Space", 0x0D => "Enter", 0x1B => "Esc", 0x09 => "Tab",
            _ => $"VK{key:X}"
        };
        return modStr + keyName;
    }

    private void DrawDefaultPreset()
    {
        ImGui.Text("Default Preset (used when zone has no override):");
        DrawZonePresetCombo("##defaultPreset", ref plugin.Config.DefaultPresetPath);
        plugin.Config.Save();
        ImGui.Spacing();
        ImGui.Text("Preset to use in Cutscenes:");
        DrawZonePresetCombo("##cutscenePreset", ref plugin.Config.CutscenePresetPath);
        plugin.Config.Save();
        ImGui.Spacing();
        ImGui.Text("Preset to use in Duty:");
        DrawZonePresetCombo("##dutyPreset", ref plugin.Config.DutyPresetPath);
        plugin.Config.Save();
    }

    private string? GetNickname(string techKey)
    {
        var k = techKey.ToLowerInvariant();
        foreach (var kvp in plugin.Config.TechniqueNicknames)
            if (kvp.Key.ToLowerInvariant() == k && !string.IsNullOrWhiteSpace(kvp.Value))
                return kvp.Value;
        return null;
    }

    private void SetNickname(string techKey, string nickname)
    {
        var k = techKey.ToLowerInvariant();
        string? drop = null;
        foreach (var existing in plugin.Config.TechniqueNicknames.Keys)
            if (existing.ToLowerInvariant() == k) { drop = existing; break; }
        if (drop != null) plugin.Config.TechniqueNicknames.Remove(drop);
        nickname = nickname.Trim();
        if (!string.IsNullOrEmpty(nickname))
            plugin.Config.TechniqueNicknames[k] = nickname;
        plugin.Config.Save();
    }

    private bool RowMatchesSearch(string tech, string file, string key, string queryLower)
    {
        if (string.IsNullOrEmpty(queryLower)) return true;
        if (tech.ToLowerInvariant().Contains(queryLower)) return true;
        if (file.ToLowerInvariant().Contains(queryLower)) return true;
        var nick = GetNickname(key);
        return nick != null && nick.ToLowerInvariant().Contains(queryLower);
    }

    private bool FileMatchesSearch(string file, string queryLower)
    {
        if (string.IsNullOrEmpty(queryLower)) return true;
        if (file.ToLowerInvariant().Contains(queryLower)) return true;
        foreach (var t in VisibleTechNamesFor(file))
        {
            if (t.ToLowerInvariant().Contains(queryLower)) return true;
            var nick = GetNickname($"{t}@{file}");
            if (nick != null && nick.ToLowerInvariant().Contains(queryLower)) return true;
        }
        return false;
    }

    private bool IsFavoriteEffect(string techKey) =>
        plugin.Config.FavoriteEffects.Any(f => TechKeyEquals(f, techKey));

    private void ToggleFavoriteEffect(string techKey)
    {
        var list = plugin.Config.FavoriteEffects;
        int idx = list.FindIndex(f => TechKeyEquals(f, techKey));
        if (idx >= 0) list.RemoveAt(idx);
        else list.Add(techKey);
        plugin.Config.Save();
    }

    private bool IsFavoritePreset(string preset) =>
        plugin.Config.FavoritePresets.Any(f => string.Equals(f, preset, StringComparison.OrdinalIgnoreCase));

    private void ToggleFavoritePreset(string preset)
    {
        var list = plugin.Config.FavoritePresets;
        int idx = list.FindIndex(f => string.Equals(f, preset, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) list.RemoveAt(idx);
        else list.Add(preset);
        plugin.Config.Save();
    }

    private IEnumerable<string> OrderedPresetFiles()
    {
        // Stable: favorites first, then xlAnimPresets, then xlPresets, then
        // the rest — alphabetical within each group.
        var xlDir = Path.Combine(GetGameDir(), "reshade-presets", "xlAnimPresets") + Path.DirectorySeparatorChar;
        var xpDir = Path.Combine(GetGameDir(), "reshade-presets", "xlPresets") + Path.DirectorySeparatorChar;
        bool IsXl(string p) => p.StartsWith(xlDir, StringComparison.OrdinalIgnoreCase);
        bool IsXp(string p) => p.StartsWith(xpDir, StringComparison.OrdinalIgnoreCase);
        foreach (var p in presetFiles)
            if (IsFavoritePreset(p)) yield return p;
        foreach (var p in presetFiles)
            if (!IsFavoritePreset(p) && IsXl(p)) yield return p;
        foreach (var p in presetFiles)
            if (!IsFavoritePreset(p) && !IsXl(p) && IsXp(p)) yield return p;
        foreach (var p in presetFiles)
            if (!IsFavoritePreset(p) && !IsXl(p) && !IsXp(p)) yield return p;
    }

    private bool DrawPresetRow(string preset, string display, bool isSelected)
    {
        bool fav = IsFavoritePreset(preset);
        string star = FontAwesomeIcon.Star.ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        Vector2 glyphSize = ImGui.CalcTextSize(star);
        ImGui.PopFont();
        // Square 1:1 invisible hitbox; the glyph is drawn centered and only
        // the glyph itself recolors on hover (no button background at all).
        float side = MathF.Max(glyphSize.X, glyphSize.Y);
        if (ImGui.InvisibleButton("##fav_" + preset, new Vector2(side, side)))
            ToggleFavoritePreset(preset);
        bool hovered = ImGui.IsItemHovered();
        {
            Vector2 min = ImGui.GetItemRectMin();
            Vector2 pos = min + (new Vector2(side, side) - glyphSize) * 0.5f;
            Vector4 col = fav ? new Vector4(1f, 0.84f, 0f, 1f) : new Vector4(0.45f, 0.45f, 0.45f, 1f);
            if (hovered) col = fav ? new Vector4(1f, 0.95f, 0.45f, 1f) : new Vector4(0.9f, 0.9f, 0.9f, 1f);
            ImGui.GetWindowDrawList().AddText(UiBuilder.IconFont, ImGui.GetFontSize(), pos, ImGui.ColorConvertFloat4ToU32(col), star);
        }
        if (hovered)
            ImGui.SetTooltip(fav ? "Remove from favorites" : "Add to favorites");
        ImGui.SameLine();
        bool picked = ImGui.Selectable(display, isSelected);
        if (isSelected) ImGui.SetItemDefaultFocus();
        return picked;
    }

    // Shared by the zone rows, the default/cutscene pickers, and the
    // canvas preset-trigger nodes. Positive width overrides the default
    // full-avail sizing (required on the canvas, where avail is huge).
    public void DrawZonePresetCombo(string id, ref string target, float width = -1)
    {
        var currentDisplay = string.IsNullOrEmpty(target) ? "(none)" : GetPresetDisplayName(target);
        ImGui.SetNextItemWidth(width > 0 ? width : ImGui.GetContentRegionAvail().X);
        if (ImGui.BeginCombo(id, currentDisplay, ImGuiComboFlags.HeightLargest))
        {
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 25);
            ImGui.InputTextWithHint("##zsearch", "Search presets...", ref presetSearch, 256);
            ImGui.Separator();
            if (ImGui.Selectable("(none)", string.IsNullOrEmpty(target)))
            { target = string.Empty; plugin.Config.Save(); }
            var searchLower = presetSearch.ToLowerInvariant();
            float listHeight = Math.Min(presetFiles.Count * ImGui.GetFrameHeightWithSpacing(), 300);
            if (ImGui.BeginChild("##zonePresetList", new Vector2(0, listHeight), false))
            {
                foreach (var preset in OrderedPresetFiles())
                {
                    var display = GetPresetDisplayName(preset);
                    if (!string.IsNullOrEmpty(searchLower) && !display.ToLowerInvariant().Contains(searchLower)) continue;
                    bool isSelected = string.Equals(target, preset, StringComparison.OrdinalIgnoreCase);
                    if (DrawPresetRow(preset, display, isSelected))
                    { target = preset; plugin.Config.Save(); }
                }
                ImGui.EndChild();
            }
            ImGui.EndCombo();
        }
    }

    private void DrawZoneList()
    {
        var territories = plugin.Config.ZonePresets;
        var sheet = Service.DataManager.GetExcelSheet<TerritoryType>();
        ImGui.Text("Filter:"); ImGui.SameLine(); ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##filter", "Zone name or ID", ref filter, 256);
        ImGui.SameLine(); ImGui.Checkbox("Only modified", ref showOnlyModified);
        ImGui.Spacing();
        if (ImGui.BeginTable("##zones", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(0, ImGui.GetContentRegionAvail().Y)))
        {
            ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("Zone Name", ImGuiTableColumnFlags.WidthStretch, 3);
            ImGui.TableSetupColumn("Preset", ImGuiTableColumnFlags.WidthStretch, 4);
            ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();
            if (plugin.CurrentTerritoryId != 0) DrawZoneRow(plugin.CurrentTerritoryId, sheet, true);
            foreach (var zone in territories.ToList())
            { if (zone.TerritoryId != plugin.CurrentTerritoryId) DrawZoneRow(zone.TerritoryId, sheet, false); }
            if (sheet != null && !string.IsNullOrEmpty(filter) && !showOnlyModified)
            {
                foreach (var row in sheet)
                {
                    var id = row.RowId;
                    if (territories.Any(z => z.TerritoryId == id) || id == plugin.CurrentTerritoryId) continue;
                    var name = GetZoneName(id);
                    if (!MatchesFilter(id, name)) continue;
                    DrawZoneRow(id, sheet, false);
                }
            }
            ImGui.EndTable();
        }
    }

    private void DrawZoneRow(uint territoryId, ExcelSheet<TerritoryType>? sheet, bool isCurrent)
    {
        var zonePreset = plugin.Config.GetPresetForZone(territoryId);
        var name = sheet != null ? GetZoneName(territoryId) : $"Zone {territoryId}";
        if (!string.IsNullOrEmpty(filter) && !MatchesFilter(territoryId, name)) return;
        if (isCurrent) ImGui.PushStyleColor(ImGuiCol.TableRowBg, new Vector4(0.2f, 0.4f, 0.6f, 0.3f));
        ImGui.TableNextRow();
        ImGui.TableNextColumn(); ImGui.Text($"{territoryId}");
        ImGui.TableNextColumn();
        if (isCurrent) ImGui.TextColored(new Vector4(0f, 1f, 1f, 1f), name);
        else ImGui.TextUnformatted(name);
        ImGui.TableNextColumn();
        var displayPath = string.IsNullOrEmpty(zonePreset?.PresetPath ?? "") ? "(default)" : GetPresetDisplayName(zonePreset!.PresetPath);
        ImGui.TextUnformatted(displayPath);
        ImGui.TableNextColumn();
        var btnId = $"##{territoryId}";
        if (ImGui.Button($"Set{btnId}", new Vector2(35, 0))) ImGui.OpenPopup($"PresetPopup{territoryId}");
        ImGui.SameLine();
        if (zonePreset != null && ImGui.Button($"X{btnId}", new Vector2(25, 0)))
        { plugin.Config.ZonePresets.Remove(zonePreset); plugin.Config.Save(); }
        if (ImGui.BeginPopup($"PresetPopup{territoryId}"))
        {
            ImGui.Text($"Preset for {name}:");
            var preset = plugin.Config.GetOrCreatePreset(territoryId);
            DrawZonePresetCombo($"##preset{territoryId}", ref preset.PresetPath);
            ImGui.Separator(); ImGui.Text("QoLBar Condition Set:");
            if (plugin.ConditionSetNames.Length == 0) ImGui.TextDisabled("(none available)");
            else
            {
                var currentCond = preset.ConditionSetIndex;
                var preview = currentCond >= 0 && currentCond < plugin.ConditionSetNames.Length ? plugin.ConditionSetNames[currentCond] : "None";
                if (ImGui.BeginCombo($"##cond{territoryId}", preview))
                {
                    if (ImGui.Selectable("None", currentCond < 0)) { preset.ConditionSetIndex = -1; plugin.Config.Save(); }
                    for (int i = 0; i < plugin.ConditionSetNames.Length; i++)
                    {
                        bool isSel = currentCond == i;
                        if (ImGui.Selectable(plugin.ConditionSetNames[i], isSel)) { preset.ConditionSetIndex = i; plugin.Config.Save(); }
                        if (isSel) ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }
            }
            ImGui.EndPopup();
        }
        if (isCurrent) ImGui.PopStyleColor();
    }

    private bool MatchesFilter(uint id, string name) => string.IsNullOrEmpty(filter) || id.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase) || name.Contains(filter, StringComparison.OrdinalIgnoreCase);

    public static string GetZoneName(uint territoryId)
    {
        try
        {
            var sheet = Service.DataManager.GetExcelSheet<TerritoryType>();
            if (sheet == null) return $"Zone {territoryId}";
            var t = sheet.GetRow((ushort)territoryId);
            var name = t.PlaceNameZone.Value.Name.ToString();
            if (string.IsNullOrEmpty(name)) name = t.PlaceName.Value.Name.ToString();
            return string.IsNullOrEmpty(name) ? $"Zone {territoryId}" : name;
        }
        catch { return $"Zone {territoryId}"; }
    }

    // ==================== SHADERS TAB ====================

    private void ScanShaderFiles()
    {
        shaderFiles.Clear();
        shaderFileFullPaths.Clear();
        shaderFileAllPaths.Clear();
        shaderFileSet.Clear();
        shaderFileCanonical.Clear();
        effectTechniques.Clear();
        effectHiddenTechniques.Clear();
        effectTechTips.Clear();
        techParseQueue.Clear();
        // The parsed-set MUST clear with the maps: otherwise a Rescan
        // wipes the data while ParseTechniquesForFile keeps early-returning
        // "already parsed", leaving hidden-filtering (and rows) empty
        // forever. That leaked every hidden technique incl. placeholders.
        techParsedFiles.Clear();
        try
        {
            var gameDir = GetGameDir();
            if (string.IsNullOrEmpty(gameDir)) return;
            var dir = Path.Combine(gameDir, "reshade-shaders", "Shaders");
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*.fx", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(f);
                var rel = Path.GetRelativePath(dir, f).Replace('\\', '/');
                shaderFiles.Add(fileName);
                shaderFileFullPaths[fileName] = rel;
                if (!shaderFileAllPaths.TryGetValue(fileName, out var rels))
                    shaderFileAllPaths[fileName] = rels = new List<string>();
                if (!rels.Contains(rel, StringComparer.OrdinalIgnoreCase)) rels.Add(rel);
                shaderFileSet.Add(fileName);
                shaderFileCanonical[fileName] = fileName;
                techParseQueue.Enqueue(f);
            }
            shaderFiles.Sort(StringComparer.OrdinalIgnoreCase);
        }
        catch { }
    }

    // Background technique indexing: the full 600+ file parse (~3s) used
    // to run synchronously on window open. Now enumeration is instant and
    // files parse here, time-sliced per frame, with preset-referenced
    // files parsed synchronously on selection (see EnsureTechParsed).
    // Rows stream into the effects list as indexing proceeds.
    private readonly Queue<string> techParseQueue = new();
    private readonly HashSet<string> techParsedFiles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> placeholderRowLogged = new(StringComparer.OrdinalIgnoreCase);

    private void PumpTechParseQueue(int budgetMs = 10)
    {
        try
        {
            if (techParseQueue.Count == 0) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (techParseQueue.Count > 0 && sw.ElapsedMilliseconds < budgetMs)
            {
                var f = techParseQueue.Dequeue();
                ParseTechniquesForFile(f);
            }
        }
        catch { }
    }

    private void ParseTechniquesForFile(string fxPath)
    {
        try
        {
            if (!techParsedFiles.Add(fxPath)) return;
            var fileName = Path.GetFileName(fxPath);
            // Same filename can exist in multiple subdirs: UNION their
            // techniques instead of last-wins, or one's hidden list
            // erases the other's (e.g. the two crt-royale.fx copies).
            var parsed = GetTechniquesFromFx(fxPath);
            if (!effectTechniques.TryGetValue(fileName, out var visList))
                effectTechniques[fileName] = visList = new List<string>();
            foreach (var t in parsed.Visible)
                if (!visList.Contains(t, StringComparer.OrdinalIgnoreCase)) visList.Add(t);
            if (!effectHiddenTechniques.TryGetValue(fileName, out var hidList))
                effectHiddenTechniques[fileName] = hidList = new List<string>();
            foreach (var t in parsed.Hidden)
                if (!hidList.Contains(t, StringComparer.OrdinalIgnoreCase)) hidList.Add(t);
            foreach (var kvp in parsed.Tooltips)
                effectTechTips.TryAdd($"{kvp.Key}@{fileName}", kvp.Value);
            // Late parse == fresh live state for these keys: seed from the
            // addon ground truth like the runtime merge does, so the
            // engine doesn't re-fire them as unknown.
            if (runtimeTechniques.TryGetValue(fileName, out var rt))
            {
                foreach (var t in visList)
                {
                    var key = $"{t}@{fileName}";
                    if (!effectEnabledState.ContainsKey(key))
                        effectEnabledState[key] = rt.Contains(t, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch { }
    }

    // Synchronous priority parse (preset selection): the preset's own
    // files must be indexed now, not whenever the background gets there.
    // Parses EVERY on-disk copy sharing the filename: hidden flags union
    // across duplicates (e.g. the root crt-royale.fx stub hides the
    // _x_gposingway_placeholder technique the real copy lacks).
    private void EnsureTechParsed(IEnumerable<string> fileNames)
    {
        try
        {
            var gameDir = GetGameDir();
            if (string.IsNullOrEmpty(gameDir)) return;
            var dir = Path.Combine(gameDir, "reshade-shaders", "Shaders");
            foreach (var fileName in fileNames)
            {
                if (shaderFileAllPaths.TryGetValue(fileName, out var rels))
                {
                    foreach (var rel in rels)
                    {
                        var full = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(full)) ParseTechniquesForFile(full);
                    }
                    continue;
                }
                var single = shaderFileFullPaths.TryGetValue(fileName, out var rp) ? rp : fileName;
                var one = Path.Combine(dir, single.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(one)) ParseTechniquesForFile(one);
            }
        }
        catch { }
    }

    private static string UnescapeFxString(string s) =>
        s.Replace("\\\\", "\0").Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\r", "\r").Replace("\0", "\\");

    private static (List<string> Visible, List<string> Hidden, Dictionary<string, string> Tooltips) GetTechniquesFromFx(string fxPath)
    {
        var visible = new List<string>();
        var hidden = new List<string>();
        var tooltips = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var content = File.ReadAllText(fxPath);
            // Strip comments first: templates keep sample "technique"
            // declarations inside comments, which ReShade ignores. Block
            // stripping only runs when /* and */ are balanced — some files
            // use /* decoratively, where stripping would eat real code.
            int opens = Regex.Matches(content, @"/\*").Count;
            int closes = Regex.Matches(content, @"\*/").Count;
            if (opens == closes)
                content = Regex.Replace(content, @"/\*.*?\*/", "", RegexOptions.Singleline);
            content = Regex.Replace(content, @"//.*$", "", RegexOptions.Multiline);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Anchor to line start and require the declaration shape
            // (annotation block '<' or body '{'): tooltip prose like
            // "Place this technique before ..." must not match. Techniques
            // annotated hidden=true are compiled but never listed by ReShade.
            foreach (Match match in Regex.Matches(content, @"^\s*technique\s+(\w+)\s*[<{]", RegexOptions.Multiline))
            {
                var name = match.Groups[1].Value;
                if (!seen.Add(name)) continue;
                int regionStart = match.Index + match.Length - 1;
                int brace = content.IndexOf('{', regionStart);
                int regionEnd = brace >= 0 ? brace : Math.Min(content.Length, regionStart + 500);
                string region = regionEnd > regionStart ? content.Substring(regionStart, regionEnd - regionStart) : "";
                bool isHidden = Regex.IsMatch(region, @"hidden\s*=\s*true", RegexOptions.IgnoreCase);
                (isHidden ? hidden : visible).Add(name);
                // ui_tooltip can be several adjacent string literals (HLSL
                // concatenates them); join and unescape.
                var tipMatch = Regex.Match(region, @"ui_tooltip\s*=\s*((?:""(?:[^""\\]|\\.)*""\s*)+)\s*;", RegexOptions.Singleline);
                if (tipMatch.Success)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (Match part in Regex.Matches(tipMatch.Groups[1].Value, @"""((?:[^""\\]|\\.)*)"""))
                        sb.Append(UnescapeFxString(part.Groups[1].Value));
                    var tip = sb.ToString().Trim();
                    if (!string.IsNullOrEmpty(tip))
                        tooltips[name] = tip;
                }
            }
        }
        catch { }
        return (visible, hidden, tooltips);
    }

    private void ParsePresetFile(string presetPath)
    {
        ClearDynOverrides();
        selKfConfig = ""; selKfId = ""; kfRenameId = "";
        FlushDynSidecar();
        nicknameEditingKey = null;
        pendingDynamicSrc = null;
        effectEnabledState.Clear();
        effectSettings.Clear();
        techniqueSorting.Clear();
        fxParsed.Clear();
        effectUniforms.Clear();

        if (!File.Exists(presetPath)) return;

        try
        {
            var lines = File.ReadAllLines(presetPath);
            var enabledKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.StartsWith("Techniques=", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var tech in line.Substring(11).Split(',', StringSplitOptions.RemoveEmptyEntries))
                        enabledKeys.Add(tech.Trim());
                }
                else if (line.StartsWith("TechniqueSorting=", StringComparison.OrdinalIgnoreCase))
                {
                    techniqueSorting.Clear();
                    foreach (var tech in line.Substring(17).Split(',', StringSplitOptions.RemoveEmptyEntries))
                        techniqueSorting.Add(tech.Trim());
                    DedupeTechniqueSorting();
                }
            }

            // Priority-index the preset's own files now (technique rows +
            // hidden-filter need them); the rest streams in via background.
            try
            {
                var presetFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var tech in techniqueSorting)
                {
                    var at = tech.LastIndexOf('@');
                    if (at >= 0) presetFiles.Add(tech.Substring(at + 1));
                }
                EnsureTechParsed(presetFiles);
            }
            catch { }

            foreach (var tech in techniqueSorting)
                effectEnabledState[tech] = enabledKeys.Contains(tech);
            foreach (var shader in shaderFiles)
            {
                if (!effectTechniques.TryGetValue(shader, out var techs)) continue;
                foreach (var t in techs)
                {
                    var key = $"{t}@{shader}";
                    if (!effectEnabledState.ContainsKey(key))
                        effectEnabledState[key] = enabledKeys.Contains(key);
                }
            }
            // Fresh model == fresh live state after a preset load: prime the
            // engine's sent-map so it doesn't re-fire the whole state.
            try { plugin.PrimeDynToggles(new Dictionary<string, bool>(effectEnabledState)); } catch { }

            string currentSectionEffect = "";
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;
                var sectionMatch = Regex.Match(line, @"^\[(.+\.fx)\]$");
                if (sectionMatch.Success)
                {
                    currentSectionEffect = sectionMatch.Groups[1].Value;
                    if (!effectSettings.ContainsKey(currentSectionEffect))
                        effectSettings[currentSectionEffect] = new();
                    continue;
                }
                if (!string.IsNullOrEmpty(currentSectionEffect))
                {
                    var eqIdx = line.IndexOf('=');
                    if (eqIdx >= 0)
                    {
                        var key = line.Substring(0, eqIdx).Trim();
                        var value = line.Substring(eqIdx + 1).Trim();
                        effectSettings[currentSectionEffect][key] = value;
                    }
                }
            }
        }
        catch { }
    }

    private Dictionary<string, List<string>> runtimeTechniques = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> runtimeAddedKeys = new(StringComparer.OrdinalIgnoreCase);
    private DateTime techFileReadTime = DateTime.MinValue;

    private DateTime presetFileReadTime = DateTime.MinValue;

    // Follow ReShade's live preset: zone switches and overlay switches
    // change it behind our back. Runs headless from the framework thread
    // (not just Draw) so the engine learns the active preset even when
    // the window was never opened. Own mtime so the effects merge below
    // is never starved.
    public void FollowLivePreset()
    {
        try
        {
            var path = Path.Combine(GetGameDir(), "ffxiv_reshade_techniques.json");
            if (!File.Exists(path)) return;
            var mtime = File.GetLastWriteTimeUtc(path);
            if (mtime <= presetFileReadTime) return;
            presetFileReadTime = mtime;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("preset", out var presetEl))
            {
                var activePreset = presetEl.GetString() ?? "";
                if (!string.IsNullOrEmpty(activePreset)
                    && !string.Equals(activePreset, selectedPresetPath, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(activePreset)
                    && (string.IsNullOrEmpty(selectedPresetPath) || !PresetContentsEqual(selectedPresetPath, activePreset)))
                {
                    selectedPresetPath = activePreset;
                    ParsePresetFile(activePreset);
                    SetFocus(null);
                }
            }
        }
        catch { }
    }

    // Merge the addon's ground-truth technique list (macro-generated
    // techniques static parsing can't see). Only touches runtime-sourced
    // keys; preset/static state always wins on parse, runtime corrects after.
    private void MergeRuntimeTechniques()
    {
        try
        {
            FollowLivePreset();
            var path = Path.Combine(GetGameDir(), "ffxiv_reshade_techniques.json");
            if (!File.Exists(path)) return;
            var mtime = File.GetLastWriteTimeUtc(path);
            if (mtime <= techFileReadTime) return;
            techFileReadTime = mtime;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("effects", out var arr)) return;
            var fresh = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var seenNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var el in arr.EnumerateArray())
            {
                var file = el.TryGetProperty("effect", out var fe) ? fe.GetString() ?? "" : "";
                var tech = el.TryGetProperty("tech", out var te) ? te.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(tech)) continue;
                if (!fresh.TryGetValue(file, out var list))
                    fresh[file] = list = new List<string>();
                if (!list.Contains(tech, StringComparer.OrdinalIgnoreCase))
                    list.Add(tech);
                var key = $"{tech}@{file}";
                seenNow.Add(key);
                if (runtimeAddedKeys.Contains(key) || !effectEnabledState.ContainsKey(key))
                {
                    bool enabled = el.TryGetProperty("enabled", out var ee) && ee.ValueKind == System.Text.Json.JsonValueKind.True;
                    effectEnabledState[key] = enabled;
                    runtimeAddedKeys.Add(key);
                }
            }
            runtimeTechniques = fresh;
            foreach (var k in runtimeAddedKeys.Where(k => !seenNow.Contains(k)).ToList())
            {
                effectEnabledState.Remove(k);
                runtimeAddedKeys.Remove(k);
            }
        }
        catch { }
    }

    private string? pendingDynamicSrc;
    private DateTime pendingDynamicSince;

    // Preset name prompt (New / Duplicate). Dynamic targets
    // reshade-presets\xlAnimPresets\, static targets xlPresets\.
    private bool presetNameOpen;
    private bool presetNameIsDuplicate;
    private string presetNameDraft = "";
    private bool presetNameDynamic = true;
    private string presetNameError = "";

    private void OpenPresetNameModal(bool isDuplicate)
    {
        presetNameOpen = true;
        presetNameIsDuplicate = isDuplicate;
        presetNameError = "";
        try
        {
            if (isDuplicate && !string.IsNullOrEmpty(selectedPresetPath))
                presetNameDraft = Path.GetFileNameWithoutExtension(selectedPresetPath) + " Copy";
            else
                presetNameDraft = "";
            presetNameDynamic = string.IsNullOrEmpty(selectedPresetPath) || IsDynamicPreset(selectedPresetPath);
        }
        catch { presetNameDraft = ""; presetNameDynamic = true; }
        ImGui.OpenPopup("Create Preset##wx");
    }

    private static string SanitizePresetName(string name)
    {
        try
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(name.Trim());
            for (int i = 0; i < sb.Length; i++)
                if (invalid.Contains(sb[i])) sb[i] = '_';
            var s = sb.ToString().Trim().Trim('.');
            if (!s.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
                s += ".ini";
            return s;
        }
        catch { return ""; }
    }

    private void DrawPresetNameModal()
    {
        if (!presetNameOpen) return;
        ImGui.SetNextWindowSize(new Vector2(380, 0), ImGuiCond.FirstUseEver);
        if (!ImGui.BeginPopupModal("Create Preset##wx", ImGuiWindowFlags.AlwaysAutoResize))
        {
            // Popup was closed externally; drop the state.
            presetNameOpen = false;
            return;
        }
        ImGui.Text(presetNameIsDuplicate ? "Duplicate preset as:" : "New preset name:");
        ImGui.SetNextItemWidth(340);
        ImGui.InputText("##wxPresetName", ref presetNameDraft, 128);
        bool dyn = presetNameDynamic;
        if (ImGui.Checkbox("Dynamic preset##wxPresetDyn", ref dyn)) presetNameDynamic = dyn;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(presetNameDynamic
                ? "Created in reshade-presets\\xlAnimPresets\\ with an animation sidecar."
                : "Created in reshade-presets\\xlPresets\\. No sidecar.");
        if (!string.IsNullOrEmpty(presetNameError))
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), presetNameError);
        if (ImGui.Button(presetNameIsDuplicate ? "Duplicate##wxPresetGo" : "Create##wxPresetGo"))
        {
            if (ConfirmPresetNameModal())
            {
                presetNameOpen = false;
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##wxPresetCancel"))
        {
            presetNameOpen = false;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private bool ConfirmPresetNameModal()
    {
        presetNameError = "";
        try
        {
            var gameDir = GetGameDir();
            if (string.IsNullOrEmpty(gameDir)) { presetNameError = "Game dir unknown."; return false; }
            var fileName = SanitizePresetName(presetNameDraft);
            if (string.IsNullOrEmpty(fileName) || string.Equals(fileName, ".ini", StringComparison.OrdinalIgnoreCase))
            {
                presetNameError = "Enter a name.";
                return false;
            }
            var targetDir = Path.Combine(gameDir, "reshade-presets", presetNameDynamic ? "xlAnimPresets" : "xlPresets");
            Directory.CreateDirectory(targetDir);
            var newPath = Path.Combine(targetDir, fileName);
            if (File.Exists(newPath))
            {
                presetNameError = "A preset with that name already exists.";
                return false;
            }
            if (presetNameIsDuplicate)
            {
                if (string.IsNullOrEmpty(selectedPresetPath) || !File.Exists(selectedPresetPath))
                {
                    presetNameError = "Nothing selected to duplicate.";
                    return false;
                }
                File.Copy(selectedPresetPath, newPath);
                // Carry the animation sidecar when duplicating into a
                // dynamic preset; a static target takes no sidecar.
                try
                {
                    var srcSide = DynamicPresetStore.SidecarPath(selectedPresetPath);
                    if (presetNameDynamic && File.Exists(srcSide))
                        File.Copy(srcSide, DynamicPresetStore.SidecarPath(newPath));
                }
                catch { }
            }
            else
            {
                File.WriteAllText(newPath, "Techniques=\nTechniqueSorting=\n");
            }
            ScanZonePresets();
            selectedPresetPath = newPath;
            ParsePresetFile(newPath);
            SetFocus(null);
            WritePresetSignal(newPath);
            if (presetNameDynamic && !presetNameIsDuplicate)
                CreateFreshDynamicSidecar(newPath);
            else if (presetNameDynamic && presetNameIsDuplicate && !File.Exists(DynamicPresetStore.SidecarPath(newPath)))
                CreateFreshDynamicSidecar(newPath);
            return true;
        }
        catch (Exception ex)
        {
            presetNameError = "Failed: " + ex.Message;
            return false;
        }
    }

    // ==================== DLSS NEURAL RENDERING ====================
    // Drives renodx-dlss5.addon64's "Enable DLSS Neural Rendering" via
    // ReShade.ini [RenoDX.DLSS5] NeuralUplift=0/1 (verified against the
    // addon's strings). Atomic tmp+move writes, same pattern as the other
    // signal files; ReShade does not revert external edits. If the addon
    // ever needs a reload to pick the value up, follow with
    // WritePresetSignal to force one.

    private string GetDlssAddonPath() => Path.Combine(GetGameDir(), "renodx-dlss5.addon64");

    private bool IsDlssAddonPresent()
    {
        try { return File.Exists(GetDlssAddonPath()); } catch { return false; }
    }

    // Engine-ready API: null = unknown/absent.
    public bool? GetDlssNeural()
    {
        try
        {
            var ini = Path.Combine(GetGameDir(), "ReShade.ini");
            if (!File.Exists(ini)) return null;
            var text = File.ReadAllText(ini);
            var m = Regex.Match(text, @"(?m)^NeuralUplift=(\d+)", RegexOptions.Multiline);
            if (!m.Success) return null;
            return m.Groups[1].Value != "0";
        }
        catch { return null; }
    }

    // Engine-ready API: returns true when the value changed.
    public bool SetDlssNeural(bool enabled)
    {
        try
        {
            var gameDir = GetGameDir();
            if (string.IsNullOrEmpty(gameDir)) return false;
            var ini = Path.Combine(gameDir, "ReShade.ini");
            if (!File.Exists(ini)) return false;
            var text = File.ReadAllText(ini);
            var want = enabled ? "1" : "0";
            string updated;
            if (Regex.IsMatch(text, @"(?m)^NeuralUplift=\d+", RegexOptions.Multiline))
            {
                updated = Regex.Replace(text, @"(?m)^NeuralUplift=\d+", $"NeuralUplift={want}", RegexOptions.Multiline);
            }
            else if (text.Contains("[RenoDX.DLSS5]"))
            {
                updated = Regex.Replace(text, @"\[RenoDX\.DLSS5\]", $"[RenoDX.DLSS5]\nNeuralUplift={want}", RegexOptions.None);
            }
            else
            {
                updated = text.TrimEnd() + $"\n\n[RenoDX.DLSS5]\nNeuralUplift={want}\n";
            }
            if (string.Equals(updated, text, StringComparison.Ordinal)) return false;
            var tmp = ini + ".tmp";
            File.WriteAllText(tmp, updated);
            File.Move(tmp, ini, true);
            return true;
        }
        catch { return false; }
    }

    private void DrawDlssToggle(string idSuffix)
    {
        bool present = false;
        try { present = IsDlssAddonPresent(); } catch { }
        if (!present)
        {
            ImGui.BeginDisabled();
            bool dummy = false;
            ImGui.Checkbox($"DLSS Neural Rendering##dlss{idSuffix}", ref dummy);
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "(Not Detected)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("renodx-dlss5.addon64 not found in the game folder.");
            return;
        }
        bool on = false;
        try { on = plugin.GetDlssLive(); } catch { }
        if (ImGui.Checkbox($"DLSS Neural Rendering##dlss{idSuffix}", ref on))
        {
            try { plugin.SetDlssNeuralLive(on); } catch { }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("renodx-dlss5: Enable DLSS Neural Rendering.\nToggles live via the addon's F6 hotkey + persists to ReShade.ini.\nManual F6 taps are mirrored back here.\nNote: F6 also targets party member 6 in-game (addon design).");
    }
    // Fresh animation sidecar seeded from the live state (same capture as
    // snapshot): a single primary keyframe so the animator has a base.
    private void CreateFreshDynamicSidecar(string presetPath)
    {
        try
        {
            EnsureDynCache();
            var data = new DynamicPresetData();
            var cfg = data.GetOrCreatePrimary();
            var snap = CaptureLiveKeyframe(plugin.GetEorzeaSecondsPublic());
            snap.IsPrimary = true;
            snap.Sparse = false;
            cfg.Keyframes.Add(snap);
            DynamicPresetStore.Save(presetPath, data);
            activeDynData = data;
            activeDynPath = presetPath;
            sidecarDirty = false;
            lastSidecarSave = DateTime.UtcNow;
            // Fresh snapshots start trimmed (ON techs, zero uniforms);
            // ticking later adopts driven keys (see tick setters).
            try { TrimPrimaryToDriven(); } catch { }
        }
        catch { }
    }

    private void MakePresetDynamic()
    {
        if (string.IsNullOrEmpty(selectedPresetPath) || !File.Exists(selectedPresetPath)) return;
        if (pendingDynamicSrc != null) return;
        // Flush live toggle/uniform state to disk first: the .ini only
        // holds the last-saved state, so copying blindly would revert
        // recent changes. FinishMakeDynamic runs once the file updates.
        _pendingCmdLines.Add("SAVE");
        pendingDynamicSrc = selectedPresetPath;
        pendingDynamicSince = DateTime.UtcNow;
    }

    private void PollPendingDynamic()
    {
        if (pendingDynamicSrc == null) return;
        bool done = false;
        try
        {
            if (File.Exists(pendingDynamicSrc) && File.GetLastWriteTimeUtc(pendingDynamicSrc) > pendingDynamicSince)
                done = true;
            else if ((DateTime.UtcNow - pendingDynamicSince).TotalSeconds > 5)
                done = true; // addon didn't save in time: proceed best-effort
        }
        catch { done = true; }
        if (!done) return;
        var src = pendingDynamicSrc;
        pendingDynamicSrc = null;
        FinishMakeDynamic(src);
    }

    private void FinishMakeDynamic(string src)
    {
        selectedPresetPath = src;
        if (string.IsNullOrEmpty(selectedPresetPath) || !File.Exists(selectedPresetPath)) return;
        try
        {
            var gameDir = GetGameDir();
            if (string.IsNullOrEmpty(gameDir)) return;
            var xlDir = Path.Combine(gameDir, "reshade-presets", "xlAnimPresets");
            Directory.CreateDirectory(xlDir);
            var baseName = Path.GetFileName(selectedPresetPath);
            if (!baseName.StartsWith("xl_", StringComparison.OrdinalIgnoreCase))
                baseName = "xl_" + baseName;
            var newPath = Path.Combine(xlDir, baseName);
            if (!string.Equals(Path.GetFullPath(selectedPresetPath), Path.GetFullPath(newPath), StringComparison.OrdinalIgnoreCase))
            {
                // Byte-identical on purpose: identical presets are
                // interchangeable, so switching between them stays instant
                // (no pointless reload storm, no toggle stalls).
                File.Copy(selectedPresetPath, newPath, true);
                selectedPresetPath = newPath;
            }
            ScanZonePresets();
            ParsePresetFile(selectedPresetPath);
            SetFocus(null);
            WritePresetSignal(selectedPresetPath);
        }
        catch { }
    }

    private static bool PresetContentsEqual(string a, string b)
    {
        try
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            var ba = File.ReadAllBytes(a);
            var bb = File.ReadAllBytes(b);
            if (ba.Length != bb.Length) return false;
            for (int i = 0; i < ba.Length; i++)
                if (ba[i] != bb[i]) return false;
            return true;
        }
        catch { return false; }
    }

    // ==================== DYNAMIC PRESETS (STAGE 2 P1a) ====================

    private static bool IsDynamicPreset(string presetPath)
    {
        if (string.IsNullOrEmpty(presetPath)) return false;
        var parts = presetPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var p in parts)
            if (string.Equals(p, "xlAnimPresets", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private void EnsureDynCache()
    {
        if (string.Equals(activeDynPath, selectedPresetPath, StringComparison.OrdinalIgnoreCase)) return;
        activeDynPath = selectedPresetPath;
        activeDynData = (!string.IsNullOrEmpty(selectedPresetPath) && IsDynamicPreset(selectedPresetPath))
            ? DynamicPresetStore.Load(selectedPresetPath) : null;
        // Dynamic preset with no sidecar yet (hand-dropped .ini): seed a
        // fresh live-captured primary so the animator opens on it instead
        // of claiming it isn't dynamic. A present-but-corrupt sidecar is
        // left alone (never overwrite real data with a guess).
        if (activeDynData == null && !string.IsNullOrEmpty(selectedPresetPath) && IsDynamicPreset(selectedPresetPath))
        {
            try
            {
                if (!File.Exists(DynamicPresetStore.SidecarPath(selectedPresetPath)))
                    CreateFreshDynamicSidecar(selectedPresetPath);
                activeDynData = DynamicPresetStore.Load(selectedPresetPath);
            }
            catch { }
        }
        activeDynData?.Normalize();
        if (activeDynData != null && activeDynData.PurgeUnticked()) SaveActiveDyn();
        // One-time wipe of incidental order snapshots (auto-captured, never
        // deliberately arranged). Order is global/manual again.
        if (activeDynData != null)
        {
            bool wiped = false;
            foreach (var c in activeDynData.Configs)
                foreach (var k in c.Keyframes)
                    if (k.TechOrder.Count > 0) { k.TechOrder.Clear(); wiped = true; }
            if (wiped) SaveActiveDyn();
        }
        // One-time migration back: wall LocXYZ from rotated local frame to
        // world coords. Local storage made rotation swing the door around
        // the world origin, so the center is world again (rotation pivots
        // in place). Nodes never migrated (already world) are kept as-is;
        // LocLocal=true now just means "handled, world storage".
        if (activeDynData != null)
        {
            bool migrated = false;
            foreach (var n in activeDynData.Nodes)
            {
                if (!string.Equals(n.Source, "wall", StringComparison.OrdinalIgnoreCase)) continue;
                if (n.LocLocal)
                {
                    var world = EulerBox.Rotate(new Vector3(n.LocX, n.LocY, n.LocZ), n.BoxYaw, n.BoxPitch, n.BoxRoll, false);
                    n.LocX = world.X;
                    n.LocY = world.Y;
                    n.LocZ = world.Z;
                    migrated = true;
                }
                else
                {
                    n.LocLocal = true;
                    migrated = true;
                }
            }
            if (migrated) SaveActiveDyn();
        }
    }

    public DynamicPresetData? GetActiveDynamicData()
    {
        EnsureDynCache();
        if (activeDynData == null || !activeDynData.Enabled) return null;
        if (string.IsNullOrEmpty(selectedPresetPath) || !IsDynamicPreset(selectedPresetPath)) return null;
        return activeDynData;
    }

    private bool IsEngineDriving()
    {
        var frames = DynamicTimeline.EvalFrames(GetActiveDynamicData());
        return frames != null && frames.Count > 0;
    }

    // Manual edits while the engine drives become TEMPORARY overrides (like
    // grabbing a fader): they win over interpolation immediately, are
    // absorbed into the timeline on snapshot, and never rewrite history.
    private readonly Dictionary<string, (string Value, string BaseType)> dynOverrideUniforms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> dynOverrideToggles = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, (string Value, string BaseType)> DynOverrideUniforms => dynOverrideUniforms;
    public IReadOnlyDictionary<string, bool> DynOverrideToggles => dynOverrideToggles;

    // Mirror engine output into the UI model so sliders/checkboxes display
    // live animated values. Display only: no signals, no dirty flags, no
    // sidecar writes — the engine already sent everything.
    public void MirrorDynamicState(Dictionary<string, string> uniforms, Dictionary<string, bool>? toggles)
    {
        foreach (var kvp in uniforms)
        {
            int sep = kvp.Key.IndexOf('\0');
            if (sep < 0) continue;
            var file = kvp.Key.Substring(0, sep);
            var uname = kvp.Key.Substring(sep + 1);
            if (!effectSettings.TryGetValue(file, out var m))
                effectSettings[file] = m = new Dictionary<string, string>();
            m[uname] = kvp.Value;
        }
        if (toggles == null) return;
        foreach (var kvp in toggles)
            effectEnabledState[kvp.Key] = kvp.Value;
    }

    private void ClearDynOverrides()
    {
        dynOverrideUniforms.Clear();
        dynOverrideToggles.Clear();
    }

    // Forget every ticked setting of one FX file (shared by toggle-untick
    // and toggle-off: a disabled FX keeps no driven settings).
    private void UntickFileUniforms(DynamicKeyframe kf, string ufile)
    {
        if (string.IsNullOrEmpty(ufile)) return;
        foreach (var f in kf.Uniforms.Keys.ToList())
        {
            if (!string.Equals(f, ufile, StringComparison.OrdinalIgnoreCase)) continue;
            kf.TickedUniforms.RemoveAll(k => k.StartsWith(f + "\0", StringComparison.OrdinalIgnoreCase));
            kf.Uniforms.Remove(f);
        }
        var pkU = PrimaryKeyframe();
        if (!IsEngineDriving() && pkU != null)
        {
            foreach (var kvp in pkU.Uniforms)
            {
                if (!string.Equals(kvp.Key, ufile, StringComparison.OrdinalIgnoreCase)) continue;
                if (!effectSettings.TryGetValue(kvp.Key, out var smU))
                    effectSettings[kvp.Key] = smU = new Dictionary<string, string>();
                foreach (var u in kvp.Value)
                {
                    smU[u.Key] = u.Value.Value;
                    MarkDirtyUniform(kvp.Key, u.Key, u.Value.BaseType, u.Value.Value);
                }
            }
        }
    }

    // Sparse-edit target: the selected keyframe when it drives only ticked
    // settings. Its rows get leading red tick boxes.
    private DynamicKeyframe? SparseEditTarget()
    {
        var kf = GetSelectedKeyframe();
        return (kf != null && kf.Sparse && !kf.IsPrimary) ? kf : null;
    }

    private void SetTechTick(DynamicKeyframe kf, string techKey, bool on)
    {
        if (on)
        {
            if (!kf.TickedTechs.Any(k => string.Equals(k, techKey, StringComparison.OrdinalIgnoreCase)))
                kf.TickedTechs.Add(techKey);
            // Re-ticks start from the primary base, not transient live state.
            bool pv = effectEnabledState.TryGetValue(techKey, out bool en) && en;
            var pkT = PrimaryKeyframe();
            if (pkT != null && pkT.TechStates.TryGetValue(techKey, out bool pst)) pv = pst;
            kf.TechStates[techKey] = pv;
            if (!IsEngineDriving())
            {
                effectEnabledState[techKey] = pv;
                SplitTechKey(techKey, out var ttech, out var tfile);
                WriteToggleSignal(tfile, ttech, pv);
            }
            // Adopt into primary base if absent (trimmed primaries must
            // gain every newly-driven key; existing bases are history).
            var pkA = PrimaryKeyframe();
            if (pkA != null && !pkA.TechStates.ContainsKey(techKey) && kf.TechStates.TryGetValue(techKey, out bool adopted))
                pkA.TechStates[techKey] = adopted;
        }
        else
        {
            kf.TickedTechs.RemoveAll(k => string.Equals(k, techKey, StringComparison.OrdinalIgnoreCase));
            kf.TechStates.Remove(techKey);
            // Untracking the toggle drops its whole file: untick all settings
            // of the same FX and forget their values.
            SplitTechKey(techKey, out var _, out var ufile);
            UntickFileUniforms(kf, ufile);
            // Push the primary state live (sidecar-only edits never reach ReShade).
            if (!IsEngineDriving() && PrimaryKeyframe() is DynamicKeyframe pk && pk.TechStates.TryGetValue(techKey, out bool pst))
            {
                effectEnabledState[techKey] = pst;
                SplitTechKey(techKey, out var ttech, out var tfile);
                WriteToggleSignal(tfile, ttech, pst);
            }
            // Drop from primary when nothing drives it anymore (off and
            // unticked everywhere): values nobody needs aren't stored.
            // Panel toggles are untouched (they still drive triggers).
            if (!TechDrivenAnywhere(techKey) && PrimaryKeyframe() is DynamicKeyframe pkDrop)
            {
                var existingT = pkDrop.TechStates.Keys.FirstOrDefault(k => string.Equals(k, techKey, StringComparison.OrdinalIgnoreCase));
                if (existingT != null) pkDrop.TechStates.Remove(existingT);
            }
            // File uniforms unticked above cascade the same way.
            if (PrimaryKeyframe() is DynamicKeyframe pkFiles)
            {
                SplitTechKey(techKey, out var dropTech, out var dropFile);
                if (pkFiles.Uniforms.TryGetValue(dropFile, out var dropMap))
                {
                    foreach (var du in dropMap.Keys.ToList())
                        if (!UniformTickedAnywhere(dropFile, du)) dropMap.Remove(du);
                    if (dropMap.Count == 0) pkFiles.Uniforms.Remove(dropFile);
                }
            }
        }
        SaveActiveDyn();
    }

    private void SetUniformTick(DynamicKeyframe kf, string effectFile, string uniName, string baseType, bool on)
    {
        string key = effectFile + "\0" + uniName;
        if (on)
        {
            if (!kf.TickedUniforms.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)))
                kf.TickedUniforms.Add(key);
            string live = "";
            if (effectSettings.TryGetValue(effectFile, out var m) && m.TryGetValue(uniName, out var lv)) live = lv;
            // Re-ticks start from the primary base, not transient live state.
            var pvU = PrimaryUniformValue(effectFile, uniName);
            string start = pvU ?? live;
            if (!kf.Uniforms.TryGetValue(effectFile, out var umap))
                kf.Uniforms[effectFile] = umap = new Dictionary<string, DynamicUniformValue>(StringComparer.OrdinalIgnoreCase);
            umap[uniName] = new DynamicUniformValue { Value = start, BaseType = baseType };
            // Adopt into primary base if absent (see SetTechTick).
            var pkU = PrimaryKeyframe();
            if (pkU != null && umap.TryGetValue(uniName, out var adoptedUv))
            {
                if (!pkU.Uniforms.TryGetValue(effectFile, out var pkmap))
                    pkU.Uniforms[effectFile] = pkmap = new Dictionary<string, DynamicUniformValue>(StringComparer.OrdinalIgnoreCase);
                if (!pkmap.ContainsKey(uniName))
                    pkmap[uniName] = new DynamicUniformValue { Value = adoptedUv.Value, BaseType = adoptedUv.BaseType };
            }
            if (!IsEngineDriving() && pvU != null)
            {
                if (!effectSettings.TryGetValue(effectFile, out var sm2))
                    effectSettings[effectFile] = sm2 = new Dictionary<string, string>();
                sm2[uniName] = pvU;
                MarkDirtyUniform(effectFile, uniName, baseType, pvU);
            }
            // A ticked setting needs its FX toggle driven too: tick every
            // technique of this file (capturing live state, not forcing it).
            var fileTechs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in techniqueSorting)
            {
                SplitTechKey(e, out var tt, out var tf);
                if (string.Equals(tf, effectFile, StringComparison.OrdinalIgnoreCase)) fileTechs.Add($"{tt}@{tf}");
            }
            foreach (var t in VisibleTechNamesFor(effectFile)) fileTechs.Add($"{t}@{effectFile}");
            if (runtimeTechniques.TryGetValue(effectFile, out var rt))
                foreach (var t in rt) fileTechs.Add($"{t}@{effectFile}");
            foreach (var tk in fileTechs)
            {
                if (kf.TickedTechs.Any(k => string.Equals(k, tk, StringComparison.OrdinalIgnoreCase))) continue;
                kf.TickedTechs.Add(tk);
                bool ten = effectEnabledState.TryGetValue(tk, out bool tenLive) && tenLive;
                var pkF = PrimaryKeyframe();
                if (pkF != null && pkF.TechStates.TryGetValue(tk, out bool tenBase)) ten = tenBase;
                kf.TechStates[tk] = ten;
            }
        }
        else
        {
            kf.TickedUniforms.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            if (kf.Uniforms.TryGetValue(effectFile, out var umap))
            {
                umap.Remove(uniName);
                if (umap.Count == 0) kf.Uniforms.Remove(effectFile);
            }
            // Push the primary value live.
            var pv = PrimaryUniformValue(effectFile, uniName);
            if (pv != null && !IsEngineDriving())
            {
                if (!effectSettings.TryGetValue(effectFile, out var sm))
                    effectSettings[effectFile] = sm = new Dictionary<string, string>();
                sm[uniName] = pv;
                MarkDirtyUniform(effectFile, uniName, baseType, pv);
            }
            // Drop from primary when nothing ticks it anymore (see techs).
            if (!UniformTickedAnywhere(effectFile, uniName) && PrimaryKeyframe() is DynamicKeyframe pkDropU)
            {
                foreach (var fk in pkDropU.Uniforms.Keys.ToList())
                {
                    if (!string.Equals(fk, effectFile, StringComparison.OrdinalIgnoreCase)) continue;
                    var dropMapU = pkDropU.Uniforms[fk];
                    var existingU = dropMapU.Keys.FirstOrDefault(k => string.Equals(k, uniName, StringComparison.OrdinalIgnoreCase));
                    if (existingU != null) dropMapU.Remove(existingU);
                    if (dropMapU.Count == 0) pkDropU.Uniforms.Remove(fk);
                }
            }
        }
        SaveActiveDyn();
    }

    // Primary (full base) for display fallback on unticked sparse rows.
    // Trim helpers: is a key driven anywhere (ticked or ON in any
    // keyframe)? Unknown (no data) counts as driven = keep (safe).
    private bool TechDrivenAnywhere(string techKey)
    {
        try
        {
            if (activeDynData == null) return true;
            foreach (var c in activeDynData.Configs)
                foreach (var k in c.Keyframes)
                {
                    if (k.TickedTechs.Any(t => string.Equals(t, techKey, StringComparison.OrdinalIgnoreCase))) return true;
                    if (k.TechStates.TryGetValue(techKey, out bool v) && v) return true;
                }
        }
        catch { return true; }
        return false;
    }

    private bool UniformTickedAnywhere(string effectFile, string uniName)
    {
        try
        {
            if (activeDynData == null) return true;
            foreach (var c in activeDynData.Configs)
                foreach (var k in c.Keyframes)
                    if (k.TickedUniforms.Any(u => string.Equals(u, effectFile + "\0" + uniName, StringComparison.OrdinalIgnoreCase))) return true;
        }
        catch { return true; }
        return false;
    }

    private DynamicKeyframe? PrimaryKeyframe()
    {
        var cfg = activeDynData?.GetConfig("Primary") ?? activeDynData?.Configs.FirstOrDefault();
        if (cfg == null) return null;
        return cfg.Keyframes.FirstOrDefault(k => k.IsPrimary) ?? cfg.Keyframes.FirstOrDefault(k => !k.Sparse);
    }

    private bool PrimaryTechState(string techKey)
    {
        var pk = PrimaryKeyframe();
        return pk != null && pk.TechStates.TryGetValue(techKey, out bool v) && v;
    }

    // Trim primary to driven keys: keep a tech iff ON anywhere (any
    // keyframe, or live now — covers .ini-ON-but-sidecar-OFF), keep a
    // uniform iff ticked in any keyframe. Values: stored first, else live
    // panel state, else dropped. Undriven output vanishes (the addon holds
    // preset values, identical screen); ticking later re-adopts via the
    // tick setters below. Backup first; returns counts for status.
    private (int techs, int unis) TrimPrimaryToDriven()
    {
        var dd = activeDynData;
        if (dd == null) return (0, 0);
        var pk = PrimaryKeyframe();
        if (pk == null) return (0, 0);
        var onTech = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tickedTech = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tickedUni = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in dd.Configs)
            foreach (var k in c.Keyframes)
            {
                foreach (var kvp in k.TechStates) if (kvp.Value) onTech.Add(kvp.Key);
                foreach (var t in k.TickedTechs) tickedTech.Add(t);
                foreach (var u in k.TickedUniforms) tickedUni.Add(u);
            }
        foreach (var kvp in effectEnabledState) if (kvp.Value) onTech.Add(kvp.Key);
        int dropT = 0, dropU = 0;
        foreach (var k in pk.TechStates.Keys.ToList())
        {
            if (onTech.Contains(k) || tickedTech.Contains(k)) continue;
            pk.TechStates.Remove(k);
            dropT++;
        }
        // Seed kept-but-absent techs from live (trim of an older trim).
        foreach (var k in onTech)
        {
            if (pk.TechStates.ContainsKey(k)) continue;
            if (effectEnabledState.TryGetValue(k, out bool en) && en) pk.TechStates[k] = true;
        }
        foreach (var f in pk.Uniforms.Keys.ToList())
        {
            var m = pk.Uniforms[f];
            foreach (var u in m.Keys.ToList())
            {
                string kk = f + "\0" + u;
                if (tickedUni.Contains(kk)) continue;
                m.Remove(u);
                dropU++;
            }
            if (m.Count == 0) pk.Uniforms.Remove(f);
        }
        // Seed kept-but-absent uniforms from live panels.
        foreach (var k in tickedUni)
        {
            int s = k.IndexOf('\0');
            if (s < 0) continue;
            var f = k.Substring(0, s);
            var u = k.Substring(s + 1);
            if (pk.Uniforms.TryGetValue(f, out var m) && m.ContainsKey(u)) continue;
            if (effectSettings.TryGetValue(f, out var sm) && sm.TryGetValue(u, out var lv))
            {
                if (!pk.Uniforms.TryGetValue(f, out var m2))
                    pk.Uniforms[f] = m2 = new Dictionary<string, DynamicUniformValue>(StringComparer.OrdinalIgnoreCase);
                m2[u] = new DynamicUniformValue { Value = lv, BaseType = UniformBaseType(f, u) };
            }
        }
        SaveActiveDyn();
        return (dropT, dropU);
    }

    private string UniformBaseType(string effectFile, string uniName)
    {
        try
        {
            if (effectUniforms.TryGetValue(effectFile, out var ul))
                foreach (var u in ul)
                    if (string.Equals(u.Name, uniName, StringComparison.OrdinalIgnoreCase))
                        return string.IsNullOrEmpty(u.BaseType) ? "float" : u.BaseType;
        }
        catch { }
        return "float";
    }

    // Display value for the settings panel: live, unless a keyframe is
    // selected — then that keyframe (ticked/full → its stored value,
    // otherwise the primary base).
    private string KfDisplayUniform(DynamicKeyframe? kf, string effectFile, string uniName, string liveValue)
    {
        if (kf == null) return liveValue;
        bool full = kf.IsPrimary || !kf.Sparse;
        bool ticked = kf.TickedUniforms.Any(k => string.Equals(k, effectFile + "\0" + uniName, StringComparison.OrdinalIgnoreCase));
        if ((full || ticked)
            && kf.Uniforms.TryGetValue(effectFile, out var m)
            && m.TryGetValue(uniName, out var uv))
            return uv.Value;
        return PrimaryUniformValue(effectFile, uniName) ?? liveValue;
    }

    // Toggle display for the effects list: live, unless a keyframe is
    // selected — then that keyframe (ticked/full → its stored state,
    // otherwise the primary base).
    private bool KfDisplayToggle(DynamicKeyframe? kf, string techKey, bool live)
    {
        if (kf == null) return live;
        bool full = kf.IsPrimary || !kf.Sparse;
        bool ticked = kf.TickedTechs.Any(k => string.Equals(k, techKey, StringComparison.OrdinalIgnoreCase));
        if ((full || ticked) && kf.TechStates.TryGetValue(techKey, out bool v)) return v;
        var pk = PrimaryKeyframe();
        if (pk != null && pk.TechStates.TryGetValue(techKey, out bool pv)) return pv;
        return live;
    }

    private string? PrimaryUniformValue(string effectFile, string uniName)
    {
        var pk = PrimaryKeyframe();
        if (pk != null && pk.Uniforms.TryGetValue(effectFile, out var m) && m.TryGetValue(uniName, out var uv))
            return uv.Value;
        return null;
    }

    private void NoteManualToggle(string techKey, bool enabled)
    {
        var kf = GetSelectedKeyframe();
        if (kf != null)
        {
            if (kf.Sparse && !kf.IsPrimary
                && !kf.TickedTechs.Any(k => string.Equals(k, techKey, StringComparison.OrdinalIgnoreCase)))
                return; // unticked = forgotten, never stored
            kf.TechStates[techKey] = enabled;
            sidecarDirty = true;
            return;
        }
        if (!IsEngineDriving()) return;
        dynOverrideToggles[techKey] = enabled;
    }

    private void NoteManualUniform(string effectFile, string uniName, string baseType, string value)
    {
        var kf = GetSelectedKeyframe();
        if (kf != null)
        {
            if (kf.Sparse && !kf.IsPrimary
                && !kf.TickedUniforms.Any(k => string.Equals(k, effectFile + "\0" + uniName, StringComparison.OrdinalIgnoreCase)))
                return; // unticked = forgotten, never stored
            if (!kf.Uniforms.TryGetValue(effectFile, out var umap))
                kf.Uniforms[effectFile] = umap = new Dictionary<string, DynamicUniformValue>(StringComparer.OrdinalIgnoreCase);
            umap[uniName] = new DynamicUniformValue { Value = value, BaseType = baseType };
            sidecarDirty = true;
            return;
        }
        dynOverrideUniforms[effectFile + "\0" + uniName] = (value, baseType);
    }

    private void FlushDynSidecar()
    {
        if (!sidecarDirty) return;
        if (activeDynData == null || string.IsNullOrEmpty(activeDynPath)) { sidecarDirty = false; return; }
        try { DynamicPresetStore.Save(activeDynPath, activeDynData); } catch { }
        sidecarDirty = false;
        lastSidecarSave = DateTime.UtcNow;
    }



    private string selKfConfig = "";
    private string selKfId = "";
    private string kfRenameDraft = "";
    private string kfRenameId = "";

    private DynamicKeyframe? GetSelectedKeyframe()
    {
        if (activeDynData == null || string.IsNullOrEmpty(selKfConfig) || string.IsNullOrEmpty(selKfId))
            return null;
        var cfg = activeDynData.GetConfig(selKfConfig);
        if (cfg == null) return null;
        foreach (var kf in cfg.Keyframes)
            if (kf.Id == selKfId) return kf;
        return null;
    }

    public bool IsKeyframeSelected() => GetSelectedKeyframe() != null;

    private void SelectKeyframe(string? config, string? id)
    {
        selKfConfig = config ?? "";
        selKfId = id ?? "";
        kfRenameId = "";
        if (string.IsNullOrEmpty(selKfId)) return;
        var kf = GetSelectedKeyframe();
        if (kf == null) { selKfConfig = ""; selKfId = ""; return; }
        // Selection is view-focus only: panels preview the keyframe via
        // KfDisplay* without touching live state. (Loading sparse content
        // into the live model wiped all non-carried state every swap.)
        // Manual edits from here write straight back into it (autosaved).
        // Engine-idle only: push the keyframe's effective state live so the
        // game previews it (diffs only, batched). While driving, the engine
        // owns live output.
        if (!IsEngineDriving())
            PreviewKeyframeLive(kf);
    }

    private void PreviewKeyframeLive(DynamicKeyframe kf)
    {
        var pk = PrimaryKeyframe();
        bool kfFull = kf.IsPrimary || !kf.Sparse || pk == null;
        var techVals = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (pk != null)
            foreach (var kvp in pk.TechStates) techVals[kvp.Key] = kvp.Value;
        if (kfFull)
        {
            foreach (var kvp in kf.TechStates) techVals[kvp.Key] = kvp.Value;
        }
        else
        {
            foreach (var k in kf.TickedTechs)
                if (kf.TechStates.TryGetValue(k, out bool v)) techVals[k] = v;
        }
        var toggleLines = new List<string>();
        foreach (var kvp in techVals)
        {
            bool live = effectEnabledState.TryGetValue(kvp.Key, out bool e) && e;
            if (live == kvp.Value) continue;
            effectEnabledState[kvp.Key] = kvp.Value;
            SplitTechKey(kvp.Key, out var ttech, out var tfile);
            toggleLines.Add($"{tfile}|{ttech}|{(kvp.Value ? "1" : "0")}");
        }
        if (toggleLines.Count > 0)
        {
            try { AtomicWriteText(Path.Combine(GetGameDir(), "ffxiv_reshade_toggle"), string.Join("\n", toggleLines) + "\n"); } catch { }
        }
        var uniVals = new Dictionary<string, (string Value, string BaseType)>(StringComparer.OrdinalIgnoreCase);
        if (pk != null)
            foreach (var fkvp in pk.Uniforms)
                foreach (var u in fkvp.Value)
                    uniVals[fkvp.Key + "\0" + u.Key] = (u.Value.Value, u.Value.BaseType);
        if (kfFull)
        {
            foreach (var fkvp in kf.Uniforms)
                foreach (var u in fkvp.Value)
                    uniVals[fkvp.Key + "\0" + u.Key] = (u.Value.Value, u.Value.BaseType);
        }
        else
        {
            foreach (var k in kf.TickedUniforms)
            {
                int sep = k.IndexOf('\0');
                if (sep < 0) continue;
                var file = k.Substring(0, sep);
                var uname = k.Substring(sep + 1);
                if (kf.Uniforms.TryGetValue(file, out var m) && m.TryGetValue(uname, out var uv))
                    uniVals[k] = (uv.Value, uv.BaseType);
            }
        }
        foreach (var kvp in uniVals)
        {
            int sep = kvp.Key.IndexOf('\0');
            var file = kvp.Key.Substring(0, sep);
            var uname = kvp.Key.Substring(sep + 1);
            string live = "";
            if (effectSettings.TryGetValue(file, out var m) && m.TryGetValue(uname, out var lv)) live = lv;
            if (live == kvp.Value.Value) continue;
            if (!effectSettings.TryGetValue(file, out var sm))
                effectSettings[file] = sm = new Dictionary<string, string>();
            sm[uname] = kvp.Value.Value;
            MarkDirtyUniform(file, uname, kvp.Value.BaseType, kvp.Value.Value);
        }
    }

    public string SelectedPreset => selectedPresetPath;

    // Programmatic preset switch (same path as clicking a row): select,
    // parse into the panels, refocus, and signal ReShade. No-op on
    // missing/identical paths.
    public void ApplyPresetPath(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            if (string.Equals(selectedPresetPath, path, StringComparison.OrdinalIgnoreCase)) return;
            selectedPresetPath = path;
            ParsePresetFile(path);
            SetFocus(null);
            WritePresetSignal(path);
        }
        catch { }
    }
    public DynamicPresetData? ActiveDynData => activeDynData;
    // Daylight resolution: one global file for all presets (sidecar
    // curves are legacy fallback + one-time adoption seed).
    public DynamicDaylight? ActiveDaylight() => ActiveDaylight(activeDynData);
    public DynamicDaylight? ActiveDaylight(DynamicPresetData? data)
    {
        try
        {
            var gd = GetGameDir();
            if (string.IsNullOrEmpty(gd)) return data?.Daylight;
            return DynamicDaylightStore.Load(gd, data?.Daylight);
        }
        catch { return data?.Daylight; }
    }
    public int GetEorzeaSec() => plugin.GetEorzeaSecondsPublic();

    public void SaveActiveDyn()
    {
        if (activeDynData == null || string.IsNullOrEmpty(activeDynPath)) return;
        try { DynamicPresetStore.Save(activeDynPath, activeDynData); } catch { }
    }

    // Snapshot live state into a config at a time (used by the snapshot
    // button and the canvas "snapshot here"). Replaces a keyframe within
    // ±30s (circular), else appends, keeps sorted.
    public void SnapshotToConfig(string configName, int timeSeconds)
    {
        if (string.IsNullOrEmpty(selectedPresetPath) || !IsDynamicPreset(selectedPresetPath)) return;
        EnsureDynCache();
        var data = activeDynData ?? new DynamicPresetData();
        var cfg = data.GetConfig(configName);
        if (cfg == null) { cfg = new DynamicAnimConfig { Name = configName }; data.Configs.Add(cfg); }
        SnapshotToConfig(data, cfg, timeSeconds);
        activeDynData = data;
        activeDynPath = selectedPresetPath;
        try { DynamicPresetStore.Save(selectedPresetPath, data); } catch { }
        sidecarDirty = false;
        lastSidecarSave = DateTime.UtcNow;
        ClearDynOverrides();
        try { plugin.PrimeDynToggles(new Dictionary<string, bool>(effectEnabledState)); } catch { }
    }

    public DynamicKeyframe CaptureLiveForNode() => CaptureLiveKeyframe(plugin.GetEorzeaSecondsPublic());

    private DynamicKeyframe CaptureLiveKeyframe(int timeSeconds)
    {
        var snap = new DynamicKeyframe { TimeSeconds = timeSeconds };
        snap.TechStates = new Dictionary<string, bool>(effectEnabledState, StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in effectSettings)
        {
            ParseFxFile(kvp.Key);
            var umap = new Dictionary<string, DynamicUniformValue>(StringComparer.OrdinalIgnoreCase);
            var lookup = new Dictionary<string, ShaderUniformInfo>(StringComparer.OrdinalIgnoreCase);
            if (effectUniforms.TryGetValue(kvp.Key, out var ul))
                foreach (var u in ul)
                    if (!lookup.ContainsKey(u.Name)) lookup[u.Name] = u;
            foreach (var s in kvp.Value)
                umap[s.Key] = new DynamicUniformValue
                {
                    Value = s.Value,
                    BaseType = lookup.TryGetValue(s.Key, out var ui) ? ui.BaseType : "float"
                };
            snap.Uniforms[kvp.Key] = umap;
        }
        return snap;
    }

    private void SnapshotToConfig(DynamicPresetData data, DynamicAnimConfig cfg, int now)
    {
        var snap = CaptureLiveKeyframe(now);
        snap.IsPrimary = !cfg.Keyframes.Any(k => k.IsPrimary);
        snap.Sparse = !snap.IsPrimary;
        // Replace a keyframe within ±30s (circular), else append. Keeps the
        // timeline sorted for the engine.
        cfg.Keyframes.RemoveAll(k => DynamicTimeline.CircularDist(k.TimeSeconds, now) <= 30);
        if (!snap.IsPrimary && !cfg.Keyframes.Any(k => k.IsPrimary)) { snap.IsPrimary = true; snap.Sparse = false; }
        cfg.Keyframes.Add(snap);
        cfg.Keyframes.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
    }

    private void DrawDynamicSection()
    {
        if (string.IsNullOrEmpty(selectedPresetPath) || !IsDynamicPreset(selectedPresetPath)) return;
        ImGui.Spacing();
        if (activeDynData != null)
        {
            bool en = activeDynData.Enabled;
            if (ImGui.Checkbox("Animate this preset", ref en))
            {
                activeDynData.Enabled = en;
                try { DynamicPresetStore.Save(activeDynPath, activeDynData); } catch { }
                if (en)
                {
                    try { plugin.PrimeDynToggles(new Dictionary<string, bool>(effectEnabledState)); } catch { }
                }
                else ClearDynOverrides();
            }
        }
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.05f, 0.65f, 0.85f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.10f, 0.75f, 0.95f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.00f, 0.55f, 0.75f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.02f, 0.02f, 0.02f, 1f));
        if (ImGui.Button("Open Animator", new Vector2(ImGui.GetContentRegionAvail().X, 0)))
            plugin.ShowAnimator();
        ImGui.PopStyleColor(4);
        ImGui.Spacing();
        ImGui.Separator();
    }

    private void WritePresetSignal(string presetPath)
    {
        // Queued + retried plugin-side: the addon's poll lock can refuse
        // any single atomic replace (sharing violation).
        try { plugin.QueuePresetSignal(presetPath); } catch { }
    }

    private void WriteToggleSignal(string effectFile, string techName, bool enabled)
    {
        try { AtomicWriteText(Path.Combine(GetGameDir(), "ffxiv_reshade_toggle"), $"{effectFile}|{techName}|{(enabled ? "1" : "0")}\n"); } catch { }
    }

    // The display shows each technique once, so the model must too:
    // duplicate sorting entries (common in big presets) make insert-before
    // land on the wrong instance. First occurrence wins.
    private void DedupeTechniqueSorting()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        techniqueSorting.RemoveAll(e =>
        {
            SplitTechKey(e, out var t, out var f);
            return !seen.Add(t + "\0" + f);
        });
    }

    private static void SplitTechKey(string key, out string tech, out string file)
    {
        var atIdx = key.LastIndexOf('@');
        if (atIdx >= 0) { tech = key.Substring(0, atIdx); file = key.Substring(atIdx + 1); }
        else { tech = ""; file = key; }
    }

    private static bool TechKeyEquals(string a, string b)
    {
        SplitTechKey(a, out var at, out var af);
        SplitTechKey(b, out var bt, out var bf);
        return string.Equals(at, bt, StringComparison.OrdinalIgnoreCase)
            && string.Equals(af, bf, StringComparison.OrdinalIgnoreCase);
    }

    private void MoveTechnique(string fromKey, string? beforeKey)
    {
        if (beforeKey != null && TechKeyEquals(fromKey, beforeKey)) return;
        var next = new List<string>(techniqueSorting);
        next.RemoveAll(e => TechKeyEquals(e, fromKey));
        int idx = next.Count;
        if (beforeKey != null)
        {
            int found = next.FindIndex(e => TechKeyEquals(e, beforeKey));
            if (found >= 0) idx = found;
        }
        next.Insert(idx, fromKey);
        if (next.Count == techniqueSorting.Count)
        {
            bool same = true;
            for (int i = 0; i < next.Count; i++)
                if (!string.Equals(next[i], techniqueSorting[i], StringComparison.Ordinal)) { same = false; break; }
            if (same) return; // no visible change: skip signal spam
        }
        techniqueSorting.Clear();
        techniqueSorting.AddRange(next);
        SplitTechKey(fromKey, out var fromTech, out var fromFile);
        string beforeTech = "", beforeFile = "";
        if (beforeKey != null) SplitTechKey(beforeKey, out beforeTech, out beforeFile);
        _pendingCmdLines.Add($"REORDER|{fromFile}|{fromTech}|{beforeFile}|{beforeTech}");
    }

    private static void AtomicWriteText(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, true);
    }

    private void ParseFxFile(string effectName)
    {
        if (fxParsed.ContainsKey(effectName)) return;
        fxParsed[effectName] = true;

        var gameDir = GetGameDir();
        if (string.IsNullOrEmpty(gameDir)) return;
        var relPath = shaderFileFullPaths.TryGetValue(effectName, out var rp) ? rp : effectName;
        var fxPath = Path.Combine(gameDir, "reshade-shaders", "Shaders", relPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fxPath)) return;

        try
        {
            var uniforms = new List<ShaderUniformInfo>();
            var parsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ParseFxFileRecursive(fxPath, Path.Combine(gameDir, "reshade-shaders", "Shaders"), uniforms, parsed);
            effectUniforms[effectName] = uniforms;
        }
        catch { }
    }

    private void ParseFxFileRecursive(string filePath, string shadersDir, List<ShaderUniformInfo> uniforms, HashSet<string> parsed, Dictionary<string, string>? defines = null)
    {
        if (!File.Exists(filePath)) return;
        var key = Path.GetFullPath(filePath);
        if (parsed.Contains(key)) return;
        parsed.Add(key);

        var content = File.ReadAllText(filePath);

        content = StripFxComments(content);
        defines ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match inc in Regex.Matches(content, @"#include\s+""([^""]+)"""))
        {
            // ReShade resolves relative to the file first, then globally.
            var local = Path.Combine(Path.GetDirectoryName(filePath) ?? shadersDir, inc.Groups[1].Value);
            if (File.Exists(local))
                ParseFxFileRecursive(local, shadersDir, uniforms, parsed, defines);
            else
                ParseFxFileRecursive(Path.Combine(shadersDir, inc.Groups[1].Value), shadersDir, uniforms, parsed, defines);
        }

        // Simple numeric #defines / consts, so defaults like "= AL_REGION_LEFT"
        // show real values. Single values and one-operator folds
        // (180.0f / AS_PI); anything fancier stays unresolved on purpose.
        // Main file wins ties (processed last).
        foreach (Match dm in Regex.Matches(content, @"#define\s+([A-Za-z_]\w*)\s+([^\r\n]+?)\s*(?://|$)", RegexOptions.Multiline))
        {
            float? v = EvalConstExpr(dm.Groups[2].Value, defines);
            if (v != null) defines[dm.Groups[1].Value] = v.Value.ToString("G9", System.Globalization.CultureInfo.InvariantCulture);
        }
        foreach (Match dm in Regex.Matches(content, @"(?:static\s+)?const\s+(?:bool|int|uint|float|double|half)\s+([A-Za-z_]\w*)\s*=\s*([^;]+);"))
        {
            float? v = EvalConstExpr(dm.Groups[2].Value, defines);
            if (v != null) defines[dm.Groups[1].Value] = v.Value.ToString("G9", System.Globalization.CultureInfo.InvariantCulture);
        }

        // Identity-desc helper idiom (FGFX packs): MAKE_DESCRIPTION_VAR(X)
        // is just X_DESC. Expand textually, but only when the file defines
        // it exactly that way.
        try
        {
            if (Regex.IsMatch(content, @"#define\s+MAKE_DESCRIPTION_VAR\s*\(\s*(\w+)\s*\)\s+\1##_DESC\b"))
                content = Regex.Replace(content, @"MAKE_DESCRIPTION_VAR\s*\(\s*(\w+)\s*\)", "$1_DESC");
        }
        catch { }

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in uniforms)
            seenNames.Add(u.Name);

        foreach (Match um in Regex.Matches(content, @"\buniform\s+"))
        {
            int idx = um.Index;
            // Inside a string literal (prose mentioning uniforms)?
            int lineStart = content.LastIndexOf('\n', Math.Max(0, idx - 1)) + 1;
            bool inStr = false;
            for (int qi = lineStart; qi < idx; qi++)
            {
                if (content[qi] == '\\' && qi + 1 < idx) qi++;
                else if (content[qi] == '"') inStr = !inStr;
            }
            if (inStr) continue;
            // Function parameters aren't global uniforms (ReShade ignores them).
            int pb = idx - 1;
            while (pb >= 0 && char.IsWhiteSpace(content[pb])) pb--;
            if (pb >= 0 && (content[pb] == '(' || content[pb] == ',')) continue;

            // Leading identifier run, then strip storage-class qualifiers.
            var after = content.Substring(idx + um.Length);
            var words = new List<string>();
            int pos = 0;
            while (words.Count < 4 && pos < after.Length)
            {
                int ws = pos;
                while (ws < after.Length && char.IsWhiteSpace(after[ws])) ws++;
                var wm = Regex.Match(after.Substring(ws), @"^[A-Za-z_]\w*");
                if (!wm.Success || wm.Index != 0) break;
                // Adjacent check: only whitespace between words.
                bool adjacent = true;
                for (int ai = pos; ai < ws; ai++)
                    if (!char.IsWhiteSpace(after[ai])) { adjacent = false; break; }
                if (!adjacent) break;
                words.Add(wm.Value);
                pos = ws + wm.Length;
                int peek = pos;
                while (peek < after.Length && char.IsWhiteSpace(after[peek])) peek++;
                if (peek >= after.Length || (!char.IsLetterOrDigit(after[peek]) && after[peek] != '_')) break;
            }
            while (words.Count > 2
                && (words[0] == "static" || words[0] == "const" || words[0] == "extern"
                    || words[0] == "volatile" || words[0] == "precise" || words[0] == "uniform"))
                words.RemoveAt(0);
            if (words.Count < 2) continue;
            string baseType = words[words.Count - 2];
            string varName = words[words.Count - 1];
            // Only real value types (kills keywords, textures, structs...).
            if (!Regex.IsMatch(baseType, @"^(bool|int|uint|float|double|half|min10float|min16float|min12int|min16int|min16uint)([1-4](x[1-4])?)?$"))
                continue;
            if (!seenNames.Add(varName)) continue;

            // Declarator end: strict adjacency (whitespace/newlines only).
            // '='/';' with no block is a valid bare uniform (ReShade shows
            // it with auto widgets); anything else bails instead of stealing
            // the *next* uniform's annotation block.
            int dp = pos;
            while (dp < after.Length && char.IsWhiteSpace(after[dp])) dp++;
            char nc = dp < after.Length ? after[dp] : '\0';
            string annotBlock = "";
            string afterAnnot;
            if (nc == '<')
            {
                // Quote-aware block scan (tooltips may contain < >).
                int depth = 1, p = dp + 1;
                bool inS = false;
                while (p < after.Length && depth > 0)
                {
                    char ch = after[p];
                    if (inS)
                    {
                        if (ch == '\\' && p + 1 < after.Length) p++;
                        else if (ch == '"') inS = false;
                    }
                    else if (ch == '"') inS = true;
                    else if (ch == '<') depth++;
                    else if (ch == '>') depth--;
                    p++;
                }
                if (depth != 0) continue;
                annotBlock = after.Substring(dp + 1, (p - 1) - (dp + 1));
                afterAnnot = after.Substring(p);
            }
            else if (nc == '=' || nc == ';')
            {
                afterAnnot = after.Substring(dp);
            }
            else continue;

            // ReShade-owned auto uniforms (timer, pingpong, ...) never show.
            string annotNoStrings;
            try { annotNoStrings = Regex.Replace(annotBlock, @"""((?:[^""\\]|\\.)*)""", "\"\""); }
            catch { annotNoStrings = annotBlock; }
            if (Regex.IsMatch(annotNoStrings, @"(?:^|;)\s*source\s*=")) continue;

            var defMatch = Regex.Match(afterAnnot, @"^\s*=\s*([^;]+);");
            string rawDef = defMatch.Success ? defMatch.Groups[1].Value.Trim() : "";
            if (defines.TryGetValue(rawDef, out var dd)) rawDef = dd;
            string defaultValue = NormalizeFxDefault(rawDef);

            var info = new ShaderUniformInfo();
            info.BaseType = baseType;
            info.Name = varName;
            info.DefaultValue = defaultValue;
            info.UiType = DetectUiType(annotBlock, baseType);
            info.Label = UnescapeFxString(ExtractAnnotationString(annotBlock, "ui_label") ?? varName);
            info.Tooltip = UnescapeFxString(ExtractAnnotationJoined(annotBlock, "ui_tooltip") ?? "");
            info.UiItems = ExtractAnnotationString(annotBlock, "ui_items") ?? "";
            info.Category = ExtractAnnotationString(annotBlock, "ui_category") ?? "";
            string catClosed = ExtractAnnotationString(annotBlock, "ui_category_closed") ?? "";
            info.UiCategoryClosed = catClosed.Equals("true", StringComparison.OrdinalIgnoreCase) || catClosed == "1";
            info.UiMin = AnnotFloat(annotBlock, "ui_min", "min", -1000f, 0f, defines);
            info.UiMax = AnnotFloat(annotBlock, "ui_max", "max", 1000f, 4096f, defines);
            info.UiStep = AnnotFloat(annotBlock, "ui_step", "step", 0f, float.NaN, defines);

            uniforms.Add(info);
        }
    }

    // Strip HLSL comments so commented-out uniforms never parse as real
    // ones. Block strip only when balanced (decorative /* would eat code);
    // line comments cut quote-aware so URLs in strings survive.
    private static string StripFxComments(string content)
    {
        try
        {
            int opens = Regex.Matches(content, @"/\*").Count;
            int closes = Regex.Matches(content, @"\*/").Count;
            if (opens == closes)
                content = Regex.Replace(content, @"/\*.*?\*/", "", RegexOptions.Singleline);
            var sb = new System.Text.StringBuilder(content.Length);
            foreach (var line in content.Split('\n'))
            {
                bool inS = false;
                int cut = -1;
                for (int i = 0; i + 1 < line.Length; i++)
                {
                    char ch = line[i];
                    if (inS)
                    {
                        if (ch == '\\') i++;
                        else if (ch == '"') inS = false;
                    }
                    else if (ch == '"') inS = true;
                    else if (ch == '/' && line[i + 1] == '/') { cut = i; break; }
                }
                sb.Append(cut >= 0 ? line.Substring(0, cut) : line).Append('\n');
            }
            content = sb.ToString();
            // Preprocessor directive lines (except #include/#define, which we
            // need): #if comparisons like "__RESHADE__ < 40000" would corrupt
            // bracket counting, and both branches' code stays usable anyway.
            // (If/else branch selection is NOT evaluated: edge case noted.)
            try { content = Regex.Replace(content, @"^\s*#(if|ifdef|ifndef|elif|else|endif|error|warning|pragma|line|undef)\b.*$", "", RegexOptions.Multiline); }
            catch { }
            return content;
        }
        catch { return content; }
    }

    // Normalize an HLSL default for our pipelines: strip constructor syntax
    // (float2(0.5, 0.5) -> "0.5, 0.5", which the addon parses per token and
    // would otherwise drop) and f suffixes stof chokes on with full check.
    private static string NormalizeFxDefault(string v)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(v)) return "";
            v = v.Trim();
            var m = Regex.Match(v, @"^(?:bool|int|uint|float|double|half)(?:[1-4](?:x[1-4])?)?\s*\((.*)\)$", RegexOptions.Singleline);
            if (m.Success) v = m.Groups[1].Value.Trim();
            v = Regex.Replace(v, @"(?<=\d)[fFdD]\b", "");
            return v;
        }
        catch { return v; }
    }

    private static string DetectUiType(string block, string baseType)
    {
        var uiType = ExtractAnnotationString(block, "ui_type");
        if (!string.IsNullOrEmpty(uiType))
        {
            string lt = uiType.ToLowerInvariant();
            // ReShade's drag widget has no bar; our slider is the closest.
            if (lt == "drag") return "slider";
            // "list" is combo-with-items; bool words / momentary buttons on
            // bools are checkboxes (on other bases: unknown, fall through).
            if (lt == "list") return "combo";
            if (lt == "bool" || lt == "boolean" || lt == "button")
            {
                if (baseType == "bool") return "checkbox";
            }
            else return lt;
        }

        string upper = block.ToUpperInvariant();
        if (upper.Contains("__UNIFORM_COMBO")) return "combo";
        if (upper.Contains("__UNIFORM_SLIDER")) return "slider";
        if (upper.Contains("__UNIFORM_RADIO")) return "radio";
        if (upper.Contains("__UNIFORM_COLOR")) return "color";
        if (upper.Contains("__UNIFORM_LIST")) return "combo";
        if (upper.Contains("__UNIFORM_DRAG")) return "slider";
        if (upper.Contains("__UNIFORM_INPUT")) return "input";

        if (baseType == "bool") return "checkbox";
        return "input";
    }

    private static string? ExtractAnnotationString(string block, string name)
    {
        var match = Regex.Match(block, $@"(?<![A-Za-z_]){name}\s*=\s*""([^""]*)""");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ExtractAnnotationToken(string block, string name)
    {
        // Value runs to ';' (or block end) so simple folds like
        // AS_MIN * 100.0 arrive whole. min/max/step never contain ';'.
        var match = Regex.Match(block, $@"(?<![A-Za-z_]){name}\s*=\s*""?([^"";]+?)""?\s*(?:;|$)");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string StripNumSuffix(string t)
    {
        var m = Regex.Match(t, @"^([0-9.eE+-]+)[fFdD]$");
        return m.Success ? m.Groups[1].Value : t;
    }

    private static bool ConstOperand(string t, Dictionary<string, string> defines, out float v)
    {
        v = 0;
        try
        {
            t = t.Trim();
            if (t.StartsWith("(") && t.EndsWith(")")) t = t.Substring(1, t.Length - 2).Trim();
            t = StripNumSuffix(t);
            if (defines.TryGetValue(t, out var dv)) t = StripNumSuffix(dv.Trim());
            return float.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);
        }
        catch { return false; }
    }

    private static float? EvalConstExpr(string expr, Dictionary<string, string> defines)
    {
        try
        {
            expr = expr.Trim();
            if (expr.StartsWith("(") && expr.EndsWith(")")) expr = expr.Substring(1, expr.Length - 2).Trim();
            var m = Regex.Match(expr, @"^(.+?)\s*([*/])\s*(.+)$");
            if (m.Success)
            {
                if (!ConstOperand(m.Groups[1].Value, defines, out float l)) return null;
                if (!ConstOperand(m.Groups[3].Value, defines, out float r)) return null;
                if (m.Groups[2].Value[0] == '/' && Math.Abs(r) < 1e-12f) return null;
                return m.Groups[2].Value[0] == '*' ? l * r : l / r;
            }
            return ConstOperand(expr, defines, out float v) ? v : (float?)null;
        }
        catch { return null; }
    }

    private static float ResolveAnnotNumber(string? tok, float fallback, float macroCap, Dictionary<string, string>? defines)
    {
        if (tok == null) return fallback;
        string t = StripNumSuffix(tok.Trim());
        if (defines != null)
        {
            // Simple NAME * NUMBER / NUMBER * NAME / NAME * NAME folds.
            var parts = t.Split('*');
            if (parts.Length == 2)
            {
                string l = StripNumSuffix(parts[0].Trim());
                string r = StripNumSuffix(parts[1].Trim());
                if (defines.TryGetValue(l, out var dl)) l = StripNumSuffix(dl.Trim());
                if (defines.TryGetValue(r, out var dr)) r = StripNumSuffix(dr.Trim());
                if (float.TryParse(l, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fl)
                    && float.TryParse(r, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fr))
                    return fl * fr;
            }
            else if (defines.TryGetValue(t, out var dv)
                && float.TryParse(StripNumSuffix(dv.Trim()), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float vv))
                return vv;
        }
        if (float.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v))
            return v;
        // Dimension macros (true res unknown at parse): generous caps.
        if ((t == "BUFFER_WIDTH" || t == "BUFFER_HEIGHT") && !float.IsNaN(macroCap))
            return macroCap;
        return fallback;
    }

    private static float AnnotFloat(string block, string uiName, string bareName, float fallback, float macroCap, Dictionary<string, string>? defines)
    {
        string? t = ExtractAnnotationToken(block, uiName) ?? ExtractAnnotationToken(block, bareName);
        return ResolveAnnotNumber(t, fallback, macroCap, defines);
    }

    // ui_tooltip can be several adjacent string literals (HLSL concatenates
    // them); join and unescape like the technique path does.
    private static string? ExtractAnnotationJoined(string block, string name)
    {
        var m = Regex.Match(block, $@"(?<![A-Za-z_]){name}\s*=\s*((?:""(?:[^""\\]|\\.)*""\s*)+)\s*;");
        if (!m.Success) return ExtractAnnotationString(block, name);
        var sb = new System.Text.StringBuilder();
        foreach (Match part in Regex.Matches(m.Groups[1].Value, @"""((?:[^""\\]|\\.)*)"""))
            sb.Append(UnescapeFxString(part.Groups[1].Value));
        var s = sb.ToString().Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    public void FlushDynSidecarNow() => FlushDynSidecar();

    private void DrawShadersTab()
    {
        PollPendingDynamic();
        EnsureDynCache();
        if (sidecarDirty && (DateTime.UtcNow - lastSidecarSave).TotalSeconds > 2)
            FlushDynSidecar();
        ImGui.Text("Preset:"); ImGui.SameLine(); ImGui.SetNextItemWidth(350);
        if (ImGui.BeginCombo("##reshadePreset", string.IsNullOrEmpty(selectedPresetPath) ? "Select preset..." : GetPresetDisplayName(selectedPresetPath), ImGuiComboFlags.HeightLargest))
        {
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
            ImGui.InputTextWithHint("##presetSearch", "Search presets...", ref presetSearch, 256);
            ImGui.Separator();
            var searchLower = presetSearch.ToLowerInvariant();
            float listHeight = Math.Min(presetFiles.Count * ImGui.GetFrameHeightWithSpacing(), 300);
            if (ImGui.BeginChild("##presetList", new Vector2(0, listHeight), false))
            {
                foreach (var preset in OrderedPresetFiles())
                {
                    var display = GetPresetDisplayName(preset);
                    if (!string.IsNullOrEmpty(searchLower) && !display.ToLowerInvariant().Contains(searchLower)) continue;
                    bool isSel = string.Equals(selectedPresetPath, preset, StringComparison.OrdinalIgnoreCase);
                    if (DrawPresetRow(preset, display, isSel)) { selectedPresetPath = preset; ParsePresetFile(preset); SetFocus(null); WritePresetSignal(preset); }
                }
                ImGui.EndChild();
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button("Rescan")) { ScanShaderFiles(); if (!string.IsNullOrEmpty(selectedPresetPath)) ParsePresetFile(selectedPresetPath); }
        ImGui.SameLine();
        if (ImGui.Button("New Preset")) OpenPresetNameModal(isDuplicate: false);
        ImGui.SameLine();
        bool noSel = string.IsNullOrEmpty(selectedPresetPath) || !File.Exists(selectedPresetPath);
        if (noSel) ImGui.BeginDisabled();
        if (ImGui.Button("Duplicate Preset")) OpenPresetNameModal(isDuplicate: true);
        if (noSel) ImGui.EndDisabled();
        bool alreadyDynamic = false;
        if (!string.IsNullOrEmpty(selectedPresetPath))
        {
            var xlDir = Path.Combine(GetGameDir(), "reshade-presets", "xlAnimPresets") + Path.DirectorySeparatorChar;
            alreadyDynamic = selectedPresetPath.StartsWith(xlDir, StringComparison.OrdinalIgnoreCase);
        }
        if (!alreadyDynamic)
        {
            if (pendingDynamicSrc != null)
            {
                ImGui.BeginDisabled();
                ImGui.Button("Making Dynamic...");
                ImGui.EndDisabled();
            }
            else
            {
                bool targetExists = false;
                if (!string.IsNullOrEmpty(selectedPresetPath))
                {
                    var baseName = Path.GetFileName(selectedPresetPath);
                    if (!baseName.StartsWith("xl_", StringComparison.OrdinalIgnoreCase))
                        baseName = "xl_" + baseName;
                    targetExists = File.Exists(Path.Combine(GetGameDir(), "reshade-presets", "xlAnimPresets", baseName));
                }
                if (targetExists) ImGui.BeginDisabled();
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.72f, 0.38f, 0.05f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.82f, 0.45f, 0.08f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.60f, 0.30f, 0.03f, 1f));
                if (ImGui.Button("Make Preset Dynamic")) { MakePresetDynamic(); }
                ImGui.PopStyleColor(3);
                if (targetExists)
                {
                    ImGui.EndDisabled();
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip("already exists with that name");
                }
            }
        }
        // Trim primary to driven keys (see TrimPrimaryToDriven): drops
        // everything no keyframe drives from the base snapshot, with a
        // backup. Only for dynamic presets with a loaded sidecar.
        bool canTrim = alreadyDynamic && activeDynData != null;
        if (!canTrim) ImGui.BeginDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Trim Primary"))
        {
            try
            {
                EnsureDynCache();
                if (activeDynData == null || !string.Equals(activeDynPath, selectedPresetPath, StringComparison.OrdinalIgnoreCase))
                {
                    trimStatus = "Sidecar not loaded for this preset — reselect it first.";
                }
                else
                {
                    if (!string.IsNullOrEmpty(selectedPresetPath))
                    {
                        var sc = DynamicPresetStore.SidecarPath(selectedPresetPath);
                        if (File.Exists(sc)) File.Copy(sc, sc + ".trimbak", true);
                    }
                    var (dt2, du2) = TrimPrimaryToDriven();
                    trimStatus = $"Trimmed {dt2} toggles, {du2} uniforms from primary (backup .trimbak).";
                }
            }
            catch (Exception ex) { try { trimStatus = "Trim failed: " + ex.Message; } catch { } }
        }
        if (!canTrim)
        {
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("select a dynamic preset first");
        }
            else if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Presets created in 0.9.315 might have a bloated primary key (every known shader tracked). Click this to trim it to driven keys only (backup first). Ticking later re-adopts.");
        if (!string.IsNullOrEmpty(trimStatus))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(trimStatus);
        }
        DrawPresetNameModal();
        DrawDynamicSection();

        if (string.IsNullOrEmpty(selectedPresetPath))
        {
            ImGui.Spacing(); ImGui.TextDisabled("Select a preset above to edit shaders.");
            return;
        }

        ImGui.Separator();
        var avail = ImGui.GetContentRegionAvail();
        float shaderSplitX = plugin.Config.ShaderSplitX;
        if (shaderSplitX < 0) shaderSplitX = Math.Min(avail.X * 0.35f, 280);
        shaderSplitX = Math.Clamp(shaderSplitX, 150, Math.Max(151, avail.X - 200));

        ImGui.BeginChild("##effectsList", new Vector2(shaderSplitX, avail.Y), true);
        DrawEffectsListLeft();
        ImGui.EndChild();

        ImGui.SameLine(0, 1);
        ImGui.InvisibleButton("##shadersplit", new Vector2(5, avail.Y));
        bool splitHover = ImGui.IsItemHovered();
        bool splitActive = ImGui.IsItemActive();
        if (splitHover || splitActive)
        {
            // Real ↔ cursor: the binding has no directional cursors, so set
            // the OS ew-resize cursor directly every frame (we draw last, so
            // ImGui doesn't override us mid-frame).
            if (sizeWeCursor == IntPtr.Zero)
                sizeWeCursor = LoadCursor(IntPtr.Zero, IDC_SIZEWE);
            if (sizeWeCursor != IntPtr.Zero)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.None);
                SetCursor(sizeWeCursor);
            }
        }
        if (splitHover || splitActive)
        {
            float lx = (ImGui.GetItemRectMin().X + ImGui.GetItemRectMax().X) * 0.5f;
            ImGui.GetWindowDrawList().AddLine(
                new Vector2(lx, ImGui.GetItemRectMin().Y),
                new Vector2(lx, ImGui.GetItemRectMax().Y),
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.45f, 0.65f, 0.9f, 0.8f)), 1.0f);
        }
        if (splitActive && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0))
        {
            plugin.Config.ShaderSplitX = shaderSplitX = Math.Clamp(shaderSplitX + ImGui.GetIO().MouseDelta.X, 150, Math.Max(151, avail.X - 200));
            wasSplitActive = true;
        }
        else if (wasSplitActive && !splitActive)
        {
            wasSplitActive = false;
            plugin.Config.Save();
        }

        ImGui.SameLine(0, 1);

        bool showConfigs = !string.IsNullOrEmpty(selectedPresetPath) && IsDynamicPreset(selectedPresetPath);
        float settingsW = 0;
        const float configsW = 200;
        if (showConfigs)
            settingsW = Math.Max(80, avail.X - shaderSplitX - configsW - 12);

        ImGui.BeginChild("##settingsPanel", new Vector2(settingsW, avail.Y), true);
        DrawSettingsRight();
        ImGui.EndChild();

        if (showConfigs)
        {
            ImGui.SameLine(0, 2);
            ImGui.BeginChild("##configPanel", new Vector2(configsW, avail.Y), true);
            DrawConfigPanel();
            ImGui.EndChild();
        }
    }

    private bool kfRenameFocus;

    private string UniqueKfName(DynamicAnimConfig cfg, string want)
    {
        if (cfg.Keyframes.All(k => !string.Equals(k.Name, want, StringComparison.OrdinalIgnoreCase)))
            return want;
        int i = 2;
        while (cfg.Keyframes.Any(k => string.Equals(k.Name, $"{want} ({i})", StringComparison.OrdinalIgnoreCase))) i++;
        return $"{want} ({i})";
    }

    // Tooltip body: which FX files a keyframe drives.
    private string KeyframeFxSummary(DynamicKeyframe kf)
    {
        if (kf.IsPrimary || !kf.Sparse)
            return "Base Keyframe:\nThese are the default values all the other keyframes will use.";
        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in kf.TickedTechs)
        {
            SplitTechKey(k, out var _, out var ff);
            files.Add(string.IsNullOrEmpty(ff) ? k : ff);
        }
        foreach (var k in kf.TickedUniforms)
        {
            int sep = k.IndexOf('\0');
            files.Add(sep < 0 ? k : k.Substring(0, sep));
        }
        var lines = new List<string> { "Affected:" };
        lines.AddRange(files.Take(25));
        if (files.Count > 25) lines.Add($"+{files.Count - 25} more");
        return string.Join("\n", lines);
    }

    private void DrawConfigPanel()
    {
        ImGui.Text("Keyframes");
        ImGui.Separator();
        if (activeDynData == null)
        {
            ImGui.TextDisabled("No animation data yet.");
            return;
        }
        ImGui.PushFont(UiBuilder.IconFont);
        float kfInfoBox = ImGui.CalcTextSize(FontAwesomeIcon.InfoCircle.ToIconString()).X + 4;
        string kfInfoIcon = FontAwesomeIcon.InfoCircle.ToIconString();
        ImGui.PopFont();
        var cfg = activeDynData.GetOrCreatePrimary();
        foreach (var kf in cfg.Keyframes.OrderBy(k => k.TimeSeconds).ToList())
        {
            bool isSel = selKfId == kf.Id;
            bool kfWantMenu = false;
            string disp = string.IsNullOrEmpty(kf.Name)
                ? SecondsToTimeString(kf.TimeSeconds)
                : kf.Name;
            if (kf.IsPrimary) disp += " (Base)";
            bool primaryRow = kf.IsPrimary && kfRenameId != kf.Id;
            if (primaryRow) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.35f, 0.75f, 1f, 1f));
            if (kfRenameId == kf.Id)
            {
                if (kfRenameFocus) { ImGui.SetKeyboardFocusHere(); kfRenameFocus = false; }
                ImGui.PushID("kfren" + kf.Id);
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                if (ImGui.InputText("##kfn", ref kfRenameDraft, 64, ImGuiInputTextFlags.EnterReturnsTrue))
                {
                    var nm = kfRenameDraft.Trim();
                    if (!string.IsNullOrEmpty(nm))
                        kf.Name = UniqueKfName(cfg, nm);
                    kfRenameId = "";
                    SaveActiveDyn();
                }
                if (ImGui.IsKeyPressed(ImGuiKey.Escape)) kfRenameId = "";
                ImGui.PopID();
            }
            else
            {
                float rowRight = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
                ImGui.SetNextItemWidth(Math.Max(50, rowRight - ImGui.GetCursorPosX() - kfInfoBox));
                if (ImGui.Selectable(disp, isSel))
                {
                    if (isSel) { selKfConfig = ""; selKfId = ""; kfRenameId = ""; }
                    else SelectKeyframe(cfg.Name, kf.Id);
                }
                bool rowHov = ImGui.IsItemHovered();
                ImGui.SameLine();
                ImGui.SetCursorPosX(rowRight - kfInfoBox + 2);
                ImGui.PushFont(UiBuilder.IconFont);
                ImGui.Text(kfInfoIcon);
                ImGui.PopFont();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(KeyframeFxSummary(kf));
                if (rowHov && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) kfWantMenu = true;
            }
            if (primaryRow) ImGui.PopStyleColor();
            ImGui.PushID("kfctx" + kf.Id);
            if (kfWantMenu) ImGui.OpenPopup("##kfctx");
            if (ImGui.BeginPopup("##kfctx"))
            {
                if (ImGui.MenuItem("Rename..."))
                {
                    kfRenameId = kf.Id;
                    kfRenameDraft = string.IsNullOrEmpty(kf.Name) ? SecondsToTimeString(kf.TimeSeconds) : kf.Name;
                    kfRenameFocus = true;
                }
                if (ImGui.MenuItem("Delete"))
                {
                    cfg.Keyframes.Remove(kf);
                    if (selKfId == kf.Id) { selKfConfig = ""; selKfId = ""; }
                    if (kfRenameId == kf.Id) kfRenameId = "";
                    SaveActiveDyn();
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
            ImGui.PopID();
        }
        ImGui.Separator();
        if (ImGui.Button("+ Add Keyframe", new Vector2(ImGui.GetContentRegionAvail().X, 0)))
        {
            int t = plugin.GetEorzeaSecondsPublic();
            var sel = GetSelectedKeyframe();
            DynamicKeyframe snap;
            if (sel != null && !sel.IsPrimary)
            {
                // Copy the selected keyframe (ticks included, so the copy
                // survives PurgeUnticked). Fresh id/time/name, then select it.
                snap = new DynamicKeyframe { Sparse = true, TimeSeconds = t };
                snap.TechStates = new Dictionary<string, bool>(sel.TechStates, StringComparer.OrdinalIgnoreCase);
                snap.TickedTechs = sel.TickedTechs.ToList();
                snap.TickedUniforms = sel.TickedUniforms.ToList();
                snap.TechOrder = sel.TechOrder.ToList();
                foreach (var kvp in sel.Uniforms)
                {
                    var m = new Dictionary<string, DynamicUniformValue>(StringComparer.OrdinalIgnoreCase);
                    foreach (var u in kvp.Value)
                        m[u.Key] = new DynamicUniformValue { Value = u.Value.Value, BaseType = u.Value.BaseType };
                    snap.Uniforms[kvp.Key] = m;
                }
                snap.Name = UniqueKfName(cfg, string.IsNullOrEmpty(sel.Name) ? "Keyframe" : sel.Name);
            }
            else
            {
                snap = CaptureLiveKeyframe(t);
                snap.Name = UniqueKfName(cfg, "Keyframe");
                snap.IsPrimary = !cfg.Keyframes.Any(k => k.IsPrimary);
                snap.Sparse = !snap.IsPrimary;
            }
            cfg.Keyframes.Add(snap);
            cfg.Keyframes.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
            SelectKeyframe(cfg.Name, snap.Id);
            SaveActiveDyn();
            try { plugin.PrimeDynToggles(new Dictionary<string, bool>(effectEnabledState)); } catch { }
        }
    }

    private void MoveActiveToTop()
    {
        bool IsOn(string key) => effectEnabledState.TryGetValue(key, out var en) && en;
        var active = new List<string>();
        foreach (var e in techniqueSorting)
            if (IsOn(e)) active.Add(e);
        // Include enabled techniques not yet in the sorting (macro/runtime
        // rows render below; dragging them anywhere inserts them too).
        foreach (var file in GetEffectsInDisplayOrder())
        {
            foreach (var t in VisibleTechNamesFor(file))
            {
                var key = $"{t}@{file}";
                if (techniqueSorting.Any(e => TechKeyEquals(e, key))) continue;
                if (IsOn(key) && !active.Any(a => TechKeyEquals(a, key))) active.Add(key);
            }
        }
        if (active.Count == 0 || techniqueSorting.Count == 0) return;
        for (int i = active.Count - 1; i >= 0; i--)
            MoveTechnique(active[i], techniqueSorting[0]);
    }

    private void DrawEffectsListLeft()
    {
        ImGui.Text("Effects"); ImGui.SameLine();
        {
            float bw = ImGui.CalcTextSize("Active To Top").X + ImGui.GetStyle().FramePadding.X * 2;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - bw);
            if (ImGui.Button("Active To Top", new Vector2(bw, 0)))
                MoveActiveToTop();
        }
        ImGui.Separator();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputTextWithHint("##shaderSearch", "Search...", ref shaderSearch, 256);
        if (techParseQueue.Count > 0)
        {
            ImGui.TextDisabled($"Indexing shaders… {techParseQueue.Count} left (list fills in)");
        }
        ImGui.Separator();
        var searchLower = shaderSearch.ToLowerInvariant();
        var rows = new List<(string File, string Tech, string Key)>();
        var coveredKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 1. Preset order, one row per technique entry (mirrors ReShade,
        // where e.g. FFKeepUI is at the top and FFRestoreUI at the bottom).
        // Hidden techniques are never listed by ReShade, even when the
        // preset references them (static annotation fact, always safe).
        foreach (var tech in techniqueSorting)
        {
            var atIdx = tech.LastIndexOf('@');
            if (atIdx < 0) continue;
            var t = tech.Substring(0, atIdx);
            var rawFile = tech.Substring(atIdx + 1);
            if (!shaderFileSet.Contains(rawFile)) continue;
            var file = shaderFileCanonical.TryGetValue(rawFile, out var canon) ? canon : rawFile;
            if (effectHiddenTechniques.TryGetValue(file, out var hiddenNames)
                && hiddenNames.Contains(t, StringComparer.OrdinalIgnoreCase)) continue;
            if (t.IndexOf("placeholder", StringComparison.OrdinalIgnoreCase) >= 0 && placeholderRowLogged.Add($"{t}@{file}"))
            {
                try
                {
                    bool known = effectHiddenTechniques.TryGetValue(file, out var hn);
                    string allRels = shaderFileAllPaths.TryGetValue(file, out var rr) ? string.Join(",", rr) : "(no rels)";
                    int parsedCopies = 0;
                    try
                    {
                        var gameDir = GetGameDir();
                        var dir = Path.Combine(gameDir, "reshade-shaders", "Shaders");
                        foreach (var r in (rr ?? new List<string>()))
                            if (techParsedFiles.Contains(Path.Combine(dir, r.Replace('/', Path.DirectorySeparatorChar)))) parsedCopies++;
                    }
                    catch { }
                    Service.Log.Warning($"[ReshadeController:{DynamicCanvasWindow.BuildTag}] placeholder row leaked: {t}@{file} hiddenKnown={known} parsedCopies={parsedCopies} rels={allRels}");
                }
                catch { }
            }
            if (!coveredKeys.Add($"{t}@{file}")) continue;
            if (!RowMatchesSearch(t, file, tech, searchLower)) continue;
            rows.Add((file, t, tech));
        }
        // 2. Files not in the preset: visible names only (hidden-filtered).
        // Files with zero techniques stay hidden, like ReShade.
        foreach (var file in GetEffectsInDisplayOrder())
        {
            foreach (var t in VisibleTechNamesFor(file))
            {
                if (!coveredKeys.Add($"{t}@{file}")) continue;
                if (!RowMatchesSearch(t, file, $"{t}@{file}", searchLower)) continue;
                rows.Add((file, t, $"{t}@{file}"));
            }
        }
        // 3. Runtime-only files (on disk but unscanned, or casing drift).
        foreach (var file in runtimeTechniques.Keys.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (shaderFileSet.Contains(file)) continue;
            foreach (var t in runtimeTechniques[file])
            {
                if (!coveredKeys.Add($"{t}@{file}")) continue;
                if (!RowMatchesSearch(t, file, $"{t}@{file}", searchLower)) continue;
                rows.Add((file, t, $"{t}@{file}"));
            }
        }
        var shownRows = rows;
        if (showOnlyStarred)
            shownRows = rows.Where(r => IsFavoriteEffect(r.Key) || (effectEnabledState.TryGetValue(r.Key, out var se) && se)).ToList();
        int shownEnabled = shownRows.Count(r => effectEnabledState.TryGetValue(r.Key, out var e) && e);
        ImGui.TextDisabled($"{shownEnabled}/{shownRows.Count}");
        ImGui.SameLine();
        {
            const string btnLabel = "Only Stars";
            float bw = ImGui.CalcTextSize(btnLabel).X + ImGui.GetStyle().FramePadding.X * 2;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - bw);
            bool onlyStarsOn = showOnlyStarred;
            if (onlyStarsOn)
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.45f, 0.7f, 1f));
            if (ImGui.Button(btnLabel, new Vector2(bw, 0)))
                showOnlyStarred = !showOnlyStarred;
            if (onlyStarsOn)
                ImGui.PopStyleColor();
        }
        ImGui.Separator();

        if (ImGui.BeginChild("##effectScroll"))
        {
            bool shiftHeld = (GetAsyncKeyState(0x10) & 0x8000) != 0;
            string fxStar = FontAwesomeIcon.Star.ToIconString();
            string infoGlyph = FontAwesomeIcon.InfoCircle.ToIconString();
            ImGui.PushFont(UiBuilder.IconFont);
            Vector2 fxStarGlyph = ImGui.CalcTextSize(fxStar);
            Vector2 infoGlyphSize = ImGui.CalcTextSize(infoGlyph);
            ImGui.PopFont();
            float fxStarBox = MathF.Max(fxStarGlyph.X, fxStarGlyph.Y) + 2;
            uint infoDisU32;
            try { infoDisU32 = ImGui.ColorConvertFloat4ToU32(ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]); }
            catch { infoDisU32 = ImGui.ColorConvertFloat4ToU32(new Vector4(0.6f, 0.6f, 0.6f, 1f)); }
            float? textX = null;
            bool mouseDown = ImGui.IsMouseDown(ImGuiMouseButton.Left);
            bool dragging = draggedTechKey != null && mouseDown;
            var rowRects = new List<(string Key, Vector2 Min, Vector2 Max)>();
            float dragMinY = 0, dragMaxY = 0;
            bool haveDragRect = false;
            for (int i = 0; i < shownRows.Count; i++)
            {
                var (effectFile, techName, techKey) = shownRows[i];

                bool enabled = KfDisplayToggle(GetSelectedKeyframe(), techKey, effectEnabledState.TryGetValue(techKey, out var e) && e);

                var tickTarget = SparseEditTarget();
                bool techTicked = true;
                if (tickTarget != null)
                {
                    techTicked = tickTarget.TickedTechs.Any(k => string.Equals(k, techKey, StringComparison.OrdinalIgnoreCase));
                    bool ticked = techTicked;
                    bool wasTicked = ticked;
                    ImGui.PushStyleColor(ImGuiCol.CheckMark, new Vector4(0f, 0f, 0f, 0f));
                    if (wasTicked) ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.85f, 0.12f, 0.12f, 1f));
                    ImGui.PushID("tick" + techKey);
                    if (ImGui.Checkbox("##tick", ref ticked))
                    {
                        SetTechTick(tickTarget, techKey, ticked);
                        techTicked = ticked;
                    }
                    ImGui.PopID();
                    ImGui.PopStyleColor(wasTicked ? 2 : 1);
                    ImGui.SameLine();
                }
                if (tickTarget != null && !techTicked) enabled = PrimaryTechState(techKey);
                if (!techTicked) ImGui.BeginDisabled();
                ImGui.PushID(techKey);
                if (ImGui.Checkbox("##on", ref enabled))
                {
                    try
                    {
                        effectEnabledState[techKey] = enabled;
                        var selKfT = GetSelectedKeyframe();
                        if (selKfT != null) NoteManualToggle(techKey, enabled);
                        else if (IsEngineDriving()) NoteManualToggle(techKey, enabled);
                        if (!IsEngineDriving()) WriteToggleSignal(effectFile, techName, enabled);
                        // Disabling the FX forgets its driven settings.
                        if (!enabled && SparseEditTarget() is DynamicKeyframe st)
                        {
                            UntickFileUniforms(st, effectFile);
                            SaveActiveDyn();
                        }
                    }
                    catch (Exception ex)
                    {
                        Service.Log.Error(ex, "Toggle failed");
                    }
                }
                ImGui.PopID();
                if (!techTicked) ImGui.EndDisabled();
                ImGui.SameLine();
                if (textX == null) textX = ImGui.GetCursorPosX();
                else ImGui.SetCursorPosX(textX.Value);
                bool isFocused = string.Equals(focusedEffect, effectFile, StringComparison.OrdinalIgnoreCase);
                ImGui.PushStyleColor(ImGuiCol.Text, enabled ? new Vector4(0.4f, 1f, 0.4f, 1f) : new Vector4(0.5f, 0.5f, 0.5f, 1f));
                ImGui.PushID("focus" + techKey);
                string displayTech = GetNickname(techKey) ?? (string.IsNullOrEmpty(techName) ? effectFile : techName);
                string rowLabel = shiftHeld ? effectFile : displayTech;
                float textW = ImGui.CalcTextSize(rowLabel).X;
                bool editingNick = nicknameEditingKey != null && TechKeyEquals(nicknameEditingKey, techKey);
                ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0, 0));
                if (editingNick)
                {
                    // The name area becomes a text box: Enter commits,
                    // Escape cancels.
                    if (focusNickInput) { ImGui.SetKeyboardFocusHere(); focusNickInput = false; }
                    ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                    if (ImGui.InputText("##nickedit", ref nicknameDraft, 128, ImGuiInputTextFlags.EnterReturnsTrue))
                    {
                        SetNickname(techKey, nicknameDraft);
                        nicknameEditingKey = null;
                    }
                    if (ImGui.IsKeyPressed(ImGuiKey.Escape)) nicknameEditingKey = null;
                }
                else if (ImGui.Selectable(rowLabel + "##fxrow", isFocused))
                    SetFocus(isFocused ? null : effectFile);
                Vector2 selMin = ImGui.GetItemRectMin();
                Vector2 selMax = ImGui.GetItemRectMax();
                rowRects.Add((techKey, selMin, selMax));
                // Favorite star in its own right-hand column: rect math in
                // screen coords (DrawList/hover take screen space), glyph
                // only on hover (or when already starred).
                {
                    Vector2 sMin = new Vector2(selMax.X - fxStarBox, selMin.Y + MathF.Max(0, (selMax.Y - selMin.Y - fxStarBox) * 0.5f));
                    Vector2 sMax = sMin + new Vector2(fxStarBox, fxStarBox);
                    bool fav = IsFavoriteEffect(techKey);
                    bool rowHover = !dragging && ImGui.IsMouseHoveringRect(selMin, selMax);
                    bool shover = rowHover && ImGui.IsMouseHoveringRect(sMin, sMax);
                    if (rowHover || fav)
                    {
                        Vector2 gpos = sMin + (new Vector2(fxStarBox, fxStarBox) - fxStarGlyph) * 0.5f;
                        Vector4 col = fav ? new Vector4(1f, 0.84f, 0f, 1f) : new Vector4(0.45f, 0.45f, 0.45f, 1f);
                        if (shover) col = fav ? new Vector4(1f, 0.95f, 0.45f, 1f) : new Vector4(0.9f, 0.9f, 0.9f, 1f);
                        ImGui.GetWindowDrawList().AddText(UiBuilder.IconFont, ImGui.GetFontSize(), gpos, ImGui.ColorConvertFloat4ToU32(col), fxStar);
                    }
                    if (shover)
                    {
                        ImGui.SetTooltip(fav ? "Remove from favorites" : "Add to favorites");
                        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) starPressedKey = techKey;
                    }
                    if (starPressedKey != null && TechKeyEquals(starPressedKey, techKey)
                        && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                    {
                        if (shover) ToggleFavoriteEffect(techKey);
                        starPressedKey = null;
                    }
                }
                if (dragging && draggedTechKey != null && TechKeyEquals(draggedTechKey, techKey))
                {
                    dragMinY = selMin.Y;
                    dragMaxY = selMax.Y;
                    haveDragRect = true;
                }
                if (!editingNick && ImGui.BeginDragDropSource())
                {
                    draggedTechKey = techKey;
                    ImGui.SetDragDropPayload("FXTECH", System.ReadOnlySpan<byte>.Empty);
                    ImGui.Text(displayTech);
                    ImGui.EndDragDropSource();
                }
                if (!editingNick && ImGui.BeginPopupContextItem("##fxctx", ImGuiPopupFlags.MouseButtonRight))
                {
                    // Overall top/bottom of the full order, not the top of
                    // whatever the search filter currently shows.
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 1f));
                    if (ImGui.MenuItem("Move to Top"))
                        MoveTechnique(techKey, techniqueSorting.FirstOrDefault());
                    if (ImGui.MenuItem("Move to Bottom"))
                        MoveTechnique(techKey, null);
                    if (ImGui.MenuItem("Reset to defaults"))
                        ResetFxToDefaults(effectFile);
                    string? curNick = GetNickname(techKey);
                    if (curNick != null)
                    {
                        if (ImGui.Selectable("##xNick", false))
                        {
                            SetNickname(techKey, "");
                            ImGui.CloseCurrentPopup();
                        }
                        Vector2 rmin = ImGui.GetItemRectMin();
                        Vector2 rsize = ImGui.GetItemRectSize();
                        float pad = ImGui.GetStyle().FramePadding.X;
                        float fs = ImGui.GetFontSize();
                        float tyN = rmin.Y + MathF.Max(0, (rsize.Y - fs) * 0.5f);
                        string xGlyph = FontAwesomeIcon.Times.ToIconString();
                        float iconSize = fs * 0.8f;
                        ImGui.PushFont(UiBuilder.IconFont);
                        float xAdv = ImGui.CalcTextSize(xGlyph).X * 0.8f;
                        ImGui.PopFont();
                        float tyI = tyN + (fs - iconSize) * 0.5f;
                        var dl = ImGui.GetWindowDrawList();
                        dl.AddText(UiBuilder.IconFont, iconSize, new Vector2(rmin.X + pad, tyI), ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.3f, 0.3f, 1f)), xGlyph);
                        Vector4 tcol;
                        try { tcol = ImGui.GetStyle().Colors[(int)ImGuiCol.Text]; }
                        catch { tcol = new Vector4(1f, 1f, 1f, 1f); }
                        dl.AddText(new Vector2(rmin.X + pad + xAdv + 2, tyN), ImGui.ColorConvertFloat4ToU32(tcol), "Nickname");
                    }
                    else if (ImGui.MenuItem("Nickname..."))
                    {
                        nicknameEditingKey = techKey;
                        nicknameDraft = techName;
                        focusNickInput = true;
                    }
                    ImGui.PopStyleColor();
                    ImGui.EndPopup();
                }
                ImGui.PopStyleVar();
                ImGui.PopID();
                ImGui.PopStyleColor();
                // Info icon overlaid right after the text (selectable screen
                // rect: the full-width selectable underneath still gets all
                // clicks).
                if (!string.IsNullOrEmpty(techName)
                    && effectTechTips.TryGetValue($"{techName}@{effectFile}", out var tip)
                    && !string.IsNullOrWhiteSpace(tip))
                {
                    float padX = ImGui.GetStyle().FramePadding.X;
                    Vector2 iMin = new Vector2(selMin.X + padX + textW + 4, selMin.Y + MathF.Max(0, (selMax.Y - selMin.Y - infoGlyphSize.Y) * 0.5f));
                    Vector2 iMax = iMin + infoGlyphSize;
                    ImGui.GetWindowDrawList().AddText(UiBuilder.IconFont, ImGui.GetFontSize(), iMin, infoDisU32, infoGlyph);
                    bool iHover = !dragging && ImGui.IsMouseHoveringRect(iMin, iMax);
                    if (iHover)
                    {
                        ImGui.BeginTooltip();
                        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35.0f);
                        ImGui.TextUnformatted(tip);
                        ImGui.PopTextWrapPos();
                        ImGui.EndTooltip();
                        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) iconPressedKey = techKey;
                    }
                    if (iconPressedKey != null && TechKeyEquals(iconPressedKey, techKey)
                        && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                    {
                        if (iHover) SetFocus(isFocused ? null : effectFile);
                        iconPressedKey = null;
                    }
                }
            }
            // Live reorder from mouse Y: no hover/target/payload-read APIs
            // (all proven flaky in this binding) — just row rectangles.
            // Moves only fire while the mouse itself moves: that kills
            // boundary jitter and auto-scroll feedback loops, where rows
            // would otherwise oscillate under a stationary cursor.
            if (dragging && draggedTechKey != null && rowRects.Count > 0)
            {
                Vector2 mousePos = ImGui.GetMousePos();
                bool mouseMoved = (mousePos - _lastMousePos).LengthSquared() > 0.25f;
                _lastMousePos = mousePos;
                float my = mousePos.Y;
                string? beforeKey = null;
                float lineY = rowRects[rowRects.Count - 1].Max.Y;
                foreach (var r in rowRects)
                {
                    if (my < r.Max.Y) { beforeKey = r.Key; lineY = r.Min.Y; break; }
                }
                float x0 = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMin().X;
                float x1 = x0 + ImGui.GetContentRegionAvail().X;
                ImGui.GetWindowDrawList().AddLine(
                    new Vector2(x0, lineY),
                    new Vector2(x1, lineY),
                    0xFF60FF60, 2.0f);
                if (haveDragRect)
                {
                    ImGui.GetWindowDrawList().AddLine(new Vector2(x0, dragMinY), new Vector2(x1, dragMinY), 0xFF00D7FF, 1.0f);
                    ImGui.GetWindowDrawList().AddLine(new Vector2(x0, dragMaxY), new Vector2(x1, dragMaxY), 0xFF00D7FF, 1.0f);
                }
                if (mouseMoved)
                {
                    if (beforeKey == null)
                        MoveTechnique(draggedTechKey, null);
                    else if (!TechKeyEquals(draggedTechKey, beforeKey))
                        MoveTechnique(draggedTechKey, beforeKey);
                }
            }
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                draggedTechKey = null;
                iconPressedKey = null;
                starPressedKey = null;
            }

        }
        ImGui.EndChild();
    }

    // Visible technique names for a file. The runtime list is ground truth
    // once available: static parsing cannot see macro branches, inactive
    // #ifs, or same-name collisions ReShade resolves by dropping all but one
    // file (e.g. CRT-Royale\crt-royale.fx loses to the root placeholder
    // copy). Static parse is only a fallback before the first runtime sync.
    private List<string> VisibleTechNamesFor(string file)
    {
        if (runtimeTechniques.Count > 0)
            return runtimeTechniques.TryGetValue(file, out var rt) ? rt : new List<string>();
        if (effectTechniques.TryGetValue(file, out var st)) return st;
        return new List<string>();
    }

    // Preset order first (techniqueSorting), then the rest alphabetically —
    // the same order the left effects list uses.
    private List<string> GetEffectsInDisplayOrder()
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tech in techniqueSorting)
        {
            var atIdx = tech.LastIndexOf('@');
            if (atIdx < 0) continue;
            var ef = tech.Substring(atIdx + 1);
            if (!shaderFileSet.Contains(ef)) continue;
            var canon = shaderFileCanonical.TryGetValue(ef, out var c) ? c : ef;
            if (!seen.Add(canon)) continue;
            ordered.Add(canon);
        }
        foreach (var shader in shaderFiles)
        {
            if (!seen.Add(shader)) continue;
            ordered.Add(shader);
        }
        return ordered;
    }

    private void DrawSettingsRight()
    {
        var searchLower = settingsSearch.ToLowerInvariant();

        // File -> enabled technique names, derived from per-technique state.
        var enabledTechsByFile = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in effectEnabledState)
        {
            if (!kvp.Value) continue;
            var atIdx = kvp.Key.LastIndexOf('@');
            string file, tech;
            if (atIdx >= 0) { tech = kvp.Key.Substring(0, atIdx); file = kvp.Key.Substring(atIdx + 1); }
            else { tech = ""; file = kvp.Key; }
            if (!enabledTechsByFile.TryGetValue(file, out var list))
                enabledTechsByFile[file] = list = new List<string>();
            if (!string.IsNullOrEmpty(tech) && !list.Contains(tech))
                list.Add(tech);
        }

        // Files touched by the selected keyframe stay visible even when live-off,
        // otherwise ticked-off FX can't show (or grow) their settings.
        var kfFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        {
            var skf = GetSelectedKeyframe();
            if (skf != null)
            {
                if (skf.IsPrimary || !skf.Sparse)
                {
                    foreach (var kvp in skf.TechStates)
                    {
                        if (!kvp.Value) continue;
                        SplitTechKey(kvp.Key, out var _, out var ff);
                        kfFiles.Add(string.IsNullOrEmpty(ff) ? kvp.Key : ff);
                    }
                }
                else
                {
                    foreach (var k in skf.TickedTechs)
                    {
                        SplitTechKey(k, out var _, out var ff);
                        kfFiles.Add(string.IsNullOrEmpty(ff) ? k : ff);
                    }
                    foreach (var k in skf.TickedUniforms)
                    {
                        int sep = k.IndexOf('\0');
                        kfFiles.Add(sep < 0 ? k : k.Substring(0, sep));
                    }
                }
            }
        }
        var sparseVis = SparseEditTarget();
        var displayedFiles = new List<string>();
        foreach (var effectName in GetEffectsInDisplayOrder())
        {
            if (!enabledTechsByFile.TryGetValue(effectName, out var onTechs)) onTechs = new List<string>();
            var visibleNames = VisibleTechNamesFor(effectName);
            if (sparseVis != null)
            {
                if (!kfFiles.Contains(effectName)) continue;
            }
            else if (!onTechs.Any(t => visibleNames.Contains(t, StringComparer.OrdinalIgnoreCase))) continue;
                if (focusedEffect != null && !string.Equals(effectName, focusedEffect, StringComparison.OrdinalIgnoreCase)) continue;
                if (!FileMatchesSearch(effectName, searchLower)) continue;
                displayedFiles.Add(effectName);
        }

        ImGui.Text("Settings");
        if (focusedEffect == null)
        {
            ImGui.SameLine();
            bool allCollapsed = displayedFiles.Count > 0 && displayedFiles.All(f => !expandedEffects.Contains(f));
            string btnLabel = allCollapsed ? "Expand All" : "Collapse All";
            float bw = ImGui.CalcTextSize(btnLabel).X + ImGui.GetStyle().FramePadding.X * 2;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - bw);
            if (displayedFiles.Count > 0 && ImGui.Button(btnLabel, new Vector2(bw, 0)))
            {
                if (allCollapsed)
                {
                    foreach (var f in displayedFiles) expandedEffects.Add(f);
                    foreach (var f in displayedFiles) pendingExpand.Add(f);
                }
                else
                {
                    foreach (var f in displayedFiles) expandedEffects.Remove(f);
                    foreach (var f in displayedFiles) pendingCollapse.Add(f);
                }
            }
        }
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputTextWithHint("##settingsSearch", "Search effects...", ref settingsSearch, 256);
        if (focusedEffect != null)
        {
            ImGui.Text($"Showing only: {focusedEffect}"); ImGui.SameLine();
            if (ImGui.SmallButton("Show all")) SetFocus(null);
        }
        ImGui.Separator();

        if (ImGui.BeginChild("##settingsScroll"))
        {
            bool anyEnabled = displayedFiles.Count > 0;
            searchLower = settingsSearch.ToLowerInvariant();

            foreach (var effectName in displayedFiles)
            {
                enabledTechsByFile.TryGetValue(effectName, out var onTechs);
                onTechs ??= new List<string>();
                // Mirror the left list: files with no VISIBLE enabled
                // techniques (e.g. only hidden placeholders on) stay hidden.
                var visibleNames = VisibleTechNamesFor(effectName);
                var visibleOn = onTechs.Where(t => visibleNames.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
                if (visibleOn.Count == 0) continue;
                anyEnabled = true;

                // Parse .fx file on demand
                if (!fxParsed.ContainsKey(effectName))
                    ParseFxFile(effectName);

                string headerLabel = effectName;
                if (visibleNames.Count > 1 && visibleOn.Count > 0)
                    headerLabel = $"{effectName} [{string.Join(", ", visibleOn)}]";
                bool isExpanded = expandedEffects.Contains(effectName);
                if (pendingExpand.Contains(effectName))
                {
                    ImGui.SetNextItemOpen(true);
                    pendingExpand.Remove(effectName);
                }
                else if (pendingCollapse.Contains(effectName))
                {
                    ImGui.SetNextItemOpen(false);
                    pendingCollapse.Remove(effectName);
                }
                bool fxTicked = false;
                var stFx = SparseEditTarget();
                if (stFx != null)
                {
                    string up = effectName + "\0";
                    fxTicked = stFx.TickedUniforms.Any(k => k.StartsWith(up, StringComparison.OrdinalIgnoreCase));
                    if (!fxTicked)
                    {
                        foreach (var k in stFx.TickedTechs)
                        {
                            SplitTechKey(k, out var _, out var tf);
                            if (string.Equals(tf, effectName, StringComparison.OrdinalIgnoreCase)) { fxTicked = true; break; }
                        }
                    }
                }
                if (fxTicked) ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.15f, 0.5f, 0.2f, 0.9f));
                bool fxOpen = ImGui.CollapsingHeader($"{headerLabel}##settings", isExpanded ? ImGuiTreeNodeFlags.DefaultOpen : 0);
                if (fxTicked) ImGui.PopStyleColor();
                if (ImGui.BeginPopupContextItem($"##fxreset_{effectName}"))
                {
                    if (ImGui.MenuItem("Reset to defaults"))
                        ResetFxToDefaults(effectName);
                    ImGui.EndPopup();
                }
                if (fxOpen)
                {
                    expandedEffects.Add(effectName);

                    var settings = effectSettings.TryGetValue(effectName, out var s) ? s : new();
                    var uniforms = effectUniforms.TryGetValue(effectName, out var u) ? u : new();

                    if (uniforms.Count > 0)
                    {
                        // Group by category
                        var categories = new Dictionary<string, List<ShaderUniformInfo>>();
                        var catOrder = new List<string>();
                        foreach (var uni in uniforms)
                        {
                            var cat = string.IsNullOrEmpty(uni.Category) ? "" : uni.Category;
                            if (!categories.ContainsKey(cat))
                            {
                                categories[cat] = new List<ShaderUniformInfo>();
                                catOrder.Add(cat);
                            }
                            categories[cat].Add(uni);
                        }

                        foreach (var cat in catOrder)
                        {
                            var catUniforms = categories[cat];

                            if (string.IsNullOrEmpty(cat))
                            {
                                foreach (var uni in catUniforms)
                                    DrawUniformWithSave(effectName, settings, uni);
                            }
                            else
                            {
                                string catId = $"##cat_{effectName}_{cat}";
                                ImGui.Indent(20f);

                                // Center the text by padding with spaces
                                var avail = ImGui.GetContentRegionAvail().X;
                                var textWidth = ImGui.CalcTextSize(cat).X;
                                var spaceWidth = ImGui.CalcTextSize(" ").X;
                                int padCount = Math.Max(0, (int)((avail - textWidth) / spaceWidth / 2));
                                var padded = new string(' ', padCount) + cat;

                                ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0, 0, 0, 0));
                                ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.15f, 0.15f, 0.2f, 0.4f));
                                ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0.2f, 0.2f, 0.3f, 0.4f));
                                bool catClosed = catUniforms.Any(u => u.UiCategoryClosed);
                                if (ImGui.CollapsingHeader($"{padded}##{catId}", catClosed ? ImGuiTreeNodeFlags.None : ImGuiTreeNodeFlags.DefaultOpen))
                                {
                                    ImGui.Indent(20f);
                                    foreach (var uni in catUniforms)
                                        DrawUniformWithSave(effectName, settings, uni);
                                    ImGui.Unindent(20f);
                                }
                                ImGui.PopStyleColor(3);
                                ImGui.Unindent(20f);
                            }
                        }
                    }
                    else if (settings.Count > 0)
                    {
                        var uniLookup = new Dictionary<string, ShaderUniformInfo>(StringComparer.OrdinalIgnoreCase);
                        if (effectUniforms.TryGetValue(effectName, out var ulist))
                            foreach (var un in ulist)
                                if (!uniLookup.ContainsKey(un.Name)) uniLookup[un.Name] = un;
                        foreach (var kvp in settings.ToList())
                        {
                            var tickTargetR = SparseEditTarget();
                            bool rTicked = true;
                            if (tickTargetR != null)
                            {
                                string rkey = effectName + "\0" + kvp.Key;
                                rTicked = tickTargetR.TickedUniforms.Any(k => string.Equals(k, rkey, StringComparison.OrdinalIgnoreCase));
                                bool rticked = rTicked;
                                bool wasRTicked = rticked;
                                ImGui.PushStyleColor(ImGuiCol.CheckMark, new Vector4(0f, 0f, 0f, 0f));
                                if (wasRTicked) ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.85f, 0.12f, 0.12f, 1f));
                                ImGui.PushID("tickr" + rkey);
                                if (ImGui.Checkbox("##tickr", ref rticked))
                                {
                                    SetUniformTick(tickTargetR, effectName, kvp.Key, uniLookup.TryGetValue(kvp.Key, out var rui) ? rui.BaseType : "float", rticked);
                                    rTicked = rticked;
                                }
                                ImGui.PopID();
                                ImGui.PopStyleColor(wasRTicked ? 2 : 1);
                                ImGui.SameLine();
                            }
                            string rawDef = uniLookup.TryGetValue(kvp.Key, out var rawUi) ? rawUi.DefaultValue : "";
                            string rawType = uniLookup.TryGetValue(kvp.Key, out var rawUiT) ? rawUiT.UiType : "input";
                            // Reset slot is always reserved but invisible at
                            // default (same button, zero alpha) so rows never
                            // shift when it appears.
                            bool hasDefR = !string.IsNullOrEmpty(rawDef);
                            bool dirtyR = hasDefR && !IsDefaultUniformValue(kvp.Value, rawDef, rawType);
                            if (hasDefR)
                            {
                                ImGui.PushFont(UiBuilder.IconFont);
                                if (!dirtyR)
                                {
                                    ImGui.BeginDisabled();
                                    var clear = new Vector4(0f, 0f, 0f, 0f);
                                    ImGui.PushStyleColor(ImGuiCol.Button, clear);
                                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, clear);
                                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, clear);
                                    ImGui.PushStyleColor(ImGuiCol.Text, clear);
                                }
                                bool rawReset = ImGui.SmallButton(FontAwesomeIcon.Undo.ToIconString() + "##resetr_" + effectName + "_" + kvp.Key);
                                if (!dirtyR)
                                {
                                    ImGui.PopStyleColor(4);
                                    ImGui.EndDisabled();
                                }
                                ImGui.PopFont();
                                if (dirtyR && ImGui.IsItemHovered()) ImGui.SetTooltip($"Reset to default ({rawDef})");
                                if (dirtyR && rawReset)
                                {
                                    string rawBase = uniLookup.TryGetValue(kvp.Key, out var rawUi2) ? rawUi2.BaseType : "float";
                                    CommitUniformValue(effectName, kvp.Key, rawBase, rawDef);
                                }
                                ImGui.SameLine();
                            }
                            ImGui.Text(kvp.Key); ImGui.SameLine();
                            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                            // Re-read: a reset press above just rewrote this key.
                            var val = settings.TryGetValue(kvp.Key, out var freshVal) ? freshVal : kvp.Value;
                            if (tickTargetR != null && !rTicked) val = PrimaryUniformValue(effectName, kvp.Key) ?? val;
                            else val = KfDisplayUniform(GetSelectedKeyframe(), effectName, kvp.Key, val);
                            if (!rTicked) ImGui.BeginDisabled();
                            bool rawDone = ImGui.InputText($"##raw_{effectName}_{kvp.Key}", ref val, 256);
                            if (!rTicked) ImGui.EndDisabled();
                            if (rawDone)
                            {
                                settings[kvp.Key] = val;
                                var baseType = uniLookup.TryGetValue(kvp.Key, out var ui) ? ui.BaseType : "float";
                                var selKfR = GetSelectedKeyframe();
                                if (selKfR != null) NoteManualUniform(effectName, kvp.Key, baseType, val);
                                else if (IsEngineDriving()) NoteManualUniform(effectName, kvp.Key, baseType, val);
                                if (!IsEngineDriving()) MarkDirtyUniform(effectName, kvp.Key, baseType, val);
                            }
                        }
                    }
                    else
                    {
                        ImGui.TextDisabled("No editable settings");
                    }
                }
                else expandedEffects.Remove(effectName);
            }

            if (!anyEnabled)
            {
                ImGui.Spacing();
                if (focusedEffect != null)
                    ImGui.TextDisabled($"Enable {focusedEffect} to see its settings.");
                else
                    ImGui.TextDisabled("No effects enabled. Toggle effects on the left.");
            }
        }
        ImGui.EndChild();
        FlushPendingSave();
    }

    // Coalesced by effect+uniform: slider drags update the same key at 60fps,
    // only the latest value per uniform is sent. Flushed every frame (no
    // debounce): each flush is one tiny file write, and the dict means an
    // idle UI performs zero I/O.
    private Dictionary<string, (string Effect, string Uniform, string BaseType, string Value)> _pendingUniforms = new();

    private void MarkDirtyUniform(string effectName, string uniformName, string baseType, string value)
    {
        _pendingUniforms[effectName + "\0" + uniformName] = (effectName, uniformName, baseType, value);
    }

    private readonly List<string> _pendingCmdLines = new();

    private void FlushPendingSave()
    {
        var cmds = new List<string>();
        foreach (var u in _pendingUniforms.Values)
            cmds.Add($"SET_UNIFORM|{u.Effect}|{u.Uniform}|{u.BaseType}|{u.Value}");
        _pendingUniforms.Clear();
        cmds.AddRange(_pendingCmdLines);
        _pendingCmdLines.Clear();
        if (cmds.Count == 0) return;
        try
        {
            var cmdFile = Path.Combine(
                Path.GetDirectoryName(Environment.ProcessPath) ?? "",
                "ffxiv_reshade_cmd.txt");
            AtomicWriteText(cmdFile, string.Join("\n", cmds) + "\n");
        }
        catch { }
    }

    // Default comparison tolerant of formatting ("0.5" vs "0.5000"),
    // bool words ("true" vs "1", same as the checkbox control), color
    // scale (preset 0-255 vs file 0-1, either side), and float3(...) wraps.
    private static bool IsDefaultUniformValue(string current, string def, string uiType)
    {
        if (string.Equals(current?.Trim(), def?.Trim(), StringComparison.Ordinal)) return true;
        if (string.IsNullOrEmpty(current) || string.IsNullOrEmpty(def)) return false;
        string StripWrap(string s)
        {
            var m = System.Text.RegularExpressions.Regex.Match(s ?? "", @"\(([^)]+)\)");
            return m.Success ? m.Groups[1].Value : (s ?? "");
        }
        if (uiType == "checkbox" || uiType == "toggle")
        {
            if (TryParseBool(StripWrap(current).Trim(), out var ba) && TryParseBool(StripWrap(def).Trim(), out var bb))
                return ba == bb;
            return false;
        }
        var a = StripWrap(current).Split(',');
        var b = StripWrap(def).Split(',');
        // RGB vs RGBA with opaque alpha counts as equal.
        if (a.Length == 3 && b.Length == 4) a = new[] { a[0], a[1], a[2], "1" };
        else if (a.Length == 4 && b.Length == 3) b = new[] { b[0], b[1], b[2], "1" };
        if (a.Length != b.Length) return false;
        var fa = new float[a.Length];
        var fb = new float[b.Length];
        for (int i = 0; i < a.Length; i++)
        {
            if (!float.TryParse(a[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out fa[i])
                || !float.TryParse(b[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out fb[i]))
                return false;
        }
        float eps = 1e-4f;
        if (uiType == "color")
        {
            // Normalize whichever side is in 0-255. Threshold 2 keeps genuine
            // HDR (>1, 0-1 scale) comparable; epsilon covers /255 rounding.
            float maxA = 0, maxB = 0;
            foreach (var v in fa) maxA = Math.Max(maxA, Math.Abs(v));
            foreach (var v in fb) maxB = Math.Max(maxB, Math.Abs(v));
            if (maxA > 2f) for (int i = 0; i < fa.Length; i++) fa[i] /= 255f;
            if (maxB > 2f) for (int i = 0; i < fb.Length; i++) fb[i] /= 255f;
            eps = 0.01f;
        }
        for (int i = 0; i < fa.Length; i++)
            if (Math.Abs(fa[i] - fb[i]) > eps) return false;
        return true;
    }

    private static bool TryParseBool(string s, out bool v)
    {
        if (s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) { v = true; return true; }
        if (s == "0" || string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) { v = false; return true; }
        if (float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fv)) { v = fv > 0.5f; return true; }
        v = false;
        return false;
    }

    // Single write path for an out-of-band uniform edit (resets): panel
    // model + keyframe override/sidecar + live signal, exactly like typing
    // the value in by hand.
    private void CommitUniformValue(string effectName, string uniName, string baseType, string value)
    {
        if (!effectSettings.ContainsKey(effectName))
            effectSettings[effectName] = new();
        effectSettings[effectName][uniName] = value;
        var selKf = GetSelectedKeyframe();
        if (selKf != null) NoteManualUniform(effectName, uniName, baseType, value);
        else if (IsEngineDriving()) NoteManualUniform(effectName, uniName, baseType, value);
        if (!IsEngineDriving()) MarkDirtyUniform(effectName, uniName, baseType, value);
    }

    // Reset every known setting of one FX file to its .fx default.
    private void ResetFxToDefaults(string effectName)
    {
        if (!effectUniforms.TryGetValue(effectName, out var list)) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var uni in list)
        {
            if (!seen.Add(uni.Name)) continue;
            if (string.IsNullOrEmpty(uni.DefaultValue)) continue;
            CommitUniformValue(effectName, uni.Name, uni.BaseType, uni.DefaultValue);
        }
    }

    private void DrawUniformWithSave(string effectName, Dictionary<string, string> settings, ShaderUniformInfo uni)
    {
        var tickTargetU = SparseEditTarget();
        bool uTicked = true;
        if (tickTargetU != null)
        {
            string ukey = effectName + "\0" + uni.Name;
            uTicked = tickTargetU.TickedUniforms.Any(k => string.Equals(k, ukey, StringComparison.OrdinalIgnoreCase));
            bool uticked = uTicked;
            bool wasUTicked = uticked;
            ImGui.PushStyleColor(ImGuiCol.CheckMark, new Vector4(0f, 0f, 0f, 0f));
            if (wasUTicked) ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.85f, 0.12f, 0.12f, 1f));
            ImGui.PushID("ticku" + ukey);
            if (ImGui.Checkbox("##ticku", ref uticked))
            {
                SetUniformTick(tickTargetU, effectName, uni.Name, uni.BaseType, uticked);
                uTicked = uticked;
            }
            ImGui.PopID();
            ImGui.PopStyleColor(wasUTicked ? 2 : 1);
            ImGui.SameLine();
        }
        var value = settings.TryGetValue(uni.Name, out var v) ? v : uni.DefaultValue;
        bool showPrimaryU = tickTargetU != null && !uTicked;
        if (showPrimaryU) value = PrimaryUniformValue(effectName, uni.Name) ?? value;
        else value = KfDisplayUniform(GetSelectedKeyframe(), effectName, uni.Name, value);
        string shownValue = value;
        if (!uTicked) ImGui.BeginDisabled();
        // Per-setting reset slot is always reserved but invisible at default
        // (same button, zero alpha) so the control never shifts on appear.
        bool hasDefU = !string.IsNullOrEmpty(uni.DefaultValue);
        bool dirtyU = hasDefU && !IsDefaultUniformValue(value, uni.DefaultValue, uni.UiType);
        if (hasDefU)
        {
            ImGui.PushFont(UiBuilder.IconFont);
            if (!dirtyU)
            {
                ImGui.BeginDisabled();
                var clearU = new Vector4(0f, 0f, 0f, 0f);
                ImGui.PushStyleColor(ImGuiCol.Button, clearU);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, clearU);
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, clearU);
                ImGui.PushStyleColor(ImGuiCol.Text, clearU);
            }
            bool resetU = ImGui.SmallButton(FontAwesomeIcon.Undo.ToIconString() + "##resetu_" + effectName + "_" + uni.Name);
            if (!dirtyU)
            {
                ImGui.PopStyleColor(4);
                ImGui.EndDisabled();
            }
            ImGui.PopFont();
            if (dirtyU && ImGui.IsItemHovered()) ImGui.SetTooltip($"Reset to default ({uni.DefaultValue})");
            if (dirtyU && resetU) value = uni.DefaultValue;
            ImGui.SameLine();
        }
        DrawUniformControl(effectName, uni, ref value);
        if (!uTicked) ImGui.EndDisabled();
        if (value != shownValue)
        {
            if (!effectSettings.ContainsKey(effectName))
                effectSettings[effectName] = new();
            effectSettings[effectName][uni.Name] = value;
            var selKfU = GetSelectedKeyframe();
            if (selKfU != null) NoteManualUniform(effectName, uni.Name, uni.BaseType, value);
            else if (IsEngineDriving()) NoteManualUniform(effectName, uni.Name, uni.BaseType, value);
            if (!IsEngineDriving()) MarkDirtyUniform(effectName, uni.Name, uni.BaseType, value);
        }
        ImGui.Separator();
    }

    private float _uniformWidthOverride;

    private float UniformAvailW() => _uniformWidthOverride > 0 ? _uniformWidthOverride : ImGui.GetContentRegionAvail().X;

    public bool TryGetUniformInfo(string effectFile, string uniName, out ShaderUniformInfo info)
    {
        info = null!;
        ParseFxFile(effectFile);
        if (effectUniforms.TryGetValue(effectFile, out var list))
            foreach (var u in list)
                if (string.Equals(u.Name, uniName, StringComparison.OrdinalIgnoreCase)) { info = u; return true; }
        return false;
    }

    private List<(uint Id, string Name)>? actionCache;

    public List<(uint Id, string Name)> GetActionList()
    {
        if (actionCache != null) return actionCache;
        var list = new List<(uint, string)>();
        try
        {
            var sheet = Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
            if (sheet != null)
            {
                foreach (var row in sheet)
                {
                    string name = row.Name.ToString();
                    if (!string.IsNullOrWhiteSpace(name))
                        list.Add((row.RowId, name));
                }
                list.Sort((a, b) => string.Compare(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch { }
        actionCache = list;
        return list;
    }

    private List<(uint Id, string Name)>? statusCache;

    public List<(uint Id, string Name)> GetStatusList()
    {
        if (statusCache != null) return statusCache;
        var list = new List<(uint, string)>();
        try
        {
            var sheet = Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>();
            if (sheet != null)
            {
                foreach (var row in sheet)
                {
                    string name = row.Name.ToString();
                    if (!string.IsNullOrWhiteSpace(name))
                        list.Add((row.RowId, name));
                }
                list.Sort((a, b) => string.Compare(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch { }
        statusCache = list;
        return list;
    }

    private List<(uint Id, string Name)>? emoteCache;
    private List<(uint Id, string Name)>? weatherCache;

    public List<(uint Id, string Name)> GetEmoteList()
    {
        if (emoteCache != null) return emoteCache;
        var list = new List<(uint, string)>();
        try
        {
            var sheet = Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
            if (sheet != null)
            {
                foreach (var row in sheet)
                {
                    string name = row.Name.ToString();
                    if (!string.IsNullOrWhiteSpace(name))
                        list.Add((row.RowId, name));
                }
                list.Sort((a, b) => string.Compare(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch { }
        emoteCache = list;
        return list;
    }

    public List<(uint Id, string Name)> GetWeatherList()
    {
        if (weatherCache != null) return weatherCache;
        var list = new List<(uint, string)>();
        try
        {
            var sheet = Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Weather>();
            if (sheet != null)
            {
                foreach (var row in sheet)
                {
                    string name = row.Name.ToString();
                    if (!string.IsNullOrWhiteSpace(name) && row.RowId > 0)
                        list.Add((row.RowId, name));
                }
                list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            }
        }
        catch { }
        weatherCache = list;
        return list;
    }

    public void DrawUniformControlSized(string effectName, ShaderUniformInfo uni, ref string currentValue, float w)
    {
        float prev = _uniformWidthOverride;
        _uniformWidthOverride = w;
        try { DrawUniformControl(effectName, uni, ref currentValue); }
        finally { _uniformWidthOverride = prev; }
    }

    private void DrawUniformControl(string effectName, ShaderUniformInfo uni, ref string currentValue)
    {
        var label = string.IsNullOrEmpty(uni.Label) ? uni.Name : uni.Label;
        if (!string.IsNullOrEmpty(uni.Tooltip) && ImGui.IsItemHovered()) ImGui.SetTooltip(uni.Tooltip);

        ImGui.Text($"{label}:"); ImGui.SameLine();
        ImGui.SetNextItemWidth(UniformAvailW());

        switch (uni.UiType)
        {
            case "slider":
                DrawSliderControl(effectName, uni, ref currentValue);
                break;
            case "combo":
                DrawComboControl(effectName, uni, ref currentValue);
                break;
            case "checkbox":
            case "toggle":
                DrawCheckboxControl(effectName, uni, ref currentValue);
                break;
            case "radio":
                DrawRadioControl(effectName, uni, ref currentValue);
                break;
            case "color":
                DrawColorControl(effectName, uni, ref currentValue);
                break;
            default: // "input" or unknown
                ImGui.InputText($"##{effectName}_{uni.Name}", ref currentValue, 256);
                break;
        }
    }

    private void DrawSliderControl(string effectName, ShaderUniformInfo uni, ref string currentValue)
    {
        // Determine component count from type (float2 → 2, float3 → 3, etc.)
        int components = 1;
        if (uni.BaseType.EndsWith("2")) components = 2;
        else if (uni.BaseType.EndsWith("3")) components = 3;
        else if (uni.BaseType.EndsWith("4")) components = 4;

        if (components > 1)
        {
            // Integer vectors get integer sliders (floats would write 600.0
            // into int uniforms); float vectors keep float sliders.
            if (uni.BaseType.StartsWith("int") || uni.BaseType.StartsWith("uint"))
            {
                var parts = currentValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var ivals = new int[components];
                for (int i = 0; i < components; i++)
                    int.TryParse(i < parts.Length ? parts[i] : "0", out ivals[i]);
                bool ichanged = false;
                string[] ilabels = { "X", "Y", "Z", "W" };
                float isliderWidth = (UniformAvailW() - 30) / components;
                for (int i = 0; i < components; i++)
                {
                    if (i > 0) ImGui.SameLine();
                    int val = ivals[i];
                    ImGui.PushID($"{effectName}_{uni.Name}_{i}");
                    ImGui.SetNextItemWidth(isliderWidth);
                    if (ImGui.SliderInt($"##{ilabels[i]}", ref val, (int)uni.UiMin, (int)uni.UiMax))
                    { ivals[i] = val; ichanged = true; }
                    ImGui.PopID();
                }
                if (ichanged)
                    currentValue = string.Join(",", ivals.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                return;
            }
            var vals = ParseVectorValue(currentValue, components);
            float step = uni.UiStep > 0 ? uni.UiStep : 0.001f;
            bool changed = false;
            string[] labels = { "X", "Y", "Z", "W" };

            float sliderWidth = (UniformAvailW() - 30) / components;

            for (int i = 0; i < components; i++)
            {
                if (i > 0) ImGui.SameLine();
                float val = vals[i];
                ImGui.PushID($"{effectName}_{uni.Name}_{i}");
                ImGui.SetNextItemWidth(sliderWidth);
                if (ImGui.SliderFloat($"##{labels[i]}", ref val, uni.UiMin, uni.UiMax, "%.3f"))
                { vals[i] = val; changed = true; }
                ImGui.PopID();
            }

            if (changed)
                currentValue = string.Join(",", vals.Select(v => v.ToString("G", System.Globalization.CultureInfo.InvariantCulture)));
            return;
        }

        // Scalar
        float step2 = uni.UiStep > 0 ? uni.UiStep : 0.001f;

        if (uni.BaseType.StartsWith("int") || uni.BaseType.StartsWith("uint"))
        {
            int ival = int.TryParse(currentValue, out int iv) ? iv : 0;
            int intStep = Math.Max(1, (int)step2);
            ImGui.SetNextItemWidth(UniformAvailW() - 50);
            if (ImGui.SliderInt($"##{effectName}_{uni.Name}", ref ival, (int)uni.UiMin, (int)uni.UiMax))
                currentValue = ival.ToString();
            ImGui.SameLine();
            ImGui.PushID($"{effectName}_{uni.Name}_dec");
            if (ImGui.Button("-", new Vector2(22, 0)))
            { ival = Math.Max((int)uni.UiMin, ival - intStep); currentValue = ival.ToString(); }
            ImGui.PopID();
            ImGui.SameLine();
            ImGui.PushID($"{effectName}_{uni.Name}_inc");
            if (ImGui.Button("+", new Vector2(22, 0)))
            { ival = Math.Min((int)uni.UiMax, ival + intStep); currentValue = ival.ToString(); }
            ImGui.PopID();
        }
        else
        {
            float fval = float.TryParse(currentValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fv) ? fv : 0f;
            ImGui.SetNextItemWidth(UniformAvailW() - 50);
            if (ImGui.SliderFloat($"##{effectName}_{uni.Name}", ref fval, uni.UiMin, uni.UiMax, "%.3f"))
                currentValue = fval.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ImGui.SameLine();
            ImGui.PushID($"{effectName}_{uni.Name}_dec");
            if (ImGui.Button("-", new Vector2(22, 0)))
            { fval = Math.Max(uni.UiMin, fval - step2); currentValue = fval.ToString(System.Globalization.CultureInfo.InvariantCulture); }
            ImGui.PopID();
            ImGui.SameLine();
            ImGui.PushID($"{effectName}_{uni.Name}_inc");
            if (ImGui.Button("+", new Vector2(22, 0)))
            { fval = Math.Min(uni.UiMax, fval + step2); currentValue = fval.ToString(System.Globalization.CultureInfo.InvariantCulture); }
            ImGui.PopID();
        }
    }

    private static float[] ParseVectorValue(string value, int components)
    {
        var result = new float[components];
        // Handle float2(x,y) or float3(x,y,z) format
        var match = Regex.Match(value, @"float\d\(([^)]+)\)");
        string raw = match.Success ? match.Groups[1].Value : value;
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < components; i++)
        {
            if (i < parts.Length)
                float.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result[i]);
        }
        return result;
    }

    private void DrawComboControl(string effectName, ShaderUniformInfo uni, ref string currentValue)
    {
        var itemsRaw = uni.UiItems.Replace("\\0", "\0");
        var items = itemsRaw.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        int selected = int.TryParse(currentValue, out int v) ? v : 0;
        var preview = selected >= 0 && selected < items.Length ? items[selected] : currentValue;

        if (ImGui.BeginCombo($"##{effectName}_{uni.Name}", preview))
        {
            for (int i = 0; i < items.Length; i++)
            {
                bool isSel = selected == i;
                if (ImGui.Selectable(items[i], isSel))
                { currentValue = i.ToString(); }
                if (isSel) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
    }

    private void DrawCheckboxControl(string effectName, ShaderUniformInfo uni, ref string currentValue)
    {
        bool val;
        if (currentValue == "1" || currentValue.Equals("true", StringComparison.OrdinalIgnoreCase)) val = true;
        else if (currentValue == "0" || currentValue.Equals("false", StringComparison.OrdinalIgnoreCase)) val = false;
        else if (float.TryParse(currentValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fv)) val = fv > 0.5f;
        else val = false;
        if (ImGui.Checkbox($"##{effectName}_{uni.Name}", ref val))
            currentValue = val ? "1" : "0";
    }

    private void DrawRadioControl(string effectName, ShaderUniformInfo uni, ref string currentValue)
    {
        var itemsRaw = uni.UiItems.Replace("\\0", "\0");
        var items = itemsRaw.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        int selected = int.TryParse(currentValue, out int v) ? v : 0;
        for (int i = 0; i < items.Length; i++)
        {
            if (ImGui.RadioButton($"{items[i]}##{effectName}_{uni.Name}_{i}", selected == i))
            { currentValue = i.ToString(); ImGui.SameLine(); }
        }
    }

    private void DrawColorControl(string effectName, ShaderUniformInfo uni, ref string currentValue)
    {
        var parts = currentValue.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var color = new Vector4(
            parts.Length > 0 && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float r) ? r : 1f,
            parts.Length > 1 && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float g) ? g : 1f,
            parts.Length > 2 && float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float b) ? b : 1f,
            parts.Length > 3 && float.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float a) ? a : 1f);

        if (ImGui.ColorEdit4($"##{effectName}_{uni.Name}", ref color))
            currentValue = $"{color.X.ToString(System.Globalization.CultureInfo.InvariantCulture)},{color.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)},{color.Z.ToString(System.Globalization.CultureInfo.InvariantCulture)},{color.W.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    private static int TimeStringToSeconds(string time) => EorzeaFormat.TimeStringToSeconds(time);

    private static string SecondsToTimeString(int seconds) => EorzeaFormat.SecondsToTimeString(seconds);

}

public class ShaderUniformInfo
{
    public string Name = "";
    public string BaseType = "";
    public string UiType = "input";
    public string Label = "";
    public string Tooltip = "";
    public string UiItems = "";
    public string Category = "";
    public bool UiCategoryClosed;
    public float UiMin = 0;
    public float UiMax = 1;
    public float UiStep = 0.1f;
    public string DefaultValue = "";
}
