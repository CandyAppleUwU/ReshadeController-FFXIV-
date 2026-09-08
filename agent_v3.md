# FFXIV ReshadeController — Agent Briefing v3

Comprehensive reference for AI agents working on this project. Supersedes `AGENTS.md` and
`agent_settings_work.md` where they conflict (both kept for history).
Covers code state as of plugin build **b146**. Line numbers are approximate.

Two components, one system:

| Component | Path | Language | Builds to |
|---|---|---|---|
| ReShade addon | `C:\Users\FSOS\Documents\Projects\FFXIVReshadeController\` | C++17 (MinGW) | `reshade_controller.addon` in game dir |
| Dalamud plugin | `C:\Users\FSOS\Documents\Projects\ReshadeController\` | C# (.NET 10) | `%AppData%\XIVLauncher\devPlugins\ReshadeController\` |

Game dir: `C:\Steam\steamapps\common\FINAL FANTASY XIV Online\game\`.

## Table of contents

1. [Architecture](#1-architecture)
2. [Signal-file protocol](#2-signal-file-protocol)
3. [ReShade addon (`dllmain.cpp`, ~1584 lines)](#3-reshade-addon-dllmaincpp)
4. [Plugin core (`Plugin.cs`, ~2500 lines)](#4-plugin-core-plugincs)
5. [Data model (`DynamicAnimation.cs`)](#5-data-model-dynamicanimationcs)
6. [Node catalog](#6-node-catalog)
7. [Pins, edges, gates](#7-pins-edges-gates)
8. [Trigger envelopes](#8-trigger-envelopes)
9. [Time system (chains, freeze, fades)](#9-time-system-chains-freeze-fades)
10. [Weather system](#10-weather-system)
11. [Canvas UI (`DynamicCanvasWindow.cs`, ~2000 lines)](#11-canvas-ui-dynamiccanvaswindowcs)
12. [Shaders tab (`ConfigWindow.cs`, ~2970 lines)](#12-shaders-tab-configwindowcs)
13. [Dynamic engine I/O (anim file, toggles, mirror)](#13-dynamic-engine-io-anim-file-toggles-mirror)
14. [Shader edits (separate area, same game)](#14-shader-edits)
15. [Build / deploy / runbook](#15-build--deploy--runbook)
16. [Hard lessons (read before touching anything)](#16-hard-lessons)
17. [Known issues / active investigations](#17-known-issues--active-investigations)
18. [Ideas on deck (not built)](#18-ideas-on-deck-not-built)

---

## 1. Architecture

```
Dalamud plugin (C#, in-game UI + logic, framework thread)
    ↕ signal files in game dir (see §2)
ReShade addon (C++, render thread only for all ReShade API writes)
    ↕ ReShade API
ReShade effects (.fx shaders, some hand-edited, see §14)
```

- The plugin NEVER calls ReShade APIs. The addon NEVER reads game state. Files are the only bridge.
- Plugin logic runs on Dalamud's framework `Update` thread. Addon applies everything on
  ReShade's `reshade_present` thread (`on_present`). `reshade_begin_effects` is intentionally
  empty — mutating state there once caused use-after-free AVs.
- All ReShade object resolution is **enumerate-only** (`enumerate_techniques`,
  `enumerate_uniform_variables`). `find_technique` / `find_uniform_variable` handles proved
  unstable and corrupted the heap. Never reintroduce them.

## 2. Signal-file protocol

All in the game dir. Plugin → addon unless noted.

| File | Format | Purpose |
|---|---|---|
| `ffxiv_reshade_pause` | existence = paused | Global shader pause (zone change, logout, hotkey) |
| `ffxiv_reshade_preset` | `path\|counter` | `set_current_preset_path()` on counter change |
| `ffxiv_reshade_anim` | lines `effect\|uniform\|type\|values` | Per-frame uniform values (change-gated, atomic tmp+move) |
| `ffxiv_reshade_cmd.txt` | lines, consumed+deleted | One-shot: `ACTIVATE\|1`, `SET_PRESET\|path`, `SET_TECHNIQUE\|eff\|tech\|0/1`, `REORDER\|fe\|ft\|be\|bt`, `SET_UNIFORM\|eff\|uni\|claimedType\|v,...`, `SAVE` |
| `ffxiv_reshade_toggle` | lines `effect\|tech\|0/1`, consumed+deleted | Technique on/off (edge-triggered, batched, ≤30/s) |
| `ffxiv_reshade_techniques.json` | addon → plugin | Ground truth: `{"preset": path, "effects":[{effect, tech, enabled}]}`; hidden techniques excluded |
| `ffxiv_reshade_state.json` | addon → plugin | Currently DISABLED (was a crash suspect) |

Notes: `ACTIVATE|1` requires the pipe (bare `ACTIVATE` is ignored by the parser). Preset
signal needs a bumping counter or it's ignored as duplicate. Anim file deleted = hold last
values (addon clears its list, keeps last-applied). Toggle/uniform commands collapse to
latest per target (kind 1 = tech, 2 = uniform); preset/reorder/save always run in full.

## 3. ReShade addon (`dllmain.cpp`)

- `on_init`: sets signal paths from exe dir, deletes stale signals, starts 16ms poll thread.
- Poll thread = file I/O only, queues `RenderCommand`s. `on_present` drains the queue OUTSIDE
  the lock (a command can block on compiles).
- **Preset tiers** on `set_current_preset_path`: identical-content skip (byte compare) →
  fast tier (all wanted techniques already live, 750ms guard) → full tier (enumeration calm:
  stable 30 presents, 60s backstop). Reload event does NOT fire for preset switches, so the
  guard never waits on it. `g_anim_applied` cache cleared on every real switch.
- **Uniform sets** (`SET_UNIFORM` + anim file): split tokens on poll thread, convert on render
  thread against the LIVE queried type (never trust the file's claimed type). Float/int/uint
  sized by rows×columns×array; bool/typeless as bytes. Malformed numbers drop the command.
  Redundant sets skipped by signature map (cleared on reload).
- **Toggles**: resolved via enumeration with tolerant name matching (basename, case-insensitive,
  extension-optional). No `find_*` ever.
- **Reorder**: rebuilds technique order in memory, calls `reorder_techniques`, persists
  `TechniqueSorting=` back to the preset atomically (tolerant matching, unknown entries kept).
- **Pause**: global `set_effects_state` (no handles → reload-safe). Per-technique states untouched.
- **Anim file**: `effect|uniform|type|values` lines → same type-checked apply path, every present.
- **Technique list**: published on reload + startup from the present thread (reads are safe there).
- Overlay: one-line status (Active/Paused/preset name).
- Diagnostic logging exists but is sparse; recent builds log preset-exec, uniform-exec, toggle
  queues at info level.

## 4. Plugin core (`Plugin.cs`)

- `/reshade` commands: open config, pause/resume/toggle/status/help.
- **Zone presets**: `TerritoryChanged` + per-frame check → preset signal + pause file.
  `Environment.ProcessPath` = game dir (plugin runs in-process).
- **Global pause**: `!IsLoggedIn || BetweenAreas` forces pause; resume snaps all fade/run
  state (see §9) so loads don't glide.
- **Hotkeys**: shader pause toggle + Shaders-tab hotkey via `GetAsyncKeyState` + modifiers.
- **QoLBar IPC**: optional condition-set link (`QoLBar.GetConditionSets` /
  `QoLBar.CheckConditionSet`) for zone presets and trigger kind 12.
- **Weatherman IPC**: `Weatherman.IsTimeCustom` + `GetDisplayedTimeString` (Eorzea time
  override), `Weatherman.GetDisplayedWeather` (preferred weather source, falls back to
  `EnvManager+0x26` true-weather byte — same address Weatherman itself uses).
- **Emote hook**: `PlayEmote` detour records self-emotes (owner == local player) for
  trigger kind 15, with per-node seen-tracking (each node fires once per emote).
- **Chat commands**: trigger kind 17 registers a custom slash command per node (local player).
- **Camera/player tracking**: `CameraManager` world-cam offsets (+0x60/64/68, first-person flag
  at +0x180), `UIState` weapon sheath, HP/MP/dead/casting/status reads, nearby dead-player
  count — all guarded, false on any doubt.
- **Pin actions**: trigger kind 19 can `Send Command` (via `UIModule.ProcessChatBoxEntry`,
  i.e. game chat submit — NOT `CommandManager.ProcessCommand`, which only dispatches
  Dalamud-registered commands and silently drops game commands).
- **Eorzea time**: cached once per update (`_eorzeaNow`) for gate evaluation.
- Build tag `bNNN` constant lives in `DynamicCanvasWindow.cs` (`BuildTag`); the About/toolbar
  shows it. Bump it on every deployed change — it's the only reliable "what's running" check.
  Verify bytes if paranoid: UTF-16 scan of the deployed dll.

## 5. Data model (`DynamicAnimation.cs`)

Sidecar file: `<preset>.anim.json` next to dynamic presets under
`reshade-presets\xlAnimPresets\`.

```text
DynamicPresetData { Version, Enabled, Configs[], Nodes[], Edges[] }
DynamicAnimConfig { Name, Keyframes[] }                      // "Primary" is main
DynamicKeyframe   { Id, Name, TimeSeconds, TechStates{}, Uniforms{file->{uname->{Value,BaseType}}},
                    IsPrimary, Sparse, TickedTechs[], TickedUniforms[], TechOrder[] }
DynamicAnimNode   { Id, NodeNum, Config, KeyframeId, Source, X, Y, ... ~50 fields ... }
DynamicAnimEdge   { From, To, FromPin }                      // FromPin "" = whole node
DynamicWeatherGroup { Id, Any, WeatherIds[] }
```

- Primary keyframe = full snapshot (base values). Sparse keyframes drive only ticked settings.
- `TickedUniforms` entries are `"file\0uname"`; `TickedTechs` are `"Tech@File"`.
- `PurgeUnticked()` strips unticked stored values on load — new sparse frames MUST tick what
  they store or they self-erase. (+ Add Keyframe copies ticks for exactly this reason.)
- `EulerBox.Rotate` (YXZ euler, forward/inverse) is shared by blend math and wireframes so
  they can never disagree. `Wrap180` normalizes angles.
- `DynamicTimeline`: `EvalFrames` (chain else legacy), `ResolveChainFrames` (longest walk,
  time-gate filterable), `Segment`/`SegmentFactor` (wrap-aware, smoothstepped),
  `CircularDist`. `DynamicPresetStore` = atomic sidecar load/save.

Node fields of note: `TriggerKind` 0–19, `Hotkey`, `ThresholdPct` + `ThresholdRevert` (HP/MP
Below↔Above) + `ThresholdMax` (band mode), `StatusIds`, `CastActionIds`, `EmoteIds`,
`QolbarSet`, `DeadCount`, `ChatCommand`, `DelaySec/FadeInSec/StaySec/FadeOutSec`,
`StayWhilePresent`, `StartValues{}`, `ForcedStarts[]` (keys), `PinAction/PinActionCommand`,
`LocTerritory/LocX/Y/Z/LocShape(sphere|box)/RadiusStart/Max`, box extents
`BoxOX/OY/OZ + BoxIX/IY/IZ + BoxOCX/OCY/OCZ + BoxYaw/Pitch/Roll`, wall `WallW/WallH/KeepZones`,
`TimeFadeSec`, `TimeGateOn/TimeGateOff`, `InPinMode`, `Collapsed`, `ShowVolume`,
`LocUseCamera`, `LocLocal` (legacy wall migration flag, now just "handled").

## 6. Node catalog

| Node | Source | Color | Binds kf? | Runs envelopes? | Notes |
|---|---|---|---|---|---|
| In-Game Time | `time` | blue | yes | no | Timeline drivers; chain links; gateable (All-On); Fade row on starts |
| Trigger | `trigger` | orange/brown | yes (dry-run if not) | yes | Kinds below; phase pins; pin actions (kind 19) |
| Coords Zone | `coords` | teal | no | no | Location sensor (kind 18); sphere/box; wireframes |
| Door Trigger | `wall` | purple | no | no | Plane-cross latch + fade prog; KeepZones; wireframe rect |
| Weather | `weather` | dark red `#880015` | no | no | Sensor groups with out-pins; green = live group |
| Time Gate | `timegate` | gray | no | no | Pure ON/OFF window; out-pin only |

**Trigger kinds** (`TriggerKindNames`): 0 Hotkey, 1 InCombat, 2 Mounted, 3 Crafting,
4 Gathering, 5 Weapon Out, 6 Dead, 7 Casting, 8 Emoting, 9 HP %, 10 MP %, 11 Status Effect,
12 QoLBar Set, 13 First Person, 14 Sit/Sleep, 15 Emote, 16 Dead Players, 17 Chat Command,
18 Location, 19 Pin Activation. Kind 18 on trigger-source nodes = location trigger.
`SupportsStayHold`: 0–14, 16, 19 (all state kinds + hotkey-held + pin; one-shot events excluded).
HP/MP: Revert flips Below↔Above; separated Start/Max = continuous band mode (else legacy edge).

## 7. Pins, edges, gates

- **Titlebar pins**: time nodes have in+out (chain links). Trigger/coords/door have in+out
  (gates). Weather has per-group out-pins only. TimeGate has out-pin only (not gateable).
  Gate edges draw orange, chain links green.
- **Chain edges** (time→time, whole-node): timeline membership. Excluded: trigger/coords/
  wall/weather/timegate nodes and their edges never join chains.
- **Gate edges** (anything → trigger/coords/door/time): target fires only per its logic.
  `In Pin Mode` dropdown (on wired trigger/coords/door): All On (default), All Off, One On,
  One Off. Time nodes are hardcoded All-On.
- **Phase pins** (`FromPin` delay/fadein/stay/fadeout): trigger timer rows; sticky levels per
  run (fadeout persists until retrigger — by design, latch-like). Cleared on retrigger.
- **Weather pins** (`w:<groupId>`): live while the group matches current weather.
- **Propagation**: OFF flows downstream — gated-off time nodes cut the chain; gated coords
  read OFF downstream too; walls fade out and stay out while gated.
- **Exclusivity rules**: max one weather pin per time node; chained time nodes reject weather
  wires and vice versa. Weather dropdowns hide IDs claimed by sibling selecting groups
  (Any = anything not claimed elsewhere). Cycle-checked globally.
- Gate evaluation is per-edge (`EdgeLive`: pin-aware) shared by mode counting and time-ANDing.

## 8. Trigger envelopes

`delay → fade-in → stay/hold → fade-out`, per-run factor `_runF` (rate-based linear:
`+= dt/Fi`, `-= dt/Fo`; delay/stay on wall clocks). Zero timers = instant. Keyframe bound or
not (unbound nodes dry-run timers/phases/titlebar for sequencing).

- **Edge**: `down && !was`. Retriggering an active run keeps factor + captures live values
  (`_retriggerFrom`) so attacks resume instead of rewinding. Fresh edges outside the
  fade-out-duration grace window punch from authored starts; inside grace, from live.
- **Hold** (`StayWhilePresent`, state kinds): ramps toward peak at fade-in rate while the
  condition holds (hotkey = physically held; pin = gate state), releases from live.
  Dropping mid-fade-in aborts straight to release. Hotkey suppressed while typing.
- **HP/MP band mode** (`ThresholdMax ≠ ThresholdPct`): continuous factor like location
  blends (reuses that overlay shape), slewed at Fade-in/out rates; timed envelope ignored;
  binary edge behavior preserved when Start == Max.
- **Start/End editor**: per-ticked-uniform start values + per-row **Force** checkbox.
  Starts apply to attacks; **releases always blend from the live timeline** (never authored —
  that's what makes handoffs snap-free). Force = attack from authored even with live
  history (punch-in, e.g. Drunk from 0). Missing keys snap (nothing to blend from).
- **Phase pings** per run; **Send Command** action fires once per kind-19 edge.
- Delay emits starts (or retrigger-live); toggles emit while `f > 0`.

## 9. Time system (chains, freeze, fades)

- **Chains**: longest connected time-node walk wins (ties → first created, silently).
  `EvalFrames(data, timeGate?)`; `timeGate` cuts the walk at switched-off nodes.
- **Gated time nodes**: ON iff all incoming live (recursive, cycle-guarded). OFF cuts the
  chain; downstream reads OFF through propagation.
- **Freeze**: chain exists but fully gated → emit nothing (hold last values), no fallback.
  No chain at all → legacy Primary timeline (existing behavior preserved).
- **Twins rule**: same-`TimeSeconds` carriers resolve first-wins so chain frames beat the
  pooled primary (fixed the midnight-Primary bug for both uniforms and toggles).
- **Fade-in** (`TimeFadeSec` on chain starts, chained nodes inherit head's): OFF→ON ramps
  frozen→timeline over Fade seconds (smoothstep). Snapshot frozen at the edge; manual grabs
  skip it; trigger overlays layer on top. Missing history snaps (nothing to blend from).
- **Fade-out**: symmetrical hold-and-ramp for orphaned keys/techs toward starts (or hold),
  yielding to live drivers; toggles held till release. Sparse ticks only — full frames are
  base content (holding a primary-bound node once flooded 600+ ONs).
- **BetweenAreas/offline resume**: all fade/run state wiped + synced → instant final values.
  Same reset runs on manual unpause (accepted side effect).
- Chain starts show Curve + Fade rows; chained nodes hide both and inherit.

## 10. Weather system

- Source: Weatherman `GetDisplayedWeather` IPC → fallback `EnvManager+0x26` true byte
  (255 = unknown). Same address Weatherman itself reads.
- `ConfigWindow.GetWeatherList()` from Lumina `Weather` sheet, sorted by ID ascending.
- Groups: multi-select + Any-first-item (Any = not-claimed-elsewhere), +Add/x controls,
  exclusivity across sibling selecting groups, green label + orange ring when live/wired.
- Engine: pure level evaluation per frame (`WeatherGroupLive`); feeds gates, never envelopes.

## 11. Canvas UI (`DynamicCanvasWindow.cs`)

- Toolbar: preset name, Eorzea clock, centered **Add Node** dropdown (In-Game Time, Trigger,
  Coords Zone, Door Trigger, Weather, Time Gate), build tag `bNNN` (bump EVERY deploy).
- Canvas: grid, middle-drag pan, click-out-then-in linking (cycle-checked), Del key,
  context menu (Snapshot/New keyframe/Disconnect/Delete).
- Nodes: draggable/selectable titlebars (type colors), collapse arrow (chromeless triangle,
  persisted), titlebar status (ON/OFF green-gray; Eorzea time on time nodes; weather name
  on weather nodes), footer `• Node N •`, fixed pin geometry (22px), bezier edges.
- Editors per type: time (Key/Time/Now/Fade/Curve), trigger (type-specific params, envelope,
  Start/End editor, pin actions), coords (shape/sphere/box/rotation/containment/volume),
  door (XYZ local-frame, rotation, W/H, zones), weather (groups), timegate (On/Off HH:MM:SS).
- Rows position explicitly (`SetCursorPosX`) — every conditional row must reserve `NodeHeight`
  or it bleeds. Editor uses safe ImGui primitives only.
- Node canvas sizes: time 220 (+30 Fade, −30 chained extras), trigger 260, coords/box 300,
  door 380-tall, weather 300 + per-group rows.

## 12. Shaders tab (`ConfigWindow.cs`)

- Settings tab (zone list, default preset, hotkeys, opacity) + Shaders tab (3 columns:
  effects list | settings | keyframes panel).
- Right panel: Primary-config keyframes sorted by time, select/rename/delete,
  **+ Add Keyframe** (deep-copies selected non-primary incl. ticks; live-captures otherwise),
  snapshot button (±30s replace semantics).
- Selection is view-focus; idle engine previews selection live (diffs only); driving engine
  owns output. Manual edits become overrides (win immediately, absorbed on snapshot).
- `.fx` parser (techniques/uniforms/annotations incl. hidden), technique sorting with drag
  reorder, nicknames, favorites, search, mirror into panels, toggle batching, sidecar
  debounced saves, `PrimeDynToggles` from live state.
- `FollowLivePreset()` runs headless from the framework thread (b125) — engine starts
  without ever opening the window. Own mtime so the effects merge isn't starved.

## 13. Dynamic engine I/O (anim file, toggles, mirror)

- One atomic anim file write per frame, only on content change; empty = delete (addon holds).
- Toggles: edge-triggered batch file, collapse-to-latest, **30/s** budget (`DynTogglePerSecond`).
- Provenance tracking (`_prevBaseOnly`): authored starts apply when forced or without live
  history; otherwise frozen-live wins.
- Mirror pushes engine output into panels every frame (display only).

## 14. Shader edits

Hand-edited `.fx` files in the game's `reshade-shaders/Shaders/` (region restriction to the
center 3840×1440 of 5120×1440 + assorted controls; each shader uses a unique uniform prefix):
NGLighting(+perf opts, color controls), AmbientLight (all passes zeroed outside region),
MeshEdges (+Line Darkness), GaussianBlur (+enable toggle, depth sky-ignore + debug view),
rj_sharpen (depth sky-ignore, color-space-safe early-out), Depth_Cues/DC (depth sky-ignore),
Vignette/VG (depth sky-ignore), DPX, Clarity2 (CL_), MXAO (MXAO_), Curves (CV_),
Drunk (`Drunk_Strength` 0–1 master), MagicHDR (`HDRStrength` 0–1 master, bloom debug kept pure).
KeepUI_FFXIV.fx was reverted to stock — do not touch.

## 15. Build / deploy / runbook

- Plugin: `$env:PATH = "C:\Users\FSOS\.dotnet10;$env:PATH"; dotnet build -c Release`
  in `ReshadeController/` → copy `dll+pdb+json` to
  `%AppData%\XIVLauncher\devPlugins\ReshadeController\`. deps.json rarely changes (verify
  by hash if unsure). **Dev plugins load at boot / need explicit reload — file copies do
  nothing until then.** Byte-verify the tag: UTF-16 scan the deployed dll for `bNNN`.
- Addon: `cmd /c build.bat` in `FFXIVReshadeController/` (MinGW g++). Deploy
  `reshade_controller.addon` to game dir with the **game closed** (file lock).
- Sidecar (`.anim.json`) loads on preset switch only — reswitch presets (or restart plugin)
  after hand-editing it. Keep `.bak` copies; JSON must stay valid (python round-trip works,
  watch `\u0000` escapes surviving).
- Dalamud log level is **Warning (3)** on this machine: `Information` logs are filtered.
  Diagnostics must log at Warning+ or they vanish silently.
- Repro protocol that actually works: reload → confirm tag → single slow cycle, report
  titlebar + exact values per stage → then rapid. Video works (ffmpeg present);
  1fps sweeps show trends, full-rate series need programmatic measurement (slider/swatch
  tracking is compression-noisy — prefer timestamps + logs).

## 16. Hard lessons

- Never `find_*` handles; never write in `begin_effects`; never hold locks across ReShade calls.
- Anim/toggle files: atomic tmp+move; change-gated writes; addon tolerates absence.
- `ACTIVATE|1` needs the pipe; preset signals need counter bumps; MinGW needs `catch (...)`.
- Technique names ≠ filenames (`Bloom.fx` → `BloomAndLensFlares`); match tolerantly.
- `TechniqueSorting=` persistence must preserve unknown entries verbatim.
- Hidden-annotated techniques are compiled but unlisted — mirror the ReShade UI.
- Fresh triggers punch from authored starts; grace/retrigger glide from live; release
  always hands to live base. Force = always-authored on attack.
- Full frames are base content: never fade-out-hold them (600+ toggle flood), never
  expect them to carry exclusives.
- Equal `TimeSeconds` twins resolve chain-first (b117) — recheck if pool order ever changes.
- OnUpdate ~100ms hitches exist in the wild; `dt` is clamped at 0.5s so worst case is a
  half-ramp step, and stalls make any ramp look steppy. Perf work (per-frame file rewrite,
  per-key regex/`TryParse` in `DynLerpValue`, mirror churn) is the next perf lever, not logic.

## 17. Known issues / active investigations

- **Rapid-tap snap saga (node 17, Sepia):** fire/retrigger/grace proven smooth via logs;
  values aligned; toggles stable; no pause activity during bursts. Remaining suspects in
  order: (a) release-boundary handoff under load (unlogged path), (b) hitch stepping
  (verified ~100ms spikes in log), (c) a second driver interaction. B146 release-to-live
  + b145 resume-reset are in. Next step if it persists: log release f-values, or watch
  one slow cycle's exact numbers.
- **Trigger toggles for techs missing from Primary** stick (no carrier reverts them).
  Common case is covered by Primary fallback; residual fix options are documented in
  chat history (explicit release vs primary-merge). Currently unbuilt.
- **Stale `fadeout` phase pings** persist till retrigger (latch-like by design).
- **Unwired weather groups** still claim exclusivity.
- **Time→time edges are always chain links** (no gate-only variant).
- **Equal-length chains** tie-break silently to first-created.

## 18. Ideas on deck (not built)

- Live chain highlighting (color the driving chain); why-is-off hints for unlit nodes.
- Door latch/unlatch phase pins; wall fade progress as gate source (already is via prog>0).
- Command queue/cooldown for Send Command spam; weather settle debounce (1–2s anti-flap).
- Toggle release modes per keyframe (restore-previous vs base vs hold).
- Edge modes (chain vs gate) for time→time links; In Pin Mode for time nodes.
- Sidecar `Version` + migration log; full-flood toggle path for bulk transitions.
- Perf: throttle anim writes (change-threshold), cache parsed uniform numbers, calm mirror updates.

---

*Conventions for future work: bump `BuildTag` every deploy; byte-verify the deployed dll;
prefer discussing semantics before building gates/envelopes; never trust a fix you can't
see in logs, titlebars, or frame measurements.*
