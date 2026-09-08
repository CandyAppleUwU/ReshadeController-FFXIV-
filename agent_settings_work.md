# How Uniform Slider Settings Work

## Problem
Adjusting uniform sliders in the Shaders tab instantly crashed the game with a CLR error (exit code `0x12345679`) at `FloppyUtils.Graphics.RenderEvents.InternalOnPostTick`.

## Root Cause
The crash was caused by writing uniform values to `ffxiv_reshade_anim` from a background thread (`Task.Run`) while the addon's render-thread callback read the same file and applied values via ReShade API calls (`set_uniform_value_float`/`set_uniform_value_int`). This race condition corrupted ReShade's internal D3D11 state.

Every file I/O approach was tried and crashed:
- `File.WriteAllText` to `ffxiv_reshade_anim` (synchronous) — crash
- `Task.Run` with anim file write — crash
- Atomic write via temp file + `File.Move` — crash
- `WriteCommandAsync` to `cmd.txt` — crash

## Solution
**Removed ALL anim file code from the slider path.** Sliders now use the exact same proven mechanism as the effect toggle checkbox:

### Slider Code Flow (ConfigWindow.cs `DrawUniformWithSave`)
1. ImGui slider detects value change
2. Update in-memory `effectSettings[effectName][uni.Name] = value`
3. Call `MarkDirty()` — sets `_pendingSave = true`, records timestamp
4. No file I/O, no background threads, no `ToDictionary` snapshot

### Debounced Save (ConfigWindow.cs `FlushPendingSave`)
Called at end of `DrawSettingsRight` every frame:
1. If no pending save, return immediately
2. If less than 200ms since last change, return (debounce)
3. Otherwise: `SavePresetFile(path)` + `WritePresetSignal(path)`
4. This is synchronous on the render thread — same as the checkbox toggle

### `SavePresetFile` writes:
- `Techniques=Name@Effect.fx,...` — which effects are enabled/disabled
- `TechniqueSorting=...` — effect ordering
- `[EffectFile.fx]` sections — uniform key=value pairs for each effect

### `WritePresetSignal` writes:
- `ffxiv_reshade_preset` file with content `path|counter`
- Addon detects counter change, calls `set_current_preset_path()` + `reload_effect_next_frame()`
- ReShade re-reads the preset .ini from disk, applying all uniform values

## Key Principle
**The only file I/O path that never crashed was `SavePresetFile` + `WritePresetSignal`** — used by the effect checkbox toggle since the beginning. Any other approach (anim file, cmd.txt, background threads) introduced race conditions with the addon's render-thread callbacks.

## What Was Removed
- `WriteAnimUniforms()` — iterated all effect settings, created lines, wrote to anim file
- `WriteAnimUniformsFromSnapshot()` — same but took a pre-built snapshot dict
- `WriteAnimUniformsSafe()` — atomic file write (temp + File.Move)
- All `Task.Run` calls from slider/draw code
- All `ToDictionary` snapshot creation from slider/draw code
- `using System.Threading.Tasks` import

## Files Modified
- `ConfigWindow.cs` — `DrawUniformWithSave`, `FlushPendingSave`, removed anim methods
- `dllmain.cpp` — addon try-catch around `std::stof`/`std::stoi` (kept as safety net)

## Performance
Slider drag updates in-memory dict at 60fps (instant, no I/O). After 200ms of no changes, preset file is saved once and ReShade reloads it. This means:
- No per-frame file I/O during drag
- Single save + reload after settling
- Brief preset reload flicker (same as checkbox toggle)
