# FFXIV ReshadeController — Agent Briefing v5

Comprehensive reference for AI agents working on this project. Supersedes `agent_v4.md`
(kept for history) as of plugin build **Beta 0.9.293 - Pre-Release**. Line numbers are
approximate (`Plugin.cs` ~4280, `ConfigWindow.cs` ~4280,
`DynamicCanvasWindow.cs` ~3490).

Two components, one system:

| Component | Path | Language | Builds to |
|---|---|---|---|
| ReShade addon | `C:\Users\FSOS\Documents\Projects\FFXIVReshadeController\` | C++17 (MinGW) | `reshade_controller.addon` in game dir |
| Dalamud plugin | `C:\Users\FSOS\Documents\Projects\ReshadeController\` | C# (.NET 10) | `%AppData%\XIVLauncher\devPlugins\ReshadeController\` |

Game dir: `C:\Steam\steamapps\common\FINAL FANTASY XIV Online\game\`.
Shaders dir: `reshade-shaders\Shaders\` under game dir (665 .fx files).
Sidecars: `reshade-presets\xlAnimPresets\<preset>.anim.json`.

## Table of contents

1. [Versioning](#1-versioning)
2. [Architecture](#2-architecture)
3. [Signal-file protocol](#3-signal-file-protocol)
4. [ReShade addon](#4-reshade-addon)
5. [Plugin core](#5-plugin-core-plugincs)
6. [Data model](#6-data-model-dynamicanimationcs)
7. [Node catalog](#7-node-catalog)
8. [Pins, edges, gates](#8-pins-edges-gates)
9. [Auto-Start rule](#9-auto-start-rule)
10. [Trigger envelopes](#10-trigger-envelopes)
11. [Layer priority, ambient landing, crossfades](#11-layer-priority-ambient-landing-crossfades)
12. [Time system](#12-time-system-chains-freeze-fades)
13. [Location system](#13-location-system-coords-nodes)
14. [Wall system](#14-wall-system-door-nodes)
15. [Timer Switch nodes](#15-timer-switch-nodes)
16. [Weather system + built-in override](#16-weather-system--built-in-override)
17. [DLSS5 integration](#17-dlss5-integration)
18. [Cutscene / Duty presets](#18-cutscene--duty-presets)
19. [Preset management](#19-preset-management)
20. [Canvas UI](#20-canvas-ui-dynamiccanvaswindowcs)
21. [Shaders tab + Main/Settings tabs](#21-shaders-tab--mainsettings-tabs-configwindowcs)
22. [.fx parser spec](#22-fx-parser-spec)
23. [Shader edits (xlrc_strength rollout)](#23-shader-edits-xlrc_strength-rollout)
24. [Engine I/O + pause semantics](#24-engine-io--pause-semantics)
25. [Build / deploy / runbook](#25-build--deploy--runbook)
26. [Hard lessons](#26-hard-lessons)
27. [Known issues / open audit items](#27-known-issues--open-audit-items)
28. [Ideas on deck](#28-ideas-on-deck)

---

## 1. Versioning

- `BuildTag` is a single `internal const string` in `DynamicCanvasWindow.cs`:
  `"Beta 0.9.293 - Pre-Release"`. Bump the trailing number every deploy.
- All log tags reference it (`$"…{DynamicCanvasWindow.BuildTag}…"`), so toolbar,
  About, and logs always agree (they drifted before centralization — never split them).
- Byte-verify the deployed dll: UTF-16 scan for the tag string.

## 2. Architecture

```
Dalamud plugin (C#, in-game UI + logic, framework thread)
    ↕ signal files in game dir (see §3) + ReShade.ini edits + F6 injection
ReShade addon (C++, render thread only for all ReShade API writes)
    ↕ ReShade API
ReShade effects (.fx shaders, ~290 hand/generator-edited, see §23)
Third-party: Weatherman (optional weather source), QoLBar (optional trigger
source), renodx-dlss5 + dlss5-bridge (optional DLSS NR target)
```

- The plugin NEVER calls ReShade APIs. The addon NEVER reads game state. Files are the only bridge.
- Plugin logic runs on Dalamud's framework `Update` thread. Addon applies everything on
  ReShade's `reshade_present` thread. `reshade_begin_effects` is intentionally empty.
- All ReShade object resolution is **enumerate-only**. `find_technique` /
  `find_uniform_variable` handles proved unstable. Never reintroduce them.
- **Nothing in the addon changed during the v5 arc.** All v5 work is plugin + shaders.

## 3. Signal-file protocol

All in the game dir. Plugin → addon unless noted. Preset-signal writes are
**queued + retried across frames** (`QueuePresetSignal`/`DrainPresetSignal` in
`Plugin.cs`): the addon's 200ms poll holds a read-lock that fails any single
atomic replace (sharing violation → the 01:28 `OnUpdate` crash class). Counter
bumps once per switch, never per retry. `ConfigWindow.WritePresetSignal`
delegates to the queue.

| File | Format | Purpose |
|---|---|---|
| `ffxiv_reshade_pause` | existence = paused | Global shader pause (zone change, logout, hotkey, manual) |
| `ffxiv_reshade_preset` | `path\|counter` | `set_current_preset_path()` on counter change |
| `ffxiv_reshade_anim` | lines `effect\|uniform\|type\|values` | Per-frame uniform values (change-gated, atomic tmp+move) |
| `ffxiv_reshade_cmd.txt` | lines, consumed+deleted | One-shot: `ACTIVATE\|1`, `SET_PRESET\|path`, `SET_TECHNIQUE\|eff\|tech\|0/1`, `REORDER\|…`, `SET_UNIFORM\|…`, `SAVE` |
| `ffxiv_reshade_toggle` | lines `effect\|tech\|0/1`, consumed+deleted | Technique on/off (edge-triggered, batched, ≤30/s) |
| `ffxiv_reshade_techniques.json` | addon → plugin | Ground truth technique list + live `preset`; hidden techniques excluded |
| `ffxiv_reshade_state.json` | addon → plugin | DISABLED (crash suspect, do not re-enable blindly) |

`ACTIVATE|1` requires the pipe. Preset signals need counter bumps. MinGW needs `catch (...)`.

## 4. ReShade addon

Unchanged since v3 (see `agent_v3.md` §3 for the full description): 16ms poll thread =
file I/O only → `RenderCommand` queue → drained on `on_present` outside the lock; preset
tiers (byte-compare skip → fast 750ms → full enumeration-calm); type-checked uniform sets
against live types; tolerant technique matching; `TechniqueSorting=` persistence preserving
unknowns; global pause via `set_effects_state`; per-frame anim-file apply; technique-list
publishing; one-line overlay.

Note for preset work: the addon **re-saves the still-loaded preset file**
(TechniqueSorting persistence) on switches/toggles. Deleting a live preset
outside the game resurrects it — always switch away first.

## 5. Plugin core (`Plugin.cs`)

- `/reshade` commands: open config, pause/resume/toggle/status/help.
- **Zone presets**: `TerritoryChanged` + per-frame check → preset signal + pause file.
  `ApplyZoneConfig` always bumps the preset counter (forces ReShade reload, even on boot).
  `Environment.ProcessPath` = game dir (in-process).
- **Global pause**: `!IsLoggedIn || BetweenAreas` forces pause; resume wipes fade/run
  state + resyncs (instant values, no glides across loads). Wall latches are NOT wiped.
- **Manual pause** (hotkey/file): rendering pauses; see §24 for output-hold semantics.
- **Hotkeys**: shader pause toggle + Shaders-tab hotkey + Animator-window hotkey
  (`AnimatorHotkeyKey/Mod`, flips `animatorWindow.IsOpen`) via `GetAsyncKeyState` + modifiers.
- **QoLBar IPC**: condition-set link for zone presets and trigger kind 12.
  `ConditionSetNames` refreshes on main-window open + lazily when the kind-12
  dropdown draws empty or opens (self-heals late-loading QoLBar).
- **Weatherman IPC**: Eorzea-time override + displayed weather + `IsWeatherCustom`
  (falls back to `EnvManager+0x26` true-weather byte). Subscribers **retry on a
  30s throttle** — Weatherman registers gates on its first framework tick, which
  can land after our first check; a one-shot latch blinded us permanently.
- **Emote hook**: `PlayEmote` detour records self-emotes for trigger kind 15 (per-node
  seen-tracking, each node fires once per emote).
- **Chat commands**: trigger kind 17 registers a slash command per node (local player).
- **Camera/player tracking**: world-cam offsets (+0x60/64/68, first-person flag +0x180),
  weapon sheath, HP/MP/dead/casting/status reads, nearby dead-player count — all guarded.
- **Game client size**: `TryGetGameClientSize` (client rect, `FFXIVGAME` window) feeds
  the Resolution sensor + coords visibility. False when unknown.
- **Pin actions**: kind 19 `Send Command` goes through `UIModule.ProcessChatBoxEntry`
  (game chat submit — NOT `CommandManager.ProcessCommand`, which drops game commands).
- **Eorzea time**: cached once per update (`_eorzeaNow`).
- **Wall zone tracking**: `_wallTerritory` + `_lastTeleportCast` (5/6) + KeepZones; see §14.
  **Cold-adopt**: first zone sighting while already live in-world (hot-enable, tracked via
  `_sawPauseSinceBoot`) adopts the territory silently — no entry actions, so home doors
  don't phantom-latch for a zone never entered. Boots that saw loading keep entry actions.
- **Wireframe gate**: `IsInWorld()` (logged in + real territory + not between areas);
  zone/door wireframes don't draw on title/loading screens.
- **Cutscene/duty ticks**: `TickCutscenePreset` (stash + force + restore, §18),
  `TickDutyPreset` (duty first so cinematics win ties), `TickDlssDrivers` (node
  demand > cutscene automation > manual, §17), `TickDlssF6Mirror` (F6 edge mirror
  + log-tail sync, §17), `TickDlssCutscene` folded into the drivers arbiter.
- **Stale customs reset**: persisted weather/time/day customs clear on every load
  (render patches can't survive reload; stale `true` bypasses refusal checks).

## 6. Data model (`DynamicAnimation.cs`)

Sidecar: `<preset>.anim.json`. `DynamicPresetData { Version, Enabled, Configs[], Nodes[], Edges[] }`.
`DynamicAnimConfig { Name, Keyframes[] }` ("Primary" is main).
`DynamicKeyframe { Id, Name, TimeSeconds, TechStates{}, Uniforms{file->{uname->{Value,BaseType}}}, IsPrimary, Sparse, TickedTechs[], TickedUniforms[], TechOrder[] }`.

Node fields of note: `Id, NodeNum, Nickname, Config, KeyframeId, Source, X, Y, Collapsed`,
`TriggerKind` 0–20, `Hotkey`, `ThresholdPct/ThresholdMax/ThresholdRevert`, `StatusIds`,
`CastActionIds`, `EmoteIds`, `QolbarSet`, `DeadCount`, `ChatCommand`, envelope timers,
`StayWhilePresent`, `StartValues{}` (+`MidValues{}`), phase `FromPin`s, `InPinMode`, trigger-19 pin actions,
`Timers[] + StopIfStarterOff` (timer nodes), `Doors[] + SelectedDoorId` (wall),
`Zones[] + SelectedZoneId` (coords), `LocUseCamera/ShowVolume` (node-level),
`TimeFadeSec`, `TimeGateOn/Off`, `ResMode/ResCustomH/ResCompare` (resolution),
`PresetPath` (preset nodes).

- `DynamicTimer { Id, DelaySec=5, StaySec }`. `DynamicDoor { Id, LocTerritory, LocXYZ,
  BoxYaw/Pitch/Roll, WallW=10, WallH=4, KeepZones[] }` (+ legacy node-level fields kept as
  fallback/migration source). `DynamicZone { Id, LocTerritory, LocXYZ, LocShape,
  RadiusStart=20, RadiusMax=5, BoxO/I/C + rotation, FadeInSec=1, FadeOutSec=1,
  LocVisMode, LocVisCoverage, LocVisFadeSec=0.1 }`.
- Primary keyframe = full snapshot. Sparse keyframes drive ticked settings only.
  `TickedUniforms` = `"file\0uname"`; `TickedTechs` = `"Tech@File"`. `PurgeUnticked()`
  strips unticked stored values on load.
- **`ForcedStarts` is DELETED** (Beta 0.9.161, see §9). Old sidecars may carry the array;
  System.Text.Json ignores it.
- `EulerBox.Rotate` (YXZ, shared by blend math and wireframes), `Wrap180`.
- `DynamicTimeline`: `EvalFrames`, `ResolveChainFrames` (skips trigger/coords/wall/
  weather/timegate/**timer/dlss/preset/res** sources), `Segment`/`SegmentFactor`, `CircularDist`.
  `DynamicPresetStore` = atomic sidecar load/save.

## 7. Node catalog

| Node | Source | Color | Binds kf? | Pins | Notes |
|---|---|---|---|---|---|
| In-Game Time | `time` | blue | yes | in+out (chain) | Timeline drivers; gateable (All-On) |
| Trigger | `trigger` | orange/brown | yes (dry-run if not) | in + titlebar out + phase pins | Kinds 0–20; envelopes; pin actions (19) |
| Coords Zone | `coords` | teal | no (kind 18) | in + titlebar out | Multi-zone sensor (§13) |
| Door Trigger | `wall` | purple | no | in + titlebar out | Multi-door shared latch (§14) |
| Weather | `weather` | dark red | no | group out-pins only | Sensor groups, per-group out-pins |
| Time Gate | `timegate` | gray | no | out-pin only | ON/OFF window; out-pin only |
| Timer Switch | `timer` | green | no | in-pin + per-row out-pins | Countdown rows (§15) |
| DLSS 5 Trigger | `dlss` | nvidia green | no | in-pin only | Gate-driven NR, In Pin Mode always visible (§17) |
| Preset Trigger | `preset` | cyan | no | in-pin only | Rising edge switches preset (§19) |
| Resolution | `res` | yellow | no | out-pin only | Client-height match, Equal/Higher/Lower (§7.1) |
| Time Lock | `timelock` | magenta | no | in-pin only | Gate-driven Eorzea override: time nodes read its time + fades (§7.2) |
| Location Switch | `spot` | coral | no | out-pin only | Zone list sensor, ON in any listed zone, Reverse flips (§7.3) |

Trigger kinds: 0 Hotkey, 1 InCombat, 2 Mounted, 3 Crafting, 4 Gathering, 5 Weapon Out,
6 Dead, 7 Casting, 8 Emoting, 9 HP %, 10 MP %, 11 Status, 12 QoLBar, 13 First Person,
14 Sit/Sleep, 15 Emote, 16 Dead Players, 17 Chat Command, 18 Location, 19 Pin Activation.
Kind 20 Resolution trigger is **legacy** (hidden from the picker, still evaluated;
shows as "Resolution (legacy)"): superseded by the standalone Resolution node.
Kind 21 Underwater (`ConditionFlag.Diving`, id 81), 22 Flying (`ConditionFlag.InFlight`, id 77).
`SupportsStayHold`: 0–14, 16, 19, 20, 21, 22 (one-shots 15/17 excluded; 18 blends instead).
HP/MP: Revert flips Below↔Above; separated Start/Max = band mode.

### 7.1 Resolution node details

- `ResMode`: 0 = 720p, 1 = 1080p, 2 = 1440p, 3 = 4K (2160p), 4 = Custom (`ResCustomH`).
- `ResCompare`: 0 = Equal (exact), 1 = Higher-or-equal, 2 = Lower-or-equal.
- Engine: `Plugin.ResolutionLive(node)` (exact client-height compare via
  `TryGetGameClientSize`). Shared by the node and the legacy kind-20 sensor.
- UI: Height dropdown + Mode dropdown + Custom-px input (Custom only) + live
  `Now: WxH` readout. Titlebar ON/OFF = live match.

### 7.2 Time Lock node details

- Fields: `TimeLockTimeSec` (default noon), `TimeLockFadeIn/FadeOut` sec (0 = snap).
- Engine (`Plugin.TickTimeLock`): newest activation among wired + satisfied locks
  wins (ties → list order); uniforms dual-evaluate (live + locked) and merge by
  the crossfade; toggles flip at factor ≥ 0.5; provenance ANDed across both evals.
- Scope is time nodes only (uniforms + toggles). Time gates, weather, and the
  toolbar clock stay live. Manual pause holds single-path output; global resume
  wipes lock state. Never a chain member (`timelock` skipped in chain walks,
  excluded from `IsTime`/`IsTimeNode`).

### 7.3 Location Switch node details

- `SpotZones[]` (zone IDs) + `SpotInvert` (Reverse). List starts empty;
  stand somewhere and press `+ Current`.
- Engine (`Plugin.SpotLive`): ON in any listed zone, OFF elsewhere; inverted
  flips both. Pure level, no latch.
- Pure out-pin source (like timegate/res): never gated, never chained, never
  bound (`spot` skipped in chain walks, excluded from `IsTime`/`IsTimeNode`).
  UI: one row per zone (name + x) + `+ Current` + Reverse checkbox.

## 8. Pins, edges, gates

- **Titlebar pins**: time in+out (chain); trigger/coords/door in-pin + titlebar
  out-pin; dlss/preset/timelock in-pin only (no titlebar out); weather group out-pins only;
  timegate/res/spot out-pin only (pure sources, not gateable).
- **Chain edges** (time→time, whole-node): timeline membership. Time→timer whole-node
  edges are allowed as gates. `TimeLinkAllowed` keeps time nodes chain-OR-weather, never both.
- **Gate edges**: target fires only per logic. `InPinMode`: All On (default), All Off,
  One On, One Off. Time nodes hardcoded All-On. Timer nodes require ≥1 incoming edge.
  DLSS/preset nodes show the mode picker always (never an empty node); other
  gate targets show it only while wired (NodeHeight lockstep).
- **Phase pins** (`delay/fadein/stay/fadeout`, trigger rows): sticky levels per run,
  cleared on retrigger. `stay` pings at stay END (release transition for hold mode —
  aborting mid-fade-in pings nothing).
- **Weather pins** (`w:<groupId>`), **timer pins** (`t:<timerId>`, live while output ON;
  dangling pins read satisfied so deleted rows can't wedge nodes off).
- **Exclusivity**: max one weather pin per time node; chained time nodes reject weather
  wires and vice versa; weather dropdowns hide sibling-claimed IDs. Cycle-checked globally.
- `EdgeLive` is per-edge and shared by mode counting and time-ANDing. Gate evaluation is
  one level, chains settle frame by frame.
- DLSS nodes as *sources* read live NR state; preset nodes as sources read their own
  gate state; res nodes as sources read the resolution match; timelock as a source
  reads its own gate state (wired + satisfied).

## 9. Auto-Start rule

Replaces the deleted Force flag (Beta 0.9.161). Per setting, per activation:
**idle FX punches from its authored Start; live FX glides from live values.**
"Enabled" = any same-file technique resolved ON last frame (`_prevToggles`, the full
resolved map snapshotted next to `_prevMirrored`), falling back to driven-provenance
(`_prevBaseOnly`); on any doubt, glide (snap-free direction). Applies uniformly to time
fades, wall latches, and zone/band visit entries. Deliberate re-punch while active is
considered misuse — no flag for it.

## 10. Trigger envelopes

`delay → fade-in → stay/hold → fade-out`, per-run factor `_runF` (rate-based:
`+= dt/Fi`, `-= dt/Fo`; delay/stay on wall clocks; `dt` clamped 0.5s). Zero timers = instant.
- **Delay is a pure windup**: writes NOTHING (uniforms or toggles) — the live world shows
  through — reseeds the attack every frame from live-or-authored, and ticks the clock so
  the first attack frame can't eat the delay as one clamped step.
- **Edge**: `down && !was`. Retriggering an ACTIVE run keeps factor + blend-from (never
  re-capture live mid-run — that double-applies). Fresh edges outside the fade-out grace
  window punch from authored; inside grace, or on takeover of live keys, seed proportional.
- **Proportional attacks** (grace + live takeovers): measure live progress across each
  key's Start→peak span (min over keys/components), start the factor there, solve each
  key's From so output still equals the screen exactly. Constant value-rate, time shrinks
  with distance. In-span → short trip; behind-start or beyond-peak → full trip from
  screen (beyond-peak NEVER instant-completes — that teleported to peak).
- **Hold** (`StayWhilePresent`, state kinds): ramps to peak while held, releases from
  live. Dropping mid-fade-in aborts to release. Hotkey suppressed while typing.
- **HP/MP band mode**: continuous factor like location blends; timed envelope ignored.
- **Release**: always hands to live base/ambient (never authored). Wall unlatches and
  falling bands join the same rule.
- **Newest run wins** shared keys (recency offers, ties → list order); single-run behavior
  byte-identical. The loser keeps ticking underneath and resumes if alive.
- **Toggles** are binary (`ON` while `f>0`): they pop by nature. Shared techs can't
  strobe between overlapping runs (newest owns).
- Phase pings per run; **Send Command** fires once per kind-19 edge.
- Titlebar ON/OFF = run state OR live sensor truth (state kinds only; one-shots
  excluded since reading them consumes the edge — `TriggerSensorLive`).

## 11. Layer priority, ambient landing, crossfades

Precedence: **trigger envelopes > wall/location/band (ambient) > base timeline.**
Active trigger runs claim ticked keys (frozen post-envelope into `envU/envT`); ambient
layers fill unclaimed keys only. Ambient still computes full intent into side channels
(`_ambU/_ambT`, never from a releasing layer — that would double-apply fades).
- **Ambient landing**: releasing keys resolve From = same-frame ambient value when one
  drives the key (trigger → zone handoffs glide instead of cutting), else base. Releases
  carry their authored start as fallback for carrier-less keys only (fixes pool-less
  fade-outs sitting at peak, e.g. Drunk) — pooled releases explicitly ignore it.
- **Ownership crossfade** (0.3s): when trigger ownership of a key jumps between live
  runs (takeover or handback), morph screen → new owner. Fresh punches and final releases
  keep direct paths. Simultaneous-teardown mid-morph can still cut (accepted rarity).

## 12. Time system (chains, freeze, fades)

- Longest connected time-node walk wins (ties → first created, silently).
  `EvalFrames(data, timeGate?)`; gated-off nodes cut the walk.
- Fully gated chain → freeze (hold last), no fallback. No chain → legacy Primary timeline.
- Twins (same `TimeSeconds`) resolve chain-first (midnight-Primary bug class).
- **Fade-in** (`TimeFadeSec`, head setting inherited): OFF→ON ramps frozen→timeline,
  smoothstepped; snapshot at edge; manual grabs skip it; trigger overlays layer on top.
  Uses the auto-Start rule (§9).
- **Fade-out**: symmetric hold-and-ramp for orphaned keys/techs (sparse ticks only —
  full frames never held: 600+ toggle flood).
- BetweenAreas/offline resume wipes fade/run state (NOT wall latches).

## 13. Location system (coords nodes)

- One node holds many **zones** (`Zones[]`, selected-row editing, +Add at player position,
  per-row Shift-gated delete, never the last). Migration: legacy node fields → Zones[0].
- **Shapes per zone**: 0 advanced sphere (outer Start → inner Max smooth ramp),
  1 advanced box, 2 simple sphere (one Radius, binary), 3 simple box (In + Rot, binary).
  Node blend = max over zones.
- **Onset is exactly RadiusStart, full at RadiusMax, nothing outside.** (The old feather
  band was removed in Beta 0.9.191: it contradicted the two rows. Deleted, not configurable.)
- **Simple fades**: per-zone Fade in/out seconds slew the binary target (0 = snap,
  default 1.0). Advanced zones use the same slew path when fade times are set, else direct.
- **Camera visibility** (spheres only, per zone): Off / Center / Sphere(RadiusMax).
  AND-combined with distance; frustum test only (no occlusion); fail-open on read doubt;
  50ms time-based debounce; per-zone fade slew applies. **Coverage** toggle (Sphere mode
  only): scale by visible fraction (32-point Fibonacci lattice, engulfed = 1) with its own
  **Coverage Fade** time (default 0.1s). Monitor bounds for the test live in Settings
  (game-window fractions + outline overlay); degenerate/unknown falls back to full window.
- Uniforms: authored starts only when forced by idle entry, else live base per frame;
  entry blend-from captured once per visit and held (can't switch mid-visit).
- Wireframes draw every zone (sphere radii / oriented boxes), labeled
  `Nickname Zone N` (doors: `Nickname Door N`); hidden unless in-world.
- Legacy kind-18 trigger nodes share the same rows/engine.

## 14. Wall system (door nodes)

- One node holds many **doors** (dropdown + Shift-gated delete + +Add at player pos,
  per-door XYZ/territory/rotation/W/H; node-wide KeepZones + Fade in/out; migration to
  Doors[0]). In-world labels per door.
- **One latch per node shared by all doors**: crossing any door toggles it (enter one,
  leave through another). Progress slews at node fade rates; node outputs max progress.
- **KeepZones chips wrap 2-per-row** (first row shares the label; + Current trails);
  NodeHeight grows +30 per extra row in lockstep.
- Gated doors can't latch (forced off, fade out). Zone changes: teleports drop;
  entering a listed/home zone auto-latches instantly (prog snapped); other transitions
  drop. See §5 for hot-enable cold-adopt.
- Wall attacks use the auto-Start rule with frozen-live capture at the latch edge;
  releases hand to live base. Wall toggles/uniforms yield to trigger-claimed keys.

## 15. Timer Switch nodes

Green utility nodes: in-pin gate (needs ≥1 wire) + per-timer-row out-pins (`t:`),
no titlebar out-pin, no keyframe, never in chains. Rising gate edge (re)starts every
timer; each output goes ON after Delay, OFF after Stay (Stay 0 = 0.25s activation ping
so edge detectors see it). **Stop if Starter Off** (default off = fire-and-forget):
cancels all timers + outputs when the starter drops. In Pin Mode dropdown when wired.
Titlebar shows active while anything armed/firing.

## 16. Weather system + built-in override

- Sensor source: built-in override first, then Weatherman `GetDisplayedWeather`,
  then fallback `EnvManager+0x26` (255 = unknown). `GetWeatherList()` from Lumina,
  ID-sorted. Groups: multi-select + Any (not-claimed-elsewhere), exclusivity across
  sibling selecting groups, green/orange live/wired styling. Engine: pure level
  evaluation per frame; feeds gates, never envelopes.
- **Built-in override** (`WeatherOverride.cs`, ported render patches from the edited
  Weatherman fork; needs the `ECommons` package): `RenderWeatherPatch` +
  `RenderSunlightShadowPatch` (index via ported `.lvb` parse in `LvbFile.cs`),
  `RenderTimePatch` (time-of-day freeze), `RenderMoonPatch` (day 1–32). Skipped v1:
  sound hook, BGM hook, per-zone persistence, blacklists.
- **Gating (all must pass before anything touches memory)**:
  master switch → external-Weatherman IPC refusals (weather/time separately, with
  visible lockouts + chat feedback) → silent byte pre-verify (own sig scan; ECommons
  verifies noisily inside its constructors, so objects are constructed lazily, only
  after our check passes). Failed enables recreate dead patch objects.
- **No patch fights**: mid-session external engagement auto-yields ours (one-time
  notice); stale persisted customs clear on load; weather auto-clears on zone change.
- **Weather tab** (quick-control style): master toggle, `Time:` freeze + play button
  (full 24h over configurable 5–600s, frame-delta driven) + right-click precise
  H/M/S popup, `Date:` day slider with moon phases, zone weather radio list
  (normals green) + Reset/Re-apply. Engine (`GetCurrentWeather`,
  `GetEorzeaSeconds`) reads built-in first so nodes/timelines follow forced state.
- **Animator toolbar row** (master-gated): compact time checkbox + play + HH:MM:SS
  slider + zone-only weather dropdown + Reset, under the main toolbar.
- ECommons `EzPatchWithPointer` ctor arg order is `(sig, offset, dataPair,
  pointerIndex, autoEnable: false)` — and `EzSignatureHelper.Initialize` only
  handles `[EzHook]` attributes (a no-op for patch fields; the real scan happens
  in the ctors).
- **Daylight curve (global)**: one measured day in `<game>\daylight.json`
  (`DynamicDaylightStore`, mtime-cached), shared by every preset; nodes enable
  it individually via chain-start Curve 2 (24H Brightness Curve) + Day/Night
  roles. Weather tab (developer-gated) records (60s playback via go-signal),
  imports CSVs (80% coverage gate), and snaps chains to measured extrema.
  Legacy sidecar curves seed the global file once on first sight; global wins.

## 17. DLSS5 integration

Target: `renodx-dlss5.addon64` "Enable DLSS Neural Rendering" (= `ReShade.ini`
`[RenoDX.DLSS5] NeuralUplift`, confirmed via binary strings). The ini value is
**load-time only**; live control is the addon's **F6 hotkey** (verified in
`ReShade.log`: `NR toggled ON/OFF via F6`, including a remote `keybd_event` test).
- **Mechanism that works**: 300ms F6 hold (timestamp release from the tick, no
  thread sleep). 1-frame taps fall between the addon's polls and never land —
  proven by log forensics (8 sent, 0 landed pre-hold; instant landing post-hold).
- **State**: absolute `SetDlssNeuralLive(bool)` (no-op when already there).
  Manual F6 taps mirror back via per-frame edge detect; `ReShade.log` tail
  (~0.5s, offset-tracked) is the authoritative corrector for taps lost in
  framework hitches. Ini write = persistence. Diagnostics at Warning level
  (`DLSS F6 sent`, `tap mirrored`, `synced from log`).
- **Arbitration** (`TickDlssDrivers`, one decision/frame): wired DLSS 5 Trigger
  nodes (OR demand) > `DLSS 5 In Cutscenes` automation > manual checkbox/F6.
- **UI**: Main tab toggle + `DLSS 5 In Cutscenes` (both grey out with red
  `(Not Detected)` when the addon is absent) + live readout; cutscene preset
  pairs with it (§18). Side effect by addon design: F6 also targets party
  member 6 in-game.
- Notes: the user's stack (RenoDX addon + NIGos dlss5-bridge + 3090) is the
  standard community route, running off-spec on Ampere — freeze drivers, keep
  intensity conservative. Depth-buffer auto-pick vs DLSS half-res buffers was
  investigated end-to-end (Generic Depth source read): the manual tick is not
  drivable (runtime handle, no key/API); `[DEPTH] FilterResolutionWidth/Height`
  pin to full res is set-and-forget (row only appears under "Match custom width
  and height exactly"); draw-stats options can't prefer the minimal-draw buffer.
  Addon-side DEPTH rebinding was scoped but shelved by user ("forget it").

## 18. Cutscene / Duty presets

- Main tab pickers (same searchable combo, `(none)` default): **Preset to use in
  Cutscenes** + **Preset to use in Duty**.
- `TickCutscenePreset` (`OccupiedInCutSceneEvent`) and `TickDutyPreset`
  (`BoundByDuty`, id 34): stash live preset on entry, force configured, restore
  on exit. Edge-triggered, skip when already on it / `(none)` / file missing.
  Duty runs first so cinematics win ties. Shared helper:
  `ConfigWindow.ApplyPresetPath` (select + parse + focus + signal).

## 19. Preset management

- Shaders-tab row: preset combo (favorites → `xlAnimPresets\` → `xlPresets\` →
  rest, alphabetical within groups) + Rescan + **New Preset** + **Duplicate
  Preset** (disabled without selection) + orange **Make Preset Dynamic** on its
  own row below the dropdown.
- New/Duplicate open a name modal first (sanitized, `.ini` appended,
  exists-guard): Dynamic → `xlAnimPresets\` (+ live-seeded primary sidecar if
  none, sidecar carried on duplicate), static → `xlPresets\` (created on
  demand). Success rescans, selects, parses, signals.
- Dynamic presets without a sidecar (hand-dropped ini) get a fresh live-seeded
  sidecar on selection instead of the misleading "not a dynamic preset" message;
  present-but-corrupt sidecars are never overwritten.
- **Delete Preset was REMOVED (Beta 0.9.270)** after the resurrection saga:
  ReShade/addon re-saves the still-loaded preset (toggle persistence), so
  deleted files came back smaller and sidecar-less; same-frame swap-then-delete
  loses to the async signal; unverified deletes lied in logs. Delete from
  Explorer with the game closed.
- Missing `xlPresets\` folder and no `xlAnimPresets` special-casing beyond
  `IsDynamicPreset` (path-segment check).

## 20. Canvas UI (`DynamicCanvasWindow.cs`)

- Toolbar: preset, Eorzea clock, centered Add Node menu (Time, Trigger, Coords,
  Door, Weather, Time Gate, Timer Switch, DLSS 5 Trigger, Preset Trigger,
  Resolution, Time Lock, Location Switch), build tag, **No BG** + **Keep Grid** toggles (persisted).
- **No-BG mode**: `NoBackground` window+child flags (style pushes can't do it
  reliably), opaque node bodies, grid optional, toolbar keeps a solid backdrop
  (painted from last frame's measured bottom — painting after covers widgets),
  manual outline (NoBackground eats ImGui's border; content clip eats edges, so
  full-window clip), **smart clickthrough**: `IsClickthrough` toggled per frame
  by mouse position (toolbar strip, node rects via scroll-baked origin math,
  scrollbar strips, resize grip, active drag/link) with a 150ms grace hold.
  No focus latch (deadlocks: clicks never escape a focused window). No-preset
  path forces input on.
- Canvas: grid, middle-drag pan, click-out-then-in linking (cycle-checked), Del key,
  Disconnect/Delete/Duplicate context items (duplicate = JSON deep copy, fresh
  Id/number, +24px offset, no edges).
- Nodes: draggable titlebars (type colors), collapse arrows (persisted), live status
  stamps (trigger run OR sensor truth for state kinds — one-shots excluded since
  reads consume edges; blend / prog / timer activity / time-gate truth / weather
  name / Eorzea time / NR demand / preset gate / resolution match), paint order =
  creation except dragged + focused last (on top), footer `• nickname-or-Node N •`,
  fixed pin geometry, bezier edges (orange = gate target incl. dlss/preset,
  green = chain). Key picker has **(none)** to unbind.
- Editors per type; rows positioned explicitly — every conditional row must reserve
  `NodeHeight` or it bleeds. NodeHeight has per-type buckets + conditional +30s
  (wired InPinMode row, pin-action rows, coverage rows, start/end editor,
  QoLBar tip row, resolution custom row, KeepZones wrap rows) — keep each
  editor's rows in lockstep with its bucket.
- Nicknames: right-click → inline body editor (settings hide during edit), shown in
  footer + wireframes; empty = `Node N`.
- Safe ImGui primitives only (no payload/hover-target APIs).
- Keyframe picker: Primary-config dropdown per node; time editor (HR/MIN/SEC + Now);
  chain starts show Curve + Fade, chained nodes inherit.
- Door/zone dropdowns: per-item Shift-gated delete, never-the-last protection.
- Start/End + Midpoint editors: **uncapped** rows (tall nodes scroll with canvas),
  grouped under bright-cyan per-shader headers (display order only), `xlrc_strength`
  starts draft at `0.000`, Confirm writes + exits; Back exits the empty state.
- Trigger editors: QoLBar Condition Set = Cammy-style live-name dropdown (`Set N`
  fallback for blank names, self-healing refresh, `None` = -1 never fires) +
  Stay tip row; Resolution rows (Height/Mode/Custom/Now) shared with the res node.

## 21. Shaders tab + Main/Settings tabs (`ConfigWindow.cs`)

- Tabs: **Main** (Ko-Fi + Discord buttons top-right, zone list, default/cutscene/
  duty presets, hotkeys incl. Animator toggle, opacity, DLSS section), **Shaders**
  (preset row, effects | settings | keyframes), **Weather**, **Settings** (plain-
  language play-area text).
- **Instant open**: technique indexing is time-sliced background (~10ms/frame)
  with an `Indexing shaders… N left` indicator; preset files priority-parse on
  selection (ALL on-disk copies sharing a filename, so hidden flags union
  across duplicates like the root `crt-royale.fx` stubs). The parsed-set clears
  with the maps (a Rescan otherwise poisons filtering forever — the placeholder
  leak). Late-parsed files seed toggle states from addon ground truth.
- Right panel: Primary keyframes by time, select/rename/delete, + Add Keyframe
  (deep-copies selected non-primary incl. ticks; live-captures otherwise), snapshot
  (±30s replace semantics). Selection is view-focus; idle engine previews selection
  (display only); driving engine owns output. Manual edits become overrides (win now,
  absorbed on snapshot). `FollowLivePreset()` runs headless from the framework thread.
- `hidden=true` techniques (e.g. GPosingway `_x_gposingway_placeholder` stubs)
  never render as rows — mirrors ReShade. One-shot `placeholder row leaked`
  diagnostic exists if it ever regresses.
- **Reset buttons**: per-setting undo (only when value ≠ `.fx` default; type-aware
  compare: numeric tolerance, bool words, color 0–255 vs 0–1 scale, RGB/RGBA, float()
  wraps) + FX-level **Reset to defaults** on both context menus (settings header +
  effects rows). Commits through the normal write path (keyframe override/sidecar/live).
- `.fx` parser: techniques/uniforms/annotations (see §22). Technique sorting with drag
  reorder; nicknames, favorites, search; mirror into panels; toggle batching; sidecar
  debounced saves; `PrimeDynToggles` from live state.
- Pause banner (Shaders: PAUSED/Running). Manual pause holds outputs (§24).
- `DrawZonePresetCombo` is shared (zone rows, default/cutscene/duty pickers, canvas
  preset nodes) with optional explicit width for canvas use.
- `ApplyPresetPath` is the single programmatic preset-switch path.

## 22. .fx parser spec

Uniform loop (`ParseFxFileRecursive`): include resolution (file dir → global dir),
**balance-guarded** comment stripping (BSD `//*` license lines must not eat files),
quote-aware line comments, preprocessor directive-line stripping (except
`#include`/`#define`; branch selection NOT evaluated), `\buniform\s+` matching,
in-string + function-parameter guards, storage-class-tolerant declarator runs, strict
value-type whitelist, first-wins name dedupe, **strict declarator adjacency** (never
steal the next uniform's block; bare `uniform float x = 5;` supported), quote-aware
annotation scanner, **`source`-annotated uniforms skipped** (timer/pingpong/etc. —
ReShade hides them), ctor-syntax default normalization (`float2(...)` + `f` suffixes,
which the addon would drop), bare `min/max/step` fallback, `#define`/`static const`
numeric constant collection with one-operator folds, dimension-macro caps (4096),
`MAKE_DESCRIPTION_VAR(x)`→`x_DESC` expansion when defined exactly so.
`DetectUiType`: explicit string (+`drag`→slider, `list`→combo, bool-words/buttons on
bools→checkbox), else `__UNIFORM_*` macros, else bool→checkbox, else input.
Labels/tooltips unescaped; multi-literal tooltips joined; `ui_category_closed` honored;
int vectors get int sliders; annotation-less ranges stay text inputs.
Techniques (`GetTechniquesFromFx`): line-anchored `technique Name <|{`, own comment
strip, hidden=true detection, adjacent-literal tooltip join. Namespaced techniques,
indented declarations, and annotation-bearing declarations all parse.

## 23. Shader edits (xlrc_strength rollout)

Hand-edited game shaders get `uniform float xlrc_strength` (slider 0–1, default 1.0 =
stock look) + `lerp(original, effected, xlrc_strength)` at the final composite, so
transitions fade instead of toggling. Same fixed name in every file (independent
instances per effect); presence of the name IS the marker (no stock shader ships it).
Backups as `<file>.xlrcbak` (first edit only; byte content, line endings normalized).
**Batch 2 (game dir, 665 files): 240 applied and compiling** — 150 value-style
(`return expr;` finals, proven batch-1 transform) + 91 void-style (`out float4`
finals, new offset-mapped end-insert transform), 50 pre-existing (incl. NGLighting
via its shared `.fxh`), 4 reverted-to-stock with documented reasons. Total
**~290/665** carrying the marker.
- **Postmortems that hardened the pipeline**: (1) `\`-continued `#define` blocks —
  decl_pos stopped mid-macro and DECL split it (Dehaze/Pong/pkd_LayerCake) →
  continuation-swallowing in both scripts, those files correctly manual;
  (2) conditional-only out-writes — reading the out-var in the fade turns a
  warning-level pattern into hard x4000 (FluoroDuoTone) → `out-cond-write` guard
  (top-level full `out =` required; swizzles don't count); (3) body-offset math
  must use the brace start, not the match start (caught pre-apply by the balance
  gate + diff review).
- Remaining manual tail (~371): multi-return finals, no-backbuffer AA/depth/data/
  compute pipelines, technique-less libs, shared-midchain passes, colorspace
  converters, debug views, LUT appliers, blooms, qUINT/PD80 house styles.
- Skipped with reasons: native 0–1 bypass masters (Sepia, DPX, GaussianBlur,
  MagicHDR, Drunk, Clarity2, CA, ChromaticAberration, ColorMatrix, GaussianBloom,
  PD80 Technicolor, Technicolor 1+2); stock do-not-touch (KeepUI×2); non-effects
  (LAUNCHPAD); mask/data/compute pipelines (no backbuffer final); debug views,
  region/sky bypasses, depth-sky guards.
- Batch runners live outside the repo: `fxgen_apply.py` (value), `fxgen_void.py` +
  `fxvoid_run.py` + `fxunified.py` (void + union report `fxreport.txt`), plus
  survey/probe/preview/retry one-offs in `%LocalAppData%\Temp\opencode\`.
  Dry-run default everywhere; `--apply` writes.
- Ship vehicle: `xlrc_patch.py` in the plugin repo consolidates both transforms
  (auto dir-detect, `--apply`/`--restore`/`--filter`/`--force`, game-running
  refusal, no hardcoded paths). Users patch their own files; never ship .fx.
- Zero-dependency ship vehicle: `XlPatcher/` (net8.0 console, runs on the
  XIVLauncher runtime, double-click interactive + CLI parity). Byte-identical
  output to the .py, verified by dry-run parity over all 665 + apply/restore
  round-trips. Landmine found in port: `string.StartsWith(string)` is
  linguistic — U+FEFF is ignorable, so BOM checks must compare chars ordinally
  (plugin's own StartsWith calls all pass explicit Ordinal — unaffected).
- ReShade recompiles live; plugin needs reload/reselect to show new rows.
- **Apply discipline**: game CLOSED for batch writes (recompile storm + locks);
  single-file fixes are fine live; verify via recount + `ReShade.log` error
  review; revert individuals from `.xlrcbak`.

## 24. Engine I/O + pause semantics

- One atomic anim-file write per frame, only on content change; empty = delete (addon
  holds). Toggles: edge-triggered batch file, collapse-to-latest, 30/s budget.
- Provenance tracking (`_prevBaseOnly`) + full toggle mirror (`_prevToggles`).
- Mirror pushes engine output into panels every frame (display only).
- **Pause**: global (BetweenAreas/offline) stops the engine + resume-resets runs/fades.
  Manual (hotkey/file) holds OUTPUTS (anim file incl. change-gate, panels, toggles —
  toggles self-gate in flush) while the sim keeps ticking on live clocks, so resume
  picks up current state instead of replaying stale frames.

## 25. Build / deploy / runbook

- Plugin: `$env:PATH = "C:\Users\FSOS\.dotnet10;$env:PATH"; dotnet build -c Release`
  in `ReshadeController/` → copy `dll+pdb+json` to
  `%AppData%\XIVLauncher\devPlugins\ReshadeController\`. **Dev plugins need explicit
  reload (sometimes full restart — stale builds fake success) — copies do nothing
  until then.** Byte-verify the tag (UTF-16 scan). `ECommons.dll` + `deps.json`
  ship alongside (needs `CopyLocalLockFileAssemblies`).
- Addon: `cmd /c build.bat` in `FFXIVReshadeController/` (MinGW g++). Deploy the
  `.addon` to the game dir with the **game closed** (file lock).
- Shaders: batch edits with game closed; single-file fixes live OK (one recompile).
  Keep `.xlrcbak`s; plugin reload/reselect re-parses (per-session parse cache).
- Sidecar loads on preset switch only — reswitch after hand-editing. Keep `.bak`s;
  JSON must stay valid.
- Dalamud log level is **Warning (3)**: diagnostics must log at Warning+.
  Per-edge envelope logs were demoted to Debug (spam); keep them reachable.
- Repro protocol: reload → confirm tag → single slow cycle, report titlebar + exact
  values per stage → then rapid. Video works (ffmpeg present); prefer timestamps + logs.

## 26. Hard lessons

- Never `find_*` handles; never write in `begin_effects`; never hold locks across ReShade calls.
- Anim/toggle files: atomic tmp+move; change-gated writes; addon tolerates absence.
- Preset-signal writes MUST be queued + retried: the addon's `CreateFileA(..., GENERIC_READ, FILE_SHARE_READ)` poll lock fails atomic replaces mid-poll.
- `ACTIVATE|1` needs the pipe; preset signals need counter bumps; MinGW needs `catch (...)`.
- Technique names ≠ filenames; match tolerantly. `TechniqueSorting=` preserves unknowns.
- Hidden-annotated techniques are compiled but unlisted — mirror the ReShade UI.
- Fresh attacks punch from authored (or nothing when idle and unknowable); grace/
  takeover attacks glide from live with proportional time; releases always land on
  ambient/base; never capture live over a kept factor (double-application).
- Scalar run factors can't serve per-key times: progress takes the min (slow, never violent).
- Overshoot (past peak) counts as zero progress — never instant-complete.
- Full frames are base content: never fade-out-hold them, never expect exclusives.
- Equal `TimeSeconds` twins resolve chain-first.
- OnUpdate ~100ms hitches exist; `dt` clamps at 0.5s. Distrust wall-clock/elapsed parity
  across stalls (live-window vs factor desync class).
- Toggles are binary and can't glide; int uniforms stair-step; colors are identities
  (snap, don't lerp); strength/factor does the visible fading.
- Delay writes nothing (it's a windup, not a hold); tick its clock anyway.
- `#if` inside annotation blocks breaks naive bracket counting; `//`-stripping eats
  `https://` in strings (quote-aware scanning required); balance-guard comment strips.
- HLSL decl-before-use bites across `#if` gates, namespaces, `if/else` chains, and
  include order — verify placement, never assume. Multi-line `#define` continuations
  are invisible to line-based decl scans (swallow them). End-inserts must map offsets
  through comment/directive stripping (match start ≠ brace start). Fades READ the
  out-var: conditional-only writes turn warnings into hard x4000 — require a
  top-level full write.
- ECommons verifies original bytes inside its constructors (noisily): construct patch
  objects lazily, only after a silent self pre-verify. `EzSignatureHelper.Initialize`
  only handles `[EzHook]` attributes — nothing for patch fields.
- External IPC (Weatherman, QoLBar) registers late: never latch a failed subscribe;
  retry throttled. QoLBar set names can all be blank — show `Set N` fallbacks.
- Polled keys need holds, not taps (present-thread polls miss 1-frame presses).
  Log tails beat mirrors for ground truth (hitches eat edges).
- `GetCursorScreenPos` already bakes scroll in — never subtract scroll again.
- No focus latch on clickthrough (deadlock: clicks never escape a focused window).
- Style pushes can't kill window backgrounds reliably — use `NoBackground` flags;
  `NoBackground` also eats ImGui's border (draw it manually, full-window clip).
- Draw order in ImGui is paint order — draw backdrops from last frame's measurements.
- Unverified deletes lie: verify file absence, retry, and watch for resurrection;
  never delete a live preset (it gets re-saved back).
- Lazy caches must clear their parsed-sets with their maps (Rescan poisoned
  hidden-filtering forever). Priority-parse ALL same-named copies (hidden flags
  union across duplicates). Unparsed files must not pass hidden filters silently
  — or, better, make the parse complete before rows need it.
- Lowest-friction failure order for new blend code: continuity first (no snap), then
  timing, then aesthetics.
- Never trust a fix you can't see in logs, titlebars, or frame measurements.
- Stale builds fake success: byte-verify + full restart when the UI disagrees
  with the code. One-shot diagnostics must self-remove or demote (log spam rots trust).
- Settings must not paper over bugs (the reverted Feather): if the model says X, the
  engine must do X.

## 27. Known issues / open audit items

- **Audit #2 (open):** hitch longer than the fade flips a live attack to authored
  mid-ramp (wall-clock window vs clamped factor). Repro: takeover pair, FadeIn 1.0,
  pause 2s mid-fade via shader-pause hotkey, resume, watch the snap. Solo punches
  unaffected (control case).
- **Audit #3 (open):** door–door and zone–zone overlaps are still list-order last-wins
  (no recency/proportional/crossfade like triggers). Repro: two doors/zones, same
  keyframe, both active.
- Simultaneous-teardown mid-crossfade can cut a frame (accepted rarity).
- Stale `fadeout` phase pings persist till retrigger (latch-like by design).
- Unwired weather groups still claim exclusivity.
- Time→time edges are always chain links (no gate-only variant).
- Equal-length chains tie-break silently to first-created.
- Template-pasted (`##index##`) and array uniforms are statically invisible; `#if 0`
  blocks parse as live (same as technique parser); pool-less preset-ON values are
  unknowable to the auto-Start rule.
- Node 17 rapid-tap saga: resolved through b147/b175/b176/b178 (double-apply,
  live-takeover, proportional time, overshoot). Remaining endpoint pops are technique
  toggles (binary, inherent).
- QoLBar set reorder/removal isn't followed live (Cammy remaps via
  Moved/RemovedConditionSet; we don't — re-pick after reorder).
- DLSS NR on pre-RTX-50 (user: 3090): works via lenient community builds, heavy
  perf cost, driver updates may enforce the check. Freeze drivers.
- Generic Depth vs DLSS half-res buffers: `[DEPTH]` resolution pin is the
  supported fix; per-buffer override is not drivable; draw-stats options can't
  prefer minimal-draw buffers. Addon-side DEPTH rebinding scoped, shelved.
- `placeholder row leaked` one-shot diagnostic stays in (silent unless regressed).

## 28. Ideas on deck (not built)

- Live chain highlighting; why-is-off hints for unlit nodes.
- Door latch/unlatch phase pins; wall fade progress as gate source.
- Command queue/cooldown for Send Command spam; weather settle debounce.
- Toggle release modes per keyframe (restore-previous vs base vs hold).
- Edge modes (chain vs gate) for time→time links; In Pin Mode for time nodes.
- Sidecar `Version` + migration log; full-flood toggle path for bulk transitions.
- Perf: throttle anim writes (change-threshold), cache parsed uniform numbers, calm mirror.
- Crossfade duration control; distance-proportional release times.
- Addon-published uniform enumeration (would unlock macro-generated uniforms).
- xrLC batch 3: remaining ~371 manual files (multi-return finals, AA/depth/data
  pipelines, libs, midchain, colorspace, LUTs/blooms/qUINT/PD80 house, 3 macro
  files by hand: Dehaze/Pong/pkd_LayerCake).
- Fire-once latch mode for Preset Trigger nodes.
- QoLBar Moved/RemovedConditionSet remapping (Cammy parity).

---

*Conventions: bump `BuildTag` every deploy; byte-verify; discuss semantics before
building gates/envelopes; evidence before synthesis; keep exactly one source of truth
for version-dependent strings.*
