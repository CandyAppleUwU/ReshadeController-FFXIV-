# FFXIV ReshadeController — Agent Briefing v4

Comprehensive reference for AI agents working on this project. Supersedes `agent_v3.md`
(kept for history) as of plugin build **Beta 0.9.198 - Pre-Release**. Line numbers are
approximate — files have grown a lot since v3 (`Plugin.cs` ~3390, `ConfigWindow.cs` ~3377,
`DynamicCanvasWindow.cs` ~2560).

Two components, one system:

| Component | Path | Language | Builds to |
|---|---|---|---|
| ReShade addon | `C:\Users\FSOS\Documents\Projects\FFXIVReshadeController\` | C++17 (MinGW) | `reshade_controller.addon` in game dir |
| Dalamud plugin | `C:\Users\FSOS\Documents\Projects\ReshadeController\` | C# (.NET 10) | `%AppData%\XIVLauncher\devPlugins\ReshadeController\` |

Game dir: `C:\Steam\steamapps\common\FINAL FANTASY XIV Online\game\`.
Shaders dir: `reshade-shaders\Shaders\` under game dir (610 .fx files).
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
9. [Auto-Start rule (replaces Force)](#9-auto-start-rule)
10. [Trigger envelopes](#10-trigger-envelopes)
11. [Layer priority, ambient landing, crossfades](#11-layer-priority-ambient-landing-crossfades)
12. [Time system](#12-time-system-chains-freeze-fades)
13. [Location system (coords nodes)](#13-location-system-coords-nodes)
14. [Wall system (door nodes)](#14-wall-system-door-nodes)
15. [Timer Switch nodes](#15-timer-switch-nodes)
16. [Weather system](#16-weather-system)
17. [Canvas UI](#17-canvas-ui-dynamiccanvaswindowcs)
18. [Shaders tab + Main/Settings tabs](#18-shaders-tab--mainsettings-tabs-configwindowcs)
19. [.fx parser spec](#19-fx-parser-spec)
20. [Shader edits (xlrc_strength rollout)](#20-shader-edits-xlrc_strength-rollout)
21. [Engine I/O + pause semantics](#21-engine-io--pause-semantics)
22. [Build / deploy / runbook](#22-build--deploy--runbook)
23. [Hard lessons](#23-hard-lessons)
24. [Known issues / open audit items](#24-known-issues--open-audit-items)
25. [Ideas on deck](#25-ideas-on-deck)

---

## 1. Versioning

- `BuildTag` is a single `internal const string` in `DynamicCanvasWindow.cs`:
  `"Beta 0.9.198 - Pre-Release"`. Bump the trailing number every deploy.
- All log tags reference it (`$"…{DynamicCanvasWindow.BuildTag}…"`), so toolbar,
  About, and logs always agree (they drifted before centralization — never split them).
- Byte-verify the deployed dll: UTF-16 scan for the tag string. Old `bNNN` tags are gone.

## 2. Architecture

```
Dalamud plugin (C#, in-game UI + logic, framework thread)
    ↕ signal files in game dir (see §3)
ReShade addon (C++, render thread only for all ReShade API writes)
    ↕ ReShade API
ReShade effects (.fx shaders, some hand-edited, see §20)
```

- The plugin NEVER calls ReShade APIs. The addon NEVER reads game state. Files are the only bridge.
- Plugin logic runs on Dalamud's framework `Update` thread. Addon applies everything on
  ReShade's `reshade_present` thread. `reshade_begin_effects` is intentionally empty.
- All ReShade object resolution is **enumerate-only**. `find_technique` /
  `find_uniform_variable` handles proved unstable. Never reintroduce them.
- **Nothing in the addon changed during the v4 arc.** All v4 work is plugin + shaders.

## 3. Signal-file protocol

All in the game dir. Plugin → addon unless noted. Unchanged since v3.

| File | Format | Purpose |
|---|---|---|
| `ffxiv_reshade_pause` | existence = paused | Global shader pause (zone change, logout, hotkey, manual) |
| `ffxiv_reshade_preset` | `path\|counter` | `set_current_preset_path()` on counter change |
| `ffxiv_reshade_anim` | lines `effect\|uniform\|type\|values` | Per-frame uniform values (change-gated, atomic tmp+move) |
| `ffxiv_reshade_cmd.txt` | lines, consumed+deleted | One-shot: `ACTIVATE\|1`, `SET_PRESET\|path`, `SET_TECHNIQUE\|eff\|tech\|0/1`, `REORDER\|…`, `SET_UNIFORM\|…`, `SAVE` |
| `ffxiv_reshade_toggle` | lines `effect\|tech\|0/1`, consumed+deleted | Technique on/off (edge-triggered, batched, ≤30/s) |
| `ffxiv_reshade_techniques.json` | addon → plugin | Ground truth technique list; hidden techniques excluded |
| `ffxiv_reshade_state.json` | addon → plugin | DISABLED (crash suspect, do not re-enable blindly) |

`ACTIVATE|1` requires the pipe. Preset signals need counter bumps. MinGW needs `catch (...)`.

## 4. ReShade addon

Unchanged since v3 (see `agent_v3.md` §3 for the full description): 16ms poll thread =
file I/O only → `RenderCommand` queue → drained on `on_present` outside the lock; preset
tiers (byte-compare skip → fast 750ms → full enumeration-calm); type-checked uniform sets
against live types; tolerant technique matching; `TechniqueSorting=` persistence preserving
unknowns; global pause via `set_effects_state`; per-frame anim-file apply; technique-list
publishing; one-line overlay.

## 5. Plugin core (`Plugin.cs`)

- `/reshade` commands: open config, pause/resume/toggle/status/help.
- **Zone presets**: `TerritoryChanged` + per-frame check → preset signal + pause file.
  `ApplyZoneConfig` always bumps the preset counter (forces ReShade reload, even on boot).
  `Environment.ProcessPath` = game dir (in-process).
- **Global pause**: `!IsLoggedIn || BetweenAreas` forces pause; resume wipes fade/run
  state + resyncs (instant values, no glides across loads). Wall latches are NOT wiped.
- **Manual pause** (hotkey/file): rendering pauses; see §21 for output-hold semantics.
- **Hotkeys**: shader pause toggle + Shaders-tab hotkey via `GetAsyncKeyState` + modifiers.
- **QoLBar IPC**: optional condition-set link for zone presets and trigger kind 12.
- **Weatherman IPC**: Eorzea-time override + displayed weather (falls back to
  `EnvManager+0x26` true-weather byte).
- **Emote hook**: `PlayEmote` detour records self-emotes for trigger kind 15 (per-node
  seen-tracking, each node fires once per emote).
- **Chat commands**: trigger kind 17 registers a slash command per node (local player).
- **Camera/player tracking**: world-cam offsets (+0x60/64/68, first-person flag +0x180),
  weapon sheath, HP/MP/dead/casting/status reads, nearby dead-player count — all guarded.
- **Pin actions**: kind 19 `Send Command` goes through `UIModule.ProcessChatBoxEntry`
  (game chat submit — NOT `CommandManager.ProcessCommand`, which drops game commands).
- **Eorzea time**: cached once per update (`_eorzeaNow`).
- **Wall zone tracking**: `_wallTerritory` + `_lastTeleportCast` (5/6) + KeepZones; see §14.
  **Cold-adopt**: first zone sighting while already live in-world (hot-enable, tracked via
  `_sawPauseSinceBoot`) adopts the territory silently — no entry actions, so home doors
  don't phantom-latch for a zone never entered. Boots that saw loading keep entry actions.
- **Wireframe gate**: `IsInWorld()` (logged in + real territory + not between areas);
  zone/door wireframes don't draw on title/loading screens.

## 6. Data model (`DynamicAnimation.cs`)

Sidecar: `<preset>.anim.json`. `DynamicPresetData { Version, Enabled, Configs[], Nodes[], Edges[] }`.
`DynamicAnimConfig { Name, Keyframes[] }` ("Primary" is main).
`DynamicKeyframe { Id, Name, TimeSeconds, TechStates{}, Uniforms{file->{uname->{Value,BaseType}}}, IsPrimary, Sparse, TickedTechs[], TickedUniforms[], TechOrder[] }`.

Node fields of note: `Id, NodeNum, Nickname, Config, KeyframeId, Source, X, Y, Collapsed`,
`TriggerKind` 0–19, `Hotkey`, `ThresholdPct/ThresholdMax/ThresholdRevert`, `StatusIds`,
`CastActionIds`, `EmoteIds`, `QolbarSet`, `DeadCount`, `ChatCommand`, envelope timers,
`StayWhilePresent`, `StartValues{}`, phase `FromPin`s, `InPinMode`, trigger-19 pin actions,
`Timers[] + StopIfStarterOff` (timer nodes), `Doors[] + SelectedDoorId` (wall),
`Zones[] + SelectedZoneId` (coords), `LocUseCamera/ShowVolume` (node-level),
`TimeFadeSec`, `TimeGateOn/Off`.

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
  weather/timegate/**timer** sources), `Segment`/`SegmentFactor`, `CircularDist`.
  `DynamicPresetStore` = atomic sidecar load/save.

## 7. Node catalog

| Node | Source | Color | Binds kf? | Notes |
|---|---|---|---|---|
| In-Game Time | `time` | blue | yes | Timeline drivers; chain links; gateable (All-On) |
| Trigger | `trigger` | orange/brown | yes (dry-run if not) | Kinds 0–19; envelopes; phase pins; pin actions (19) |
| Coords Zone | `coords` | teal | no (kind 18) | Multi-zone sensor (§13) |
| Door Trigger | `wall` | purple | no | Multi-door shared latch (§14) |
| Weather | `weather` | dark red | no | Sensor groups, per-group out-pins |
| Time Gate | `timegate` | gray | no | ON/OFF window; out-pin only |
| Timer Switch | `timer` | green | no | Countdown rows, per-timer out-pins (§15) |

Trigger kinds: 0 Hotkey, 1 InCombat, 2 Mounted, 3 Crafting, 4 Gathering, 5 Weapon Out,
6 Dead, 7 Casting, 8 Emoting, 9 HP %, 10 MP %, 11 Status, 12 QoLBar, 13 First Person,
14 Sit/Sleep, 15 Emote, 16 Dead Players, 17 Chat Command, 18 Location, 19 Pin Activation.
(Kind 20 Resolution trigger is legacy: hidden from the picker, still evaluated.)
Standalone switch nodes (no envelope/keyframe, never in chains): Timer Switch,
DLSS 5 Trigger (gate-driven NR), Preset Trigger (rising-edge preset switch),
Resolution (client-height match, out-pin only).
`SupportsStayHold`: 0–14, 16, 19, 20 (one-shots 15/17 excluded; 18 blends instead).
HP/MP: Revert flips Below↔Above; separated Start/Max = band mode.

## 8. Pins, edges, gates

- **Titlebar pins**: time in+out (chain); trigger/coords/door/timer in-pin + titlebar
  out-pin (time/coords/door/trigger; timer and weather have NO titlebar out-pin —
  timer outs live on rows, weather outs on groups). TimeGate out-pin only.
- **Chain edges** (time→time, whole-node): timeline membership. Time→timer whole-node
  edges are allowed as gates. `TimeLinkAllowed` keeps time nodes chain-OR-weather, never both.
- **Gate edges**: target fires only per logic. `InPinMode`: All On (default), All Off,
  One On, One Off. Time nodes hardcoded All-On. Timer nodes require ≥1 incoming edge.
- **Phase pins** (`delay/fadein/stay/fadeout`, trigger rows): sticky levels per run,
  cleared on retrigger. `stay` pings at stay END (release transition for hold mode —
  aborting mid-fade-in pings nothing).
- **Weather pins** (`w:<groupId>`), **timer pins** (`t:<timerId>`, live while output ON;
  dangling pins read satisfied so deleted rows can't wedge nodes off).
- **Exclusivity**: max one weather pin per time node; chained time nodes reject weather
  wires and vice versa; weather dropdowns hide sibling-claimed IDs. Cycle-checked globally.
- `EdgeLive` is per-edge and shared by mode counting and time-ANDing. Gate evaluation is
  one level, chains settle frame by frame.

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

## 16. Weather system

Source: Weatherman `GetDisplayedWeather` → fallback `EnvManager+0x26` (255 = unknown).
`GetWeatherList()` from Lumina, ID-sorted. Groups: multi-select + Any (not-claimed-
elsewhere), exclusivity across sibling selecting groups, green/orange live/wired styling.
Engine: pure level evaluation per frame; feeds gates, never envelopes.

## 17. Canvas UI (`DynamicCanvasWindow.cs`)

- Toolbar: preset, Eorzea clock, centered Add Node menu, build tag (bump EVERY deploy).
- Canvas: grid, middle-drag pan, click-out-then-in linking (cycle-checked), Del key,
  Disconnect/Delete context items. Snapshot/New-keyframe items parked behind `#if false`.
- Nodes: draggable titlebars (type colors), collapse arrows (persisted), live status
  stamps (trigger runs / blend / prog / timer activity / time-gate truth / weather name /
  Eorzea time), footer `• nickname-or-Node N •`, fixed pin geometry, bezier edges
  (orange = gate target, green = chain).
- Editors per type; rows positioned explicitly — every conditional row must reserve
  `NodeHeight` or it bleeds. NodeHeight has per-type buckets + conditional +30s
  (wired InPinMode row, pin-action rows, coverage rows, start/end editor) — keep each
  editor's rows in lockstep with its bucket.
- Nicknames: right-click → inline body editor (settings hide during edit), shown in
  footer + wireframes; empty = `Node N`.
- Safe ImGui primitives only (no payload/hover-target APIs).
- Keyframe picker: Primary-config dropdown per node; time editor (HR/MIN/SEC + Now);
  chain starts show Curve + Fade, chained nodes inherit.
- Door/zone dropdowns: per-item Shift-gated delete, never-the-last protection.
- Start/End editor over bound keyframes (autosave, inline rename/delete/+Add with
  tick-copy semantics); Confirm writes + exits; Back exits the empty state.

## 18. Shaders tab + Main/Settings tabs (`ConfigWindow.cs`)

- Tabs: **Main** (zone list, default preset, hotkeys, opacity), **Settings** (visible
  play-area fractions + outline toggle), **Shaders** (effects | settings | keyframes).
- Right panel: Primary keyframes by time, select/rename/delete, + Add Keyframe
  (deep-copies selected non-primary incl. ticks; live-captures otherwise), snapshot
  (±30s replace semantics). Selection is view-focus; idle engine previews selection
  (display only); driving engine owns output. Manual edits become overrides (win now,
  absorbed on snapshot). `FollowLivePreset()` runs headless from the framework thread.
- **Reset buttons**: per-setting undo (only when value ≠ `.fx` default; type-aware
  compare: numeric tolerance, bool words, color 0–255 vs 0–1 scale, RGB/RGBA, float()
  wraps) + FX-level **Reset to defaults** on both context menus (settings header +
  effects rows). Commits through the normal write path (keyframe override/sidecar/live).
- `.fx` parser: techniques/uniforms/annotations (see §19). Technique sorting with drag
  reorder; nicknames, favorites, search; mirror into panels; toggle batching; sidecar
  debounced saves; `PrimeDynToggles` from live state.
- Pause banner (Shaders: PAUSED/Running). Manual pause holds outputs (§21).

## 19. .fx parser spec

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

## 20. Shader edits (xlrc_strength rollout)

Hand-edited game shaders get `uniform float xlrc_strength` (slider 0–1, default 1.0 =
stock look) + `lerp(original, effected, xlrc_strength)` at the final composite, so
transitions fade instead of toggling. Same fixed name in every file (independent
instances per effect); presence of the name IS the marker (no stock shader ships it).
Backups as `<file>.xlrcbak` (first edit only; byte content, line endings normalized).
51 files so far: 10 hand-cut (Vibrance, Vignette, MeshEdges, Curves, rj_sharpen in
linear space, PD80 Selective, AmbientLight both branches, Depth_Cues, MXAO, NGLighting
in the shared `.fxh` before debug views) + 41 generated (single-return finals, assert-
or-skip pipeline with backups, balance invariant, decl-before-use ordering).
Skipped with reasons: native 0–1 bypass masters, verified by reading (Sepia, DPX,
GaussianBlur, MagicHDR, Drunk, Clarity2, CA, ChromaticAberration, ColorMatrix,
GaussianBloom, PD80 Technicolor, Technicolor 1+2); stock do-not-touch (KeepUI×2);
non-effects (LAUNCHPAD); mask/data/compute pipelines (no backbuffer final).
~100 preset-referenced files remain manual (qUINT family, LUT appliers, blooms, AA,
PD80 house-style `float4(color.xyz, 1.0f)` endings batch well). Debug views, region/sky
bypasses, and depth-sky guards are never faded. ReShade recompiles live; plugin needs
reload/reselect to show new rows. Batch generator: `C:\Users\FSOS\AppData\Local\Temp\opencode\fxgen_apply.py`
(`--apply`; dry-run default; backs up; refuses on imbalance).

## 21. Engine I/O + pause semantics

- One atomic anim-file write per frame, only on content change; empty = delete (addon
  holds). Toggles: edge-triggered batch file, collapse-to-latest, 30/s budget.
- Provenance tracking (`_prevBaseOnly`) + full toggle mirror (`_prevToggles`).
- Mirror pushes engine output into panels every frame (display only).
- **Pause**: global (BetweenAreas/offline) stops the engine + resume-resets runs/fades.
  Manual (hotkey/file) holds OUTPUTS (anim file incl. change-gate, panels, toggles —
  toggles self-gate in flush) while the sim keeps ticking on live clocks, so resume
  picks up current state instead of replaying stale frames.

## 22. Build / deploy / runbook

- Plugin: `$env:PATH = "C:\Users\FSOS\.dotnet10;$env:PATH"; dotnet build -c Release`
  in `ReshadeController/` → copy `dll+pdb+json` to
  `%AppData%\XIVLauncher\devPlugins\ReshadeController\`. **Dev plugins need explicit
  reload — copies do nothing until then.** Byte-verify the tag (UTF-16 scan).
- Addon: `cmd /c build.bat` in `FFXIVReshadeController/` (MinGW g++). Deploy the
  `.addon` to the game dir with the **game closed** (file lock).
- Shaders: edit live (ReShade recompiles); keep `.xlrcbak`s; plugin reload/reselect
  re-parses (per-session parse cache).
- Sidecar loads on preset switch only — reswitch after hand-editing. Keep `.bak`s;
  JSON must stay valid.
- Dalamud log level is **Warning (3)**: diagnostics must log at Warning+.
- Repro protocol: reload → confirm tag → single slow cycle, report titlebar + exact
  values per stage → then rapid. Video works (ffmpeg present); prefer timestamps + logs.

## 23. Hard lessons

- Never `find_*` handles; never write in `begin_effects`; never hold locks across ReShade calls.
- Anim/toggle files: atomic tmp+move; change-gated writes; addon tolerates absence.
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
  include order — verify placement, never assume.
- Lowest-friction failure order for new blend code: continuity first (no snap), then
  timing, then aesthetics.
- Never trust a fix you can't see in logs, titlebars, or frame measurements.
- Settings must not paper over bugs (the reverted Feather): if the model says X, the
  engine must do X.

## 24. Known issues / open audit items

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

## 25. Ideas on deck (not built)

- Live chain highlighting; why-is-off hints for unlit nodes.
- Door latch/unlatch phase pins; wall fade progress as gate source.
- Command queue/cooldown for Send Command spam; weather settle debounce.
- Toggle release modes per keyframe (restore-previous vs base vs hold).
- Edge modes (chain vs gate) for time→time links; In Pin Mode for time nodes.
- Sidecar `Version` + migration log; full-flood toggle path for bulk transitions.
- Perf: throttle anim writes (change-threshold), cache parsed uniform numbers, calm mirror.
- Crossfade duration control; distance-proportional release times.
- Addon-published uniform enumeration (would unlock macro-generated uniforms).
- xrLC batch 2: ~100 manual files by preset-count (qUINT, LUTs, blooms, AA, PD80 house).

---

*Conventions: bump `BuildTag` every deploy; byte-verify; discuss semantics before
building gates/envelopes; evidence before synthesis; keep exactly one source of truth
for version-dependent strings.*
