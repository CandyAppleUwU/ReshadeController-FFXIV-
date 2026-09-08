# ReshadeController - Current State

## Architecture
Two components communicate via signal files in the game directory (`C:\Steam\steamapps\common\FINAL FANTASY XIV Online\game\`):

1. **Dalamud Plugin** (C#/.NET) — UI + logic, runs inside Dalamud
2. **ReShade Addon** (C++/MinGW) — applies commands on ReShade's render thread

### Signal Files (all in game dir)
| File | Direction | Purpose |
|------|-----------|---------|
| `ffxiv_reshade_pause` | Plugin → Addon | File exists = shaders paused (zone change, logout) |
| `ffxiv_reshade_preset` | Plugin → Addon | Format: `path\|counter`. Triggers `set_current_preset_path()` |
| `ffxiv_reshade_anim` | Plugin → Addon | Uniform values: `effect\|uniform\|type\|values`. Applied every frame |
| `ffxiv_reshade_cmd.txt` | Plugin → Addon | One-shot commands: `ACTIVATE\|1` |
| `ffxiv_reshade_state.json` | Addon → Plugin | Effect state JSON (disabled for now) |

### Addon Lifecycle
1. Addon loads → `on_init()` sets up paths, starts poll thread
2. Poll thread runs every 200ms: checks pause file → reads cmd file → reads preset signal → reads anim signal
3. `g_active` must be `true` (via `ACTIVATE|1` command) for preset/anim signals to be processed
4. Commands are queued to `g_render_commands` and executed on ReShade's render thread in `on_begin_effects()`

## What Works
- **Zone-based preset switching**: When territory changes, writes new preset path to signal file → addon calls `set_current_preset_path()` → ReShade loads new preset
- **Toggle FX on/off**: Saves technique state to preset .ini, writes preset signal → addon reloads preset
- **Shader pause/resume**: File existence check on poll thread (no ReShade API calls)
- **Global conditions**: `BetweenAreas` and `!IsLoggedIn` force shader pause
- **Hotkey toggle**: `GetAsyncKeyState` P/Invoke

## What's Broken
- **Adjusting uniform sliders crashes the game instantly** — likely race condition between C#'s `File.WriteAllText` to `ffxiv_reshade_anim` and the addon's `read_file()` on the poll thread, or synchronous file I/O in `SavePresetFile` blocking the ImGui render thread. The crash is a CLR error (exit code `0x12345679`) in Dalamud's `FloppyUtils.Graphics.RenderEvents.InternalOnPostTick`.

## Key Code Locations

### Plugin (C#)
- `Plugin.cs` — Main plugin: zone detection, ACTIVATE command, territory change handler
- `ConfigWindow.cs` — Three-tab UI (Zone Presets / Shaders / Shader Animation)
  - `SavePresetFile()` — Writes technique states + uniform values to preset .ini
  - `WritePresetSignal()` — Writes to `ffxiv_reshade_preset` with counter
  - `WriteAnimUniforms()` — Writes all current uniform values to `ffxiv_reshade_anim`
  - `DrawUniformWithSave()` — Draws ImGui slider, saves on change ← CRASHES HERE
  - `.fx` parser: `ParseFxFile()` / `ParseFxFileRecursive()` — Parses shader files for uniforms

### Addon (C++)
- `dllmain.cpp` — Everything in one file
  - `poll_thread()` — 200ms loop, reads all signal files
  - `check_command_file()` — Parses cmd.txt, requires `|` separator
  - `check_preset_signal()` — Reads preset signal, queues `set_current_preset_path()`
  - `check_anim_signal()` — Reads anim file, stores uniforms for per-frame application
  - `on_begin_effects()` — Render thread: drains command queue, applies pause/resume, applies anim uniforms
  - `apply_pause()` / `apply_resume()` — Saves/restores technique states

### Build
- Plugin: `$env:PATH = "C:\Users\FSOS\.dotnet10;$env:PATH"; dotnet build` in `ReshadeController/`
- Addon: `cmd /c build.bat` in `FFXIVReshadeController/` (MinGW g++)
- Deploy plugin to: `%AppData%\XIVLauncher\devPlugins\ReshadeController\`
- Deploy addon to: `C:\Steam\steamapps\common\FINAL FANTASY XIV Online\game\reshade_controller.addon`

### Important Notes
- Addon file is locked while game is running — must close game before deploying addon
- Plugin can be hot-reloaded by Dalamud
- `ACTIVATE|1` command format requires the pipe — bare `ACTIVATE` is silently ignored by the parser
- MinGW does not support bare `catch {}` — must use `catch (...) {}`
- `Environment.ProcessPath` returns game exe path (ffxiv_dx11.exe dir) since plugin runs in game process
- ReShade presets dir: `reshade-presets\`, shaders dir: `reshade-shaders\Shaders\` under game dir
- Technique names in ReShade differ from filenames (e.g. `Bloom.fx` → technique `BloomAndLensFlares`); use `techniqueSorting` entries to get correct names

### Stage 2 P1a — Dynamic presets (time timeline)
- Presets under `reshade-presets\xlAnimPresets\` are dynamic. Sidecar `<preset>.anim.json` holds `DynamicPresetData` (Primary + named configs, keyframes with tech states + uniforms at Eorzea times).
- `ConfigWindow` owns selection/snapshot/editing; `Plugin` owns time + anim-file writing. Engine follows the Shaders-tab selection (headless after first select, not across reboot).
- Manual slider/toggle edits while the engine drives become temporary overrides (win immediately, absorbed on snapshot, cleared on snapshot/preset-change/disable) — never rewrite history. Sidecar debounced-saves (2s) + on preset switch + on dispose.
- Uniforms go through the existing anim file (change-gated, atomic); toggles go through the existing toggle-signal file in one batch, edge-triggered only. No addon protocol changes in P1a.
- Toggles flip at keyframe time (segment-start); uniforms smoothstep-interp (ints rounded, bools thresholded at 0.5).
- Old Shader Animation tab untouched (retires at P2).

### Stage 2 P1b — Node canvas + named configs + keyframe-first editing
- Sidecar adds `Nodes[] {Id, Config, KeyframeId, Source, X, Y}` + `Edges[] {From, To}`. Keyframes are named objects `{Id, Name, TimeSeconds, TechStates, Uniforms}` inside configs.
- Nodes bind keyframes BY ID; chain resolution: longest connected walk wins, unresolvable skipped, points sorted by keyframe time (edges = membership). No chain → legacy Primary timeline.
- Third column = keyframe editor over the single set (click loads state into panels for direct in-place editing with autosave, inline rename, delete, one-click Add). Times are node-canvas business only. Older multi-config sidecars merge into Primary on load, nodes rebound.
- With no keyframe selected, manual edits become temporary overrides (win immediately, absorbed on snapshot). Engine + mirror read the same objects either way — no fights.
- `DynamicCanvasWindow` ("ReShade Animator"): draggable nodes, bezier links, click-out-then-in to connect (cycle-checked), node context menu (snapshot-here/disconnect/delete), Del key, per-node config + keyframe dropdowns, in-node time editor bound to the keyframe. No pan/zoom polish, fixed node size.
- Node canvas uses only safe ImGui primitives (no payload/hover-target APIs per stage-1 lessons).
