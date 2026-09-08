using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Interface.Windowing;
using Dalamud.Interface;
using Dalamud.Bindings.ImGui;

namespace ReshadeController;

// Stage 2 P1b: node-based timeline editor. Nodes bind (config, keyframe
// time); edges chain them into the evaluation timeline (longest connected
// chain wins, unconnected nodes are dormant). No hover/target/payload-read
// APIs anywhere here — pins are plain clickable rects (see Shaders tab).
public class DynamicCanvasWindow : Window, IDisposable
{
    private const float NodeW = 220;
    private const float TriggerNodeW = 260;
    private const float LocNodeW = 300;
    private const float WeatherNodeW = 300;
    private const float TimerNodeW = 300;

    private static float NodeWidth(DynamicAnimNode node)
        => IsWeather(node) ? WeatherNodeW : IsTimer(node) ? TimerNodeW : IsTimeGate(node) ? TriggerNodeW : node.TriggerKind == 18 ? LocNodeW : ((!IsTrigger(node) && !IsWall(node)) ? NodeW : TriggerNodeW);
    private const float NodeH = 168;
    private const float TriggerNodeH = 300;

    private static bool IsTrigger(DynamicAnimNode node)
        => string.Equals(node.Source, "trigger", StringComparison.OrdinalIgnoreCase);

    private static bool IsCoords(DynamicAnimNode node)
        => string.Equals(node.Source, "coords", StringComparison.OrdinalIgnoreCase);

    private static bool IsWall(DynamicAnimNode node)
        => string.Equals(node.Source, "wall", StringComparison.OrdinalIgnoreCase);

    private static bool IsWeather(DynamicAnimNode node)
        => string.Equals(node.Source, "weather", StringComparison.OrdinalIgnoreCase);

    private static bool IsTimeGate(DynamicAnimNode node)
        => string.Equals(node.Source, "timegate", StringComparison.OrdinalIgnoreCase);

    private static bool IsTimer(DynamicAnimNode node)
        => string.Equals(node.Source, "timer", StringComparison.OrdinalIgnoreCase);

    private static bool IsDlss(DynamicAnimNode node)
        => string.Equals(node.Source, "dlss", StringComparison.OrdinalIgnoreCase);

    private static bool IsPreset(DynamicAnimNode node)
        => string.Equals(node.Source, "preset", StringComparison.OrdinalIgnoreCase);

    private static bool IsRes(DynamicAnimNode node)
        => string.Equals(node.Source, "res", StringComparison.OrdinalIgnoreCase);

    private static bool IsTimeLock(DynamicAnimNode node)
        => string.Equals(node.Source, "timelock", StringComparison.OrdinalIgnoreCase);

    private static bool IsSpot(DynamicAnimNode node)
        => string.Equals(node.Source, "spot", StringComparison.OrdinalIgnoreCase);

    // Shape names: 0 advanced sphere, 1 advanced box, 2 simple sphere
    // (single radius, binary), 3 simple box (inner only, binary).
    private static string ZoneShapeName(int shape)
        => shape == 2 ? "Sphere (Simple)"
        : shape == 3 ? "Box (Simple)"
        : shape == 1 ? "Box (Advanced)"
        : "Sphere (Advanced)";

    // Extra visibility rows on the selected zone (each reserves 30px in
    // NodeHeight): max reduction (gated sphere), coverage toggle (Sphere
    // visibility), coverage fade (coverage on). Keep in lockstep with the UI.
    private static int ZoneVisExtraRows(DynamicAnimNode node)
    {
        try
        {
            if (node.TriggerKind != 18 || node.Zones == null || node.Zones.Count == 0) return 0;
            var s = node.Zones.Find(x => x.Id == node.SelectedZoneId) ?? node.Zones[0];
            if (s.LocShape != 0 && s.LocShape != 2) return 0;
            if (s.LocVisMode == 0) return 0;
            int n = 1;
            if (s.LocVisMode == 2) { n++; if (s.LocVisCoverage) n++; }
            return n;
        }
        catch { return 0; }
    }

    // Midpoint row shows for kind-18 nodes on advanced shapes (box/sphere
    // need a ramp to shape; binary simples have none). Keep NodeHeight's
    // reserve in lockstep.
    private static bool ShowMidRows(DynamicAnimNode node)
        => node.TriggerKind == 18 && (LocZoneShape(node) == 0 || LocZoneShape(node) == 1);

    private static int LocZoneShape(DynamicAnimNode node)
    {
        try
        {
            if (node.Zones != null && node.Zones.Count > 0)
            {
                var s = node.Zones.Find(x => x.Id == node.SelectedZoneId) ?? node.Zones[0];
                return s.LocShape;
            }
        }
        catch { }
        return node.LocShape;
    }

    private static bool IsTime(DynamicAnimNode node)
        => !IsTrigger(node) && !IsCoords(node) && !IsWall(node) && !IsWeather(node) && !IsTimeGate(node) && !IsTimer(node) && !IsDlss(node) && !IsPreset(node) && !IsRes(node) && !IsTimeLock(node) && !IsSpot(node);

    // Whether a chained time input feeds this node (curve lives on the
    // chain start only).
    private static bool HasTimeInput(DynamicPresetData data, string nodeId)
    {
        try
        {
            foreach (var e in data.Edges)
            {
                if (e.To != nodeId || !string.IsNullOrEmpty(e.FromPin)) continue;
                var s = data.Nodes.Find(n => n.Id == e.From);
                if (s != null && IsTime(s)) return true;
            }
            return false;
        }
        catch { return false; }
    }

    // Chain head for a time node (self when no time input feeds it).
    // Cycle-guarded; falls back to the node itself on any doubt.
    private static DynamicAnimNode? TimeChainHead(DynamicPresetData data, DynamicAnimNode node)
    {
        try
        {
            var seen = new HashSet<string>();
            var cur = node;
            while (cur != null && seen.Add(cur.Id))
            {
                DynamicAnimNode? prev = null;
                foreach (var e in data.Edges)
                {
                    if (e.To != cur.Id || !string.IsNullOrEmpty(e.FromPin)) continue;
                    var s = data.Nodes.Find(n => n.Id == e.From);
                    if (s != null && IsTime(s)) { prev = s; break; }
                }
                if (prev == null) return cur;
                cur = prev;
            }
            return cur;
        }
        catch { return node; }
    }

    // Day/Night role row: shown on chain-start AND chained time nodes
    // while the chain head runs 24H Brightness Curve. Data-independent
    // (role is kept even without recorded data); picking a role jumps
    // the bound keyframe to its extreme only when data exists.
    private bool DayNightRowVisible(DynamicPresetData data, DynamicAnimNode node)
    {
        try
        {
            if (!IsTime(node)) return false;
            if (BoundKeyframe(data, node) == null) return false;
            var head = TimeChainHead(data, node);
            return head != null && head.CurveMode == 2;
        }
        catch { return false; }
    }

    // Time-node input exclusivity: a time node is either chained (time
    // inputs) or weather-gated (max one weather pin), never both. Other
    // targets are unrestricted.
    private static bool TimeLinkAllowed(DynamicPresetData data, string fromId, string fromPin, string toId)
    {
        try
        {
            var to = data.Nodes.Find(n => n.Id == toId);
            var from = data.Nodes.Find(n => n.Id == fromId);
            if (to == null || from == null || !IsTime(to)) return true;
            bool fromIsWeatherPin = !string.IsNullOrEmpty(fromPin) && fromPin.StartsWith("w:");
            bool fromIsChain = IsTime(from) && string.IsNullOrEmpty(fromPin);
            bool hasTimeIn = HasTimeInput(data, toId), hasWeatherIn = false;
            foreach (var e in data.Edges)
            {
                if (e.To != toId) continue;
                if (!string.IsNullOrEmpty(e.FromPin) && e.FromPin.StartsWith("w:")) hasWeatherIn = true;
            }
            if (fromIsWeatherPin && (hasTimeIn || hasWeatherIn)) return false;
            if (fromIsChain && hasWeatherIn) return false;
            return true;
        }
        catch { return true; }
    }

    private string? startEndNode;
    private readonly Dictionary<string, Dictionary<string, string>> startEndDrafts = new();
    private string? midEndNode;
    private readonly Dictionary<string, Dictionary<string, string>> midEndDrafts = new();
    private string weatherSearch = string.Empty;
    private readonly Dictionary<string, string[]> gateTimeDrafts = new();

    // Extra KeepZones chip rows on wall nodes (2 chips/row after the
    // label row, + Current trailing). Matches DrawWallRows layout.
    private static int WallKeepExtraRows(DynamicAnimNode node)
    {
        try
        {
            int n = node.KeepZones?.Count ?? 0;
            if (n <= 2) return 0;
            return ((n - 2 + 1) / 2) + ((n - 2) % 2 == 0 ? 1 : 0);
        }
        catch { return 0; }
    }

    private float NodeHeight(DynamicPresetData data, DynamicAnimNode node)
    {
        // Collapsed = titlebar only (6px pad + 22px header).
        if (node.Collapsed) return 6f + HeaderH;
        // Weather: title + one row per group (combo) + Add button + padding.
        if (IsWeather(node)) return 6f + HeaderH + 8 + node.WeatherGroups.Count * 30 + 30;
        // Timer: title + stop toggle + one row per timer + Add button + padding.
        if (IsTimer(node)) return 6f + HeaderH + 8 + 30 + node.Timers.Count * 30 + 30;
        // DLSS5 trigger: title + in-pin-mode row + footer. The mode row is
        // always shown (never an empty node), unlike the wired-only
        // trigger/coords/door/timer variant.
        if (IsDlss(node)) return 6f + HeaderH + 8 + 30 + 30;
        // Preset trigger: title + preset picker + in-pin-mode row + footer.
        if (IsPreset(node)) return 6f + HeaderH + 8 + 30 + 30 + 30;
        // Resolution switch: title + height dropdown + mode dropdown +
        // live readout + custom row (Custom mode only) + footer. No
        // envelope, no keyframe.
        if (IsRes(node)) return 6f + HeaderH + 8 + 30 + 30 + 30 + (node.ResMode == 4 ? 30 : 0) + 30;
        // Time Lock: title + locked-time row + fade in/out rows + footer
        // (+30 while wired for the in-pin-mode row, reserved generically).
        if (IsTimeLock(node)) return 6f + HeaderH + 8 + 30 + 30 + 30 + 30;
        // Location Switch: title + one row per listed zone (never fewer than
        // one, so the +Current row always fits) + +Current/Reverse row +
        // footer. Pure source, no keyframe.
        if (IsSpot(node)) return 6f + HeaderH + 8 + 30 * SpotZoneRows(node) + 30 + 30;
        if (!IsTrigger(node) && !IsCoords(node) && !IsWall(node))
            return NodeH + 60 - (HasTimeInput(data, node.Id) ? 60 : 0)
                + (DayNightRowVisible(data, node) ? 30 : 0);
        float h = IsWall(node) ? 410f + 30f * WallKeepExtraRows(node)
            : node.TriggerKind == 18
            ? 440f + 30f * ZoneVisExtraRows(node)
            : TriggerNodeH + (node.TriggerKind == 7 ? 30 : 0)
            + ((node.TriggerKind == 9 || node.TriggerKind == 10) ? 60 : 0)
            + ((IsTrigger(node) && node.TriggerKind == 12 && !node.StayWhilePresent) ? 30 : 0)
            + ((IsTrigger(node) && node.TriggerKind == 20) ? 60 : 0)
            + ((IsTrigger(node) && node.TriggerKind == 20 && node.ResMode == 4) ? 30 : 0);
        if (startEndNode == node.Id)
        {
            int rows = 1;
            var kf = BoundKeyframe(data, node);
            if (kf != null)
            {
                // Uncapped: tall nodes scroll with the canvas.
                var tk = kf.TickedUniforms.ToList();
                rows += tk.Count;
                // One file-header row per shader group (see editor).
                rows += tk.Select(k => { int s = k.IndexOf('\0'); return s < 0 ? "" : k.Substring(0, s); }).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            }
            h += rows * 30 + 6;
        }
        if (midEndNode == node.Id)
        {
            int rows = 1;
            var kf = BoundKeyframe(data, node);
            if (kf != null)
            {
                // Uncapped: tall nodes scroll with the canvas.
                var tk = kf.TickedUniforms.ToList();
                rows += tk.Count;
                rows += tk.Select(k => { int s = k.IndexOf('\0'); return s < 0 ? "" : k.Substring(0, s); }).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            }
            h += rows * 30 + 6;
        }
        // Midpoint toggle row (kind-18 advanced shapes only).
        if (ShowMidRows(node))
            h += 30;
        // In-pin-mode row reserves space only while wired (time nodes
        // returned early above, so this is trigger/coords/door/dlss only).
        if (data.Edges.Any(e => e.To == node.Id))
            h += 30;
        // Pin-activation action rows (trigger kind 19 only).
        if (IsTrigger(node) && node.TriggerKind == 19)
            h += 30 + (node.PinAction == 1 ? 30 : 0);
        return h;
    }
    private const float HeaderH = 22;
    private const float RowPadX = 10;

    private readonly Plugin plugin;
    private readonly ConfigWindow config;

    private string? selectedNode;
    private string? dragNode;
    private readonly Dictionary<string, string[]> timeDrafts = new();
    private Vector2 dragDownPos;
    private Vector2 dragStartPos;
    private bool dragMoved;
    private string? pendingLinkFrom;
    // Which out-pin the pending link starts from: "" = whole node (running
    // state), else a trigger phase pin ("delay"/"fadein"/"stay"/"fadeout").
    private string pendingLinkPin = "";
    private int linkCancelCooldown;

    // Pin geometry per node: titlebar in/out dots plus per-phase out dots
    // on trigger timer rows.
    private class PinSet
    {
        public Vector2 In;
        public Vector2 Out;
        public readonly Dictionary<string, Vector2> OutPins = new();
    }

    // Baked per stage (bump the trailing number on every build): proves which code is running.
    internal const string BuildTag = "Beta 0.9.316";

    public DynamicCanvasWindow(Plugin plugin, ConfigWindow config)
        : base("ReShade Animator")
    {
        this.plugin = plugin;
        this.config = config;
        this.Size = new Vector2(960, 640);
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var data = config.ActiveDynData;
        var preset = config.SelectedPreset;
        // Keep the Begin-time background flag in sync even on paths that
        // return before the toolbar. Borders stay (the outline defines
        // the window area in no-background mode).
        try
        {
            if (plugin.Config.AnimatorNoBackground) Flags |= ImGuiWindowFlags.NoBackground;
            else Flags &= ~ImGuiWindowFlags.NoBackground;
        }
        catch { }
        if (data == null || string.IsNullOrEmpty(preset))
        {
            try { IsClickthrough = false; } catch { }
            try
            {
                if (plugin.Config.WeatherControlEnabled)
                    DrawAnimatorWeatherRow();
            }
            catch { }
            ImGui.TextDisabled("Select a dynamic preset (xlAnimPresets) in the Shaders tab first.");
            return;
        }
        // Main-window rect + content top for the toolbar backdrop and the
        // clickthrough zones below (captured while the main window is
        // current, i.e. before BeginChild).
        Vector2 mainPos = ImGui.GetWindowPos();
        Vector2 mainSize = ImGui.GetWindowSize();
        Vector2 contentTop = ImGui.GetCursorScreenPos();

        // No-background mode: solid backdrop behind the toolbar strip only
        // (preset/add-node/tag/No-BG/weather rows), so the controls stay
        // readable over the game. Painted BEFORE the widgets from last
        // frame's measured bottom (painting after would cover them).
        if (plugin.Config.AnimatorNoBackground && toolbarBgBottom > contentTop.Y)
        {
            try
            {
                var c = ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.ChildBg));
                uint barBg = ImGui.ColorConvertFloat4ToU32(new Vector4(c.X, c.Y, c.Z, 0.92f));
                ImGui.GetWindowDrawList().AddRectFilled(
                    new Vector2(mainPos.X, contentTop.Y - 4),
                    new Vector2(mainPos.X + mainSize.X, toolbarBgBottom + 2),
                    barBg);
            }
            catch { }
        }

        ImGui.Text($"Preset: {System.IO.Path.GetFileName(preset)}");
        ImGui.SameLine();
        ImGui.TextDisabled($"Eorzea: {EorzeaFormat.SecondsToTimeString(config.GetEorzeaSec())}");
        ImGui.SameLine();
        float comboW = 140;
        float rowCenter = (ImGui.GetWindowContentRegionMin().X + ImGui.GetWindowContentRegionMax().X - comboW) * 0.5f;
        if (rowCenter > ImGui.GetCursorPosX()) ImGui.SetCursorPosX(rowCenter);
        ImGui.SetNextItemWidth(comboW);
        if (ImGui.BeginCombo("##addnode", "Add Node"))
        {
            if (ImGui.Selectable("In-Game Time"))
                AddNode(data);
            if (ImGui.Selectable("Trigger"))
                AddTriggerNode(data);
            if (ImGui.Selectable("Coords Zone"))
                AddCoordsNode(data);
            if (ImGui.Selectable("Door Trigger"))
                AddWallNode(data);
            if (ImGui.Selectable("Weather"))
                AddWeatherNode(data);
            if (ImGui.Selectable("Time Gate"))
                AddTimeGateNode(data);
            if (ImGui.Selectable("Timer Switch"))
                AddTimerNode(data);
            if (ImGui.Selectable("DLSS 5 Trigger"))
                AddDlssNode(data);
            if (ImGui.Selectable("Preset Trigger"))
                AddPresetNode(data);
            if (ImGui.Selectable("Resolution"))
                AddResNode(data);
            if (ImGui.Selectable("Time Lock"))
                AddTimeLockNode(data);
            if (ImGui.Selectable("Location Switch"))
                AddSpotNode(data);
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.TextDisabled(BuildTag);
        ImGui.SameLine();
        bool noBg = plugin.Config.AnimatorNoBackground;
        if (ImGui.Checkbox("No BG##anbg", ref noBg))
        {
            plugin.Config.AnimatorNoBackground = noBg;
            plugin.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Hide the window background and pass clicks outside nodes through to the game.");
        if (noBg)
        {
            ImGui.SameLine();
            bool keepGrid = plugin.Config.AnimatorKeepGrid;
            if (ImGui.Checkbox("Keep Grid##anbg", ref keepGrid))
            {
                plugin.Config.AnimatorKeepGrid = keepGrid;
                plugin.Config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Keep the canvas grid while the background is off.");
        }
        // (Background flag itself is synced at the top of Draw so every
        // path, including the no-preset early return, stays consistent.)
        // Built-in weather quick controls live under the toolbar row.
        // (The row ends with its own separator.)
        try
        {
            if (plugin.Config.WeatherControlEnabled)
                DrawAnimatorWeatherRow();
            else
                ImGui.Separator();
        }
        catch { }

        bool panning = ImGui.IsWindowHovered() && ImGui.IsMouseDragging(ImGuiMouseButton.Middle, 0);
        if (panning)
        {
            ImGui.SetScrollX(ImGui.GetScrollX() - ImGui.GetIO().MouseDelta.X);
            ImGui.SetScrollY(ImGui.GetScrollY() - ImGui.GetIO().MouseDelta.Y);
        }
        if (linkCancelCooldown > 0) linkCancelCooldown--;
        if (pendingLinkFrom != null && (ImGui.IsKeyPressed(ImGuiKey.Escape) || ImGui.IsMouseClicked(ImGuiMouseButton.Right)))
        {
            pendingLinkFrom = null;
            pendingLinkPin = "";
            linkCancelCooldown = 2;
        }

        // Measure the toolbar bottom for next frame's backdrop (see
        // above); the backdrop itself must paint before the widgets.
        try { toolbarBgBottom = ImGui.GetCursorScreenPos().Y; } catch { }

        if (ImGui.BeginChild("##animcanvas", new Vector2(0, 0), true, ImGuiWindowFlags.HorizontalScrollbar | (plugin.Config.AnimatorNoBackground ? ImGuiWindowFlags.NoBackground : ImGuiWindowFlags.None)))
        {
            // No background mode: nodes float over the game, so the grid
            // goes too unless Keep Grid is on.
            if (!plugin.Config.AnimatorNoBackground || plugin.Config.AnimatorKeepGrid)
                DrawAnimGrid();
            // Canvas origin for the clickthrough hover test at the end.
            // Child rect too (scrollbar lives on its edges).
            // NOTE: GetCursorScreenPos already bakes the scroll offset in,
            // so node rects are origin + (node - local0) with NO extra
            // scroll subtraction (subtracting again was the scrolled-node
            // bug: rects missed by exactly the scroll offset).
            Vector2 canvasOrigin = ImGui.GetCursorScreenPos();
            Vector2 canvasLocal0 = ImGui.GetCursorPos();
            Vector2 childPos = ImGui.GetWindowPos();
            Vector2 childSize = ImGui.GetWindowSize();
            var pinRects = new Dictionary<string, PinSet>();
            float contentMaxX = 0, contentMaxY = 0;
            // Paint order = creation order, except the focused node (and
            // the node being dragged) paints last = on top.
            var paintOrder = data.Nodes.ToList();
            try
            {
                paintOrder = paintOrder.OrderBy(n =>
                    n.Id == selectedNode ? 2 : (dragNode != null && n.Id == dragNode ? 1 : 0)).ToList();
            }
            catch { }
            foreach (var node in paintOrder)
            {
                DrawNode(data, node, pinRects);
                contentMaxX = Math.Max(contentMaxX, node.X + NodeWidth(node));
                contentMaxY = Math.Max(contentMaxY, node.Y + NodeHeight(data, node));
            }
            if (data.Nodes.Count > 0)
            {
                ImGui.SetCursorPos(new Vector2(contentMaxX + 40, contentMaxY + 40));
                ImGui.Dummy(new Vector2(4, 4));
            }
            DrawEdges(data, pinRects);
            if (pendingLinkFrom != null && data.Nodes.Any(n => n.Id == pendingLinkFrom))
            {
                if (pinRects.TryGetValue(pendingLinkFrom, out var pr))
                {
                    var a = (!string.IsNullOrEmpty(pendingLinkPin) && pr.OutPins.TryGetValue(pendingLinkPin, out var prp))
                        ? prp + new Vector2(11, 11)
                        : pr.Out + new Vector2(11, 11);
                    var b = ImGui.GetMousePos();
                    ImGui.GetWindowDrawList().AddBezierCubic(a, a + new Vector2(40, 0), b - new Vector2(40, 0), b, 0xFF60FF60, 2.0f);
                }
                else pendingLinkFrom = null;
            }
            HandleDeleteKey(data);
            ImGui.EndChild();
            // Smart clickthrough (no-background mode only): clicks pass to
            // the game unless the mouse is over the toolbar, a node, the
            // scrollbars, the resize grip, or an open interaction. Applies
            // next frame (1-frame lag, plus a grace hold). Nodes stay fully
            // interactive.
            try { UpdateNoBgClickthrough(data, mainPos, mainSize, childPos, childSize, canvasOrigin, canvasLocal0); } catch { }
        }
        else
        {
            // Child collapsed: keep input.
            try { IsClickthrough = false; } catch { }
        }
        DrawTriggerSpheres(data);
        // NoBackground also eats ImGui's own border, so outline the window
        // manually. Painted last = on top. Full-window clip: the content
        // clip rect would otherwise eat the top/left/right segments.
        try
        {
            if (plugin.Config.AnimatorNoBackground)
            {
                uint bcol = ImGui.GetColorU32(ImGuiCol.Border);
                float round = 0f;
                try { round = ImGui.GetStyle().WindowRounding; } catch { }
                ImGui.PushClipRect(mainPos, mainPos + mainSize, false);
                ImGui.GetWindowDrawList().AddRect(mainPos, mainPos + mainSize, bcol, round, ImDrawFlags.None, 1f);
                ImGui.PopClipRect();
            }
        }
        catch { }
    }

    private void UpdateNoBgClickthrough(DynamicPresetData data, Vector2 mainPos, Vector2 mainSize, Vector2 childPos, Vector2 childSize, Vector2 canvasOrigin, Vector2 canvasLocal0)
    {
        bool noBg = false;
        try { noBg = plugin.Config.AnimatorNoBackground; } catch { }
        if (!noBg)
        {
            try { IsClickthrough = false; } catch { }
            return;
        }
        bool interactive = false;
        try
        {
            if (dragNode != null || pendingLinkFrom != null) interactive = true;
            // NOTE: no focus latch here on purpose. A focus check
            // deadlocks: every click lands on the window, so focus is
            // never lost and clickthrough never engages. Position only.
            Vector2 mouse = ImGui.GetMousePos();
            if (!interactive)
            {
                // Toolbar strip (titlebar + rows above the canvas).
                float canvasTop = canvasOrigin.Y;
                if (mouse.X >= mainPos.X && mouse.X <= mainPos.X + mainSize.X && mouse.Y >= mainPos.Y && mouse.Y <= canvasTop + 4)
                    interactive = true;
            }
            if (!interactive)
            {
                // Canvas scrollbars (vertical right edge + horizontal
                // bottom edge) so they stay grabbable.
                float sb = 18f;
                try { sb = ImGui.GetStyle().ScrollbarSize + 4; } catch { }
                bool onVBar = mouse.X >= childPos.X + childSize.X - sb && mouse.X <= childPos.X + childSize.X
                    && mouse.Y >= childPos.Y && mouse.Y <= childPos.Y + childSize.Y;
                bool onHBar = mouse.Y >= childPos.Y + childSize.Y - sb && mouse.Y <= childPos.Y + childSize.Y
                    && mouse.X >= childPos.X && mouse.X <= childPos.X + childSize.X;
                if (onVBar || onHBar) interactive = true;
            }
            if (!interactive)
            {
                // Main-window resize grip (bottom-right corner).
                const float grip = 26f;
                if (mouse.X >= mainPos.X + mainSize.X - grip && mouse.X <= mainPos.X + mainSize.X
                    && mouse.Y >= mainPos.Y + mainSize.Y - grip && mouse.Y <= mainPos.Y + mainSize.Y)
                    interactive = true;
            }
            if (!interactive)
            {
                foreach (var n in data.Nodes)
                {
                    float w = NodeWidth(n), h = NodeHeight(data, n);
                    float x0 = canvasOrigin.X + n.X - canvasLocal0.X - 4;
                    float y0 = canvasOrigin.Y + n.Y - canvasLocal0.Y - 4;
                    if (mouse.X >= x0 && mouse.X <= x0 + w + 8 && mouse.Y >= y0 && mouse.Y <= y0 + h + 8)
                    {
                        interactive = true;
                        break;
                    }
                }
            }
        }
        catch { interactive = true; }
        // Grace hold (~150ms): the flag applies at next Begin, so a click
        // made right after scrolling onto a node would otherwise eat one
        // click with the stale pass-through decision.
        try
        {
            long now = Environment.TickCount64;
            if (interactive) lastNobgHit = now;
            else if (now - lastNobgHit < 150) interactive = true;
        }
        catch { }
        try { IsClickthrough = !interactive; } catch { }
    }

    // Animator toolbar weather row cache (zone .lvb read, per territory).
    private uint animWxTerritory = uint.MaxValue;
    private List<byte> animWxZone = new();
    // Last frame the mouse hit an interactive zone (grace timer below).
    private long lastNobgHit;
    // Last frame's toolbar bottom (for the no-background backdrop above).
    private float toolbarBgBottom;

    // Compact weather/time quick controls for the animator toolbar.
    // Same engine calls as the Weather tab; weather list is zone-only.
    private void DrawAnimatorWeatherRow()
    {
        var cfg = plugin.Config;
        if (animWxTerritory != plugin.CurrentTerritoryId)
        {
            animWxTerritory = plugin.CurrentTerritoryId;
            try { animWxZone = plugin.Weather.GetZoneWeathers((ushort)plugin.CurrentTerritoryId); }
            catch { animWxZone = new List<byte>(); }
        }
        bool extWeather = false, extTime = false;
        try { extWeather = plugin.IsExternalWeathermanWeatherCustom(); } catch { }
        try { extTime = plugin.IsExternalWeathermanTimeCustom(); } catch { }

        // Time: checkbox + play + compact slider.
        bool tOn = cfg.TimeCustomOn;
        if (ImGui.Checkbox("Time##axwx", ref tOn))
        {
            if (tOn)
            {
                if (extTime && !cfg.TimeCustomOn) { try { Service.ChatGui.Print("[ReshadeController] Built-in time freeze refused: external Weatherman time override is active."); } catch { } }
                else
                {
                    int live = cfg.ForcedTimeSeconds;
                    try { if (!plugin.Weather.IsTimeCustom()) live = plugin.GetEorzeaSecondsPublic(); } catch { }
                    live = ((live % 86400) + 86400) % 86400;
                    if (plugin.Weather.EnableTime((uint)live)) { cfg.ForcedTimeSeconds = live; cfg.TimeCustomOn = true; }
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
        ImGui.SameLine();
        {
            bool playing = false;
            try { playing = plugin.WeatherTimePlaying; } catch { }
            if (ImGui.Button(playing ? "■##axwxPlay" : "▶##axwxPlay"))
            {
                if (playing) { try { plugin.StopWeatherTimePlay(); } catch { } }
                else if (extTime && !cfg.TimeCustomOn) { try { Service.ChatGui.Print("[ReshadeController] Built-in time playback refused: external Weatherman time override is active."); } catch { } }
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
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(playing ? "Stop playback" : "Play the full 24h cycle");
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130f);
        {
            var span = TimeSpan.FromSeconds(cfg.ForcedTimeSeconds);
            int position = (int)MathF.Ceiling(cfg.ForcedTimeSeconds / 3600f);
            if (ImGui.SliderInt("##axwxTime", ref position, 0, 24, $"{span.Hours:D2}:{span.Minutes:D2}:{span.Seconds:D2}"))
            {
                int v = position * 3600;
                if (v == 86400) v -= 1;
                if (extTime && !cfg.TimeCustomOn) { try { Service.ChatGui.Print("[ReshadeController] Built-in time freeze refused: external Weatherman time override is active."); } catch { } }
                else if (plugin.Weather.EnableTime((uint)v)) { cfg.ForcedTimeSeconds = v; cfg.TimeCustomOn = true; cfg.Save(); }
            }
        }
        ImGui.SameLine();
        // Weather dropdown: current-zone weathers ONLY.
        {
            var names = new Dictionary<byte, string>();
            try { foreach (var w in config.GetWeatherList()) names[(byte)w.Id] = w.Name; } catch { }
            string preview = "Weather";
            if (cfg.WeatherCustomOn)
                preview = names.TryGetValue(cfg.ForcedWeatherId, out var pn) && !string.IsNullOrWhiteSpace(pn) ? pn : $"#{cfg.ForcedWeatherId}";
            else
            {
                try
                {
                    byte live = plugin.GetCurrentWeather();
                    if (names.TryGetValue(live, out var ln) && !string.IsNullOrWhiteSpace(ln)) preview = ln;
                }
                catch { }
            }
            ImGui.SetNextItemWidth(140f);
            if (ImGui.BeginCombo("##axwxW", preview))
            {
                foreach (var i in animWxZone)
                {
                    string label = names.TryGetValue(i, out var n) && !string.IsNullOrWhiteSpace(n) ? n : i.ToString();
                    bool isSel = cfg.WeatherCustomOn && cfg.ForcedWeatherId == i;
                    if (ImGui.Selectable(label, isSel))
                    {
                        if (extWeather && !cfg.WeatherCustomOn) { try { Service.ChatGui.Print("[ReshadeController] Built-in weather refused: external Weatherman override is active."); } catch { } }
                        else if (plugin.Weather.EnableWeather(i, (ushort)plugin.CurrentTerritoryId))
                        {
                            cfg.ForcedWeatherId = i;
                            cfg.WeatherCustomOn = true;
                            cfg.Save();
                        }
                    }
                    if (isSel) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Current-zone weathers only. Full controls in the Weather tab.");
            if (cfg.WeatherCustomOn)
            {
                ImGui.SameLine();
                if (ImGui.Button("Reset##axwxW"))
                {
                    try { plugin.Weather.DisableWeather(); } catch { }
                    cfg.WeatherCustomOn = false;
                    cfg.Save();
                }
            }
        }
        ImGui.Separator();
    }

    // Wireframe spheres for location triggers (start + max radii), projected
    // to screen. Authoring aid: always on top, no depth test.
    private void DrawTriggerSpheres(DynamicPresetData data)
    {
        try
        {
            var gg = Service.GameGui;
            if (gg == null) return;
            // No world at the title screen / loading: volumes would project
            // from stale coordinates onto the menu.
            try { if (!plugin.IsInWorld()) return; } catch { return; }
            bool any = false;
            foreach (var n in data.Nodes)
                if ((IsTrigger(n) || IsCoords(n)) && n.TriggerKind == 18) { any = true; break; }
            if (!any) return;
            var dl = ImGui.GetBackgroundDrawList();
            uint terr = plugin.CurrentTerritoryId;
            foreach (var n in data.Nodes)
            {
                if (!(IsTrigger(n) || IsCoords(n)) || n.TriggerKind != 18) continue;
                if (!n.ShowVolume) continue;
                var lzones = n.Zones.Count > 0 ? n.Zones : new List<DynamicZone> { new DynamicZone {
                    LocTerritory = n.LocTerritory, LocX = n.LocX, LocY = n.LocY, LocZ = n.LocZ,
                    LocShape = n.LocShape, RadiusStart = n.RadiusStart, RadiusMax = n.RadiusMax,
                    BoxOX = n.BoxOX, BoxOY = n.BoxOY, BoxOZ = n.BoxOZ,
                    BoxIX = n.BoxIX, BoxIY = n.BoxIY, BoxIZ = n.BoxIZ,
                    BoxOCX = n.BoxOCX, BoxOCY = n.BoxOCY, BoxOCZ = n.BoxOCZ,
                    BoxYaw = n.BoxYaw, BoxPitch = n.BoxPitch, BoxRoll = n.BoxRoll } };
                int lzi = 0;
                foreach (var lz in lzones)
                {
                    if (lz.LocTerritory != 0 && lz.LocTerritory != terr) { lzi++; continue; }
                    var c = new Vector3(lz.LocX, lz.LocY, lz.LocZ);
                    if (lz.LocShape == 2)
                    {
                        DrawSphere(dl, gg, c, Math.Max(0.5f, lz.RadiusMax), 0x9933FF66);
                    }
                    else if (lz.LocShape == 3)
                    {
                        DrawBox(dl, gg, c, Math.Max(0.5f, lz.BoxIX), Math.Max(0.5f, lz.BoxIY), Math.Max(0.5f, lz.BoxIZ), lz.BoxYaw, lz.BoxPitch, lz.BoxRoll, 0x9933FF66);
                    }
                    else if (lz.LocShape == 1)
                    {
                        var co = c + new Vector3(lz.BoxOCX, lz.BoxOCY, lz.BoxOCZ);
                        DrawBox(dl, gg, co, Math.Max(0.5f, lz.BoxOX), Math.Max(0.5f, lz.BoxOY), Math.Max(0.5f, lz.BoxOZ), lz.BoxYaw, lz.BoxPitch, lz.BoxRoll, 0x99FFCC33);
                        DrawBox(dl, gg, c, Math.Max(0.5f, lz.BoxIX), Math.Max(0.5f, lz.BoxIY), Math.Max(0.5f, lz.BoxIZ), lz.BoxYaw, lz.BoxPitch, lz.BoxRoll, 0x9933FF66);
                        // Midpoint shell (half factor) while shaping is on.
                        if (n.UseMidpoint)
                        {
                            var cm = c + new Vector3(lz.BoxOCX, lz.BoxOCY, lz.BoxOCZ) * 0.5f;
                            DrawBox(dl, gg, cm, Math.Max(0.5f, (lz.BoxOX + lz.BoxIX) * 0.5f), Math.Max(0.5f, (lz.BoxOY + lz.BoxIY) * 0.5f), Math.Max(0.5f, (lz.BoxOZ + lz.BoxIZ) * 0.5f), lz.BoxYaw, lz.BoxPitch, lz.BoxRoll, 0x9900FFFF);
                        }
                    }
                    else
                    {
                        DrawSphere(dl, gg, c, Math.Max(0.5f, lz.RadiusStart), 0x99FFCC33);
                        DrawSphere(dl, gg, c, Math.Max(0.5f, lz.RadiusMax), 0x9933FF66);
                        // Midpoint shell (half factor) while shaping is on.
                        if (n.UseMidpoint)
                            DrawSphere(dl, gg, c, Math.Max(0.5f, (lz.RadiusStart + lz.RadiusMax) * 0.5f), 0x9900FFFF);
                    }
                    if (gg.WorldToScreen(c, out Vector2 sp))
                        dl.AddText(sp + new Vector2(6, -8), 0xFFFFFFFF, $"{NodeLabel(n)} Zone {lzi + 1}");
                    lzi++;
                }
            }
            foreach (var n in data.Nodes)
            {
                if (!IsWall(n) || !n.ShowVolume) continue;
                var wdoors = n.Doors.Count > 0 ? n.Doors : new List<DynamicDoor> { new DynamicDoor {
                    LocTerritory = n.LocTerritory, LocX = n.LocX, LocY = n.LocY, LocZ = n.LocZ,
                    BoxYaw = n.BoxYaw, BoxPitch = n.BoxPitch, BoxRoll = n.BoxRoll,
                    WallW = n.WallW, WallH = n.WallH } };
                int wdi = 0;
                foreach (var wd in wdoors)
                {
                    if (wd.LocTerritory != 0 && wd.LocTerritory != terr) { wdi++; continue; }
                    var c = new Vector3(wd.LocX, wd.LocY, wd.LocZ);
                    DrawWallRect(dl, gg, c, wd.BoxYaw, wd.BoxPitch, wd.BoxRoll, Math.Max(0.5f, wd.WallW), Math.Max(0.5f, wd.WallH));
                    if (gg.WorldToScreen(c, out Vector2 sp))
                        dl.AddText(sp + new Vector2(6, -8), 0xFFFFFFFF, $"{NodeLabel(n)} Door {wdi + 1}");
                    wdi++;
                }
            }
        }
        catch { }
    }

    private static void DrawWallRect(ImDrawListPtr dl, Dalamud.Plugin.Services.IGameGui gg, Vector3 c, float yaw, float pitch, float roll, float w, float h, uint col = 0x99CC66FF)
    {
        var corners = new Vector3[4];
        for (int i = 0; i < 4; i++)
        {
            float ox = (i == 0 || i == 3) ? -w / 2 : w / 2;
            float oy = (i < 2) ? -h / 2 : h / 2;
            corners[i] = c + EulerBox.Rotate(new Vector3(ox, oy, 0), yaw, pitch, roll, false);
        }
        const int segs = 8;
        for (int i = 0; i < 4; i++)
        {
            Vector2 prev = default;
            bool havePrev = false;
            for (int s = 0; s <= segs; s++)
            {
                Vector3 wp = corners[i] + (corners[(i + 1) % 4] - corners[i]) * (s / (float)segs);
                if (gg.WorldToScreen(wp, out Vector2 sp))
                {
                    if (havePrev) dl.AddLine(prev, sp, col, 1.5f);
                    prev = sp;
                    havePrev = true;
                }
                else havePrev = false;
            }
        }
    }

    private static void DrawSphere(ImDrawListPtr dl, Dalamud.Plugin.Services.IGameGui gg, Vector3 c, float r, uint col)
    {
        const int segs = 36;
        var Y = new Vector3(0, 1, 0);
        // Meridians: vertical circles through the poles, every 30 degrees.
        for (int m = 0; m < 6; m++)
        {
            float lon = m * MathF.PI / 6;
            var u = new Vector3(MathF.Cos(lon), 0, MathF.Sin(lon));
            Circle(dl, gg, c, u, Y, r, segs, col);
        }
        // Parallels: horizontal rings every 30 degrees of latitude.
        foreach (float deg in new[] { -60f, -30f, 0f, 30f, 60f })
        {
            float lat = deg * MathF.PI / 180f;
            LatCircle(dl, gg, c, r * MathF.Cos(lat), r * MathF.Sin(lat), segs, col);
        }
    }

    private static void Circle(ImDrawListPtr dl, Dalamud.Plugin.Services.IGameGui gg, Vector3 c, Vector3 u, Vector3 v, float r, int segs, uint col)
    {
        Vector2 prev = default;
        bool havePrev = false;
        for (int i = 0; i <= segs; i++)
        {
            float a = i / (float)segs * MathF.PI * 2;
            Vector3 wp = c + (MathF.Cos(a) * u + MathF.Sin(a) * v) * r;
            if (gg.WorldToScreen(wp, out Vector2 sp))
            {
                if (havePrev) dl.AddLine(prev, sp, col, 1.5f);
                prev = sp;
                havePrev = true;
            }
            else havePrev = false;
        }
    }

    private static void DrawBox(ImDrawListPtr dl, Dalamud.Plugin.Services.IGameGui gg, Vector3 c, float hx, float hy, float hz, float yaw, float pitch, float roll, uint col)
    {
        var v = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            var o = new Vector3((i & 1) == 0 ? -hx : hx, (i & 2) == 0 ? -hy : hy, (i & 4) == 0 ? -hz : hz);
            v[i] = c + EulerBox.Rotate(o, yaw, pitch, roll, false);
        }
        int[,] e = { { 0, 1 }, { 2, 3 }, { 4, 5 }, { 6, 7 }, { 0, 2 }, { 1, 3 }, { 4, 6 }, { 5, 7 }, { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 } };
        const int segs = 12;
        for (int i = 0; i < 12; i++)
        {
            Vector2 prev = default;
            bool havePrev = false;
            for (int s = 0; s <= segs; s++)
            {
                Vector3 wp = v[e[i, 0]] + (v[e[i, 1]] - v[e[i, 0]]) * (s / (float)segs);
                if (gg.WorldToScreen(wp, out Vector2 sp))
                {
                    if (havePrev) dl.AddLine(prev, sp, col, 1.5f);
                    prev = sp;
                    havePrev = true;
                }
                else havePrev = false;
            }
        }
    }

    private static void LatCircle(ImDrawListPtr dl, Dalamud.Plugin.Services.IGameGui gg, Vector3 c, float rr, float y, int segs, uint col)
    {
        Vector2 prev = default;
        bool havePrev = false;
        for (int i = 0; i <= segs; i++)
        {
            float a = i / (float)segs * MathF.PI * 2;
            Vector3 wp = c + new Vector3(MathF.Cos(a) * rr, y, MathF.Sin(a) * rr);
            if (gg.WorldToScreen(wp, out Vector2 sp))
            {
                if (havePrev) dl.AddLine(prev, sp, col, 1f);
                prev = sp;
                havePrev = true;
            }
            else havePrev = false;
        }
    }

    private const float GridStep = 32;

    private void DrawAnimGrid()
    {
        var dl = ImGui.GetWindowDrawList();
        Vector2 origin = ImGui.GetWindowPos();
        float sx = ImGui.GetScrollX();
        float sy = ImGui.GetScrollY();
        Vector2 size = ImGui.GetContentRegionAvail();
        uint minor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.055f));
        uint major = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.11f));
        long ix0 = (long)MathF.Floor(sx / GridStep);
        for (long i = ix0; ; i++)
        {
            float x = origin.X + i * GridStep - sx;
            if (x > origin.X + size.X) break;
            dl.AddLine(new Vector2(x, origin.Y), new Vector2(x, origin.Y + size.Y), i % 4 == 0 ? major : minor, 1.0f);
        }
        long iy0 = (long)MathF.Floor(sy / GridStep);
        for (long j = iy0; ; j++)
        {
            float y = origin.Y + j * GridStep - sy;
            if (y > origin.Y + size.Y) break;
            dl.AddLine(new Vector2(origin.X, y), new Vector2(origin.X + size.X, y), j % 4 == 0 ? major : minor, 1.0f);
        }
    }

    private void AddNode(DynamicPresetData data)
    {
        var node = new DynamicAnimNode
        {
            Config = "Primary",
            X = 60,
            Y = data.Nodes.Count == 0 ? 60 : data.Nodes.Max(n => n.Y) + NodeH + 24,
        };
        // New nodes start unbound (Key = none); bind via the dropdown.
        node.KeyframeId = "";
        node.NodeNum = data.Nodes.Count == 0 ? 1 : data.Nodes.Max(n => n.NodeNum) + 1;
        data.Nodes.Add(node);
        config.SaveActiveDyn();
    }

    private void AddTriggerNode(DynamicPresetData data)
    {
        var node = new DynamicAnimNode
        {
            Config = "Primary",
            Source = "trigger",
            X = 60,
            Y = data.Nodes.Count == 0 ? 60 : data.Nodes.Max(n => n.Y + NodeHeight(data, n)) + 24,
        };
        // New nodes start unbound (Key = none); bind via the dropdown.
        node.KeyframeId = "";
        node.NodeNum = data.Nodes.Count == 0 ? 1 : data.Nodes.Max(n => n.NodeNum) + 1;
        data.Nodes.Add(node);
        config.SaveActiveDyn();
    }

    private void AddCoordsNode(DynamicPresetData data)
    {
        var node = new DynamicAnimNode
        {
            Config = "Primary",
            Source = "coords",
            TriggerKind = 18,
            X = 60,
            Y = data.Nodes.Count == 0 ? 60 : data.Nodes.Max(n => n.Y + NodeHeight(data, n)) + 24,
            LocTerritory = plugin.CurrentTerritoryId,
        };
        var pp0 = plugin.PlayerPosition();
        if (pp0 != null)
        {
            node.LocX = pp0.Value.X;
            node.LocY = pp0.Value.Y;
            node.LocZ = pp0.Value.Z;
        }
        // New nodes start unbound (Key = none); bind via the dropdown.
        node.KeyframeId = "";
        node.NodeNum = data.Nodes.Count == 0 ? 1 : data.Nodes.Max(n => n.NodeNum) + 1;
        data.Nodes.Add(node);
        config.SaveActiveDyn();
    }

    private void AddWeatherNode(DynamicPresetData data)
    {
        var node = new DynamicAnimNode
        {
            Config = "Primary",
            Source = "weather",
            X = 60,
            Y = data.Nodes.Count == 0 ? 60 : data.Nodes.Max(n => n.Y + NodeHeight(data, n)) + 24,
        };
        // Start with one empty group; no keyframe binding (sensor only).
        node.WeatherGroups.Add(new DynamicWeatherGroup());
        node.NodeNum = data.Nodes.Count == 0 ? 1 : data.Nodes.Max(n => n.NodeNum) + 1;
        data.Nodes.Add(node);
        config.SaveActiveDyn();
    }

    private void AddTimeGateNode(DynamicPresetData data)
    {
        var node = new DynamicAnimNode
        {
            Config = "Primary",
            Source = "timegate",
            X = 60,
            Y = data.Nodes.Count == 0 ? 60 : data.Nodes.Max(n => n.Y + NodeHeight(data, n)) + 24,
        };
        // Equal bounds = always on until configured. No keyframe binding.
        node.NodeNum = data.Nodes.Count == 0 ? 1 : data.Nodes.Max(n => n.NodeNum) + 1;
        data.Nodes.Add(node);
        config.SaveActiveDyn();
    }

    private void AddTimerNode(DynamicPresetData data)
    {
        var node = new DynamicAnimNode
        {
            Config = "Primary",
            Source = "timer",
            X = 60,
            Y = data.Nodes.Count == 0 ? 60 : data.Nodes.Max(n => n.Y + NodeHeight(data, n)) + 24,
        };
        // Start with one timer; no keyframe binding (pin levels only).
        node.Timers.Add(new DynamicTimer());
        node.NodeNum = data.Nodes.Count == 0 ? 1 : data.Nodes.Max(n => n.NodeNum) + 1;
        data.Nodes.Add(node);
        config.SaveActiveDyn();
    }

    private void AddDlssNode(DynamicPresetData data)
    {
        var node = new DynamicAnimNode
        {
            Config = "Primary",
            Source = "dlss",
            X = 60,
            Y = data.Nodes.Count == 0 ? 60 : data.Nodes.Max(n => n.Y + NodeHeight(data, n)) + 24,
        };
        // Gate-driven NR switch; no keyframe binding (pin levels only).
        node.NodeNum = data.Nodes.Count == 0 ? 1 : data.Nodes.Max(n => n.NodeNum) + 1;
        data.Nodes.Add(node);
        config.SaveActiveDyn();
    }

    private void AddPresetNode(DynamicPresetData data)
    {
        var node = new DynamicAnimNode
        {
            Config = "Primary",
            Source = "preset",
            X = 60,
            Y = data.Nodes.Count == 0 ? 60 : data.Nodes.Max(n => n.Y + NodeHeight(data, n)) + 24,
        };
        // Rising gate edge switches to PresetPath; no keyframe binding.
        node.NodeNum = data.Nodes.Count == 0 ? 1 : data.Nodes.Max(n => n.NodeNum) + 1;
        data.Nodes.Add(node);
        config.SaveActiveDyn();
    }

    private void AddResNode(DynamicPresetData data)
    {
        var node = new DynamicAnimNode
        {
            Config = "Primary",
            Source = "res",
            X = 60,
            Y = data.Nodes.Count == 0 ? 60 : data.Nodes.Max(n => n.Y + NodeHeight(data, n)) + 24,
        };
        // Pure gate source (out-pin only): no envelope, no keyframe.
        node.NodeNum = data.Nodes.Count == 0 ? 1 : data.Nodes.Max(n => n.NodeNum) + 1;
        data.Nodes.Add(node);
        config.SaveActiveDyn();
    }

    private void AddTimeLockNode(DynamicPresetData data)
    {
        var node = new DynamicAnimNode
        {
            Config = "Primary",
            Source = "timelock",
            X = 60,
            Y = data.Nodes.Count == 0 ? 60 : data.Nodes.Max(n => n.Y + NodeHeight(data, n)) + 24,
            TimeLockTimeSec = 43200,
            TimeLockFadeIn = 1f,
            TimeLockFadeOut = 1f,
        };
        // Gate-driven time override; no keyframe binding (pin levels only).
        node.NodeNum = data.Nodes.Count == 0 ? 1 : data.Nodes.Max(n => n.NodeNum) + 1;
        data.Nodes.Add(node);
        config.SaveActiveDyn();
    }

    private void AddSpotNode(DynamicPresetData data)
    {
        var node = new DynamicAnimNode
        {
            Config = "Primary",
            Source = "spot",
            X = 60,
            Y = data.Nodes.Count == 0 ? 60 : data.Nodes.Max(n => n.Y + NodeHeight(data, n)) + 24,
        };
        // Zone list starts empty; stand somewhere and press + Current.
        node.NodeNum = data.Nodes.Count == 0 ? 1 : data.Nodes.Max(n => n.NodeNum) + 1;
        data.Nodes.Add(node);
        config.SaveActiveDyn();
    }

    private void AddWallNode(DynamicPresetData data)
    {
        var node = new DynamicAnimNode
        {
            Config = "Primary",
            Source = "wall",
            X = 60,
            Y = data.Nodes.Count == 0 ? 60 : data.Nodes.Max(n => n.Y + NodeHeight(data, n)) + 24,
            LocTerritory = plugin.CurrentTerritoryId,
        };
        var pp0 = plugin.PlayerPosition();
        if (pp0 != null)
        {
            node.LocX = pp0.Value.X;
            node.LocY = pp0.Value.Y;
            node.LocZ = pp0.Value.Z;
        }
        // New nodes start unbound (Key = none); bind via the dropdown.
        node.KeyframeId = "";
        node.NodeNum = data.Nodes.Count == 0 ? 1 : data.Nodes.Max(n => n.NodeNum) + 1;
        data.Nodes.Add(node);
        config.SaveActiveDyn();
    }

    private string? captureNode;
    private string emoteSearch = string.Empty;
    private string? nicknameNode;
    private string nicknameDraft = string.Empty;

    // Footer + in-world label: nickname when set, else "Node N".
    private static string NodeLabel(DynamicAnimNode node)
        => string.IsNullOrWhiteSpace(node.Nickname) ? $"Node {node.NodeNum}" : node.Nickname.Trim();
    private string statusSearch = string.Empty;
    private string actionSearch = string.Empty;

    private static readonly Dictionary<int, string> VkNames = BuildVkNames();

    private static Dictionary<int, string> BuildVkNames()
    {
        var d = new Dictionary<int, string>();
        for (int vk = 0x30; vk <= 0x39; vk++) d[vk] = ((char)vk).ToString();
        for (int vk = 0x41; vk <= 0x5A; vk++) d[vk] = ((char)vk).ToString();
        for (int i = 1; i <= 12; i++) d[0x6F + i] = "F" + i;
        d[0x20] = "Space";
        return d;
    }

    private static string VkName(int vk)
        => VkNames.TryGetValue(vk, out var n) ? n : $"0x{vk:X2}";

    private static readonly int[] VkScan = BuildVkScan();

    private static int[] BuildVkScan()
    {
        var l = new List<int>();
        for (int vk = 0x30; vk <= 0x39; vk++) l.Add(vk);
        for (int vk = 0x41; vk <= 0x5A; vk++) l.Add(vk);
        for (int i = 1; i <= 12; i++) l.Add(0x6F + i);
        l.Add(0x20);
        return l.ToArray();
    }

    private DynamicKeyframe? BoundKeyframe(DynamicPresetData data, DynamicAnimNode node)
    {
        var cfg = data.GetConfig(node.Config);
        if (cfg == null || string.IsNullOrEmpty(node.KeyframeId)) return null;
        foreach (var kf in cfg.Keyframes)
            if (kf.Id == node.KeyframeId) return kf;
        return null;
    }

    private static string[] SplitTime(int t) => new[]
    {
        (t / 3600).ToString("00"),
        ((t % 3600) / 60).ToString("00"),
        (t % 60).ToString("00"),
    };

    private static int ParseTimeParts(string[] parts, int fallback)
    {
        var cur = SplitTime(fallback);
        int[] max = { 23, 59, 59 };
        int[] val = new int[3];
        for (int i = 0; i < 3; i++)
        {
            if (!int.TryParse(parts[i].Trim(), out val[i]))
                val[i] = int.Parse(cur[i]);
            val[i] = Math.Clamp(val[i], 0, max[i]);
            parts[i] = val[i].ToString("00");
        }
        return val[0] * 3600 + val[1] * 60 + val[2];
    }

    private bool TimePart(string id, string[] parts, int idx, int max, float w, ref bool anyActive)
    {
        ImGui.SetNextItemWidth(w);
        bool done = ImGui.InputText(id, ref parts[idx], 2, ImGuiInputTextFlags.EnterReturnsTrue);
        if (ImGui.IsItemActive()) anyActive = true;
        if (done && int.TryParse(parts[idx].Trim(), out int v) && v > max)
            parts[idx] = max.ToString("00");
        return done;
    }

    // Location Switch rows: zone list (name + x per row) + +Current
    // capture + Reverse checkbox (ON outside every listed zone, OFF inside).
    private void DrawSpotRows(DynamicPresetData data, DynamicAnimNode node)
    {
        var zones = node.SpotZones.ToList();
        if (zones.Count == 0)
        {
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled("No zones — go there, + Current.");
        }
        else
        {
            foreach (var z in zones)
            {
                ImGui.SetCursorPosX(node.X + RowPadX);
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(ConfigWindow.GetZoneName(z));
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Zone ID {z} — click x to remove");
                ImGui.SameLine();
                if (ImGui.SmallButton($"x##spot{node.Id}_{z}"))
                {
                    node.SpotZones.Remove(z);
                    config.SaveActiveDyn();
                }
            }
        }
        ImGui.SetCursorPosX(node.X + RowPadX);
        if (ImGui.SmallButton($"+ Current##spotadd{node.Id}"))
        {
            uint cur = plugin.CurrentTerritoryId;
            if (cur != 0 && !node.SpotZones.Contains(cur))
            {
                node.SpotZones.Add(cur);
                config.SaveActiveDyn();
            }
        }
        ImGui.SameLine(0, 8);
        bool inv = node.SpotInvert;
        if (ImGui.Checkbox("Reverse##anspoti_" + node.Id, ref inv))
        {
            node.SpotInvert = inv;
            config.SaveActiveDyn();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Reversed: ON outside every listed zone, OFF inside.");
    }

    // Location Switch zone rows: one per listed zone (never fewer than
    // one, so the +Current row always has room). Keep NodeHeight's
    // reserve in lockstep.
    private static int SpotZoneRows(DynamicAnimNode node)
    {
        try
        {
            int n = node.SpotZones?.Count ?? 0;
            return Math.Max(1, n);
        }
        catch { return 1; }
    }

    // Time gate rows: ON/OFF window as HH:MM:SS (Eorzea time), Enter to
    // commit. Overnight ranges wrap past midnight; equal bounds = always on.
    private void DrawTimeGateRows(DynamicPresetData data, DynamicAnimNode node)
    {
        DrawGateTimeRow(node, "On:", node.TimeGateOn, v => node.TimeGateOn = v, "_on");
        DrawGateTimeRow(node, "Off:", node.TimeGateOff, v => node.TimeGateOff = v, "_off");
    }

    private void DrawGateTimeRow(DynamicAnimNode node, string label, int current, Action<int> assign, string tag)
    {
        if (!gateTimeDrafts.TryGetValue(node.Id + tag, out var parts))
            gateTimeDrafts[node.Id + tag] = parts = SplitTime(current);
        ImGui.SetCursorPosX(node.X + RowPadX);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        ImGui.SameLine(0, 3);
        float digitW = ImGui.CalcTextSize("00").X + ImGui.GetStyle().FramePadding.X * 2 + 6;
        int[] partMax = { 23, 59, 59 };
        string[] partIds = { "##angh" + tag + node.Id, "##angmi" + tag + node.Id, "##angs" + tag + node.Id };
        bool commit = false;
        bool anyActive = false;
        for (int p = 0; p < 3; p++)
        {
            if (p > 0)
            {
                ImGui.SameLine(0, 3);
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(":");
                ImGui.SameLine(0, 3);
            }
            commit |= TimePart(partIds[p], parts, p, partMax[p], digitW, ref anyActive);
        }
        if (commit)
        {
            int nv = ParseTimeParts(parts, current);
            assign(nv);
            gateTimeDrafts[node.Id + tag] = parts = SplitTime(nv);
            config.SaveActiveDyn();
        }
        else if (!anyActive)
            gateTimeDrafts[node.Id + tag] = parts = SplitTime(current);
    }

    private static readonly string[] TriggerKindNames =
    {
        "Hotkey", "In Combat", "Mounted", "Crafting", "Gathering",
        "Weapon Out", "Dead", "Casting", "Emoting", "HP %",
        "MP %", "Status Effect", "QolBar Condition Set", "First Person",
        "Sit / Sleep", "Emote", "Dead Players", "Chat Command",
        "Location", "Pin Activation", "Resolution (legacy)", "Underwater", "Flying",
    };
    // Kind 20 (Resolution trigger) is legacy: superseded by the standalone
    // Resolution node. Hidden from the picker above, but the engine still
    // evaluates already-saved kind-20 triggers.

    // Out-pin on a trigger timer row: fires when that envelope phase
    // completes (engine tracks per-run pings). Anchored to the row's right
    // edge from the last widget's rect; no layout impact.
    private void PhasePin(DynamicPresetData data, DynamicAnimNode node, Dictionary<string, PinSet> pinRects, Vector2 nodeScreen, float nodeW, string pin)
    {
        var rMin = ImGui.GetItemRectMin();
        var rMax = ImGui.GetItemRectMax();
        float cy = (rMin.Y + rMax.Y) * 0.5f;
        var rect = new Vector2(nodeScreen.X + nodeW - 11, cy - 11);
        if (!pinRects.TryGetValue(node.Id, out var ps))
            pinRects[node.Id] = ps = new PinSet();
        ps.OutPins[pin] = rect;
        ImGui.SetCursorScreenPos(rect);
        bool clicked = ImGui.InvisibleButton("##pin_phase_" + pin + "_" + node.Id, new Vector2(22, 22));
        bool hov = ImGui.IsItemHovered();
        if (clicked)
        {
            if (pendingLinkFrom == node.Id && pendingLinkPin == pin) { pendingLinkFrom = null; pendingLinkPin = ""; }
            else { pendingLinkFrom = node.Id; pendingLinkPin = pin; }
        }
        uint dot = (pendingLinkFrom == node.Id && pendingLinkPin == pin) ? 0xFF80FF80 : 0xFF60C060;
        var dl = ImGui.GetWindowDrawList();
        dl.AddCircleFilled(rect + new Vector2(11, 11), 5, dot);
        if (hov || (pendingLinkFrom == node.Id && pendingLinkPin == pin))
            dl.AddCircle(rect + new Vector2(11, 11), 8, 0xFFFFFFFF, 0, 1.5f);
        if (data.Edges.Any(e => e.From == node.Id && (e.FromPin ?? "") == pin))
            dl.AddCircle(rect + new Vector2(11, 11), 8, 0xFF00A5FF, 0, 1.5f);
    }

    // Out-pin on a weather group row: live while the current weather
    // matches the group. Same mechanics as phase pins.
    private void WeatherPin(DynamicPresetData data, DynamicAnimNode node, Dictionary<string, PinSet> pinRects, Vector2 nodeScreen, float nodeW, DynamicWeatherGroup g)
    {
        var rMin = ImGui.GetItemRectMin();
        var rMax = ImGui.GetItemRectMax();
        float cy = (rMin.Y + rMax.Y) * 0.5f;
        var rect = new Vector2(nodeScreen.X + nodeW - 11, cy - 11);
        if (!pinRects.TryGetValue(node.Id, out var ps))
            pinRects[node.Id] = ps = new PinSet();
        string pin = "w:" + g.Id;
        ps.OutPins[pin] = rect;
        ImGui.SetCursorScreenPos(rect);
        bool clicked = ImGui.InvisibleButton("##pin_w_" + g.Id, new Vector2(22, 22));
        bool hov = ImGui.IsItemHovered();
        if (clicked)
        {
            if (pendingLinkFrom == node.Id && pendingLinkPin == pin) { pendingLinkFrom = null; pendingLinkPin = ""; }
            else { pendingLinkFrom = node.Id; pendingLinkPin = pin; }
        }
        uint dot = (pendingLinkFrom == node.Id && pendingLinkPin == pin) ? 0xFF80FF80 : 0xFF60C060;
        var dl = ImGui.GetWindowDrawList();
        dl.AddCircleFilled(rect + new Vector2(11, 11), 5, dot);
        if (hov || (pendingLinkFrom == node.Id && pendingLinkPin == pin))
            dl.AddCircle(rect + new Vector2(11, 11), 8, 0xFFFFFFFF, 0, 1.5f);
        if (data.Edges.Any(e => e.From == node.Id && (e.FromPin ?? "") == pin))
            dl.AddCircle(rect + new Vector2(11, 11), 8, 0xFF00A5FF, 0, 1.5f);
    }

    private string WeatherName(uint id)
    {
        foreach (var w in config.GetWeatherList())
            if (w.Id == id) return w.Name;
        return "?";
    }

    private void DrawWeatherRows(DynamicPresetData data, DynamicAnimNode node, Dictionary<string, PinSet> pinRects, Vector2 nodeScreen, float nodeW)
    {
        byte current = plugin.GetCurrentWeather();
        // Ids claimed by sibling selecting groups: excluded elsewhere.
        var claimed = new HashSet<int>();
        foreach (var o in node.WeatherGroups)
        {
            if (o.Any) continue;
            foreach (var id in o.WeatherIds) claimed.Add(id);
        }
        foreach (var entry in node.WeatherGroups.ToList())
        {
            var g = entry;
            int gi = node.WeatherGroups.IndexOf(g);
            bool live = Plugin.WeatherGroupLive(g, node.WeatherGroups, current);
            Vector4 labelCol = live ? new Vector4(0.45f, 1f, 0.45f, 1f) : new Vector4(0.91f, 0.91f, 0.91f, 1f);
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(labelCol, $"Group {gi + 1}:");
            ImGui.SameLine(0, 4);
            // Reserve room for x button + pin zone.
            ImGui.SetNextItemWidth(Math.Max(40, node.X + nodeW - RowPadX - ImGui.GetCursorPosX() - 58));
            string wPreview = g.Any ? "Any"
                : g.WeatherIds.Count == 0 ? "Select..."
                : g.WeatherIds.Count == 1 ? $"{WeatherName((uint)g.WeatherIds[0])} ({g.WeatherIds[0]})"
                : $"{g.WeatherIds.Count} selected";
            if (ImGui.BeginCombo("##anw_" + g.Id, wPreview))
            {
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                ImGui.InputTextWithHint("##anws_" + g.Id, "Search...", ref weatherSearch, 64);
                ImGui.Separator();
                if (ImGui.Selectable("Any", g.Any, ImGuiSelectableFlags.DontClosePopups))
                {
                    // Toggle Any mode; entering it clears specifics.
                    g.Any = !g.Any;
                    if (g.Any) g.WeatherIds.Clear();
                    config.SaveActiveDyn();
                }
                var sl = weatherSearch.ToLowerInvariant();
                foreach (var w in config.GetWeatherList())
                {
                    if (!string.IsNullOrEmpty(sl) && !w.Name.ToLowerInvariant().Contains(sl)) continue;
                    if (claimed.Contains((int)w.Id) && !g.WeatherIds.Contains((int)w.Id)) continue;
                    bool wsel = g.WeatherIds.Contains((int)w.Id);
                    if (ImGui.Selectable($"{(wsel ? "[x] " : "[ ] ")}{w.Name} ({w.Id})", wsel, ImGuiSelectableFlags.DontClosePopups))
                    {
                        // Picking a concrete weather exits Any mode.
                        g.Any = false;
                        if (wsel) g.WeatherIds.Remove((int)w.Id);
                        else g.WeatherIds.Add((int)w.Id);
                        config.SaveActiveDyn();
                    }
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine(0, 4);
            if (ImGui.SmallButton("x##anwx_" + g.Id))
            {
                node.WeatherGroups.Remove(g);
                data.Edges.RemoveAll(e => e.From == node.Id && (e.FromPin ?? "") == "w:" + g.Id);
                config.SaveActiveDyn();
            }
            else
            {
                WeatherPin(data, node, pinRects, nodeScreen, nodeW, g);
            }
        }
        ImGui.SetCursorPosX(node.X + RowPadX);
        if (ImGui.Button("+Add##anwadd_" + node.Id))
        {
            node.WeatherGroups.Add(new DynamicWeatherGroup());
            config.SaveActiveDyn();
        }
    }

    // Out-pin on a timer row: live while that timer's output is ON.
    // Same mechanics as weather group pins.
    private void TimerPin(DynamicPresetData data, DynamicAnimNode node, Dictionary<string, PinSet> pinRects, Vector2 nodeScreen, float nodeW, DynamicTimer t)
    {
        var rMin = ImGui.GetItemRectMin();
        var rMax = ImGui.GetItemRectMax();
        float cy = (rMin.Y + rMax.Y) * 0.5f;
        var rect = new Vector2(nodeScreen.X + nodeW - 11, cy - 11);
        if (!pinRects.TryGetValue(node.Id, out var ps))
            pinRects[node.Id] = ps = new PinSet();
        string pin = "t:" + t.Id;
        ps.OutPins[pin] = rect;
        ImGui.SetCursorScreenPos(rect);
        bool clicked = ImGui.InvisibleButton("##pin_t_" + t.Id, new Vector2(22, 22));
        bool hov = ImGui.IsItemHovered();
        if (clicked)
        {
            if (pendingLinkFrom == node.Id && pendingLinkPin == pin) { pendingLinkFrom = null; pendingLinkPin = ""; }
            else { pendingLinkFrom = node.Id; pendingLinkPin = pin; }
        }
        uint dot = (pendingLinkFrom == node.Id && pendingLinkPin == pin) ? 0xFF80FF80 : 0xFF60C060;
        var dl = ImGui.GetWindowDrawList();
        dl.AddCircleFilled(rect + new Vector2(11, 11), 5, dot);
        if (hov || (pendingLinkFrom == node.Id && pendingLinkPin == pin))
            dl.AddCircle(rect + new Vector2(11, 11), 8, 0xFFFFFFFF, 0, 1.5f);
        if (data.Edges.Any(e => e.From == node.Id && (e.FromPin ?? "") == pin))
            dl.AddCircle(rect + new Vector2(11, 11), 8, 0xFF00A5FF, 0, 1.5f);
    }

    private void DrawTimerRows(DynamicPresetData data, DynamicAnimNode node, Dictionary<string, PinSet> pinRects, Vector2 nodeScreen, float nodeW)
    {
        bool stop = node.StopIfStarterOff;
        ImGui.SetCursorPosX(node.X + RowPadX);
        if (ImGui.Checkbox("Stop if Starter Off##antstop_" + node.Id, ref stop))
        {
            node.StopIfStarterOff = stop;
            config.SaveActiveDyn();
        }
        foreach (var entry in node.Timers.ToList())
        {
            var t = entry;
            int ti = node.Timers.IndexOf(t);
            bool live = plugin.TimerOutputOn(node.Id, t.Id);
            Vector4 labelCol = live ? new Vector4(0.45f, 1f, 0.45f, 1f) : new Vector4(0.91f, 0.91f, 0.91f, 1f);
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(labelCol, $"T{ti + 1}:");
            ImGui.SameLine(0, 4);
            // Split the remaining row evenly between both inputs.
            // Reserve room for x button + pin zone.
            float stayLabelW = ImGui.CalcTextSize("Stay:").X;
            float pairW = Math.Max(84, node.X + nodeW - RowPadX - ImGui.GetCursorPosX() - 58);
            float eachW = Math.Max(40, (pairW - stayLabelW - 8) / 2);
            ImGui.SetNextItemWidth(eachW);
            float delay = t.DelaySec;
            if (ImGui.InputFloat("##antmd_" + t.Id, ref delay, 0.5f, 1f, "%.1f"))
            {
                t.DelaySec = Math.Max(0, delay);
                config.SaveActiveDyn();
            }
            ImGui.SameLine(0, 4);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Stay:");
            ImGui.SameLine(0, 4);
            ImGui.SetNextItemWidth(eachW);
            float stay = t.StaySec;
            if (ImGui.InputFloat("##antms_" + t.Id, ref stay, 0.5f, 1f, "%.1f"))
            {
                t.StaySec = Math.Max(0, stay);
                config.SaveActiveDyn();
            }
            ImGui.SameLine(0, 4);
            if (ImGui.SmallButton("x##antmx_" + t.Id))
            {
                node.Timers.Remove(t);
                data.Edges.RemoveAll(e => e.From == node.Id && (e.FromPin ?? "") == "t:" + t.Id);
                config.SaveActiveDyn();
            }
            else
            {
                TimerPin(data, node, pinRects, nodeScreen, nodeW, t);
            }
        }
        ImGui.SetCursorPosX(node.X + RowPadX);
        if (ImGui.Button("+Add##antadd_" + node.Id))
        {
            node.Timers.Add(new DynamicTimer());
            config.SaveActiveDyn();
        }
    }

    private void DrawTriggerRows(DynamicPresetData data, DynamicAnimNode node, Dictionary<string, PinSet> pinRects, Vector2 nodeScreen, float nodeW)
    {
        if (startEndNode == node.Id)
        {
            DrawStartEndEditor(data, node);
            return;
        }
        if (midEndNode == node.Id)
        {
            DrawMidEditor(data, node);
            return;
        }
        int kind = node.TriggerKind;
        if (!IsCoords(node))
        {
            // Legacy kind 20 shows under its real name (not clamped to 19).
            bool legacyRes = IsTrigger(node) && node.TriggerKind == 20;
            kind = Math.Clamp(kind, 0, TriggerKindNames.Length - 1);
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Trigger Type:");
            ImGui.SameLine(0, 4);
            ImGui.SetNextItemWidth(Math.Max(40, node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX()));
            if (ImGui.BeginCombo("##antk_" + node.Id, legacyRes ? "Resolution (legacy)" : TriggerKindNames[kind]))
            {
                for (int i = 0; i < TriggerKindNames.Length; i++)
                {
                if (i == 20) continue; // legacy Resolution stays hidden
                if (ImGui.Selectable(TriggerKindNames[i], i == kind))
                {
                    node.TriggerKind = i;
                    config.SaveActiveDyn();
                }
            }
            ImGui.EndCombo();
        }
    }
        // Pin-activation action: what firing this trigger does besides its
        // envelope. Command row appears only for Send Command.
        if (kind == 19)
        {
            string[] pinActions = { "(none)", "Send Command" };
            int act = Math.Clamp(node.PinAction, 0, 1);
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Action:");
            ImGui.SameLine(0, 4);
            ImGui.SetNextItemWidth(Math.Max(40, node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX()));
            if (ImGui.BeginCombo("##anpa_" + node.Id, pinActions[act]))
            {
                for (int a = 0; a < pinActions.Length; a++)
                {
                    if (ImGui.Selectable(pinActions[a], a == act))
                    {
                        node.PinAction = a;
                        config.SaveActiveDyn();
                    }
                    if (a == act) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            if (act == 1)
            {
                ImGui.SetCursorPosX(node.X + RowPadX);
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted("Command:");
                ImGui.SameLine(0, 4);
                ImGui.SetNextItemWidth(Math.Max(40, node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX()));
                string cmd = node.PinActionCommand ?? "";
                if (ImGui.InputText("##anpac_" + node.Id, ref cmd, 128))
                {
                    node.PinActionCommand = cmd;
                    config.SaveActiveDyn();
                }
            }
        }
        if (kind == 20)
        {
            // Window vertical resolution sensor: preset heights or custom.
            // Legacy trigger flavor (standalone Resolution node preferred).
            string[] resModes = { "720p", "1080p", "1440p", "4K", "Custom" };
            int rm = Math.Clamp(node.ResMode, 0, 4);
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Height:");
            ImGui.SameLine(0, 4);
            ImGui.SetNextItemWidth(Math.Max(40, node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX()));
            if (ImGui.BeginCombo("##anres_" + node.Id, resModes[rm]))
            {
                for (int m = 0; m < resModes.Length; m++)
                {
                    if (ImGui.Selectable(resModes[m], m == rm))
                    {
                        node.ResMode = m;
                        config.SaveActiveDyn();
                    }
                    if (m == rm) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            string[] resCompares = { "Equal", "Higher", "Lower" };
            int rc = Math.Clamp(node.ResCompare, 0, 2);
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Mode:");
            ImGui.SameLine(0, 4);
            ImGui.SetNextItemWidth(Math.Max(40, node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX()));
            if (ImGui.BeginCombo("##anresm_" + node.Id, resCompares[rc]))
            {
                for (int m = 0; m < resCompares.Length; m++)
                {
                    if (ImGui.Selectable(resCompares[m], m == rc))
                    {
                        node.ResCompare = m;
                        config.SaveActiveDyn();
                    }
                    if (m == rc) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            if (rm == 4)
            {
                ImGui.SetCursorPosX(node.X + RowPadX);
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted("Custom px:");
                ImGui.SameLine(0, 4);
                ImGui.SetNextItemWidth(Math.Max(40, node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX()));
                int ch = Math.Max(1, node.ResCustomH);
                if (ImGui.InputInt("##anresc_" + node.Id, ref ch))
                {
                    node.ResCustomH = Math.Max(1, ch);
                    config.SaveActiveDyn();
                }
            }
            try
            {
                if (Plugin.TryGetGameClientSize(out int cww, out int cwh))
                    ImGui.TextDisabled($"Now: {cww}x{cwh}");
            }
            catch { }
        }
        if (kind == 0)
        {
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Key:");
            ImGui.SameLine(0, 4);
            bool capturing = captureNode == node.Id;
            string keyLabel = capturing ? "press key..." : (node.Hotkey == 0 ? "Set" : VkName(node.Hotkey));
            if (ImGui.Button(keyLabel + "##ankey_" + node.Id))
                captureNode = capturing ? null : node.Id;
            if (capturing)
            {
                if (ImGui.IsKeyPressed(ImGuiKey.Escape) || ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                    captureNode = null;
                else
                {
                    foreach (int vk in VkScan)
                    {
                        if (Plugin.IsKeyDown(vk))
                        {
                            node.Hotkey = vk;
                            config.SaveActiveDyn();
                            captureNode = null;
                            break;
                        }
                    }
                }
            }
        }
        else if (kind == 9 || kind == 10)
        {
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Revert:");
            ImGui.SameLine(0, 4);
            bool rev = node.ThresholdRevert;
            if (ImGui.Checkbox("##anthr_" + node.Id, ref rev))
            {
                node.ThresholdRevert = rev;
                config.SaveActiveDyn();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Switch Below% to Above%");
            string pctLabel = node.ThresholdRevert ? "Above %:" : "Below %:";
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted($"Start {pctLabel}");
            ImGui.SameLine(0, 4);
            ImGui.SetNextItemWidth(Math.Max(40, node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX()));
            float pct = node.ThresholdPct;
            if (ImGui.InputFloat("##anth_" + node.Id, ref pct, 1f, 5f, "%.0f"))
            {
                node.ThresholdPct = Math.Clamp(pct, 0, 100);
                config.SaveActiveDyn();
            }
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted($"Max {pctLabel}");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Separate from Start for band mode: factor ramps 0->1 across the band (timed envelope ignored). Equal values = legacy binary edge.");
            ImGui.SameLine(0, 4);
            ImGui.SetNextItemWidth(Math.Max(40, node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX()));
            float pctMax = node.ThresholdMax;
            if (ImGui.InputFloat("##anthm_" + node.Id, ref pctMax, 1f, 5f, "%.0f"))
            {
                node.ThresholdMax = Math.Clamp(pctMax, 0, 100);
                config.SaveActiveDyn();
            }
        }
        else if (kind == 11)
        {
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Status:");
            ImGui.SameLine(0, 4);
            ImGui.SetNextItemWidth(Math.Max(40, node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX()));
            string sPreview = "Any";
            if (node.StatusIds.Count == 1)
            {
                sPreview = null!;
                foreach (var s in config.GetStatusList())
                    if (s.Id == (uint)node.StatusIds[0]) { sPreview = $"{s.Name} ({s.Id})"; break; }
                sPreview ??= $"{node.StatusIds.Count} selected";
            }
            else if (node.StatusIds.Count > 1)
                sPreview = $"{node.StatusIds.Count} selected";
            if (ImGui.BeginCombo("##ants_" + node.Id, sPreview))
            {
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                ImGui.InputTextWithHint("##antss_" + node.Id, "Search...", ref statusSearch, 64);
                ImGui.Separator();
                if (ImGui.Selectable("Any", node.StatusIds.Count == 0, ImGuiSelectableFlags.DontClosePopups))
                {
                    node.StatusIds.Clear();
                    config.SaveActiveDyn();
                }
                var sl = statusSearch.ToLowerInvariant();
                foreach (var s in config.GetStatusList())
                {
                    if (!string.IsNullOrEmpty(sl) && !s.Name.ToLowerInvariant().Contains(sl)) continue;
                    bool ssel = node.StatusIds.Contains((int)s.Id);
                    if (ImGui.Selectable($"{(ssel ? "[x] " : "[ ] ")}{s.Name} ({s.Id})", ssel, ImGuiSelectableFlags.DontClosePopups))
                    {
                        if (ssel) node.StatusIds.Remove((int)s.Id);
                        else node.StatusIds.Add((int)s.Id);
                        config.SaveActiveDyn();
                    }
                }
                ImGui.EndCombo();
            }
        }
        else if (kind == 12)
        {
            // QolBar condition set, Cammy-style: dropdown of the live
            // QoLBar set names (plugin.ConditionSetNames via IPC), not a
            // raw index. -1 = None (never fires).
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("QolBar Condition Set:");
            ImGui.SameLine(0, 4);
            ImGui.SetNextItemWidth(Math.Max(40, node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX()));
            string[] sets;
            try { sets = plugin.ConditionSetNames ?? Array.Empty<string>(); } catch { sets = Array.Empty<string>(); }
            if (sets.Length == 0)
            {
                // Self-heal a late-loading QoLBar (names refresh otherwise
                // only when the main window opens).
                try { plugin.RefreshConditionSets(); } catch { }
                try { sets = plugin.ConditionSetNames ?? Array.Empty<string>(); } catch { sets = Array.Empty<string>(); }
            }
            string SetLabel(int i) => string.IsNullOrWhiteSpace(sets[i]) ? $"Set {i}" : sets[i];
            string preview = node.QolbarSet >= 0 && node.QolbarSet < sets.Length ? SetLabel(node.QolbarSet) : (sets.Length == 0 ? "(QoLBar unavailable)" : "None");
            if (ImGui.BeginCombo("##antq_" + node.Id, preview))
            {
                // Refresh on open so renames/reorders show up immediately.
                try { plugin.RefreshConditionSets(); } catch { }
                try { sets = plugin.ConditionSetNames ?? Array.Empty<string>(); } catch { sets = Array.Empty<string>(); }
                if (ImGui.Selectable("None", node.QolbarSet < 0))
                {
                    node.QolbarSet = -1;
                    config.SaveActiveDyn();
                }
                for (int i = 0; i < sets.Length; i++)
                {
                    bool isSel = node.QolbarSet == i;
                    if (ImGui.Selectable(SetLabel(i), isSel))
                    {
                        node.QolbarSet = i;
                        config.SaveActiveDyn();
                    }
                    if (isSel) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            // Without Stay While Present the envelope fires once per rising
            // edge instead of staying on while the set holds.
            if (!node.StayWhilePresent)
            {
                ImGui.SetCursorPosX(node.X + RowPadX);
                ImGui.TextDisabled("Tip: Stay While Present keeps it on while the set holds.");
            }
        }
        if (kind == 16)
        {
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("At least N dead:");
            ImGui.SameLine(0, 4);
            ImGui.SetNextItemWidth(Math.Max(40, node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX()));
            int dc = node.DeadCount;
            if (ImGui.InputInt("##antd_" + node.Id, ref dc))
            {
                node.DeadCount = Math.Max(1, dc);
                config.SaveActiveDyn();
            }
        }
        if (kind == 17)
        {
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Command:");
            ImGui.SameLine(0, 4);
            ImGui.SetNextItemWidth(Math.Max(40, node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX()));
            string cc = node.ChatCommand;
            if (ImGui.InputText("##antc_" + node.Id, ref cc, 32, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                cc = cc.Trim();
                if (!cc.StartsWith("/")) cc = "/" + cc;
                node.ChatCommand = cc;
                config.SaveActiveDyn();
            }
        }
        if (kind == 15)
        {
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Emote:");
            ImGui.SameLine(0, 4);
            ImGui.SetNextItemWidth(Math.Max(40, node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX()));
            string ePreview = "Any";
            if (node.EmoteIds.Count == 1)
            {
                ePreview = null!;
                foreach (var e in config.GetEmoteList())
                    if (e.Id == (uint)node.EmoteIds[0]) { ePreview = $"{e.Name} ({e.Id})"; break; }
                ePreview ??= $"{node.EmoteIds.Count} selected";
            }
            else if (node.EmoteIds.Count > 1)
                ePreview = $"{node.EmoteIds.Count} selected";
            if (ImGui.BeginCombo("##ante_" + node.Id, ePreview))
            {
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                ImGui.InputTextWithHint("##antes_" + node.Id, "Search...", ref emoteSearch, 64);
                ImGui.Separator();
                if (ImGui.Selectable("Any", node.EmoteIds.Count == 0, ImGuiSelectableFlags.DontClosePopups))
                {
                    node.EmoteIds.Clear();
                    config.SaveActiveDyn();
                }
                var sl = emoteSearch.ToLowerInvariant();
                foreach (var e in config.GetEmoteList())
                {
                    if (!string.IsNullOrEmpty(sl) && !e.Name.ToLowerInvariant().Contains(sl)) continue;
                    bool esel = node.EmoteIds.Contains((int)e.Id);
                    if (ImGui.Selectable($"{(esel ? "[x] " : "[ ] ")}{e.Name} ({e.Id})", esel, ImGuiSelectableFlags.DontClosePopups))
                    {
                        if (esel) node.EmoteIds.Remove((int)e.Id);
                        else node.EmoteIds.Add((int)e.Id);
                        config.SaveActiveDyn();
                    }
                }
                ImGui.EndCombo();
            }
        }
        if (kind == 7)
        {
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Action:");
            ImGui.SameLine(0, 4);
            ImGui.SetNextItemWidth(Math.Max(40, node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX()));
            string aPreview = "Any";
            if (node.CastActionIds.Count == 1)
            {
                aPreview = null!;
                foreach (var a in config.GetActionList())
                    if (a.Id == (uint)node.CastActionIds[0]) { aPreview = $"{a.Name} ({a.Id})"; break; }
                aPreview ??= $"{node.CastActionIds.Count} selected";
            }
            else if (node.CastActionIds.Count > 1)
                aPreview = $"{node.CastActionIds.Count} selected";
            if (ImGui.BeginCombo("##anta_" + node.Id, aPreview))
            {
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                ImGui.InputTextWithHint("##antaa_" + node.Id, "Search...", ref actionSearch, 64);
                ImGui.Separator();
                if (ImGui.Selectable("Any", node.CastActionIds.Count == 0, ImGuiSelectableFlags.DontClosePopups))
                {
                    node.CastActionIds.Clear();
                    config.SaveActiveDyn();
                }
                var sl = actionSearch.ToLowerInvariant();
                foreach (var a in config.GetActionList())
                {
                    if (!string.IsNullOrEmpty(sl) && !a.Name.ToLowerInvariant().Contains(sl)) continue;
                    bool asel = node.CastActionIds.Contains((int)a.Id);
                    if (ImGui.Selectable($"{(asel ? "[x] " : "[ ] ")}{a.Name} ({a.Id})", asel, ImGuiSelectableFlags.DontClosePopups))
                    {
                        if (asel) node.CastActionIds.Remove((int)a.Id);
                        else node.CastActionIds.Add((int)a.Id);
                        config.SaveActiveDyn();
                    }
                }
                ImGui.EndCombo();
            }
        }
        if (kind == 18)
        {
            // One-time migration: legacy single-zone fields become Zones[0].
            if (node.Zones.Count == 0)
            {
                node.Zones.Add(new DynamicZone {
                    LocTerritory = node.LocTerritory, LocX = node.LocX, LocY = node.LocY, LocZ = node.LocZ,
                    LocShape = node.LocShape, RadiusStart = node.RadiusStart, RadiusMax = node.RadiusMax,
                    BoxOX = node.BoxOX, BoxOY = node.BoxOY, BoxOZ = node.BoxOZ,
                    BoxIX = node.BoxIX, BoxIY = node.BoxIY, BoxIZ = node.BoxIZ,
                    BoxOCX = node.BoxOCX, BoxOCY = node.BoxOCY, BoxOCZ = node.BoxOCZ,
                    BoxYaw = node.BoxYaw, BoxPitch = node.BoxPitch, BoxRoll = node.BoxRoll });
                node.SelectedZoneId = node.Zones[0].Id;
                config.SaveActiveDyn();
            }
            // Which zone the rows below edit (each zone has its own shape).
            DynamicZone lz = node.Zones.Find(x => x.Id == node.SelectedZoneId) ?? node.Zones[0];
            if (node.SelectedZoneId != lz.Id)
            {
                node.SelectedZoneId = lz.Id;
                config.SaveActiveDyn();
            }
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Zone:");
            ImGui.SameLine(0, 4);
            float lzAddW = ImGui.CalcTextSize("+Add").X + ImGui.GetStyle().FramePadding.X * 2 + 8;
            ImGui.SetNextItemWidth(Math.Max(40, node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX() - lzAddW - 4));
            if (ImGui.BeginCombo("##anlzpick_" + node.Id, $"Zone {node.Zones.IndexOf(lz) + 1}"))
            {
                float inner = ImGui.GetContentRegionAvail().X;
                foreach (var zz in node.Zones.ToList())
                {
                    int zi = node.Zones.IndexOf(zz);
                    bool isSel = zz.Id == lz.Id;
                    if (ImGui.Selectable($"Zone {zi + 1}##anlzsel_" + zz.Id, isSel, ImGuiSelectableFlags.None, new Vector2(Math.Max(20, inner - 32), 0)))
                    {
                        node.SelectedZoneId = zz.Id;
                        config.SaveActiveDyn();
                    }
                    ImGui.SameLine(0, 4);
                    // Delete is Shift-gated (and never the last zone).
                    bool shift = false;
                    try { shift = ImGui.GetIO().KeyShift; } catch { }
                    ImGui.BeginDisabled(!shift || node.Zones.Count <= 1);
                    if (ImGui.SmallButton("x##anlzx_" + zz.Id))
                    {
                        node.Zones.Remove(zz);
                        if (node.SelectedZoneId == zz.Id) node.SelectedZoneId = node.Zones.FirstOrDefault()?.Id ?? "";
                        config.SaveActiveDyn();
                    }
                    ImGui.EndDisabled();
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine(0, 4);
            if (ImGui.Button("+Add##anlzadd_" + node.Id))
            {
                var nz = new DynamicZone();
                try
                {
                    var ppz = plugin.PlayerPosition();
                    if (ppz != null)
                    {
                        nz.LocX = ppz.Value.X;
                        nz.LocY = ppz.Value.Y;
                        nz.LocZ = ppz.Value.Z;
                        nz.LocTerritory = plugin.CurrentTerritoryId;
                    }
                }
                catch { }
                node.Zones.Add(nz);
                node.SelectedZoneId = nz.Id;
                config.SaveActiveDyn();
                lz = nz;
            }
            var z = lz;
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Shape:");
            ImGui.SameLine(0, 4);
            ImGui.SetNextItemWidth(Math.Max(40, node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX()));
            if (ImGui.BeginCombo("##anls_" + z.Id, ZoneShapeName(z.LocShape)))
            {
                if (ImGui.Selectable("Sphere (Simple)", z.LocShape == 2)) { z.LocShape = 2; config.SaveActiveDyn(); }
                if (ImGui.Selectable("Sphere (Advanced)", z.LocShape == 0)) { z.LocShape = 0; config.SaveActiveDyn(); }
                if (ImGui.Selectable("Box (Simple)", z.LocShape == 3)) { z.LocShape = 3; config.SaveActiveDyn(); }
                if (ImGui.Selectable("Box (Advanced)", z.LocShape == 1)) { z.LocShape = 1; config.SaveActiveDyn(); }
                ImGui.EndCombo();
            }
            if (z.LocShape == 0 || z.LocShape == 2)
            {
                LocVisRow(node, z);
                // Max reduction: how far the gate may pull (100 = to zero).
                // Only meaningful with an active gate.
                if (z.LocVisMode != 0)
                {
                    ImGui.SetCursorPosX(node.X + RowPadX);
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted("Max Reduction:");
                    ImGui.SameLine(0, 4);
                    ImGui.SetNextItemWidth(Math.Max(20, node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX()));
                    float mr = z.LocVisMaxReduction;
                    if (ImGui.InputFloat("##anlvr_" + z.Id, ref mr, 1f, 5f, "%.0f"))
                    {
                        z.LocVisMaxReduction = Math.Clamp(mr, 0f, 100f);
                        config.SaveActiveDyn();
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Percent the visibility gate may shave off (0-100).\n100 = hidden scales to 0. 75 = hidden lands on 25%.");
                }
                // Coverage toggle lives here only: sphere zone with Sphere
                // visibility selected.
                if (z.LocVisMode == 2)
                {
                    ImGui.SetCursorPosX(node.X + RowPadX);
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted("Coverage:");
                    ImGui.SameLine(0, 4);
                    bool cov = z.LocVisCoverage;
                    if (ImGui.Checkbox("##anlvc_" + z.Id, ref cov))
                    {
                        z.LocVisCoverage = cov;
                        config.SaveActiveDyn();
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Off = full strength while any part is visible.\nOn = scale strength by the visible fraction of the sphere.");
                    if (z.LocVisCoverage)
                        TriggerParam(node, "Coverage Fade", z.LocVisFadeSec, v => z.LocVisFadeSec = v);
                }
            }
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Track:");
            ImGui.SameLine(0, 4);
            ImGui.SetNextItemWidth(Math.Max(40, node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX()));
            if (ImGui.BeginCombo("##anlt_" + node.Id, node.LocUseCamera ? "Camera" : "Character"))
            {
                if (ImGui.Selectable("Character", !node.LocUseCamera)) { node.LocUseCamera = false; config.SaveActiveDyn(); }
                if (ImGui.Selectable("Camera", node.LocUseCamera)) { node.LocUseCamera = true; config.SaveActiveDyn(); }
                ImGui.EndCombo();
            }
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.AlignTextToFramePadding();
            string zn = z.LocTerritory == 0 ? "Any" : ConfigWindow.GetZoneName(z.LocTerritory);
            ImGui.TextUnformatted("Territory: " + zn);
            ImGui.SetCursorPosX(node.X + RowPadX);
            if (ImGui.Button("Set Current##anlz_" + z.Id))
            {
                z.LocTerritory = plugin.CurrentTerritoryId;
                config.SaveActiveDyn();
            }
            // Same as doors: XYZ edits in the volume's local frame so they
            // follow the rotation. Storage stays world; only the fields
            // transform (identity for spheres/unrotated boxes).
            var cLocal = EulerBox.Rotate(new Vector3(z.LocX, z.LocY, z.LocZ), z.BoxYaw, z.BoxPitch, z.BoxRoll, true);
            LocBoxRow(node, "XYZ:", "c" + z.Id, cLocal.X, cLocal.Y, cLocal.Z,
                (a, b, c) => {
                    var cWorld = EulerBox.Rotate(new Vector3(a, b, c), z.BoxYaw, z.BoxPitch, z.BoxRoll, false);
                    z.LocX = cWorld.X; z.LocY = cWorld.Y; z.LocZ = cWorld.Z;
                }, false, "%.1f");
            ImGui.SetCursorPosX(node.X + RowPadX);
            if (ImGui.Button("Set Current##anlc_" + z.Id))
            {
                var pp = plugin.PlayerPosition();
                if (pp != null)
                {
                    z.LocX = pp.Value.X;
                    z.LocY = pp.Value.Y;
                    z.LocZ = pp.Value.Z;
                    config.SaveActiveDyn();
                }
            }
            if (z.LocShape == 1)
            {
                LocBoxRow(node, "Out:", "o" + z.Id, z.BoxOX, z.BoxOY, z.BoxOZ,
                    (a, b, c) => { z.BoxOX = a; z.BoxOY = b; z.BoxOZ = c; });
                LocBoxRow(node, "In:", "i" + z.Id, z.BoxIX, z.BoxIY, z.BoxIZ,
                    (a, b, c) => { z.BoxIX = a; z.BoxIY = b; z.BoxIZ = c; });
                LocBoxRow(node, "oPos:", "oc" + z.Id, z.BoxOCX, z.BoxOCY, z.BoxOCZ,
                    (a, b, c) => { z.BoxOCX = a; z.BoxOCY = b; z.BoxOCZ = c; }, false, "%.1f");
                LocRotRow(node, z);
            }
            else if (z.LocShape == 3)
            {
                LocBoxRow(node, "In:", "i" + z.Id, z.BoxIX, z.BoxIY, z.BoxIZ,
                    (a, b, c) => { z.BoxIX = a; z.BoxIY = b; z.BoxIZ = c; });
                LocRotRow(node, z);
                TriggerParam(node, "Fade in", z.FadeInSec, v => z.FadeInSec = v);
                TriggerParam(node, "Fade out", z.FadeOutSec, v => z.FadeOutSec = v);
            }
            else if (z.LocShape == 2)
            {
                TriggerParam(node, "Radius", z.RadiusMax, v => z.RadiusMax = v);
                TriggerParam(node, "Fade in", z.FadeInSec, v => z.FadeInSec = v);
                TriggerParam(node, "Fade out", z.FadeOutSec, v => z.FadeOutSec = v);
            }
            else
            {
                TriggerParam(node, "Radius Start", z.RadiusStart, v => z.RadiusStart = v);
                TriggerParam(node, "Radius Max", z.RadiusMax, v => z.RadiusMax = v);
                TriggerParam(node, "Fade in", z.FadeInSec, v => z.FadeInSec = v);
                TriggerParam(node, "Fade out", z.FadeOutSec, v => z.FadeOutSec = v);
            }
            if (z.LocShape == 1)
            {
                bool fixedUp = false;
                if (z.BoxIX > z.BoxOX) { z.BoxIX = z.BoxOX; fixedUp = true; }
                if (z.BoxIY > z.BoxOY) { z.BoxIY = z.BoxOY; fixedUp = true; }
                if (z.BoxIZ > z.BoxOZ) { z.BoxIZ = z.BoxOZ; fixedUp = true; }
                // Containment in box-local space so rotation can't break it.
                var offL = EulerBox.Rotate(
                    new Vector3(z.BoxOCX, z.BoxOCY, z.BoxOCZ),
                    z.BoxYaw, z.BoxPitch, z.BoxRoll, true);
                float clX = Math.Max(0, z.BoxOX - z.BoxIX);
                float clY = Math.Max(0, z.BoxOY - z.BoxIY);
                float clZ = Math.Max(0, z.BoxOZ - z.BoxIZ);
                float nx = Math.Clamp(offL.X, -clX, clX);
                float ny = Math.Clamp(offL.Y, -clY, clY);
                float nz = Math.Clamp(offL.Z, -clZ, clZ);
                if (nx != offL.X || ny != offL.Y || nz != offL.Z)
                {
                    var back = EulerBox.Rotate(new Vector3(nx, ny, nz), z.BoxYaw, z.BoxPitch, z.BoxRoll, false);
                    z.BoxOCX = back.X;
                    z.BoxOCY = back.Y;
                    z.BoxOCZ = back.Z;
                    fixedUp = true;
                }
                if (fixedUp) config.SaveActiveDyn();
            }
            else if (z.LocShape == 2 && z.RadiusMax < 0)
            {
                z.RadiusMax = 0;
                config.SaveActiveDyn();
            }
            else if (z.LocShape == 0 && z.RadiusMax > z.RadiusStart)
            {
                z.RadiusMax = z.RadiusStart;
                config.SaveActiveDyn();
            }
            float ldist = Plugin.LocationDistRaw(plugin.CurrentTerritoryId, plugin.TrackPosition(node), z);
            var (lblend, _) = plugin.LocationBlend(node);
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Outline:");
            ImGui.SameLine(0, 4);
            bool showV = node.ShowVolume;
            if (ImGui.Checkbox("##anlv_" + node.Id, ref showV))
            {
                node.ShowVolume = showV;
                config.SaveActiveDyn();
            }
            ImGui.SameLine(0, 8);
            ImGui.TextDisabled(ldist < 0 ? "Dist: —" : $"Dist: {ldist:F1} f: {lblend:F2}");
        }
        else
        {
            TriggerParam(node, "Delay", node.DelaySec, v => node.DelaySec = v);
            PhasePin(data, node, pinRects, nodeScreen, nodeW, "delay");
            TriggerParam(node, "Fade in", node.FadeInSec, v => node.FadeInSec = v);
            PhasePin(data, node, pinRects, nodeScreen, nodeW, "fadein");
            if (DynamicAnimNode.SupportsStayHold(kind))
            {
                // State-based triggers can hold at peak while the condition
                // holds instead of using the fixed Stay timer.
                string holdText = kind == 11
                    ? "as long as the status effect is present"
                    : "as long as the trigger is active";
                ImGui.SetCursorPosX(node.X + RowPadX);
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted("Stay:");
                ImGui.SameLine(0, 4);
                bool hold = node.StayWhilePresent;
                if (ImGui.Checkbox("##anst_" + node.Id, ref hold))
                {
                    node.StayWhilePresent = hold;
                    config.SaveActiveDyn();
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(holdText);
                ImGui.SameLine(0, 4);
                if (hold)
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted(holdText);
                }
                else
                {
                    ImGui.SetNextItemWidth(Math.Max(20, node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX()));
                    float sv = node.StaySec;
                    if (ImGui.InputFloat("##antpst_" + node.Id, ref sv, 0.1f, 1f, "%.1f"))
                    {
                        node.StaySec = Math.Max(0, sv);
                        config.SaveActiveDyn();
                    }
                }
            }
            else
                TriggerParam(node, "Stay", node.StaySec, v => node.StaySec = v);
            PhasePin(data, node, pinRects, nodeScreen, nodeW, "stay");
            TriggerParam(node, "Fade out", node.FadeOutSec, v => node.FadeOutSec = v);
            PhasePin(data, node, pinRects, nodeScreen, nodeW, "fadeout");
        }
        ImGui.SetCursorPosX(node.X + RowPadX);
        if (ImGui.Button("Start/End Values##anse_" + node.Id))
        {
            startEndNode = node.Id;
            startEndDrafts.Remove(node.Id);
        }
        if (ShowMidRows(node))
        {
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Midpoint:");
            ImGui.SameLine(0, 4);
            bool mid = node.UseMidpoint;
            if (ImGui.Checkbox("##anmid_" + node.Id, ref mid))
            {
                node.UseMidpoint = mid;
                config.SaveActiveDyn();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Shape the blend as start -> mid -> peak (mid pinned at half factor).");
            ImGui.SameLine(0, 4);
            if (ImGui.Button("Mid Values...##anmidv_" + node.Id))
            {
                midEndNode = node.Id;
                midEndDrafts.Remove(node.Id);
            }
        }
    }

    private void DrawWallRows(DynamicPresetData data, DynamicAnimNode node)
    {
        if (startEndNode == node.Id)
        {
            DrawStartEndEditor(data, node);
            return;
        }
        // One-time migration: legacy single-door fields become Doors[0].
        if (node.Doors.Count == 0)
        {
            node.Doors.Add(new DynamicDoor {
                LocTerritory = node.LocTerritory, LocX = node.LocX, LocY = node.LocY, LocZ = node.LocZ,
                BoxYaw = node.BoxYaw, BoxPitch = node.BoxPitch, BoxRoll = node.BoxRoll,
                WallW = node.WallW, WallH = node.WallH,
                KeepZones = new List<uint>(node.KeepZones) });
            node.SelectedDoorId = node.Doors[0].Id;
            config.SaveActiveDyn();
        }
        // Which door the rows below edit.
        DynamicDoor sel = node.Doors.Find(x => x.Id == node.SelectedDoorId) ?? node.Doors[0];
        if (node.SelectedDoorId != sel.Id)
        {
            node.SelectedDoorId = sel.Id;
            config.SaveActiveDyn();
        }
        // Door picker + Add sit between the Key row and the XYZ row.
        ImGui.SetCursorPosX(node.X + RowPadX);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Door:");
        ImGui.SameLine(0, 4);
        float nodeW = NodeWidth(node);
        float addBW = ImGui.CalcTextSize("+Add").X + ImGui.GetStyle().FramePadding.X * 2 + 8;
        ImGui.SetNextItemWidth(Math.Max(40, node.X + nodeW - RowPadX - ImGui.GetCursorPosX() - addBW - 4));
        if (ImGui.BeginCombo("##andoor_" + node.Id, $"Door {node.Doors.IndexOf(sel) + 1}"))
        {
            float inner = ImGui.GetContentRegionAvail().X;
            foreach (var dd in node.Doors.ToList())
            {
                int di = node.Doors.IndexOf(dd);
                bool isSel = dd.Id == sel.Id;
                if (ImGui.Selectable($"Door {di + 1}##andoorsel_" + dd.Id, isSel, ImGuiSelectableFlags.None, new Vector2(Math.Max(20, inner - 32), 0)))
                {
                    node.SelectedDoorId = dd.Id;
                    config.SaveActiveDyn();
                }
                ImGui.SameLine(0, 4);
                // Delete is Shift-gated (and never the last door).
                bool shift = false;
                try { shift = ImGui.GetIO().KeyShift; } catch { }
                ImGui.BeginDisabled(!shift || node.Doors.Count <= 1);
                if (ImGui.SmallButton("x##andoorx_" + dd.Id))
                {
                    node.Doors.Remove(dd);
                    if (node.SelectedDoorId == dd.Id) node.SelectedDoorId = node.Doors.FirstOrDefault()?.Id ?? "";
                    config.SaveActiveDyn();
                }
                ImGui.EndDisabled();
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine(0, 4);
        if (ImGui.Button("+Add##andooradd_" + node.Id))
        {
            var nd = new DynamicDoor();
            try
            {
                var pp0 = plugin.PlayerPosition();
                if (pp0 != null)
                {
                    nd.LocX = pp0.Value.X;
                    nd.LocY = pp0.Value.Y;
                    nd.LocZ = pp0.Value.Z;
                    nd.LocTerritory = plugin.CurrentTerritoryId;
                }
            }
            catch { }
            node.Doors.Add(nd);
            node.SelectedDoorId = nd.Id;
            config.SaveActiveDyn();
            sel = nd;
        }
        var d = sel;
        // Option B: XYZ edits in the door's local frame (X = along width,
        // Y = along height, Z = through the doorway). Storage stays world
        // so rotation still pivots in place; only the fields transform.
        var wLocal = EulerBox.Rotate(new Vector3(d.LocX, d.LocY, d.LocZ), d.BoxYaw, d.BoxPitch, d.BoxRoll, true);
        LocBoxRow(node, "XYZ:", "wc" + d.Id, wLocal.X, wLocal.Y, wLocal.Z,
            (a, b, c) => {
                var wWorld = EulerBox.Rotate(new Vector3(a, b, c), d.BoxYaw, d.BoxPitch, d.BoxRoll, false);
                d.LocX = wWorld.X; d.LocY = wWorld.Y; d.LocZ = wWorld.Z;
            }, false, "%.1f");
        ImGui.SetCursorPosX(node.X + RowPadX);
        ImGui.AlignTextToFramePadding();
        string wzn = d.LocTerritory == 0 ? "Any" : ConfigWindow.GetZoneName(d.LocTerritory);
        ImGui.TextUnformatted("Zone: " + wzn);
        ImGui.SetCursorPosX(node.X + RowPadX);
        if (ImGui.Button("Set Current##anwc_" + d.Id))
        {
            var pp = plugin.PlayerPosition();
            if (pp != null)
            {
                // World storage: door center stays put when rotated.
                d.LocX = pp.Value.X;
                d.LocY = pp.Value.Y;
                d.LocZ = pp.Value.Z;
                d.LocTerritory = plugin.CurrentTerritoryId;
                config.SaveActiveDyn();
            }
        }
        ImGui.SetCursorPosX(node.X + RowPadX);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Rot°:");
        ImGui.SameLine(0, 4);
        float wrw = Math.Max(24, (node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX()) / 3 - 3);
        float wyw = d.BoxYaw, wpw = d.BoxPitch, wrw2 = d.BoxRoll;
        bool wrchanged = false;
        ImGui.SetNextItemWidth(wrw);
        if (ImGui.InputFloat("##anwy_" + d.Id, ref wyw, 5f, 15f, "%.0f")) wrchanged = true;
        ImGui.SameLine(0, 4);
        ImGui.SetNextItemWidth(wrw);
        if (ImGui.InputFloat("##anwp_" + d.Id, ref wpw, 5f, 15f, "%.0f")) wrchanged = true;
        ImGui.SameLine(0, 4);
        ImGui.SetNextItemWidth(wrw);
        if (ImGui.InputFloat("##anwr_" + d.Id, ref wrw2, 5f, 15f, "%.0f")) wrchanged = true;
        if (wrchanged)
        {
            d.BoxYaw = EulerBox.Wrap180(wyw);
            d.BoxPitch = EulerBox.Wrap180(wpw);
            d.BoxRoll = EulerBox.Wrap180(wrw2);
            config.SaveActiveDyn();
        }
        ImGui.SetCursorPosX(node.X + RowPadX);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("W:");
        ImGui.SameLine(0, 4);
        ImGui.SetNextItemWidth(70);
        float ww = d.WallW;
        if (ImGui.InputFloat("##anww_" + d.Id, ref ww, 1f, 5f, "%.1f"))
        {
            d.WallW = Math.Max(0.5f, ww);
            config.SaveActiveDyn();
        }
        ImGui.SameLine(0, 4);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("H:");
        ImGui.SameLine(0, 4);
        ImGui.SetNextItemWidth(70);
        float hh = d.WallH;
        if (ImGui.InputFloat("##anwh_" + d.Id, ref hh, 1f, 5f, "%.1f"))
        {
            d.WallH = Math.Max(0.5f, hh);
            config.SaveActiveDyn();
        }
        ImGui.SetCursorPosX(node.X + RowPadX);
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("In Zones:");
        ImGui.SameLine();
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextDisabled(FontAwesomeIcon.InfoCircle.ToIconString());
        ImGui.PopFont();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Doors switch off when you change zones.\n\nIf a door leads somewhere that loads separately\n(like an inn room inside the same building),\nlist that room here and the door will stay on\nwhen you walk through into it.\n\nWalk into the room, then press + Current.");
        // Chips wrap 2 per row (first row shares the label); + Current
        // trails the last row when it has room, else its own row.
        {
            var zones = node.KeepZones.ToList();
            int zi = 0;
            foreach (var z in zones)
            {
                if (zi >= 2 && (zi - 2) % 2 == 0)
                    ImGui.SetCursorPosX(node.X + RowPadX);
                else
                    ImGui.SameLine();
                ImGui.TextUnformatted(z.ToString());
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Zone ID {z} — click x to remove");
                ImGui.SameLine();
                if (ImGui.SmallButton($"x##kz{node.Id}_{z}"))
                {
                    node.KeepZones.Remove(z);
                    config.SaveActiveDyn();
                }
                zi++;
            }
            bool lastRowFull = zones.Count > 2 && (zones.Count - 2) % 2 == 0;
            if (lastRowFull)
                ImGui.SetCursorPosX(node.X + RowPadX);
            else
                ImGui.SameLine();
            if (ImGui.SmallButton($"+ Current##kzadd{node.Id}"))
            {
                uint cur = plugin.CurrentTerritoryId;
                if (cur != 0 && !node.KeepZones.Contains(cur))
                {
                    node.KeepZones.Add(cur);
                    config.SaveActiveDyn();
                }
            }
        }
        TriggerParam(node, "Fade in", node.FadeInSec, v => node.FadeInSec = v);
        TriggerParam(node, "Fade out", node.FadeOutSec, v => node.FadeOutSec = v);
        ImGui.SetCursorPosX(node.X + RowPadX);
        if (ImGui.Button("Start/End Values##anse_" + node.Id))
        {
            startEndNode = node.Id;
            startEndDrafts.Remove(node.Id);
        }
    }

    private void LocBoxRow(DynamicAnimNode node, string label, string tag, float x, float y, float z, Action<float, float, float> assign, bool clampNonNegative = true, string fmt = "%.0f")
    {
        ImGui.SetCursorPosX(node.X + RowPadX);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(label == "Out:" ? "Outer box half-size XYZ" : "Inner box half-size XYZ");
        ImGui.SameLine(0, 4);
        float w = Math.Max(24, (node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX()) / 3 - 3);
        float nx = x, ny = y, nz = z;
        bool changed = false;
        ImGui.SetNextItemWidth(w);
        if (ImGui.InputFloat("##anbx_" + tag + "x" + node.Id, ref nx, 1f, 5f, fmt)) changed = true;
        ImGui.SameLine(0, 4);
        ImGui.SetNextItemWidth(w);
        if (ImGui.InputFloat("##anbx_" + tag + "y" + node.Id, ref ny, 1f, 5f, fmt)) changed = true;
        ImGui.SameLine(0, 4);
        ImGui.SetNextItemWidth(w);
        if (ImGui.InputFloat("##anbx_" + tag + "z" + node.Id, ref nz, 1f, 5f, fmt)) changed = true;
        if (changed)
        {
            if (clampNonNegative) assign(Math.Max(0, nx), Math.Max(0, ny), Math.Max(0, nz));
            else assign(nx, ny, nz);
            config.SaveActiveDyn();
        }
    }

    // Camera-visibility gate row (sphere zones only): the zone counts only
    // while its center (or RadiusMax sphere) is in camera view.
    private void LocVisRow(DynamicAnimNode node, DynamicZone z)
    {
        ImGui.SetCursorPosX(node.X + RowPadX);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Visible:");
        ImGui.SameLine(0, 4);
        ImGui.SetNextItemWidth(Math.Max(40, node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX()));
        string[] visModes = { "Off", "Center", "Sphere" };
        int vm = Math.Clamp(z.LocVisMode, 0, 2);
        if (ImGui.BeginCombo("##anlv_" + z.Id, visModes[vm]))
        {
            for (int m = 0; m < visModes.Length; m++)
            {
                if (ImGui.Selectable(visModes[m], m == vm))
                {
                    z.LocVisMode = m;
                    config.SaveActiveDyn();
                }
                if (m == vm) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Zone counts only while in camera view.\nCenter = the zone center; Sphere = any part of the RadiusMax sphere (standing inside counts).\nFrustum check only - walls and objects do NOT block it.");
    }

    private void LocRotRow(DynamicAnimNode node, DynamicZone z)
    {
        ImGui.SetCursorPosX(node.X + RowPadX);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Rot°:");
        ImGui.SameLine(0, 4);
        float rw = Math.Max(24, (node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX()) / 3 - 3);
        float yw = z.BoxYaw, pw = z.BoxPitch, rw2 = z.BoxRoll;
        bool rchanged = false;
        ImGui.SetNextItemWidth(rw);
        if (ImGui.InputFloat("##anby_" + z.Id, ref yw, 5f, 15f, "%.0f")) rchanged = true;
        ImGui.SameLine(0, 4);
        ImGui.SetNextItemWidth(rw);
        if (ImGui.InputFloat("##anbp_" + z.Id, ref pw, 5f, 15f, "%.0f")) rchanged = true;
        ImGui.SameLine(0, 4);
        ImGui.SetNextItemWidth(rw);
        if (ImGui.InputFloat("##anbr_" + z.Id, ref rw2, 5f, 15f, "%.0f")) rchanged = true;
        if (rchanged)
        {
            z.BoxYaw = EulerBox.Wrap180(yw);
            z.BoxPitch = EulerBox.Wrap180(pw);
            z.BoxRoll = EulerBox.Wrap180(rw2);
            config.SaveActiveDyn();
        }
    }

    private void DrawStartEndEditor(DynamicPresetData data, DynamicAnimNode node)
    {
        var kf = BoundKeyframe(data, node);
        if (kf == null)
        {
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.TextDisabled("None — bind a keyframe first.");
            ImGui.SetCursorPosX(node.X + RowPadX);
            if (ImGui.Button("Back##anse_back_" + node.Id))
            {
                startEndNode = null;
                startEndDrafts.Remove(node.Id);
            }
            return;
        }
        var ticks = kf.TickedUniforms.ToList();
        if (ticks.Count == 0)
        {
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.TextDisabled("No ticked settings.");
        }
        if (!startEndDrafts.TryGetValue(node.Id, out var drafts))
            startEndDrafts[node.Id] = drafts = new Dictionary<string, string>();
        // Grouped by shader file so every setting shows its source.
        // Order is display-only (drafts/confirm are key-based). Uncapped:
        // tall nodes scroll with the canvas.
        var ordered = ticks.OrderBy(k => { int s = k.IndexOf('\0'); return s < 0 ? "" : k.Substring(0, s); }, StringComparer.OrdinalIgnoreCase)
            .ThenBy(k => { int s = k.IndexOf('\0'); return s < 0 ? k : k.Substring(s + 1); }, StringComparer.OrdinalIgnoreCase).ToList();
        string lastFile = "\0NOMATCH\0";
        for (int i = 0; i < ordered.Count; i++)
        {
            string key = ordered[i];
            int sep = key.IndexOf('\0');
            var file = sep < 0 ? "" : key.Substring(0, sep);
            var uname = sep < 0 ? key : key.Substring(sep + 1);
            if (!string.Equals(file, lastFile, StringComparison.OrdinalIgnoreCase))
            {
                lastFile = file;
                ImGui.SetCursorPosX(node.X + RowPadX);
                ImGui.TextColored(new Vector4(0.45f, 0.9f, 1f, 1f), string.IsNullOrEmpty(file) ? "—" : file);
            }
            if (!drafts.ContainsKey(key))
            {
                string cur = "";
                if (node.StartValues.TryGetValue(file, out var sm) && sm.TryGetValue(uname, out var sv)) cur = sv.Value;
                else if (string.Equals(uname, "xlrc_strength", StringComparison.OrdinalIgnoreCase)) cur = "0.000";
                else if (kf.Uniforms.TryGetValue(file, out var km) && km.TryGetValue(uname, out var kv)) cur = kv.Value;
                drafts[key] = cur;
            }
            ImGui.SetCursorPosX(node.X + RowPadX);
            string tv = drafts[key];
            if (config.TryGetUniformInfo(file, uname, out var uinfo))
            {
                string lb = string.IsNullOrEmpty(uinfo.Label) ? uinfo.Name : uinfo.Label;
                float w = Math.Max(40, node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX() - ImGui.CalcTextSize(lb + ":").X - 12);
                config.DrawUniformControlSized(file, uinfo, ref tv, w);
            }
            else
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(uname);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(file);
                ImGui.SameLine(0, 4);
                ImGui.SetNextItemWidth(Math.Max(20, node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX() - 12));
                ImGui.InputText("##anse_" + node.Id + "_" + i, ref tv, 64);
            }
            drafts[key] = tv;
        }
        ImGui.SetCursorPosX(node.X + RowPadX);
        if (ImGui.Button("Confirm##anse_ok_" + node.Id))
        {
            foreach (var kvp in drafts)
            {
                int sep = kvp.Key.IndexOf('\0');
                if (sep < 0) continue;
                var file = kvp.Key.Substring(0, sep);
                var uname = kvp.Key.Substring(sep + 1);
                string bt = "float";
                if (kf.Uniforms.TryGetValue(file, out var km) && km.TryGetValue(uname, out var kv)) bt = kv.BaseType;
                if (!node.StartValues.TryGetValue(file, out var sm))
                    node.StartValues[file] = sm = new Dictionary<string, DynamicUniformValue>(StringComparer.OrdinalIgnoreCase);
                sm[uname] = new DynamicUniformValue { Value = kvp.Value.Trim(), BaseType = bt };
            }
            foreach (var f in node.StartValues.Keys.ToList())
            {
                var sm = node.StartValues[f];
                foreach (var u in sm.Keys.Where(u => !kf.TickedUniforms.Any(t => string.Equals(t, f + "\0" + u, StringComparison.OrdinalIgnoreCase))).ToList())
                    sm.Remove(u);
                if (sm.Count == 0) node.StartValues.Remove(f);
            }
            config.SaveActiveDyn();
            startEndNode = null;
            startEndDrafts.Remove(node.Id);
        }
    }

    // Midpoint value editor: mirrors the Start/End editor, but writes
    // node.MidValues (blend pinned at factor 0.5). Drafts default to the
    // keyframe peak, so enabling Midpoint starts front-loaded; drag toward
    // the start values to ease the attack in.
    private void DrawMidEditor(DynamicPresetData data, DynamicAnimNode node)
    {
        var kf = BoundKeyframe(data, node);
        if (kf == null)
        {
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.TextDisabled("None — bind a keyframe first.");
            ImGui.SetCursorPosX(node.X + RowPadX);
            if (ImGui.Button("Back##anmide_back_" + node.Id))
            {
                midEndNode = null;
                midEndDrafts.Remove(node.Id);
            }
            return;
        }
        var ticks = kf.TickedUniforms.ToList();
        if (ticks.Count == 0)
        {
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.TextDisabled("No ticked settings.");
        }
        if (!midEndDrafts.TryGetValue(node.Id, out var drafts))
            midEndDrafts[node.Id] = drafts = new Dictionary<string, string>();
        // Grouped by shader file so every setting shows its source.
        // Order is display-only (drafts/confirm are key-based).
        var ordered = ticks.OrderBy(k => { int s = k.IndexOf('\0'); return s < 0 ? "" : k.Substring(0, s); }, StringComparer.OrdinalIgnoreCase)
            .ThenBy(k => { int s = k.IndexOf('\0'); return s < 0 ? k : k.Substring(s + 1); }, StringComparer.OrdinalIgnoreCase).ToList();
        string lastFile = "\0NOMATCH\0";
        for (int i = 0; i < ordered.Count; i++)
        {
            string key = ordered[i];
            int sep = key.IndexOf('\0');
            var file = sep < 0 ? "" : key.Substring(0, sep);
            var uname = sep < 0 ? key : key.Substring(sep + 1);
            if (!string.Equals(file, lastFile, StringComparison.OrdinalIgnoreCase))
            {
                lastFile = file;
                ImGui.SetCursorPosX(node.X + RowPadX);
                ImGui.TextColored(new Vector4(0.45f, 0.9f, 1f, 1f), string.IsNullOrEmpty(file) ? "—" : file);
            }
            if (!drafts.ContainsKey(key))
            {
                string cur = "";
                if (node.MidValues.TryGetValue(file, out var sm) && sm.TryGetValue(uname, out var sv)) cur = sv.Value;
                else if (kf.Uniforms.TryGetValue(file, out var km) && km.TryGetValue(uname, out var kv)) cur = kv.Value;
                drafts[key] = cur;
            }
            ImGui.SetCursorPosX(node.X + RowPadX);
            string tv = drafts[key];
            if (config.TryGetUniformInfo(file, uname, out var uinfo))
            {
                string lb = string.IsNullOrEmpty(uinfo.Label) ? uinfo.Name : uinfo.Label;
                float w = Math.Max(40, node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX() - ImGui.CalcTextSize(lb + ":").X - 12);
                config.DrawUniformControlSized(file, uinfo, ref tv, w);
            }
            else
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(uname);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(file);
                ImGui.SameLine(0, 4);
                ImGui.SetNextItemWidth(Math.Max(20, node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX() - 12));
                ImGui.InputText("##anmid_" + node.Id + "_" + i, ref tv, 64);
            }
            drafts[key] = tv;
        }
        ImGui.SetCursorPosX(node.X + RowPadX);
        if (ImGui.Button("Confirm##anmid_ok_" + node.Id))
        {
            foreach (var kvp in drafts)
            {
                int sep = kvp.Key.IndexOf('\0');
                if (sep < 0) continue;
                var file = kvp.Key.Substring(0, sep);
                var uname = kvp.Key.Substring(sep + 1);
                string bt = "float";
                if (kf.Uniforms.TryGetValue(file, out var km) && km.TryGetValue(uname, out var kv)) bt = kv.BaseType;
                if (!node.MidValues.TryGetValue(file, out var sm))
                    node.MidValues[file] = sm = new Dictionary<string, DynamicUniformValue>(StringComparer.OrdinalIgnoreCase);
                sm[uname] = new DynamicUniformValue { Value = kvp.Value.Trim(), BaseType = bt };
            }
            foreach (var f in node.MidValues.Keys.ToList())
            {
                var sm = node.MidValues[f];
                foreach (var u in sm.Keys.Where(u => !kf.TickedUniforms.Any(t => string.Equals(t, f + "\0" + u, StringComparison.OrdinalIgnoreCase))).ToList())
                    sm.Remove(u);
                if (sm.Count == 0) node.MidValues.Remove(f);
            }
            config.SaveActiveDyn();
            midEndNode = null;
            midEndDrafts.Remove(node.Id);
        }
    }

    private void TriggerParam(DynamicAnimNode node, string label, float current, Action<float> assign)
    {
        ImGui.SetCursorPosX(node.X + RowPadX);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label + ":");
        ImGui.SameLine(0, 4);
        ImGui.SetNextItemWidth(Math.Max(20, node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX()));
        float v = current;
        if (ImGui.InputFloat("##antp_" + label + node.Id, ref v, 0.1f, 1f, "%.1f"))
        {
            assign(Math.Max(0, v));
            config.SaveActiveDyn();
        }
    }

    private void DrawNode(DynamicPresetData data, DynamicAnimNode node, Dictionary<string, PinSet> pinRects)
    {
        var headKf = BoundKeyframe(data, node);
        bool trigger = IsTrigger(node);
        bool coords = IsCoords(node);
        bool wall = IsWall(node);
        bool weather = IsWeather(node);
        bool timegate = IsTimeGate(node);
        bool timer = IsTimer(node);
        bool dlss = IsDlss(node);
        bool preset = IsPreset(node);
        bool res = IsRes(node);
        bool timelock = IsTimeLock(node);
        bool spot = IsSpot(node);
        // Weather/timegate/timer/dlss/res/timelock/spot nodes never bind
        // keyframes: no red border. Preset nodes show red until a preset
        // is picked.
        bool missing = headKf == null && !weather && !timegate && !timer && !dlss && !res && !timelock && !spot && !(preset && !string.IsNullOrEmpty(node.PresetPath));
        if (preset && string.IsNullOrEmpty(node.PresetPath)) missing = true;
        bool selHead = selectedNode == node.Id;
        float nodeH = NodeHeight(data, node);
        float nodeW = NodeWidth(node);
        bool startEndOpen = (trigger || coords || wall || IsTime(node)) && startEndNode == node.Id;
        Vector4 titleCol = spot
            ? (selHead ? new Vector4(0.95f, 0.5f, 0.42f, 1f) : new Vector4(0.62f, 0.3f, 0.26f, 1f))
            : timelock
            ? (selHead ? new Vector4(0.85f, 0.35f, 0.75f, 1f) : new Vector4(0.55f, 0.2f, 0.5f, 1f))
            : res
            ? (selHead ? new Vector4(1.0f, 0.85f, 0.25f, 1f) : new Vector4(0.65f, 0.55f, 0.12f, 1f))
            : preset
            ? (selHead ? new Vector4(0.20f, 0.80f, 0.90f, 1f) : new Vector4(0.12f, 0.55f, 0.65f, 1f))
            : dlss
            ? (selHead ? new Vector4(0.52f, 0.78f, 0.08f, 1f) : new Vector4(0.30f, 0.50f, 0.05f, 1f))
            : timer
            ? (selHead ? new Vector4(0.25f, 0.75f, 0.35f, 1f) : new Vector4(0.16f, 0.5f, 0.22f, 1f))
            : timegate
            ? (selHead ? new Vector4(0.55f, 0.55f, 0.6f, 1f) : new Vector4(0.38f, 0.38f, 0.43f, 1f))
            : weather
            ? (selHead ? new Vector4(0.75f, 0.02f, 0.13f, 1f) : new Vector4(0.533f, 0f, 0.082f, 1f))
            : wall
            ? (selHead ? new Vector4(0.6f, 0.35f, 0.85f, 1f) : new Vector4(0.42f, 0.25f, 0.6f, 1f))
            : coords
            ? (selHead ? new Vector4(0.15f, 0.65f, 0.6f, 1f) : new Vector4(0.1f, 0.45f, 0.42f, 1f))
            : trigger
            ? (selHead ? new Vector4(0.75f, 0.48f, 0.12f, 1f) : new Vector4(0.55f, 0.35f, 0.1f, 1f))
            : (selHead ? new Vector4(0.16f, 0.45f, 0.8f, 1f) : new Vector4(0.12f, 0.35f, 0.65f, 1f));
        Vector4 borderCol = missing
            ? new Vector4(1f, 0.3f, 0.3f, 1f)
            : titleCol;
        // Single-window layout: widgets go straight on the canvas so overlapping
        // nodes can't bury each other's hitboxes in transparent child windows.
        // Chrome is painted first (beneath), geometry is pure node math.
        var pinSize = new Vector2(22, 22);
        ImGui.SetCursorPos(new Vector2(node.X, node.Y));
        Vector2 nodeScreen = ImGui.GetCursorScreenPos();
        const float titleTopPad = 6f;
        float titleH = titleTopPad + HeaderH;
        float titleBottom = nodeScreen.Y + titleH;
        var canvasDl = ImGui.GetWindowDrawList();
        var nodeEnd = nodeScreen + new Vector2(nodeW, nodeH);
        // Opaque node bodies even in no-background mode (ChildBg itself
        // is transparent there, so force alpha back to 1).
        uint nodeBg;
        try
        {
            if (plugin.Config.AnimatorNoBackground)
            {
                var c = ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.ChildBg));
                nodeBg = ImGui.ColorConvertFloat4ToU32(new Vector4(c.X, c.Y, c.Z, 1f));
            }
            else nodeBg = ImGui.GetColorU32(ImGuiCol.ChildBg);
        }
        catch { nodeBg = ImGui.GetColorU32(ImGuiCol.ChildBg); }
        canvasDl.AddRectFilled(nodeScreen, nodeEnd, nodeBg, 8f);
        canvasDl.AddRectFilled(nodeScreen, new Vector2(nodeEnd.X, nodeScreen.Y + titleH), ImGui.ColorConvertFloat4ToU32(titleCol), 8f, ImDrawFlags.RoundCornersTop);
        canvasDl.AddRect(nodeScreen, nodeEnd, ImGui.ColorConvertFloat4ToU32(borderCol), 8f, ImDrawFlags.None, 1f);

        // Title label sits on the bar; the Selectable is invisible (no
        // hover/active tint) and doubles as select + drag. Pins submitted
        // later win the edge pixels.
        bool hovHeader = false;
        {
            // Header starts right of the collapse arrow (30px).
            ImGui.SetCursorPos(new Vector2(node.X + 30, node.Y + titleTopPad));
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0f, 0f, 0f, 0f));
            ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0f, 0f, 0f, 0f));
            if (ImGui.Selectable((res ? "Resolution##anhdr_" : preset ? "Preset Trigger##anhdr_" : dlss ? "DLSS 5 Trigger##anhdr_" : timer ? "Timer Switch##anhdr_" : timegate ? "Time Gate##anhdr_" : weather ? "Weather##anhdr_" : wall ? "Door##anhdr_" : coords ? "Coordinates##anhdr_" : trigger ? "Trigger##anhdr_" : timelock ? "Time Lock##anhdr_" : spot ? "Location Switch##anhdr_" : "In-Game Time##anhdr_") + node.Id, false, ImGuiSelectableFlags.None, new Vector2(nodeW - 30, HeaderH)))
                selectedNode = node.Id;
            hovHeader = ImGui.IsItemHovered();
            ImGui.PopStyleColor(2);
            if (hovHeader && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                dragNode = node.Id;
                dragDownPos = ImGui.GetMousePos();
                dragMoved = false;
            }
            // Explicit open (not BeginPopupContextItem): later widgets like
            // the collapse arrow would otherwise steal "last item".
            if (hovHeader && linkCancelCooldown <= 0 && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                ImGui.OpenPopup("##anctx_" + node.Id);
        }
        // Collapse arrow at the bar's left edge: invisible hitbox with a
        // draw-list triangle (no button chrome). Brightens on hover.
        // Submitted after the header so it wins clicks there.
        Vector2 arrMin = nodeScreen + new Vector2(9, titleTopPad + (HeaderH - 14) * 0.5f);
        ImGui.SetCursorScreenPos(arrMin);
        ImGui.InvisibleButton("##ancol_" + node.Id, new Vector2(14, 14));
        bool arrHov = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            node.Collapsed = !node.Collapsed;
            config.SaveActiveDyn();
        }
        uint arrCol = arrHov ? 0xFFFFFFFF : 0xFFBBBBBB;
        if (node.Collapsed)
            canvasDl.AddTriangleFilled(arrMin + new Vector2(4, 1), arrMin + new Vector2(4, 13), arrMin + new Vector2(11, 7), arrCol);
        else
            canvasDl.AddTriangleFilled(arrMin + new Vector2(1, 4), arrMin + new Vector2(13, 4), arrMin + new Vector2(7, 11), arrCol);
        // Live status at the bar's right edge: ON while the node is driving
        // something (trigger envelope running, location blend active, door
        // faded in, DLSS gate demanding NR), OFF otherwise. Draw-list stamp,
        // no layout impact.
        if (trigger || coords || wall || timegate || timer || dlss || preset || res || timelock || spot)
        {
            bool live = false;
            try
            {
                if (wall) live = plugin.IsWallRunning(node.Id);
                else if (timer) live = plugin.IsTimerActive(node.Id);
                else if (spot) live = plugin.SpotLive(node);
                else if (dlss) live = data.Edges.Any(e => e.To == node.Id) && plugin.GatesSatisfied(data, node);
                else if (preset) live = data.Edges.Any(e => e.To == node.Id) && plugin.GatesSatisfied(data, node);
                else if (timelock) live = data.Edges.Any(e => e.To == node.Id) && plugin.GatesSatisfied(data, node);
                else if (res) live = Plugin.ResolutionLive(node);
                else if (timegate) live = Plugin.TimeGateLive(node, config.GetEorzeaSec());
                else if (node.TriggerKind == 18) live = plugin.LocationBlend(node).F > 0.001f && plugin.GatesSatisfied(data, node);
                else if (trigger) live = plugin.IsTriggerRunning(node.Id) || plugin.TriggerSensorLive(data, node);
            }
            catch { }
            string st = live ? "ON" : "OFF";
            uint stCol = live ? 0xFF6FFF6F : 0xFF9A9A9A;
            var stSize = ImGui.CalcTextSize(st);
            canvasDl.AddText(
                new Vector2(nodeEnd.X - stSize.X - 10, nodeScreen.Y + titleTopPad + (HeaderH - stSize.Y) * 0.5f),
                stCol, st);
        }
        else if (weather)
        {
            // Weather nodes: current weather name at the same spot.
            string wName = "—";
            try
            {
                byte w = plugin.GetCurrentWeather();
                wName = w == 255 ? "—" : WeatherName(w);
            }
            catch { }
            var wSize = ImGui.CalcTextSize(wName);
            canvasDl.AddText(
                new Vector2(nodeEnd.X - wSize.X - 10, nodeScreen.Y + titleTopPad + (HeaderH - wSize.Y) * 0.5f),
                0xFFE8E8E8, wName);
        }
        else
        {
            // In-game time nodes: current Eorzea time while ON, OFF stamp
            // while gated off (OFF propagates down the chain in-engine).
            bool timeOn = true;
            try { timeOn = plugin.TimeNodeOn(data, node.Id); }
            catch { }
            string now = timeOn ? EorzeaFormat.SecondsToTimeString(config.GetEorzeaSec()) : "OFF";
            uint nowCol = timeOn ? 0xFFE8E8E8 : (uint)0xFF9A9A9A;
            var nowSize = ImGui.CalcTextSize(now);
            canvasDl.AddText(
                new Vector2(nodeEnd.X - nowSize.X - 10, nodeScreen.Y + titleTopPad + (HeaderH - nowSize.Y) * 0.5f),
                nowCol, now);
        }
        if (ImGui.BeginPopup("##anctx_" + node.Id))
        {
            // Snapshot / New-keyframe items hidden for now (revisit later).
#if false
            if (ImGui.MenuItem("Snapshot state here"))
            {
                var bound = BoundKeyframe(data, node);
                if (bound != null)
                {
                    bound.AbsorbLive(config.CaptureLiveForNode());
                    config.SaveActiveDyn();
                }
                else
                {
                    var cfg2 = data.GetConfig(node.Config);
                    if (cfg2 != null)
                    {
                        var snap = config.CaptureLiveForNode();
                        snap.Name = "Keyframe " + EorzeaFormat.SecondsToTimeString(config.GetEorzeaSec());
                        snap.IsPrimary = !cfg2.Keyframes.Any(k => k.IsPrimary);
                        snap.Sparse = !snap.IsPrimary;
                        cfg2.Keyframes.Add(snap);
                        cfg2.Keyframes.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
                        node.KeyframeId = snap.Id;
                        config.SaveActiveDyn();
                    }
                }
            }
            if (ImGui.MenuItem("New keyframe here"))
            {
                var cfgN = data.GetConfig(node.Config) ?? data.GetOrCreatePrimary();
                DynamicKeyframe snapN;
                if (!cfgN.Keyframes.Any(k => k.IsPrimary))
                {
                    snapN = config.CaptureLiveForNode();
                    snapN.IsPrimary = true;
                }
                else
                {
                    snapN = new DynamicKeyframe { Sparse = true };
                }
                snapN.TimeSeconds = config.GetEorzeaSec();
                snapN.Name = "Keyframe " + EorzeaFormat.SecondsToTimeString(snapN.TimeSeconds);
                cfgN.Keyframes.Add(snapN);
                cfgN.Keyframes.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
                node.KeyframeId = snapN.Id;
                config.SaveActiveDyn();
            }
#endif
            if (ImGui.MenuItem("Set nickname..."))
            {
                nicknameNode = node.Id;
                nicknameDraft = node.Nickname ?? "";
            }
            if (ImGui.MenuItem("Disconnect"))
            {
                data.Edges.RemoveAll(e => e.From == node.Id || e.To == node.Id);
                config.SaveActiveDyn();
            }
            if (ImGui.MenuItem("Duplicate node"))
            {
                // Deep copy via JSON (same round-trip as the sidecar), then
                // fresh identity + slight offset. Edges are connections,
                // not node state, so they are NOT copied.
                try
                {
                    var copy = JsonSerializer.Deserialize<DynamicAnimNode>(JsonSerializer.Serialize(node));
                    if (copy != null)
                    {
                        copy.Id = Guid.NewGuid().ToString("N");
                        copy.NodeNum = data.Nodes.Count == 0 ? 1 : data.Nodes.Max(n => n.NodeNum) + 1;
                        copy.X += 24;
                        copy.Y += 24;
                        data.Nodes.Add(copy);
                        selectedNode = copy.Id;
                        config.SaveActiveDyn();
                    }
                }
                catch { }
            }
            if (ImGui.MenuItem("Delete node"))
            {
                data.Nodes.Remove(node);
                data.Edges.RemoveAll(e => e.From == node.Id || e.To == node.Id);
                if (selectedNode == node.Id) selectedNode = null;
                if (pendingLinkFrom == node.Id) { pendingLinkFrom = null; pendingLinkPin = ""; }
                if (captureNode == node.Id) captureNode = null;
                if (startEndNode == node.Id) startEndNode = null;
                startEndDrafts.Remove(node.Id);
                if (midEndNode == node.Id) midEndNode = null;
                midEndDrafts.Remove(node.Id);
                if (dragNode == node.Id) { dragNode = null; dragMoved = false; }
                config.SaveActiveDyn();
                ImGui.EndPopup();
                return;
            }
            ImGui.EndPopup();
        }
        // Collapsed nodes show the titlebar only; engine + pins unaffected.
        if (!node.Collapsed)
        {
        // Body rows are indented explicitly with a gap under the title.
        ImGui.SetCursorPos(new Vector2(node.X + RowPadX, ImGui.GetCursorPosY() + 4));
        // Nickname edit mode: the whole body is replaced by the nickname
        // editor until OK/Enter (sets) or Cancel/X (drops).
        bool nickEdit = nicknameNode == node.Id;
        if (nickEdit)
        {
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Nickname:");
            ImGui.SameLine(0, 4);
            ImGui.SetNextItemWidth(Math.Max(40, node.X + nodeW - RowPadX - ImGui.GetCursorPosX()));
            bool commitNick = ImGui.InputText("##annick_in_" + node.Id, ref nicknameDraft, 48, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SetCursorPosX(node.X + RowPadX);
            if (ImGui.Button("OK##annick_ok_" + node.Id) || commitNick)
            {
                node.Nickname = nicknameDraft.Trim();
                config.SaveActiveDyn();
                nicknameNode = null;
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel##annick_x_" + node.Id))
                nicknameNode = null;
        }
        if (!nickEdit)
        {

        // Keyframe picker: which keyframe this node plays (single set).
        // Hidden while a trigger node edits start/end values. Weather,
        // timegate, timer, dlss, preset, res, timelock and spot nodes never
        // bind keyframes.
        var boundCfg = data.GetConfig("Primary") ?? data.Configs.FirstOrDefault();
        var boundKf = BoundKeyframe(data, node);
        if (!startEndOpen && !weather && !timegate && !timer && !dlss && !preset && !res && !timelock && !spot)
        {
            string kfPreview = boundKf != null
                ? (string.IsNullOrEmpty(boundKf.Name) ? EorzeaFormat.SecondsToTimeString(boundKf.TimeSeconds) : boundKf.Name)
                : "(none)";
            float keyLabelW = ImGui.CalcTextSize("Key:").X;
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Key:");
            ImGui.SameLine(0, 4);
            ImGui.SetNextItemWidth(nodeW - RowPadX - keyLabelW - 14);
            if (ImGui.BeginCombo("##ankf_" + node.Id, kfPreview))
            {
                if (ImGui.Selectable("(none)", boundKf == null))
                {
                    node.KeyframeId = "";
                    config.SaveActiveDyn();
                }
                if (boundCfg != null)
                {
                    foreach (var kf in boundCfg.Keyframes.OrderBy(k => k.TimeSeconds).ToList())
                    {
                        string label = string.IsNullOrEmpty(kf.Name)
                            ? EorzeaFormat.SecondsToTimeString(kf.TimeSeconds)
                            : kf.Name;
                        bool isSel = boundKf != null && kf.Id == boundKf.Id;
                        if (ImGui.Selectable(label, isSel))
                        {
                            node.KeyframeId = kf.Id;
                            config.SaveActiveDyn();
                        }
                        if (isSel) ImGui.SetItemDefaultFocus();
                    }
                }
                ImGui.EndCombo();
            }
            ImGui.Dummy(new Vector2(0, 4));
        }

        // Gate logic picker: only while something is wired into the in pin
        // (DLSS nodes always show it so the node is never empty).
        if (((trigger || coords || wall || timer || timelock) && data.Edges.Any(e => e.To == node.Id)) || dlss || preset)
        {
            string[] pinModes = { "All On", "All Off", "One On", "One Off" };
            int mode = Math.Clamp(node.InPinMode, 0, 3);
            float pinLabelW = ImGui.CalcTextSize("In Pin Mode:").X;
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("In Pin Mode:");
            ImGui.SameLine(0, 4);
            ImGui.SetNextItemWidth(nodeW - RowPadX - pinLabelW - 14);
            if (ImGui.BeginCombo("##anpin_" + node.Id, pinModes[mode]))
            {
                for (int m = 0; m < pinModes.Length; m++)
                {
                    if (ImGui.Selectable(pinModes[m], m == mode))
                    {
                        node.InPinMode = m;
                        config.SaveActiveDyn();
                    }
                    if (m == mode) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
        }

        // Resolution switch: height preset + live readout (+ custom row).
        // Pure gate source: no envelope, no keyframe.
        if (res)
        {
            string[] resModes = { "720p", "1080p", "1440p", "4K", "Custom" };
            int rm = Math.Clamp(node.ResMode, 0, 4);
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Height:");
            ImGui.SameLine(0, 4);
            ImGui.SetNextItemWidth(Math.Max(40, node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX()));
            if (ImGui.BeginCombo("##anres_" + node.Id, resModes[rm]))
            {
                for (int m = 0; m < resModes.Length; m++)
                {
                    if (ImGui.Selectable(resModes[m], m == rm))
                    {
                        node.ResMode = m;
                        config.SaveActiveDyn();
                    }
                    if (m == rm) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            string[] resCompares = { "Equal", "Higher", "Lower" };
            int rc = Math.Clamp(node.ResCompare, 0, 2);
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Mode:");
            ImGui.SameLine(0, 4);
            ImGui.SetNextItemWidth(Math.Max(40, node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX()));
            if (ImGui.BeginCombo("##anresm_" + node.Id, resCompares[rc]))
            {
                for (int m = 0; m < resCompares.Length; m++)
                {
                    if (ImGui.Selectable(resCompares[m], m == rc))
                    {
                        node.ResCompare = m;
                        config.SaveActiveDyn();
                    }
                    if (m == rc) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Equal = exact height. Higher/Lower include it (>= / <=).");
            if (rm == 4)
            {
                ImGui.SetCursorPosX(node.X + RowPadX);
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted("Custom px:");
                ImGui.SameLine(0, 4);
                ImGui.SetNextItemWidth(Math.Max(40, node.X + NodeWidth(node) - RowPadX - ImGui.GetCursorPosX()));
                int ch = Math.Max(1, node.ResCustomH);
                if (ImGui.InputInt("##anresc_" + node.Id, ref ch))
                {
                    node.ResCustomH = Math.Max(1, ch);
                    config.SaveActiveDyn();
                }
            }
            ImGui.SetCursorPosX(node.X + RowPadX);
            try
            {
                if (Plugin.TryGetGameClientSize(out int cww, out int cwh))
                    ImGui.TextDisabled($"Now: {cww}x{cwh}");
                else
                    ImGui.TextDisabled("Now: —");
            }
            catch { ImGui.TextDisabled("Now: —"); }
        }

        // Preset trigger: which preset a rising gate edge switches to.
        if (preset)
        {
            float presetLabelW = ImGui.CalcTextSize("Preset:").X;
            ImGui.SetCursorPosX(node.X + RowPadX);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Preset:");
            ImGui.SameLine(0, 4);
            string pp = node.PresetPath ?? "";
            config.DrawZonePresetCombo("##anpreset_" + node.Id, ref pp, nodeW - RowPadX - presetLabelW - 14);
            if (!string.Equals(pp, node.PresetPath ?? "", StringComparison.Ordinal))
            {
                node.PresetPath = pp;
                config.SaveActiveDyn();
            }
        }

        // Time Lock: locked Eorzea time (HH:MM:SS, Enter to commit) +
        // fade in/out seconds. While a wired gate holds, time nodes
        // evaluate here instead of live time (output crossfade).
        if (timelock)
        {
            DrawGateTimeRow(node, "Time:", Math.Clamp(node.TimeLockTimeSec, 0, 86399), v => node.TimeLockTimeSec = v, "_lock");
            TriggerParam(node, "Fade In", Math.Max(0f, node.TimeLockFadeIn), v => node.TimeLockFadeIn = v);
            TriggerParam(node, "Fade Out", Math.Max(0f, node.TimeLockFadeOut), v => node.TimeLockFadeOut = v);
        }

        if (trigger || coords)
            DrawTriggerRows(data, node, pinRects, nodeScreen, nodeW);
        if (wall)
            DrawWallRows(data, node);
        if (weather)
            DrawWeatherRows(data, node, pinRects, nodeScreen, nodeW);
        if (timegate)
            DrawTimeGateRows(data, node);
        if (timer)
            DrawTimerRows(data, node, pinRects, nodeScreen, nodeW);
        if (spot)
            DrawSpotRows(data, node);

        // Time editor: [HR][MIN][SEC] parts, clamped 23/59/59 on commit.
        if (!trigger && !coords && !wall && !timegate && !timer && !dlss && !preset && !res && boundKf != null && !startEndOpen)
        {
            if (!timeDrafts.TryGetValue(node.Id, out var parts))
                timeDrafts[node.Id] = parts = SplitTime(boundKf.TimeSeconds);
            ImGui.SetCursorPosX(node.X + RowPadX);
            float timeLabelW = ImGui.CalcTextSize("Time:").X;
            float colonW = ImGui.CalcTextSize(":").X;
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Time:");
            ImGui.SameLine(0, 3);
            float digitW = ImGui.CalcTextSize("00").X + ImGui.GetStyle().FramePadding.X * 2 + 6;
            float nowW = Math.Max(30, nodeW - RowPadX - timeLabelW - 3 * digitW - 2 * colonW - 22);
            string[] partIds = { "##anth_" + node.Id, "##antmi_" + node.Id, "##ants_" + node.Id };
            int[] partMax = { 23, 59, 59 };
            bool commit = false;
            bool anyActive = false;
            for (int p = 0; p < 3; p++)
            {
                if (p > 0)
                {
                    ImGui.SameLine(0, 3);
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted(":");
                    ImGui.SameLine(0, 3);
                }
                commit |= TimePart(partIds[p], parts, p, partMax[p], digitW, ref anyActive);
            }
            if (commit)
            {
                boundKf.TimeSeconds = ParseTimeParts(parts, boundKf.TimeSeconds);
                config.SaveActiveDyn();
                timeDrafts[node.Id] = parts = SplitTime(boundKf.TimeSeconds);
            }
            else if (!anyActive)
                timeDrafts[node.Id] = parts = SplitTime(boundKf.TimeSeconds);
            ImGui.SameLine(0, 3);
            if (ImGui.Button("Now", new Vector2(nowW, 0)))
            {
                boundKf.TimeSeconds = config.GetEorzeaSec();
                timeDrafts[node.Id] = SplitTime(boundKf.TimeSeconds);
                config.SaveActiveDyn();
            }
        }

        // Time nodes: fade from frozen values to timeline values on
        // gated-off -> on (seconds, 0 = snap straight to them).
        // Chain starts only; chained nodes use the head's setting.
        if (!trigger && !coords && !wall && !weather && !timegate && !timer && !dlss && !preset && !res && !timelock && !spot && boundKf != null && !HasTimeInput(data, node.Id) && !startEndOpen)
            TriggerParam(node, "Fade", node.TimeFadeSec, v => node.TimeFadeSec = v);
        // Start/end values for time nodes: Start = fade-from override
        // (forced, or when no live previous exists); End = the keyframe.
        if (!trigger && !coords && !wall && !weather && !timegate && !timer && !dlss && !preset && !res && !timelock && !spot && !startEndOpen)
        {
            ImGui.SetCursorPosX(node.X + RowPadX);
            if (ImGui.Button("Start/End Values##anse_t_" + node.Id))
            {
                startEndNode = node.Id;
                startEndDrafts.Remove(node.Id);
            }
        }
        if (startEndOpen && IsTime(node))
            DrawStartEndEditor(data, node);

        // Curve lives on the chain start only: chained time nodes hide it.
        // Selects the segment transfer function: Smoothstep (current
        // behavior), Linear, or 24H Brightness Curve (segment
        // progress paced by the sidecar daylight curve; needs recorded
        // data, else Smoothstep).
        if (!trigger && !coords && !wall && !weather && !timegate && !timer && !dlss && !preset && !res && !timelock && !spot && !HasTimeInput(data, node.Id) && !startEndOpen)
        {
            ImGui.Dummy(new Vector2(0, 4));

            ImGui.SetCursorPosX(node.X + RowPadX);
            float curveLabelW = ImGui.CalcTextSize("Curve:").X;
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Curve:");
            ImGui.SameLine(0, 4);
            ImGui.SetNextItemWidth(nodeW - RowPadX - curveLabelW - 14);
            string[] curveModes = { "Smoothstep", "Linear", "24H Brightness Curve" };
            int cm = Math.Clamp(node.CurveMode, 0, 2);
            if (ImGui.BeginCombo("##ancv_" + node.Id, curveModes[cm]))
            {
                for (int m = 0; m < curveModes.Length; m++)
                {
                    if (ImGui.Selectable(curveModes[m], m == cm))
                    {
                        bool freshlyDaylight = (m == 2 && cm != 2);
                        node.CurveMode = m;
                        config.SaveActiveDyn();
                        // Switching to 24H Brightness Curve auto-snaps the chain
                        // to the measured day/night (status-only no-op when
                        // data or a 2+ chain is missing).
                        if (freshlyDaylight)
                        {
                            try { config.SnapChainToDaylight(); } catch { }
                        }
                    }
                    if (m == cm) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            if (ImGui.IsItemHovered())
            {
                bool hasDl = false;
                try
                {
                    var ddl0 = config.ActiveDaylight();
                    hasDl = ddl0 != null && ddl0.Values.Count > 0;
                }
                catch { }
                ImGui.SetTooltip(cm == 2 && !hasDl
                    ? "No daylight data recorded yet — falls back to Smoothstep. Record in the Weather tab."
                    : "Paces keyframe blending across the segment: even (line), eased (smoothstep), or following recorded daylight (still at night, rushing at dawn/dusk).");
            }
        }

        // Day/Night role: chain-start AND chained time nodes while the
        // head runs 24H Brightness Curve. Night = measured darkest second
        // (low point), Day = brightest (high point). Picking a role jumps
        // this node's keyframe to its extreme when data exists.
        if (DayNightRowVisible(data, node) && boundKf != null && !startEndOpen)
        {
            ImGui.Dummy(new Vector2(0, 4));
            ImGui.SetCursorPosX(node.X + RowPadX);
            float dnLabelW = ImGui.CalcTextSize("Day/Night:").X;
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Day/Night:");
            ImGui.SameLine(0, 4);
            ImGui.SetNextItemWidth(nodeW - RowPadX - dnLabelW - 14);
            string[] dnModes = { "Night (low point)", "Day (high point)" };
            int dn = (node.DayNightRole == 1) ? 1 : 0;
            if (ImGui.BeginCombo("##andn_" + node.Id, dnModes[dn]))
            {
                for (int r = 0; r < 2; r++)
                {
                    if (ImGui.Selectable(dnModes[r], r == dn))
                    {
                        node.DayNightRole = r;
                        try
                        {
                            var ddl = config.ActiveDaylight();
                            if (ddl != null && ddl.Values != null && ddl.Values.Count > 0)
                            {
                                var (dnMin, dnMax) = DynamicTimeline.DaylightExtrema(ddl.Values);
                                boundKf.TimeSeconds = (r == 1) ? dnMax : dnMin;
                                timeDrafts[node.Id] = SplitTime(boundKf.TimeSeconds);
                            }
                        }
                        catch { }
                        config.SaveActiveDyn();
                    }
                    if (r == dn) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Night = measured darkest second (low point), Day = brightest (high point). Picking a role moves this node's keyframe there.");
        }

        } // !nickEdit
        // Creation-order footer, centered.
        if (node.NodeNum <= 0)
        {
            node.NodeNum = data.Nodes.Count == 0 ? 1 : data.Nodes.Max(n => n.NodeNum) + 1;
            config.SaveActiveDyn();
        }
        string numLabel = $"• {NodeLabel(node)} •";
        ImGui.SetCursorPos(new Vector2(
            node.X + (nodeW - ImGui.CalcTextSize(numLabel).X) * 0.5f,
            node.Y + nodeH - 8 - ImGui.GetTextLineHeight()));
        ImGui.TextDisabled(numLabel);
        } // !Collapsed

        // Single pin buttons: one shared window, no clipping boundary, so the
        // full 22px works. Submitted late for priority over the header.
        // All node types have pins now: time->time edges are chain links,
        // anything targeting trigger/coords/door is a gate (target only
        // fires while all its sources are ON).
        float pinCy = titleBottom - HeaderH * 0.5f;
        Vector2 inRect = new Vector2(nodeScreen.X - 11, pinCy - 11);
        Vector2 outRect = new Vector2(nodeScreen.X + nodeW - 11, pinCy - 11);
        bool inClicked = false, inHovered = false, outClicked = false, outHovered = false;
            // Preserve group/phase out-pins registered earlier in the body.
            if (!pinRects.TryGetValue(node.Id, out var pset))
                pinRects[node.Id] = pset = new PinSet();
            pset.In = inRect;
            pset.Out = outRect;
        // Weather nodes expose only their group out-pins: no titlebar pins.
        // Time gates, resolution switches and location switches expose only
        // a titlebar out-pin (pure sources, not gateable themselves). Timer,
        // DLSS, preset and timelock nodes expose an in-pin only (timer
        // out-pins live on the rows; DLSS/preset/timelock have no out-pin
        // at all).
        if (!weather && !timegate && !res && !spot)
        {
        ImGui.SetCursorScreenPos(inRect);
        inClicked = ImGui.InvisibleButton("##pin_in_" + node.Id, pinSize);
        inHovered = ImGui.IsItemHovered();
        }
        if (!weather && !timer && !dlss && !preset && !timelock)
        {
        ImGui.SetCursorScreenPos(outRect);
        outClicked = ImGui.InvisibleButton("##pin_out_" + node.Id, pinSize);
        outHovered = ImGui.IsItemHovered();
        }
        bool outArmed = pendingLinkFrom == node.Id && pendingLinkPin == "";
        if (inClicked && pendingLinkFrom != null && pendingLinkFrom != node.Id)
        {
            if (!data.Edges.Any(e => e.From == pendingLinkFrom && e.To == node.Id && (e.FromPin ?? "") == pendingLinkPin) && !CreatesCycle(data, pendingLinkFrom, node.Id) && TimeLinkAllowed(data, pendingLinkFrom, pendingLinkPin, node.Id))
            {
                data.Edges.Add(new DynamicAnimEdge { From = pendingLinkFrom, To = node.Id, FromPin = pendingLinkPin });
                config.SaveActiveDyn();
            }
            pendingLinkFrom = null;
            pendingLinkPin = "";
        }
        else if (inClicked)
        {
            // X-click: drop incoming edges; if there were none, drop
            // outgoing instead, so the visible line always goes away.
            if (data.Edges.RemoveAll(e => e.To == node.Id) == 0)
                data.Edges.RemoveAll(e => e.From == node.Id);
            config.SaveActiveDyn();
            pendingLinkFrom = null;
            pendingLinkPin = "";
        }
        if (outClicked)
        {
            if (pendingLinkFrom == node.Id && pendingLinkPin == "") pendingLinkFrom = null;
            else { pendingLinkFrom = node.Id; pendingLinkPin = ""; }
        }

        // Dots paint after widgets (same list = on top). Gate edges
        // (targeting trigger/coords/door/timer/dlss/preset/timelock) draw
        // orange in DrawEdges.
        // Weather nodes paint no titlebar dots (group pins only);
        // time gates, resolution switches and location switches paint only
        // the out-dot; timer and timelock nodes paint only the in-dot
        // (per-timer out-pins live on the rows).
        if (!weather && !timegate && !res && !spot)
            canvasDl.AddCircleFilled(inRect + new Vector2(11, 11), 5, pendingLinkFrom != null && !outArmed ? 0xFFAAAAAA : 0xFF888888);
        if (!weather && !timer && !timelock)
        {
            canvasDl.AddCircleFilled(outRect + new Vector2(11, 11), 5, outArmed ? 0xFF80FF80 : 0xFF60C060);
            if (outArmed || outHovered)
                canvasDl.AddCircle(outRect + new Vector2(11, 11), 8, 0xFFFFFFFF, 0, 1.5f);
        }
        // In-pin hover feedback lives with the in-pin (not the out-dot):
        // nodes with an in-pin but no titlebar out-pin (timer) need it too.
        // Resolution switches and location switches have no in-pin at all.
        if (!weather && !timegate && !res && !spot)
        {
            bool linkReady = pendingLinkFrom != null && pendingLinkFrom != node.Id
                && !data.Edges.Any(e => e.From == pendingLinkFrom && e.To == node.Id)
                && !CreatesCycle(data, pendingLinkFrom, node.Id)
                && TimeLinkAllowed(data, pendingLinkFrom, pendingLinkPin, node.Id);
            if (inHovered && linkReady)
                canvasDl.AddCircle(inRect + new Vector2(11, 11), 8, 0xFFFFFFFF, 0, 1.5f);
            bool hasEdges = data.Edges.Any(e => e.From == node.Id || e.To == node.Id);
            if (inHovered && pendingLinkFrom == null && hasEdges)
            {
                Vector2 xc = inRect + new Vector2(11, 11);
                canvasDl.AddLine(xc + new Vector2(-7.5f, -7.5f), xc + new Vector2(7.5f, 7.5f), 0xFF0000FF, 3f);
                canvasDl.AddLine(xc + new Vector2(7.5f, -7.5f), xc + new Vector2(-7.5f, 7.5f), 0xFF0000FF, 3f);
            }
        }

        // Drag after content so widgets keep priority. Runs every frame so a
        // fast drag off-view still tracks and releases.
        RunNodeDrag(node);
    }

    // Shared drag logic: delta-based, thresholded so plain clicks don't nudge
    // nodes. Must run every frame regardless of clipping, otherwise a fast
    // drag off-view freezes and the release is missed (stuck drag).
    private void RunNodeDrag(DynamicAnimNode node)
    {
        if (dragNode != node.Id) return;
        if (ImGui.IsKeyPressed(ImGuiKey.Escape) || ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            if (dragMoved)
            {
                node.X = dragStartPos.X;
                node.Y = dragStartPos.Y;
                config.SaveActiveDyn();
            }
            dragNode = null;
            dragMoved = false;
        }
        else if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            var mp = ImGui.GetMousePos();
            if (!dragMoved && (mp - dragDownPos).LengthSquared() > 16)
            {
                dragMoved = true;
                dragStartPos = new Vector2(node.X, node.Y);
            }
            if (dragMoved)
            {
                var io = ImGui.GetIO();
                var cPos = ImGui.GetWindowPos();
                var cSize = ImGui.GetWindowSize();
                const float zone = 28f;
                float dt = Math.Max(io.DeltaTime, 0.0001f);
                float baseSp = 300f * dt, capSp = 4200f * dt, ramp = 6f * dt;
                float wantX = 0, wantY = 0;
                if (mp.X < cPos.X + zone) wantX = -(baseSp + Math.Min(capSp - baseSp, (cPos.X + zone - mp.X) * ramp));
                else if (mp.X > cPos.X + cSize.X - zone) wantX = baseSp + Math.Min(capSp - baseSp, (mp.X - (cPos.X + cSize.X - zone)) * ramp);
                if (mp.Y < cPos.Y + zone) wantY = -(baseSp + Math.Min(capSp - baseSp, (cPos.Y + zone - mp.Y) * ramp));
                else if (mp.Y > cPos.Y + cSize.Y - zone) wantY = baseSp + Math.Min(capSp - baseSp, (mp.Y - (cPos.Y + cSize.Y - zone)) * ramp);
                float ax = 0, ay = 0;
                if (wantX != 0) { float b = ImGui.GetScrollX(); ImGui.SetScrollX(b + wantX); ax = ImGui.GetScrollX() - b; }
                if (wantY != 0) { float b = ImGui.GetScrollY(); ImGui.SetScrollY(b + wantY); ay = ImGui.GetScrollY() - b; }
                node.X = Math.Clamp(node.X + io.MouseDelta.X + ax, 0, 8000);
                node.Y = Math.Clamp(node.Y + io.MouseDelta.Y + ay, 0, 8000);
            }
        }
        else
        {
            if (dragMoved)
            {
                node.X = Math.Max(0, MathF.Round(node.X / GridStep) * GridStep);
                node.Y = Math.Max(0, MathF.Round(node.Y / GridStep) * GridStep);
                config.SaveActiveDyn();
            }
            dragNode = null;
            dragMoved = false;
        }
    }

    // An edge targeting a trigger/coords/door/dlss/preset/timelock node
    // is a gate: the target only fires while all its sources are ON.
    // Time->time edges are chain links (green); gates draw orange.
    private static bool IsGateTarget(DynamicPresetData data, string nodeId)
    {
        var t = data.Nodes.Find(n => n.Id == nodeId);
        return t != null && (IsTrigger(t) || IsCoords(t) || IsWall(t) || IsTimer(t) || IsDlss(t) || IsPreset(t) || IsTimeLock(t));
    }

    private void DrawEdges(DynamicPresetData data, Dictionary<string, PinSet> pinRects)
    {
        var dl = ImGui.GetWindowDrawList();
        foreach (var e in data.Edges)
        {
            if (!pinRects.TryGetValue(e.From, out var a) || !pinRects.TryGetValue(e.To, out var b)) continue;
            Vector2 p1 = (!string.IsNullOrEmpty(e.FromPin) && a.OutPins.TryGetValue(e.FromPin, out var pp))
                ? pp + new Vector2(11, 11)
                : a.Out + new Vector2(11, 11);
            var p2 = b.In + new Vector2(11, 11);
            var c1 = p1 + new Vector2(40, 0);
            var c2 = p2 - new Vector2(40, 0);
            if (e.From == selectedNode || e.To == selectedNode)
            {
                dl.AddBezierCubic(p1, c1, c2, p2, 0x6630B0FF, 5.0f);
                dl.AddBezierCubic(p1, c1, c2, p2, 0xFF30B0FF, 2.0f);
            }
            else if (IsGateTarget(data, e.To)) dl.AddBezierCubic(p1, c1, c2, p2, 0xFF00A5FF, 2.0f);
            else dl.AddBezierCubic(p1, c1, c2, p2, 0xFF60C060, 2.0f);
        }
    }

    private static bool CreatesCycle(DynamicPresetData data, string from, string to)
    {
        var seen = new HashSet<string> { to };
        var stack = new Stack<string>();
        stack.Push(to);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            foreach (var e in data.Edges)
            {
                if (e.From != cur) continue;
                if (e.To == from) return true;
                if (seen.Add(e.To)) stack.Push(e.To);
            }
        }
        return false;
    }

    private void HandleDeleteKey(DynamicPresetData data)
    {
        if (selectedNode == null) return;
        if (ImGui.IsAnyItemActive()) return;
        if (!ImGui.IsKeyPressed(ImGuiKey.Delete)) return;
        data.Nodes.RemoveAll(n => n.Id == selectedNode);
        data.Edges.RemoveAll(e => e.From == selectedNode || e.To == selectedNode);
        if (pendingLinkFrom == selectedNode) { pendingLinkFrom = null; pendingLinkPin = ""; }
        selectedNode = null;
        config.SaveActiveDyn();
    }
}
