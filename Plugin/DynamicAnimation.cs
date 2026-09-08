using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace ReshadeController;

// Stage 2 (P1a): dynamic presets. One sidecar file per dynamic preset:
//   <PresetPath>.anim.json  (e.g. xl_Realism.ini.anim.json, next to the preset)
// The primary keyframe is a full snapshot (base values). Later keyframes are
// sparse: they drive only ticked settings, everything else holds the base.
// The engine interpolates each setting between the keyframes carrying it.

public class DynamicUniformValue
{
    public string Value { get; set; } = "";
    public string BaseType { get; set; } = "float";
}

public class DynamicKeyframe
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public int TimeSeconds { get; set; }
    public Dictionary<string, bool> TechStates { get; set; } = new();
    public Dictionary<string, Dictionary<string, DynamicUniformValue>> Uniforms { get; set; } = new();
    // Sparse keyframes drive only ticked settings; the primary (full base)
    // drives everything. Legacy keyframes (Sparse=false) stay full.
    public bool IsPrimary { get; set; }
    public bool Sparse { get; set; }
    public List<string> TickedTechs { get; set; } = new();
    public List<string> TickedUniforms { get; set; } = new();
    // Full technique order snapshot (structural, not tick-gated). Empty =
    // inherit. Latest carrier at or before now wins; no blending.
    public List<string> TechOrder { get; set; } = new();

    // Drop stored values no tick claims (unticked = forgotten).
    public bool PurgeUnticked()
    {
        bool changed = false;
        foreach (var k in TechStates.Keys
                     .Where(k => !TickedTechs.Any(t => string.Equals(t, k, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            TechStates.Remove(k);
            changed = true;
        }
        foreach (var f in Uniforms.Keys.ToList())
        {
            var m = Uniforms[f];
            foreach (var u in m.Keys
                         .Where(u => !TickedUniforms.Any(t => string.Equals(t, f + "\0" + u, StringComparison.OrdinalIgnoreCase))).ToList())
            {
                m.Remove(u);
                changed = true;
            }
            if (m.Count == 0) Uniforms.Remove(f);
        }
        return changed;
    }

    // Tick every stored setting (new snapshots drive everything; trim later).
    public void TickAll()
    {
        TickedTechs = TechStates.Keys.ToList();
        TickedUniforms = (from kvp in Uniforms
                          from u in kvp.Value.Keys
                          select kvp.Key + "\0" + u).ToList();
    }

    // Refresh driven values from a live snapshot. Full frames take
    // everything; sparse frames refresh only ticked settings.
    public void AbsorbLive(DynamicKeyframe live)
    {
        if (IsPrimary || !Sparse)
        {
            TechStates = new Dictionary<string, bool>(live.TechStates, StringComparer.OrdinalIgnoreCase);
            var uni = new Dictionary<string, Dictionary<string, DynamicUniformValue>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in live.Uniforms)
            {
                var m = new Dictionary<string, DynamicUniformValue>(StringComparer.OrdinalIgnoreCase);
                foreach (var u in kvp.Value)
                    m[u.Key] = new DynamicUniformValue { Value = u.Value.Value, BaseType = u.Value.BaseType };
                uni[kvp.Key] = m;
            }
            Uniforms = uni;
            return;
        }
        foreach (var k in TickedTechs)
            if (live.TechStates.TryGetValue(k, out bool v))
                TechStates[k] = v;
        foreach (var k in TickedUniforms)
        {
            int sep = k.IndexOf('\0');
            if (sep < 0) continue;
            var file = k.Substring(0, sep);
            var uname = k.Substring(sep + 1);
            if (live.Uniforms.TryGetValue(file, out var lm) && lm.TryGetValue(uname, out var lv))
            {
                if (!Uniforms.TryGetValue(file, out var m))
                    Uniforms[file] = m = new Dictionary<string, DynamicUniformValue>(StringComparer.OrdinalIgnoreCase);
                m[uname] = new DynamicUniformValue { Value = lv.Value, BaseType = lv.BaseType };
            }
        }
    }
}

public class DynamicAnimConfig
{
    public string Name { get; set; } = "Primary";
    public List<DynamicKeyframe> Keyframes { get; set; } = new();
}

// Recorded daylight curve: raw mean brightness per uniform Eorzea-time bin.
public class DynamicDaylight
{
    public const int Bins = 96;
    public List<float> Values { get; set; } = new();
    public int Samples { get; set; }
    public string RecordedUtc { get; set; } = "";
    public float Min { get; set; }
    public float Max { get; set; }
}

// Node-based timeline routing (P1b). A node binds one config's keyframe at
// one time; edges chain nodes into the evaluation timeline. Unconnected or
// unresolvable nodes are dormant.
public class DynamicAnimNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int NodeNum { get; set; }
    // Optional nickname (set via the canvas context menu). Shown in the
    // node footer and the in-world zone/door labels instead of "Node N".
    public string Nickname { get; set; } = "";
    public string Config { get; set; } = "Primary";
    public string KeyframeId { get; set; } = "";
    public string Source { get; set; } = "time";
    public float X { get; set; } = 40;
    public float Y { get; set; } = 40;
    // Editor-only: collapsed in the canvas (visual only, engine unaffected).
    // Persisted in the sidecar so it survives reloads.
    public bool Collapsed { get; set; }
    // Gate logic for incoming pin edges (trigger/coords/door targets):
    // 0 All On (every source ON), 1 All Off, 2 One On, 3 One Off.
    public int InPinMode { get; set; }
    // Pin-activation action (trigger kind 19): 0 none, 1 send command.
    public int PinAction { get; set; }
    public string PinActionCommand { get; set; } = "";
    // (Removed) ForcedStarts: the auto-Start rule (idle FX punches from
    // authored, live FX glides from live) replaced the manual flag in
    // Beta 0.9.161. Old sidecars may still carry the array; deserialization
    // ignores it.
    // Weather node (Source == "weather"): sensor groups, each with its own
    // out-pin ("w:" + group Id). A group is live while the current weather
    // matches it (Any = anything not claimed by sibling groups).
    public List<DynamicWeatherGroup> WeatherGroups { get; set; } = new();
    // Timer node (Source == "timer"): countdown rows, each with its own
    // out-pin ("t:" + timer Id). A rising edge on the node's in-pin gate
    // starts every timer; each output goes ON DelaySec after the edge and
    // back OFF StaySec later (StaySec 0 = brief activation ping).
    public List<DynamicTimer> Timers { get; set; } = new();
    // Preset node (Source == "preset"): a rising edge on the node's
    // in-pin gate switches ReShade to PresetPath (full .ini path).
    public string PresetPath { get; set; } = "";
    // Timer node: when true, the starter gate going OFF cancels all running
    // timers and drops their outputs. Default false = fire and forget.
    public bool StopIfStarterOff { get; set; }
    // Trigger nodes (Source == "trigger"): hotkey fires an envelope that
    // overlays the bound keyframe's ticked settings in real seconds.
    public int Hotkey { get; set; }
    // Sensor kind: 0 Hotkey, 1 InCombat, 2 Mounted, 3 Crafting, 4 Gathering,
    // 5 WeaponOut, 6 Dead, 7 Casting (+optional action filter), 8 Emoting,
    // 9 HpBelow, 10 MpBelow, 11 StatusPresent, 12 QolbarSet, 13 FirstPerson,
    // 14 SitSleep, 15 Emote, 16 DeadPlayers (count threshold, self excluded),
    // 17 ChatCommand (custom slash command, local player only).
    // Kind 18 = coordinates rows (own node type now, kept for compat).
    // Kind 19 = pin activation: fires its envelope on the rising edge of
    // its own in-pin gate state (needs at least one incoming edge).
    // Kind 20 = window vertical resolution (ResMode preset or ResCustomH).
    // Kind 21 = underwater (ConditionFlag.Diving), 22 = flying
    // (ConditionFlag.InFlight). Pure state sensors, no extra rows.
    // Kinds 0-17, 19 and 20 fire one-shot envelopes on the false->true
    // edge (restart on retrigger); Location blends by radius every frame
    // instead.
    public int TriggerKind { get; set; }
    // Resolution sensor (kind 20): 0 = 720p, 1 = 1080p, 2 = 1440p,
    // 3 = 4K (2160p), 4 = Custom (ResCustomH).
    public int ResMode { get; set; } = 2;
    public int ResCustomH { get; set; } = 1440;
    // Resolution comparison: 0 = Equal, 1 = Higher or equal, 2 = Lower
    // or equal (against the ResMode height above).
    public int ResCompare { get; set; }

    // HP/MP threshold direction: false = Below %, true = Above %.
    public bool ThresholdRevert { get; set; }

    // Kinds supporting hold-at-peak ("stay while present"): hotkey (held,
    // not just pressed) plus all state-based kinds. One-shot events
    // (emote, chat) are excluded.
    public static bool SupportsStayHold(int kind)
        => kind == 0 || kind == 1 || kind == 2 || kind == 3 || kind == 4
        || kind == 5 || kind == 6 || kind == 7 || kind == 8
        || kind == 9 || kind == 10 || kind == 11 || kind == 12
        || kind == 13 || kind == 14 || kind == 16 || kind == 19
        || kind == 20 || kind == 21 || kind == 22;
    public uint LocTerritory { get; set; }
    public float LocX { get; set; }
    public float LocY { get; set; }
    public float LocZ { get; set; }
    public float RadiusStart { get; set; } = 20;
    public float RadiusMax { get; set; } = 5;
    // Location shape: 0 sphere (radii), 1 box (half extents per axis).
    public int LocShape { get; set; }
    public float BoxOX { get; set; } = 20;
    public float BoxOY { get; set; } = 20;
    public float BoxOZ { get; set; } = 20;
    public float BoxIX { get; set; } = 5;
    public float BoxIY { get; set; } = 5;
    public float BoxIZ { get; set; } = 5;
    // Outer-box center offset from the node center (world axes). Clamped so
    // the outer box always still contains the inner box.
    public float BoxOCX { get; set; }
    public float BoxOCY { get; set; }
    public float BoxOCZ { get; set; }
    public bool ShowVolume { get; set; } = true;
    public bool LocUseCamera { get; set; }
    // Location nodes (kind 18 on trigger/coords sources) hold one or more
    // zones, each with its own shape. The node-level Loc/shape/box/radii
    // fields above are the pre-multi-zone storage: the first UI draw
    // migrates them into Zones[0] and the engine falls back to them while
    // Zones is empty.
    public List<DynamicZone> Zones { get; set; } = new();
    // Id of the zone shown in the canvas editor.
    public string SelectedZoneId { get; set; } = "";
    // Wall triggers (Source == "wall"): vertical plane rect centered at
    // LocXYZ, yaw-rotated, WallW wide by WallH tall. Crossing it toggles a
    // latch (fade in/out, no stay/delay); zone changes reset the latch.
    // Wall LocXYZ is stored in world coords so rotation pivots the door in
    // place (it used to be the rotated local frame, which swung the door
    // around the origin on rotate). LocLocal marks nodes handled by the
    // world-storage migration.
    public bool LocLocal { get; set; }
    public float WallW { get; set; } = 10;
    public float WallH { get; set; } = 4;
    public List<uint> KeepZones { get; set; } = new();
    // Door nodes (Source == "wall") hold one or more doors. The node-level
    // Loc/rotation/size/KeepZones fields above are the pre-multi-door
    // storage: the first UI draw migrates them into Doors[0] and the
    // engine falls back to them while Doors is empty.
    public List<DynamicDoor> Doors { get; set; } = new();
    // Id of the door shown in the canvas editor.
    public string SelectedDoorId { get; set; } = "";
    public float BoxYaw { get; set; }
    public float BoxPitch { get; set; }
    public float BoxRoll { get; set; }
    public int EmoteId { get; set; }
    public List<int> EmoteIds { get; set; } = new();
    public int DeadCount { get; set; } = 1;
    public string ChatCommand { get; set; } = "";
    public float ThresholdPct { get; set; } = 25;
    // HP/MP band end. Equal to ThresholdPct (start) = legacy binary edge
    // + timed envelope; separated = continuous band (start -> max maps to
    // factor 0 -> 1, envelope timers ignored).
    public float ThresholdMax { get; set; } = 25;
    public int StatusId { get; set; }
    public List<int> StatusIds { get; set; } = new();
    public int QolbarSet { get; set; }
    public int CastActionId { get; set; }
    public List<int> CastActionIds { get; set; } = new();
    public float DelaySec { get; set; }
    public float FadeInSec { get; set; } = 1;
    public float StaySec { get; set; } = 5;
    public float FadeOutSec { get; set; } = 1;
    // Trigger kind 11 (status): hold at peak while the status is present
    // instead of the fixed Stay timer, then fade out on release.
    public bool StayWhilePresent { get; set; }
    // Time nodes: seconds to ramp from frozen values to timeline values
    // when switching gated-off -> on (0 = snap, current behavior).
    public float TimeFadeSec { get; set; }
    // Chain-start transfer function for segment blending: 0 Smoothstep
    // (current behavior, default), 1 Linear, 2 24H Brightness Curve
    // (segment progress paced by the sidecar daylight curve;
    // falls back to Smoothstep without data). Chained nodes inherit.
    public int CurveMode { get; set; }
    // 24H Brightness Curve role for this node: 0 Night (measured darkest
    // second, low point), 1 Day (brightest, high point). Set by the
    // day/night snap (endpoints by time order) or the canvas Day/Night
    // dropdown; picking a role jumps the bound keyframe to its extreme.
    public int DayNightRole { get; set; }
    // Time Lock nodes (Source == "timelock"): gate-driven Eorzea-time
    // override. While active, time nodes evaluate at TimeLockTimeSec
    // instead of live time (output crossfade over the fade times).
    // Never a chain member, never bound to a keyframe.
    public int TimeLockTimeSec { get; set; } = 43200;
    public float TimeLockFadeIn { get; set; } = 1f;
    public float TimeLockFadeOut { get; set; } = 1f;
    // Location Switch nodes (Source == "spot"): a plain zone-ID list.
    // ON while standing in any listed zone, OFF elsewhere; SpotInvert
    // flips both. Sensor only, never bound.
    public List<uint> SpotZones { get; set; } = new();
    public bool SpotInvert { get; set; }
    // Time gate nodes (Source == "timegate"): ON window in Eorzea seconds.
    // Equal values = always on. Overnight ranges wrap past midnight.
    public int TimeGateOn { get; set; }
    public int TimeGateOff { get; set; }
    // Start (= end) values for the envelope: fade runs start -> keyframe
    // peak -> start. Missing entries fall back to live base values.
    public Dictionary<string, Dictionary<string, DynamicUniformValue>> StartValues { get; set; } = new();
    // Midpoint shaping for location blends: when UseMidpoint is on, the
    // blend runs start -> mid -> peak with the midpoint pinned at factor
    // 0.5, so Mid placement bends the easing (slow-then-fast or reverse).
    public bool UseMidpoint { get; set; }
    public Dictionary<string, Dictionary<string, DynamicUniformValue>> MidValues { get; set; } = new();
}

public class DynamicZone
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public uint LocTerritory { get; set; }
    public float LocX { get; set; }
    public float LocY { get; set; }
    public float LocZ { get; set; }
    // Shape: 0 advanced sphere (outer/inner radii + skirt), 1 advanced
    // box (outer/inner + smooth edge), 2 simple sphere (one radius,
    // binary in/out), 3 simple box (inner only, binary in/out).
    // Per zone, so one node can mix all four.
    public int LocShape { get; set; }
    public float RadiusStart { get; set; } = 20;
    public float RadiusMax { get; set; } = 5;
    public float BoxOX { get; set; } = 20;
    public float BoxOY { get; set; } = 20;
    public float BoxOZ { get; set; } = 20;
    public float BoxIX { get; set; } = 5;
    public float BoxIY { get; set; } = 5;
    public float BoxIZ { get; set; } = 5;
    public float BoxOCX { get; set; }
    public float BoxOCY { get; set; }
    public float BoxOCZ { get; set; }
    public float BoxYaw { get; set; }
    public float BoxPitch { get; set; }
    public float BoxRoll { get; set; }
    // Simple shapes (2/3) only: seconds to fade the binary in/out.
    // 0 = snap. Advanced shapes blend positionally and ignore these.
    public float FadeInSec { get; set; } = 1;
    public float FadeOutSec { get; set; } = 1;
    // Camera visibility gate (sphere shapes only): 0 off, 1 the zone counts
    // only while its center is in camera view, 2 while any part of the
    // RadiusMax sphere is (camera inside counts as visible). Frustum test
    // only — occluders don't block it.
    public int LocVisMode { get; set; }
    // Sphere visibility mode only: scale strength by the visible fraction
    // of the sphere instead of the all-or-nothing gate.
    public bool LocVisCoverage { get; set; }
    // Coverage mode only: seconds to glide the fraction (0 = snap).
    public float LocVisFadeSec { get; set; } = 0.1f;
    // Visibility gate strength, percent 0-100 (default 100 = today: hidden
    // scales to 0). Lower values leave a floor: multiplier runs from
    // (1 - R) fully hidden to 1 fully visible, so 75 only ever takes 75%.
    public float LocVisMaxReduction { get; set; } = 100f;
}

public class DynamicDoor
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public uint LocTerritory { get; set; }
    public float LocX { get; set; }
    public float LocY { get; set; }
    public float LocZ { get; set; }
    public float BoxYaw { get; set; }
    public float BoxPitch { get; set; }
    public float BoxRoll { get; set; }
    public float WallW { get; set; } = 10;
    public float WallH { get; set; } = 4;
    public List<uint> KeepZones { get; set; } = new();
}

public class DynamicWeatherGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    // Any = matches whatever isn't claimed by the node's other groups.
    public bool Any { get; set; }
    public List<int> WeatherIds { get; set; } = new();
}

public class DynamicTimer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    // Seconds after the starter edge until this timer's output goes ON.
    public float DelaySec { get; set; } = 5;
    // Seconds the output stays ON (0 = brief activation ping).
    public float StaySec { get; set; }
}

public class DynamicAnimEdge
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    // Source out-pin: "" = whole node running state, else a trigger phase
    // pin ("delay"/"fadein"/"stay"/"fadeout") that goes true when that
    // envelope phase completes (sticky until the run ends/retriggers),
    // a weather group pin ("w:" + group Id), or a timer pin ("t:" + timer
    // Id) that is live while that timer's output is ON.
    public string FromPin { get; set; } = "";
}

public class DynamicPresetData
{
    public int Version { get; set; } = 1;
    // Build tag that last saved this sidecar (forensics: which logic
    // authored it). Stamped on every save, never enforced on load.
    public string SavedBy { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<DynamicAnimConfig> Configs { get; set; } = new();
    // Recorded daylight curve (brightness over Eorzea time): drives the
    // chain-start transfer function when CurveMode == 2. Bins cover the
    // full day uniformly; values are raw means (normalized per segment
    // at use, so absolute levels don't matter).
    public DynamicDaylight? Daylight { get; set; }

    public DynamicAnimConfig? GetConfig(string name)
    {
        foreach (var c in Configs)
            if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                return c;
        return null;
    }

    public List<DynamicAnimNode> Nodes { get; set; } = new();
    public List<DynamicAnimEdge> Edges { get; set; } = new();

    // Enforce unticked = forgotten on sparse keyframes. Returns true if
    // anything was dropped.
    public bool PurgeUnticked()
    {
        bool changed = false;
        foreach (var c in Configs)
            foreach (var kf in c.Keyframes)
                if (kf.Sparse && !kf.IsPrimary)
                    changed |= kf.PurgeUnticked();
        return changed;
    }

    public DynamicAnimConfig GetOrCreatePrimary()
    {
        var p = GetConfig("Primary");
        if (p == null) { p = new DynamicAnimConfig { Name = "Primary" }; Configs.Add(p); }
        return p;
    }

    // System.Text.Json deserializes dictionaries with the default comparer;
    // restore case-insensitive lookups after load.
    public void Normalize()
    {
        foreach (var c in Configs)
        {
            foreach (var kf in c.Keyframes)
            {
                kf.TechStates = new Dictionary<string, bool>(kf.TechStates, StringComparer.OrdinalIgnoreCase);
                var uni = new Dictionary<string, Dictionary<string, DynamicUniformValue>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in kf.Uniforms)
                    uni[kvp.Key] = new Dictionary<string, DynamicUniformValue>(kvp.Value, StringComparer.OrdinalIgnoreCase);
                kf.Uniforms = uni;
            }
        }
        // One keyframe set per preset: fold everything into Primary so the
        // third column and canvas never juggle groups. Nodes follow.
        var primary = GetConfig("Primary");
        foreach (var c in Configs.ToList())
        {
            if (c == primary) continue;
            if (primary == null)
            {
                foreach (var n in Nodes)
                    if (string.Equals(n.Config, c.Name, StringComparison.OrdinalIgnoreCase))
                        n.Config = "Primary";
                c.Name = "Primary";
                primary = c;
                continue;
            }
            foreach (var kf in c.Keyframes) primary.Keyframes.Add(kf);
            foreach (var n in Nodes)
                if (string.Equals(n.Config, c.Name, StringComparison.OrdinalIgnoreCase))
                    n.Config = "Primary";
            Configs.Remove(c);
        }
        primary?.Keyframes.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
        // Exactly one primary (the full base): backfill legacy sidecars.
        if (primary != null && primary.Keyframes.Count > 0 && !primary.Keyframes.Any(k => k.IsPrimary))
            primary.Keyframes.OrderBy(k => k.TimeSeconds).First().IsPrimary = true;
        // Migrate legacy single ID picks into the multi lists.
        foreach (var n in Nodes)
        {
            if (n.EmoteIds.Count == 0 && n.EmoteId > 0) { n.EmoteIds.Add(n.EmoteId); n.EmoteId = 0; }
            if (n.StatusIds.Count == 0 && n.StatusId > 0) { n.StatusIds.Add(n.StatusId); n.StatusId = 0; }
            if (n.CastActionIds.Count == 0 && n.CastActionId > 0) { n.CastActionIds.Add(n.CastActionId); n.CastActionId = 0; }
            // Coordinates split out of trigger nodes into their own type.
            if (string.Equals(n.Source, "trigger", StringComparison.OrdinalIgnoreCase) && n.TriggerKind == 18)
            {
                n.Source = "coords";
                Edges.RemoveAll(e => e.From == n.Id || e.To == n.Id);
            }
        }
    }
}

public static class DynamicTimeline
{
    public static int CircularDist(int a, int b)
    {
        int d = Math.Abs(a - b) % 86400;
        return Math.Min(d, 86400 - d);
    }

    public static (DynamicKeyframe Prev, DynamicKeyframe Next) Segment(List<DynamicKeyframe> frames, int now)
    {
        var sorted = frames.OrderBy(f => f.TimeSeconds).ToList();
        if (sorted.Count == 1) return (sorted[0], sorted[0]);
        DynamicKeyframe? prev = null;
        DynamicKeyframe? next = null;
        foreach (var f in sorted)
        {
            if (f.TimeSeconds <= now) prev = f;
            if (f.TimeSeconds >= now && next == null) next = f;
        }
        prev ??= sorted[^1];
        next ??= sorted[0];
        return (prev, next);
    }

    public static DynamicKeyframe PrevFrame(List<DynamicKeyframe> frames, int now) => Segment(frames, now).Prev;

    // Resolve the evaluation timeline: longest connected node chain wins
    // (nodes bind config keyframes by exact time); anything else falls back
    // to the legacy Primary-keyframes timeline. Returns live refs.
    // timeGate, when given, cuts the walk at switched-off time nodes.
    public static List<DynamicKeyframe>? EvalFrames(DynamicPresetData? data, Func<DynamicAnimNode, bool>? timeGate = null)
    {
        var chain = ResolveChainFrames(data, timeGate);
        if (chain != null) return chain;
        if (data == null || !data.Enabled) return null;
        var primary = data.GetConfig("Primary") ?? data.Configs.FirstOrDefault();
        if (primary == null || primary.Keyframes.Count == 0) return null;
        return primary.Keyframes;
    }

    private static DynamicKeyframe? ResolveNode(DynamicPresetData data, DynamicAnimNode node)
    {
        var cfg = data.GetConfig(node.Config);
        if (cfg == null || string.IsNullOrEmpty(node.KeyframeId)) return null;
        foreach (var kf in cfg.Keyframes)
            if (kf.Id == node.KeyframeId) return kf;
        return null;
    }

    public static List<DynamicKeyframe>? ResolveChainFrames(DynamicPresetData? data, Func<DynamicAnimNode, bool>? timeGate = null)
    {
        if (data == null || !data.Enabled) return null;
        if (data.Nodes.Count == 0 || data.Edges.Count == 0) return null;
        var byId = new Dictionary<string, DynamicAnimNode>();
        foreach (var n in data.Nodes)
        {
            if (string.Equals(n.Source, "trigger", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(n.Source, "coords", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(n.Source, "wall", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(n.Source, "weather", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(n.Source, "timegate", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(n.Source, "timer", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(n.Source, "dlss", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(n.Source, "preset", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(n.Source, "res", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(n.Source, "timelock", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(n.Source, "spot", StringComparison.OrdinalIgnoreCase)) continue;
            if (!byId.ContainsKey(n.Id)) byId[n.Id] = n;
        }
        var incoming = new HashSet<string>();
        var outs = new Dictionary<string, List<string>>();
        foreach (var e in data.Edges)
        {
            if (string.IsNullOrEmpty(e.From) || string.IsNullOrEmpty(e.To)) continue;
            if (!byId.ContainsKey(e.From) || !byId.ContainsKey(e.To)) continue;
            if (e.From == e.To) continue;
            incoming.Add(e.To);
            if (!outs.TryGetValue(e.From, out var l)) outs[e.From] = l = new List<string>();
            if (!l.Contains(e.To)) l.Add(e.To);
        }
        List<DynamicKeyframe>? best = null;
        foreach (var start in data.Nodes)
        {
            if (!byId.ContainsKey(start.Id) || incoming.Contains(start.Id)) continue;
            var seen = new HashSet<string>();
            var frames = new List<DynamicKeyframe>();
            int edgeCount = 0;
            var cur = start;
            while (cur != null && seen.Add(cur.Id))
            {
                // Gated-off time node: the timeline cuts here (downstream
                // reads OFF through propagation, so nothing is lost).
                if (timeGate != null && !timeGate(cur)) break;
                var kf = ResolveNode(data, cur);
                if (kf != null && !frames.Contains(kf)) frames.Add(kf);
                if (outs.TryGetValue(cur.Id, out var next) && next.Count > 0 && byId.TryGetValue(next[0], out var nxt))
                {
                    cur = nxt;
                    edgeCount++;
                }
                else cur = null;
            }
            if (edgeCount > 0 && (best == null || frames.Count > best.Count)) best = frames;
        }
        return (best != null && best.Count > 0) ? best : null;
    }

    public static float SegmentFactor(int t0, int t1, int now)
        => SegmentFactor(t0, t1, now, 0, null);

    public static float SegmentFactor(int t0, int t1, int now, int curveMode, DynamicDaylight? dl, bool dayNightPair = false,
        Dictionary<(int t0, int t1), (float mn, float mx)>? dlCache = null)
    {
        int span = (t1 - t0 + 86400) % 86400;
        if (span == 0) return 0;
        int elapsed = (now - t0 + 86400) % 86400;
        float p = Math.Clamp((float)elapsed / span, 0f, 1f);
        if (curveMode == 1) return p;
        if (curveMode == 2 && dl != null && dl.Values != null && dl.Values.Count > 0)
        {
            if (dayNightPair)
            {
                // Day/night pair (exactly 2 carriers): blend = live-curve
                // position between GLOBAL extrema, not segment position.
                // 0 shows the earlier keyframe (night look by convention),
                // 1 the later (day look). Continuous across the wrap
                // boundary, so nothing ever snaps. Extrema come from the
                // per-frame cache (sentinel key) when provided so hundreds
                // of keys don't each scan all bins.
                float gmn = float.MaxValue, gmx = float.MinValue;
                if (dlCache != null && dlCache.TryGetValue((int.MinValue, int.MinValue), out var gmm))
                {
                    gmn = gmm.mn; gmx = gmm.mx;
                }
                else
                {
                    foreach (var s in dl.Values)
                    {
                        if (s < gmn) gmn = s;
                        if (s > gmx) gmx = s;
                    }
                    dlCache?.Add((int.MinValue, int.MinValue), (gmn, gmx));
                }
                if (gmx - gmn < 1e-6f) return p;
                float gv = SampleDaylight(dl.Values, ((now % 86400) + 86400) % 86400);
                float f = Math.Clamp((gv - gmn) / (gmx - gmn), 0f, 1f);
                return t0 <= t1 ? f : 1f - f;
            }
            float te = (t0 + p * span) % 86400;
            float v = SampleDaylight(dl.Values, te);
            // Normalize over THIS segment so it still traverses full A->B:
            // flat stretches hold still, steep stretches rush. The scan is
            // cached per (t0,t1) for the frame: every key on the same
            // segment shares it instead of re-scanning 64 samples.
            float mn = float.MaxValue, mx = float.MinValue;
            if (dlCache != null && dlCache.TryGetValue((t0, t1), out var smm))
            {
                mn = smm.mn; mx = smm.mx;
            }
            else
            {
                const int N = 64;
                for (int i = 0; i <= N; i++)
                {
                    float s = SampleDaylight(dl.Values, (t0 + span * i / (float)N) % 86400);
                    if (s < mn) mn = s;
                    if (s > mx) mx = s;
                }
                dlCache?.Add((t0, t1), (mn, mx));
            }
            if (mx - mn < 1e-6f) return p;
            return Math.Clamp((v - mn) / (mx - mn), 0f, 1f);
        }
        return p * p * (3f - 2f * p);
    }

    private static float SampleDaylight(List<float> bins, float te)
    {
        try
        {
            int n = bins.Count;
            if (n == 0) return 0f;
            float x = Math.Clamp(te, 0f, 86399f) / 86400f * n;
            int i0 = (int)Math.Floor(x) % n;
            int i1 = (i0 + 1) % n;
            float f = x - (float)Math.Floor(x);
            return bins[i0] + (bins[i1] - bins[i0]) * f;
        }
        catch { return 0f; }
    }

    // Live-curve position between the GLOBAL extrema (0 = darkest, 1 =
    // brightest). NaN when no usable data. Day/night pairs blend by this
    // instead of segment position, so the pooled primary can never own
    // the wrap hours and snap at the extrema.
    public static float DaylightGlobalFactor(DynamicDaylight? dl, int now)
    {
        try
        {
            var bins = dl?.Values;
            if (bins == null || bins.Count == 0) return float.NaN;
            float mn = float.MaxValue, mx = float.MinValue;
            foreach (var s in bins)
            {
                if (s < mn) mn = s;
                if (s > mx) mx = s;
            }
            if (mx - mn < 1e-6f) return float.NaN;
            float v = SampleDaylight(bins, ((now % 86400) + 86400) % 86400);
            return Math.Clamp((v - mn) / (mx - mn), 0f, 1f);
        }
        catch { return float.NaN; }
    }

    // Daylight extrema: bin-center Eorzea seconds of the darkest and
    // brightest bins. Used to snap chain endpoints to the measured
    // day/night instead of hand-placing times (engine untouched).
    public static (int MinSec, int MaxSec) DaylightExtrema(List<float>? bins)
    {
        try
        {
            int n = bins?.Count ?? 0;
            if (n == 0) return (0, 43200);
            int iMin = 0, iMax = 0;
            for (int i = 1; i < n; i++)
            {
                if (bins![i] < bins[iMin]) iMin = i;
                if (bins[i] > bins[iMax]) iMax = i;
            }
            float per = 86400f / n;
            return ((int)((iMin + 0.5f) * per) % 86400,
                    (int)((iMax + 0.5f) * per) % 86400);
        }
        catch { return (0, 43200); }
    }

    // Start node of the winning walk (same longest-connected rule as
    // ResolveChainFrames): owner of chain-head settings like CurveMode.
    public static DynamicAnimNode? ResolveChainStartNode(DynamicPresetData? data, Func<DynamicAnimNode, bool>? timeGate = null)
    {
        try
        {
            if (data == null || !data.Enabled) return null;
            if (data.Nodes.Count == 0 || data.Edges.Count == 0) return null;
            var byId = new Dictionary<string, DynamicAnimNode>();
            foreach (var n in data.Nodes)
            {
                if (string.Equals(n.Source, "trigger", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(n.Source, "coords", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(n.Source, "wall", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(n.Source, "weather", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(n.Source, "timegate", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(n.Source, "timer", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(n.Source, "dlss", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(n.Source, "preset", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(n.Source, "res", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(n.Source, "timelock", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(n.Source, "spot", StringComparison.OrdinalIgnoreCase)) continue;
                if (!byId.ContainsKey(n.Id)) byId[n.Id] = n;
            }
            var incoming = new HashSet<string>();
            var outs = new Dictionary<string, List<string>>();
            foreach (var e in data.Edges)
            {
                if (string.IsNullOrEmpty(e.From) || string.IsNullOrEmpty(e.To)) continue;
                if (!byId.ContainsKey(e.From) || !byId.ContainsKey(e.To)) continue;
                if (e.From == e.To) continue;
                incoming.Add(e.To);
                if (!outs.TryGetValue(e.From, out var l)) outs[e.From] = l = new List<string>();
                if (!l.Contains(e.To)) l.Add(e.To);
            }
            DynamicAnimNode? best = null;
            int bestLen = -1;
            foreach (var start in data.Nodes)
            {
                if (!byId.ContainsKey(start.Id) || incoming.Contains(start.Id)) continue;
                if (timeGate != null && !timeGate(start)) continue;
                var seen = new HashSet<string>();
                int len = 0;
                var cur = start;
                while (cur != null && seen.Add(cur.Id))
                {
                    if (timeGate != null && !timeGate(cur)) break;
                    len++;
                    if (outs.TryGetValue(cur.Id, out var next) && next.Count > 0 && byId.TryGetValue(next[0], out var nxt))
                        cur = nxt;
                    else cur = null;
                }
                // First-created wins ties (matches ResolveChainFrames).
                // Lone nodes (no edges) don't count: legacy path keeps
                // plain smoothstep.
                if (len > bestLen) { bestLen = len; best = start; }
            }
            return bestLen >= 2 ? best : null;
        }
        catch { return null; }
    }
}

public static class EulerBox
{
    // YXZ euler rotation for box volumes. Forward maps box-local offsets to
    // world; inverse maps world deltas back into box space. Shared by the
    // blend math and the wireframe so they can never disagree.
    public static Vector3 Rotate(Vector3 v, float yawDeg, float pitchDeg, float rollDeg, bool inverse)
    {
        float yaw = yawDeg * MathF.PI / 180f;
        float pitch = pitchDeg * MathF.PI / 180f;
        float roll = rollDeg * MathF.PI / 180f;
        float cy = MathF.Cos(yaw), sy = MathF.Sin(yaw);
        float cp = MathF.Cos(pitch), sp = MathF.Sin(pitch);
        float cr = MathF.Cos(roll), sr = MathF.Sin(roll);
        float m00 = cy * cr + sy * sp * sr, m01 = -cy * sr + sy * sp * cr, m02 = sy * cp;
        float m10 = sr * cp, m11 = cr * cp, m12 = -sp;
        float m20 = -sy * cr + cy * sp * sr, m21 = sy * sr + cy * sp * cr, m22 = cy * cp;
        if (!inverse)
            return new Vector3(m00 * v.X + m01 * v.Y + m02 * v.Z, m10 * v.X + m11 * v.Y + m12 * v.Z, m20 * v.X + m21 * v.Y + m22 * v.Z);
        return new Vector3(m00 * v.X + m10 * v.Y + m20 * v.Z, m01 * v.X + m11 * v.Y + m21 * v.Z, m02 * v.X + m12 * v.Y + m22 * v.Z);
    }

    public static float Wrap180(float v) => ((v + 180) % 360 + 360) % 360 - 180;
}

public static class EorzeaFormat
{
    public static int TimeStringToSeconds(string time)
    {
        var parts = time.Split(':');
        if (parts.Length == 3 && int.TryParse(parts[0], out int h) && int.TryParse(parts[1], out int m) && int.TryParse(parts[2], out int s))
            return Math.Clamp(h * 3600 + m * 60 + s, 0, 86399);
        return 0;
    }

    public static string SecondsToTimeString(int seconds)
    {
        seconds = Math.Clamp(seconds, 0, 86399);
        return $"{seconds / 3600:D2}:{(seconds % 3600) / 60:D2}:{seconds % 60:D2}";
    }
}

public static class DynamicPresetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string SidecarPath(string presetPath) => presetPath + ".anim.json";

    public static DynamicPresetData? Load(string presetPath)
    {
        try
        {
            var path = SidecarPath(presetPath);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<DynamicPresetData>(File.ReadAllText(path), JsonOptions);
        }
        catch { return null; }
    }

    public static void Save(string presetPath, DynamicPresetData data)
    {
        try { data.SavedBy = DynamicCanvasWindow.BuildTag; } catch { }
        var path = SidecarPath(presetPath);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(tmp, path, true);
    }
}

// Global daylight curve: one measured day shared by every preset (nodes
// enable it individually via CurveMode). Lives at <game>\daylight.json.
// Sidecar curves are legacy: when no global file exists, the first preset
// carrying one adopts it into the global file once; global always wins.
public static class DynamicDaylightStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static DynamicDaylight? _cache;
    private static DateTime _cacheWrite = DateTime.MinValue;
    private static string _cachePath = "";

    public static string GlobalPath(string gameDir) => Path.Combine(gameDir, "daylight.json");

    private static bool HasValues(DynamicDaylight? dl)
    {
        try { return dl != null && dl.Values != null && dl.Values.Count > 0; }
        catch { return false; }
    }

    public static DynamicDaylight? Load(string gameDir, DynamicDaylight? sidecarFallback = null)
    {
        try
        {
            var path = GlobalPath(gameDir);
            if (File.Exists(path))
            {
                DateTime w;
                try { w = File.GetLastWriteTimeUtc(path); } catch { w = DateTime.MinValue; }
                if (_cache != null && string.Equals(_cachePath, path, StringComparison.OrdinalIgnoreCase) && w == _cacheWrite && HasValues(_cache))
                    return _cache;
                try
                {
                    var dl = JsonSerializer.Deserialize<DynamicDaylight>(File.ReadAllText(path), JsonOptions);
                    if (HasValues(dl))
                    {
                        _cache = dl;
                        _cacheWrite = w;
                        _cachePath = path;
                        return dl;
                    }
                }
                catch { }
            }
            // One-time adoption: a legacy sidecar curve seeds the global file.
            if (HasValues(sidecarFallback))
            {
                try
                {
                    var tmp = path + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(sidecarFallback, JsonOptions));
                    File.Move(tmp, path, true);
                    try { _cacheWrite = File.GetLastWriteTimeUtc(path); } catch { _cacheWrite = DateTime.UtcNow; }
                    _cache = sidecarFallback;
                    _cachePath = path;
                }
                catch { }
                return sidecarFallback;
            }
            return HasValues(_cache) ? _cache : null;
        }
        catch { return HasValues(sidecarFallback) ? sidecarFallback : null; }
    }

    public static void Save(string gameDir, DynamicDaylight dl)
    {
        try
        {
            var path = GlobalPath(gameDir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dl, JsonOptions));
            File.Move(tmp, path, true);
            try { _cacheWrite = File.GetLastWriteTimeUtc(path); } catch { _cacheWrite = DateTime.UtcNow; }
            _cache = dl;
            _cachePath = path;
        }
        catch { }
    }

    public static void Invalidate()
    {
        try { _cache = null; _cacheWrite = DateTime.MinValue; _cachePath = ""; } catch { }
    }
}
