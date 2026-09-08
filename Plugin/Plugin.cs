using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using ECommons;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using Lumina.Excel.Sheets;

namespace ReshadeController;

public sealed class Plugin : IDalamudPlugin, IDisposable
{
    internal static long PresetReloadCounter = 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    public static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint hWnd, ref POINT lpPoint);

    private static nint _gameHwnd = nint.Zero;

    // Game client size in pixels for fraction-based screen tests. False when
    // unknown (window gone) — callers fall back to full-window behavior.
    public static bool TryGetGameClientSize(out int w, out int h)
    {
        w = 0;
        h = 0;
        try
        {
            if (_gameHwnd == nint.Zero)
                _gameHwnd = FindWindow("FFXIVGAME", null);
            if (_gameHwnd == nint.Zero) return false;
            if (!GetClientRect(_gameHwnd, out var r)) { _gameHwnd = nint.Zero; return false; }
            w = r.Right - r.Left;
            h = r.Bottom - r.Top;
            return w > 0 && h > 0;
        }
        catch { return false; }
    }

    private const string CommandName = "/reshade";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IChatGui chatGui;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IDataManager dataManager;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;

    private ICallGateSubscriber<string[]>? getConditionSets;
    private ICallGateSubscriber<int, bool>? checkConditionSet;
    private ICallGateSubscriber<bool>? weathermanIsTimeCustom;
    private ICallGateSubscriber<string>? weathermanGetDisplayedTimeString;
    private ICallGateSubscriber<byte>? weathermanGetDisplayedWeather;
    private ICallGateSubscriber<bool>? weathermanIsWeatherCustom;
    private bool weathermanChecked = false;
    private DateTime ipcRetryAt = DateTime.MinValue;
    private bool ecommonsReady = false;

    // Built-in weather/time override (Weather tab). Inert until
    // Config.WeatherControlEnabled is turned on by the user.
    public WeatherOverride Weather { get; private set; } = null!;

    // Time-play: full 24h cycle over Config.WeatherPlayCycleSeconds.
    // Transient, never persisted; the clock value is saved on stop.
    public bool WeatherTimePlaying;
    private float wxPlayAccum;

    public void StopWeatherTimePlay(bool save = true)
    {
        WeatherTimePlaying = false;
        wxPlayAccum = 0f;
        if (save)
        {
            try { Config.Save(); } catch { }
        }
    }

    // Daylight recording: arms the 60s full-cycle playback from midnight
    // and publishes the live Eorzea clock for the external recorder
    // (brightness_curve.py --eorzea-file). The recorder stamps every
    // brightness sample, so the import needs no clock sync.
    public bool DaylightRecordArmed { get; private set; }
    private DateTime lastDaylightClockWrite = DateTime.MinValue;
    // Applied Eorzea seconds this run: loop completion is measured on
    // this, never on wall time.
    private float wxPlayTotal;

    public static string GetDaylightClockPath()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
            throw new InvalidOperationException("Cannot determine game path.");
        return Path.Combine(Path.GetDirectoryName(processPath)!, "daylight_clock.txt");
    }

    // Fired by the external recorder: it writes this file the moment the
    // screen region is confirmed, so playback starts from 00:00 with no
    // alt-tab race. Only honored while armed; always consumed.
    public static string GetDaylightGoPath()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
            throw new InvalidOperationException("Cannot determine game path.");
        return Path.Combine(Path.GetDirectoryName(processPath)!, "daylight_go.txt");
    }

    public bool StartDaylightRecord()
    {
        try
        {
            if (IsExternalWeathermanTimeCustom()) return false;
            Config.WeatherPlayCycleSeconds = 60;
            Config.Save();
            if (!Weather.EnableTime(0)) return false;
            Config.ForcedTimeSeconds = 0;
            Config.TimeCustomOn = true;
            Config.Save();
            try { StopWeatherTimePlay(false); } catch { }
            DaylightRecordArmed = true;
            wxPlayTotal = 0f;
            lastDaylightClockWrite = DateTime.MinValue;
            try
            {
                var go = GetDaylightGoPath();
                if (File.Exists(go)) File.Delete(go);
            }
            catch { }
            return true;
        }
        catch { return false; }
    }

    public float DaylightLoopProgress()
    {
        try { return Math.Clamp(wxPlayTotal / 86400f, 0f, 1f); } catch { return 0f; }
    }

    public void StopDaylightRecord()
    {
        try
        {
            StopWeatherTimePlay();
            DaylightRecordArmed = false;
            try
            {
                var p = GetDaylightClockPath();
                if (File.Exists(p)) File.Delete(p);
            }
            catch { }
            try
            {
                var go = GetDaylightGoPath();
                if (File.Exists(go)) File.Delete(go);
            }
            catch { }
        }
        catch { }
    }

    private void TickDaylightClock()
    {
        try
        {
            if (!DaylightRecordArmed) return;
            // Recorder signaled region confirm: (re)start the loop from
            // midnight, deterministically.
            try
            {
                var go = GetDaylightGoPath();
                if (File.Exists(go))
                {
                    try { File.Delete(go); } catch { }
                    if (Weather.EnableTime(0))
                    {
                        Config.ForcedTimeSeconds = 0;
                        Config.TimeCustomOn = true;
                        Config.Save();
                    }
                    wxPlayAccum = 0f;
                    wxPlayTotal = 0f;
                    WeatherTimePlaying = true;
                    try { Service.Log.Warning($"[ReshadeController:{DynamicCanvasWindow.BuildTag}] daylight go-signal: playback from 00:00"); } catch { }
                }
            }
            catch { }
            if ((DateTime.UtcNow - lastDaylightClockWrite).TotalMilliseconds < 100) return;
            lastDaylightClockWrite = DateTime.UtcNow;
            var tmp = GetDaylightClockPath() + ".tmp";
            File.WriteAllText(tmp, GetEorzeaSeconds().ToString());
            File.Move(tmp, GetDaylightClockPath(), true);
        }
        catch { }
    }

    private WindowSystem windowSystem = null!;
    private ConfigWindow configWindow = null!;
    private DynamicCanvasWindow animatorWindow = null!;

    public void ShowAnimator() => this.animatorWindow.IsOpen = true;

    public Configuration Config { get; private set; }
    public uint CurrentTerritoryId { get; private set; }

    // True while the player is actually standing in a world zone (not the
    // title screen, character select, or a loading screen).
    public bool IsInWorld()
    {
        try { return clientState.IsLoggedIn && clientState.TerritoryType != 0 && !condition[ConditionFlag.BetweenAreas]; }
        catch { return false; }
    }

    // Play-area outline overlay (Settings tab toggle): the configured
    // visible rect in screen space, so menus can be framed out by eye.
    private void DrawViewAreaOutline()
    {
        try
        {
            if (!this.Config.ViewAreaOutline) return;
            if (!this.configWindow.SettingsTabDrawn) return;
            this.configWindow.SettingsTabDrawn = false;
            if (_gameHwnd == nint.Zero)
                _gameHwnd = FindWindow("FFXIVGAME", null);
            if (_gameHwnd == nint.Zero) return;
            if (!GetClientRect(_gameHwnd, out var r)) return;
            int w = r.Right - r.Left;
            int h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0) return;
            var origin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(_gameHwnd, ref origin)) return;
            float L = Math.Clamp(this.Config.ViewAreaLeft, 0f, 1f);
            float T = Math.Clamp(this.Config.ViewAreaTop, 0f, 1f);
            float R = Math.Clamp(this.Config.ViewAreaRight, 0f, 1f);
            float B = Math.Clamp(this.Config.ViewAreaBottom, 0f, 1f);
            if (!(L < R && T < B)) { L = 0; T = 0; R = 1; B = 1; }
            var dl = ImGui.GetBackgroundDrawList();
            var min = new System.Numerics.Vector2(origin.X + L * w, origin.Y + T * h);
            var max = new System.Numerics.Vector2(origin.X + R * w, origin.Y + B * h);
            dl.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0.35f, 1f, 0.35f, 1f)), 0f, ImDrawFlags.None, 2f);
        }
        catch { }
    }

    public System.Numerics.Vector3? PlayerPosition()
    {
        try { return this.objectTable.LocalPlayer?.Position; }
        catch { return null; }
    }

    public static unsafe System.Numerics.Vector3? CameraPosition()
    {
        try
        {
            var mgr = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager.Instance();
            if (mgr == null) return null;
            nint worldCam = *(nint*)mgr;
            if (worldCam == nint.Zero) return null;
            float x = *(float*)(worldCam + 0x60);
            float y = *(float*)(worldCam + 0x64);
            float z = *(float*)(worldCam + 0x68);
            if (!float.IsFinite(x + y + z)) return null;
            return new System.Numerics.Vector3(x, y, z);
        }
        catch { return null; }
    }

    public System.Numerics.Vector3? TrackPosition(DynamicAnimNode node)
    {
        try
        {
            if (node.LocUseCamera) return CameraPosition();
            return this.objectTable.LocalPlayer?.Position;
        }
        catch { return null; }
    }
    public string CurrentTerritoryName { get; private set; } = string.Empty;
    public bool IsPaused => File.Exists(GetPauseFilePath());
    public string[] ConditionSetNames { get; private set; } = Array.Empty<string>();

    private int conditionSetIndex = -1;
    private bool lastState;
    private uint lastTerritoryId;
    private bool lastGlobalPause;
    private bool lastHotkeyState;
    private bool lastMenuHotkeyState;
    private bool lastAnimatorHotkeyState;

    public int ConditionSetIndex
    {
        get => conditionSetIndex;
        set => conditionSetIndex = value;
    }



    public void ResetLastState() => lastState = false;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IChatGui chatGui,
        IFramework framework,
        IClientState clientState,
        IDataManager dataManager,
        ICondition condition,
        IPluginLog pluginLog,
        IObjectTable objectTable,
        IGameInteropProvider gameInterop,
        IGameGui gameGui,
        ISigScanner sigScanner)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.chatGui = chatGui;
        this.framework = framework;
        this.clientState = clientState;
        this.dataManager = dataManager;
        this.condition = condition;
        this.objectTable = objectTable;
        Service.GameGui = gameGui;
        try { InitEmoteHook(gameInterop); } catch { }

        Service.PluginInterface = pluginInterface;
        Service.DataManager = dataManager;
        Service.ClientState = clientState;
        Service.Framework = framework;
        Service.ChatGui = chatGui;
        Service.CommandManager = commandManager;
        Service.Condition = condition;
        Service.Log = pluginLog;

        try { ECommonsMain.Init(pluginInterface, this); ecommonsReady = true; }
        catch (Exception ex) { try { Service.Log.Warning($"[ReshadeController:{DynamicCanvasWindow.BuildTag}] ECommons init failed, built-in weather override unavailable: {ex.Message}"); } catch { } }
        this.Weather = new WeatherOverride(sigScanner);

        this.Config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Render patches cannot survive a reload: any persisted "custom on"
        // state is stale (UI would claim a freeze that isn't applied, and
        // the refusal checks would misread it). Reset to off on every load.
        try
        {
            if (Config.WeatherCustomOn || Config.TimeCustomOn || Config.DayCustomOn)
            {
                Config.WeatherCustomOn = false;
                Config.TimeCustomOn = false;
                Config.DayCustomOn = false;
                Config.Save();
                try { Service.Log.Warning($"[ReshadeController:{DynamicCanvasWindow.BuildTag}] load: stale built-in weather/time state cleared (patches don't persist)"); } catch { }
            }
        }
        catch { }

        this.windowSystem = new WindowSystem("ReshadeController");
        this.configWindow = new ConfigWindow(this);
        this.windowSystem.AddWindow(this.configWindow);
        this.animatorWindow = new DynamicCanvasWindow(this, this.configWindow);
        this.windowSystem.AddWindow(this.animatorWindow);

        pluginInterface.UiBuilder.Draw += this.windowSystem.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += () => this.configWindow.Toggle();

        this.commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "/reshade pause|resume|toggle|status"
        });

        this.framework.Update += OnUpdate;
        this.clientState.TerritoryChanged += OnTerritoryChanged;
        pluginInterface.UiBuilder.Draw += DrawViewAreaOutline;

        try { Service.Log.Warning($"[ReshadeController:{DynamicCanvasWindow.BuildTag}] plugin loaded (diag)"); } catch { }

        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath))
            {
                var dir = Path.GetDirectoryName(processPath)!;
                var cmdPath = Path.Combine(dir, "ffxiv_reshade_cmd.txt");
                try
                {
                    // Preserve any already-queued commands: append ACTIVATE
                    // instead of overwriting the file.
                    var existing = File.Exists(cmdPath) ? File.ReadAllText(cmdPath) : "";
                    if (!existing.Contains("ACTIVATE"))
                    {
                        var tmp = cmdPath + ".tmp";
                        File.WriteAllText(tmp, existing + "ACTIVATE|1\n");
                        File.Move(tmp, cmdPath, true);
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        try { this.configWindow.FlushDynSidecarNow(); } catch { }
        try { StopDaylightRecord(); } catch { }
        try { this.Weather?.DisableAll(); } catch { }
        try { this.Weather?.Dispose(); } catch { }
        try { if (ecommonsReady) ECommonsMain.Dispose(); } catch { }
        try { this.emoteHook?.Dispose(); } catch { }
        try { foreach (var c in _chatCommands.Values) { try { this.commandManager.RemoveHandler(c); } catch { } } } catch { }
        this.framework.Update -= OnUpdate;
        this.clientState.TerritoryChanged -= OnTerritoryChanged;
        try { this.pluginInterface.UiBuilder.Draw -= DrawViewAreaOutline; } catch { }
        this.commandManager.RemoveHandler(CommandName);
        this.windowSystem.RemoveAllWindows();
    }

    // Advances the frozen clock while time-play runs. No config saves
    // per frame; the value persists on stop.
    private void TickWeatherTimePlay(IFramework fw)
    {
        if (!WeatherTimePlaying) return;
        if (!Config.WeatherControlEnabled || !Weather.IsTimeCustom())
        {
            WeatherTimePlaying = false;
            wxPlayAccum = 0f;
            return;
        }
        float dt;
        try { dt = (float)fw.UpdateDelta.TotalSeconds; } catch { return; }
        if (dt <= 0f || dt > 0.5f) dt = Math.Min(Math.Max(dt, 0f), 0.5f);
        if (dt <= 0f) return;
        int cycleSec = Math.Clamp(Config.WeatherPlayCycleSeconds, 5, 600);
        wxPlayAccum += dt * (86400f / cycleSec);
        int step = (int)wxPlayAccum;
        if (step < 1) return;
        wxPlayAccum -= step;
        try
        {
            int v = (Config.ForcedTimeSeconds + step) % 86400;
            if (Weather.SetTime((uint)v))
            {
                Config.ForcedTimeSeconds = v;
                // Daylight recordings end on Eorzea distance, not wall
                // time: frame-rate/hitch losses (dt clamp) mean 60 wall
                // seconds rarely span the full 24h. A full loop applied
                // stops playback; coverage is what the import checks.
                if (DaylightRecordArmed)
                {
                    wxPlayTotal += step;
                    if (wxPlayTotal >= 86400)
                    {
                        try { Service.Log.Warning($"[ReshadeController:{DynamicCanvasWindow.BuildTag}] daylight cycle complete ({wxPlayTotal} eorzea-sec applied)"); } catch { }
                        StopDaylightRecord();
                        return;
                    }
                }
            }
            else
            {
                WeatherTimePlaying = false;
                wxPlayAccum = 0f;
            }
        }
        catch { WeatherTimePlaying = false; wxPlayAccum = 0f; }
    }

    // External Weatherman reasserts its patches every frame, so a
    // mid-session engagement would stomp our bytes (and vice versa).
    // Whoever turns on second loses: if the external override is live
    // while ours claims the same side, release ours with a one-time
    // notice. This is the runtime half of the UI refusals.
    private bool yieldNoticedWeather;
    private bool yieldNoticedTime;

    private void YieldToExternalWeatherman()
    {
        if (!Config.WeatherControlEnabled)
        {
            yieldNoticedWeather = false;
            yieldNoticedTime = false;
            return;
        }
        if (Config.WeatherCustomOn && IsExternalWeathermanWeatherCustom())
        {
            try { Weather.DisableWeather(); } catch { }
            Config.WeatherCustomOn = false;
            Config.Save();
            if (!yieldNoticedWeather)
            {
                yieldNoticedWeather = true;
                try { Service.ChatGui.Print("[ReshadeController] Built-in weather released: external Weatherman override took over."); } catch { }
                try { Service.Log.Warning($"[ReshadeController:{DynamicCanvasWindow.BuildTag}] weather yielded to external Weatherman"); } catch { }
            }
        }
        else if (!Config.WeatherCustomOn)
        {
            yieldNoticedWeather = false;
        }
        if ((Config.TimeCustomOn || Config.DayCustomOn) && IsExternalWeathermanTimeCustom())
        {
            try { Weather.DisableTime(); } catch { }
            try { Weather.DisableDay(); } catch { }
            try { StopWeatherTimePlay(false); } catch { }
            Config.TimeCustomOn = false;
            Config.DayCustomOn = false;
            Config.Save();
            if (!yieldNoticedTime)
            {
                yieldNoticedTime = true;
                try { Service.ChatGui.Print("[ReshadeController] Built-in time/date released: external Weatherman override took over."); } catch { }
                try { Service.Log.Warning($"[ReshadeController:{DynamicCanvasWindow.BuildTag}] time yielded to external Weatherman"); } catch { }
            }
        }
        else if (!Config.TimeCustomOn && !Config.DayCustomOn)
        {
            yieldNoticedTime = false;
        }
    }

    // DLSS Neural Rendering live control (renodx-dlss5). The ini value is
    // load-time only; the addon toggles live on F6 (verified in ReShade.log:
    // "NR toggled ON/OFF via F6", and a remote keybd_event F6 flips it).
    // So: F6 press = live toggle, ini write = persistence. Manual F6 taps
    // are mirrored back into dlssLive + ini so the checkbox never lies.
    // F6 hold window: the addon polls the key, so a 1-frame tap can fall
    // between two of its polls (a 150ms PowerShell hold always landed,
    // 1-frame holds never did). Hold ~300ms, released from the tick.
    private bool? dlssLive;
    private bool lastDlssF6;
    private long dlssF6ReleaseAt;
    private long dlssLogOffset = -1;
    private int dlssLogCooldown;

    private bool pendingDlssF6()
    {
        try { return dlssF6ReleaseAt != 0 && Environment.TickCount64 >= dlssF6ReleaseAt; } catch { return true; }
    }

    private void pendingDlssF6Clear()
    {
        try { dlssF6ReleaseAt = 0; } catch { }
    }

    // Authoritative NR state: the addon logs every flip
    // ("NR toggled ON/OFF via F6"). Tail the log so manual F6 taps that
    // fall inside a framework hitch can never desync us.
    private bool? ReadDlssFromLog()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(processPath)) return null;
            var logPath = Path.Combine(Path.GetDirectoryName(processPath)!, "ReShade.log");
            if (!File.Exists(logPath)) return null;
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (dlssLogOffset < 0 || fs.Length < dlssLogOffset)
            {
                // First sighting (or rotated): scan the tail.
                long scan = Math.Min(fs.Length, 16384);
                fs.Seek(fs.Length - scan, SeekOrigin.Begin);
                dlssLogOffset = fs.Length;
                return ParseDlssLogChunk(fs, (int)scan);
            }
            long pending = fs.Length - dlssLogOffset;
            if (pending <= 0 || pending > 262144) { dlssLogOffset = fs.Length; return null; }
            fs.Seek(dlssLogOffset, SeekOrigin.Begin);
            dlssLogOffset = fs.Length;
            return ParseDlssLogChunk(fs, (int)pending);
        }
        catch { return null; }
    }

    private static bool? ParseDlssLogChunk(FileStream fs, int count)
    {
        try
        {
            var buf = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = fs.Read(buf, read, count - read);
                if (n <= 0) break;
                read += n;
            }
            var text = Encoding.ASCII.GetString(buf, 0, read);
            int idx = text.LastIndexOf("NR toggled ", StringComparison.Ordinal);
            if (idx < 0) return null;
            if (text.IndexOf("NR toggled ON", idx, StringComparison.Ordinal) == idx) return true;
            if (text.IndexOf("NR toggled OFF", idx, StringComparison.Ordinal) == idx) return false;
            return null;
        }
        catch { return null; }
    }

    private void TickDlssF6Mirror()
    {
        try
        {
            if (pendingDlssF6())
            {
                pendingDlssF6Clear();
                try { keybd_event(0x75, 0, 2, UIntPtr.Zero); } catch { }
            }
            if (dlssLive == null)
            {
                // Prefer the log (ground truth incl. manual taps), else ini.
                try { dlssLive = ReadDlssFromLog() ?? configWindow.GetDlssNeural() ?? false; } catch { dlssLive = false; }
            }
            bool f6 = (GetAsyncKeyState(0x75) & 0x8000) != 0;
            if (f6 && !lastDlssF6)
            {
                dlssLive = !(dlssLive ?? false);
                try { configWindow.SetDlssNeural(dlssLive.Value); } catch { }
                try { Service.Log.Warning($"[ReshadeController:{DynamicCanvasWindow.BuildTag}] DLSS F6 tap mirrored -> {(dlssLive.Value ? "on" : "off")}"); } catch { }
            }
            lastDlssF6 = f6;
            // Slow authoritative correction from the log tail (~0.5s).
            if (++dlssLogCooldown >= 30)
            {
                dlssLogCooldown = 0;
                try
                {
                    var logState = ReadDlssFromLog();
                    if (logState != null && logState != dlssLive)
                    {
                        dlssLive = logState;
                        try { configWindow.SetDlssNeural(dlssLive.Value); } catch { }
                        try { Service.Log.Warning($"[ReshadeController:{DynamicCanvasWindow.BuildTag}] DLSS state synced from log -> {(dlssLive.Value ? "on" : "off")}"); } catch { }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    public bool GetDlssLive()
    {
        try
        {
            if (dlssLive != null) return dlssLive.Value;
            return configWindow.GetDlssNeural() ?? false;
        }
        catch { return false; }
    }

    public void SetDlssNeuralLive(bool on)
    {
        try
        {
            if (dlssLive == null)
            {
                try { dlssLive = configWindow.GetDlssNeural() ?? false; } catch { dlssLive = false; }
            }
            if (dlssLive == on)
            {
                try { configWindow.SetDlssNeural(on); } catch { }
                return;
            }
            try { keybd_event(0x75, 0, 0, UIntPtr.Zero); } catch { }
            try { dlssF6ReleaseAt = Environment.TickCount64 + 300; } catch { pendingDlssF6Clear(); }
            lastDlssF6 = true; // swallow our own press so the mirror doesn't double-flip
            dlssLive = on;
            try { configWindow.SetDlssNeural(on); } catch { }
            try { Service.Log.Warning($"[ReshadeController:{DynamicCanvasWindow.BuildTag}] DLSS F6 sent (300ms hold) -> {(on ? "on" : "off")}"); } catch { }
        }
        catch { }
    }

    // "DLSS 5 In Cutscenes" automation: NR forced ON while
    // OccupiedInCutSceneEvent holds, restored OFF outside. Edge-triggered
    // only (SetDlssNeuralLive no-ops when already there), so no spam.
    // While enabled the automation owns NR — manual toggles get
    // overridden on the next frame.
    // DLSS NR arbitration, one decision per frame: wired DLSS 5 Trigger
    // nodes win (OR-combined demand) when any exist; otherwise the
    // "DLSS 5 In Cutscenes" automation applies; otherwise manual control
    // (checkbox / F6) is left alone. Single SetDlssNeuralLive call per
    // frame, which itself no-ops when already there.
    private void TickDlssDrivers()
    {
        try
        {
            bool? demand = null;
            bool anyWired = false;
            try
            {
                var dd = configWindow.GetActiveDynamicData();
                if (dd != null && dd.Enabled)
                {
                    foreach (var n in dd.Nodes)
                    {
                        if (!IsDlssNode(n)) continue;
                        if (!HasIncomingPins(dd, n.Id)) continue; // dormant unwired, like timer nodes
                        anyWired = true;
                        if (GatesSatisfied(dd, n)) { demand = true; break; }
                    }
                    // Wired nodes own NR: satisfied anywhere = ON,
                    // otherwise OFF (never defer to cutscene automation).
                    if (demand == null && anyWired) demand = false;
                }
            }
            catch { }
            if (demand != null)
            {
                SetDlssNeuralLive(demand.Value);
                return;
            }
            if (!Config.DlssInCutscenes) return;
            bool inCutscene = false;
            try { inCutscene = condition[ConditionFlag.OccupiedInCutSceneEvent]; } catch { return; }
            SetDlssNeuralLive(inCutscene);
        }
        catch { }
    }

    // Cutscene preset: on entering OccupiedInCutSceneEvent, stash the live
    // preset and force the configured one; on exit, restore the stash.
    // Empty config = (none), no switching. Edge-triggered only.
    private bool inCutscenePreset;
    private string preCutscenePreset = "";

    private void TickCutscenePreset()
    {
        try
        {
            bool inCs = false;
            try { inCs = condition[ConditionFlag.OccupiedInCutSceneEvent]; } catch { return; }
            if (inCs && !inCutscenePreset)
            {
                inCutscenePreset = true;
                preCutscenePreset = "";
                try
                {
                    var want = Config.CutscenePresetPath;
                    if (!string.IsNullOrEmpty(want) && File.Exists(want))
                    {
                        var cur = "";
                        try { cur = configWindow.SelectedPreset; } catch { }
                        if (!string.Equals(cur, want, StringComparison.OrdinalIgnoreCase))
                        {
                            preCutscenePreset = cur;
                            configWindow.ApplyPresetPath(want);
                        }
                    }
                }
                catch { }
            }
            else if (!inCs && inCutscenePreset)
            {
                inCutscenePreset = false;
                try
                {
                    if (!string.IsNullOrEmpty(preCutscenePreset))
                        configWindow.ApplyPresetPath(preCutscenePreset);
                }
                catch { }
                preCutscenePreset = "";
            }
        }
        catch { }
    }

    // Preset-signal queue: the addon polls ffxiv_reshade_preset holding a
    // read lock that fails our atomic replace (sharing violation) whenever
    // the 200ms poll lands inside our write. Queue + retry across frames
    // instead of throwing out of OnUpdate (the 01:28 crash class). The
    // counter bumps once per queued switch, not per retry; empty path =
    // delete the signal (also retried).
    private string? pendingPresetPath;
    private string pendingPresetContent = "";
    private bool presetSignalLogged;

    public void QueuePresetSignal(string presetPath)
    {
        try
        {
            pendingPresetPath = presetPath;
            pendingPresetContent = string.IsNullOrEmpty(presetPath) ? "" : $"{presetPath}|{++PresetReloadCounter}";
            presetSignalLogged = false;
            DrainPresetSignal();
        }
        catch { }
    }

    private void DrainPresetSignal()
    {
        if (pendingPresetPath == null) return;
        try
        {
            var file = GetPresetSignalPath();
            if (string.IsNullOrEmpty(pendingPresetContent))
            {
                if (File.Exists(file)) File.Delete(file);
            }
            else
            {
                var tmp = file + ".tmp";
                File.WriteAllText(tmp, pendingPresetContent);
                File.Move(tmp, file, true);
            }
            pendingPresetPath = null;
            presetSignalLogged = false;
        }
        catch
        {
            if (!presetSignalLogged)
            {
                presetSignalLogged = true;
                try { Service.Log.Warning($"[ReshadeController:{DynamicCanvasWindow.BuildTag}] preset signal busy (addon poll lock), retrying"); } catch { }
            }
        }
    }

    // Window vertical resolution sensor against the game client height.
    // ResCompare: 0 Equal, 1 Higher-or-equal, 2 Lower-or-equal.
    // Shared by the standalone Resolution node and the legacy kind-20
    // trigger.
    public static bool ResolutionLive(DynamicAnimNode node)
    {
        try
        {
            int want = Math.Clamp(node.ResMode, 0, 4) switch
            {
                0 => 720,
                1 => 1080,
                2 => 1440,
                3 => 2160,
                _ => Math.Max(1, node.ResCustomH),
            };
            if (!TryGetGameClientSize(out _, out int ch)) return false;
            return Math.Clamp(node.ResCompare, 0, 2) switch
            {
                1 => ch >= want,
                2 => ch <= want,
                _ => ch == want,
            };
        }
        catch { return false; }
    }

    // Daylight resolution: one global curve for all presets (sidecar
    // curves are legacy fallback + one-time adoption seed).
    private DynamicDaylight? ResolveDaylight(DynamicPresetData? data)
    {
        try
        {
            var gd = Path.GetDirectoryName(Environment.ProcessPath) ?? "";
            if (string.IsNullOrEmpty(gd)) return data?.Daylight;
            return DynamicDaylightStore.Load(gd, data?.Daylight);
        }
        catch { return data?.Daylight; }
    }

    // Location Switch sensor: ON while standing in any listed zone, OFF
    // elsewhere; SpotInvert flips it (ON outside, OFF inside). Pure level,
    // no latch. Public for the canvas stamp.
    public bool SpotLive(DynamicAnimNode n)
    {
        try
        {
            uint terr = CurrentTerritoryId;
            bool inside = terr != 0 && n.SpotZones != null && n.SpotZones.Contains(terr);
            return n.SpotInvert ? !inside : inside;
        }
        catch { return false; }
    }

    // Duty preset: on entering BoundByDuty, stash the live preset and
    // force the configured one; on exit, restore the stash. Empty config
    // = (none), no switching. Edge-triggered only. Runs before the
    // cutscene preset so cinematics win ties.
    private bool inDutyPreset;
    private string preDutyPreset = "";

    private void TickDutyPreset()
    {
        try
        {
            bool inDuty = false;
            try { inDuty = condition[ConditionFlag.BoundByDuty]; } catch { return; }
            if (inDuty && !inDutyPreset)
            {
                inDutyPreset = true;
                preDutyPreset = "";
                try
                {
                    var want = Config.DutyPresetPath;
                    if (!string.IsNullOrEmpty(want) && File.Exists(want))
                    {
                        var cur = "";
                        try { cur = configWindow.SelectedPreset; } catch { }
                        if (!string.Equals(cur, want, StringComparison.OrdinalIgnoreCase))
                        {
                            preDutyPreset = cur;
                            configWindow.ApplyPresetPath(want);
                        }
                    }
                }
                catch { }
            }
            else if (!inDuty && inDutyPreset)
            {
                inDutyPreset = false;
                try
                {
                    if (!string.IsNullOrEmpty(preDutyPreset))
                        configWindow.ApplyPresetPath(preDutyPreset);
                }
                catch { }
                preDutyPreset = "";
            }
        }
        catch { }
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        CurrentTerritoryId = territoryId;
        CurrentTerritoryName = GetZoneName(territoryId);
        ClearBuiltInWeatherOnZoneChange();
        ApplyZoneConfig(territoryId);
    }

    // Built-in weather override is current-zone only: leaving the zone
    // drops the weather latch (time freeze is global and persists).
    private void ClearBuiltInWeatherOnZoneChange()
    {
        try
        {
            if (Config.WeatherControlEnabled && Config.WeatherCustomOn)
            {
                try { Weather.DisableWeather(); } catch { }
                Config.WeatherCustomOn = false;
                Config.Save();
                try { Service.Log.Warning($"[ReshadeController:{DynamicCanvasWindow.BuildTag}] zone change: built-in weather override cleared"); } catch { }
            }
        }
        catch { }
    }

    private void OnUpdate(IFramework _)
    {
        // Adopt ReShade's live preset even with the window closed;
        // otherwise the engine idles until first open (mtime-gated, cheap).
        try { this.configWindow.FollowLivePreset(); } catch { }
        try { _eorzeaNow = GetEorzeaSeconds(); } catch { }
        try { TickWeatherTimePlay(_); } catch { }
        try { TickDaylightClock(); } catch { }
        try { YieldToExternalWeatherman(); } catch { }
        try { TickDlssF6Mirror(); } catch { }
        try { TickDlssDrivers(); } catch { }
        try { TickDutyPreset(); } catch { }
        try { TickCutscenePreset(); } catch { }
        try { DrainPresetSignal(); } catch { }
        // Check hotkey toggle
        if (Config.ToggleHotkeyKey != 0)
        {
            bool keyDown = (GetAsyncKeyState(Config.ToggleHotkeyKey) & 0x8000) != 0;
            bool modMatch = true;
            if ((Config.ToggleHotkeyMod & 1) != 0 && (GetAsyncKeyState(0x10) & 0x8000) == 0) modMatch = false; // Shift
            if ((Config.ToggleHotkeyMod & 2) != 0 && (GetAsyncKeyState(0x11) & 0x8000) == 0) modMatch = false; // Ctrl
            if ((Config.ToggleHotkeyMod & 4) != 0 && (GetAsyncKeyState(0x12) & 0x8000) == 0) modMatch = false; // Alt

            bool hotkeyPressed = keyDown && modMatch;
            if (hotkeyPressed && !lastHotkeyState)
            {
                var pauseFile = GetPauseFilePath();
                if (File.Exists(pauseFile))
                    File.Delete(pauseFile);
                else
                    File.WriteAllText(pauseFile, "");
            }
            lastHotkeyState = hotkeyPressed;
        }

        // Check menu hotkey toggle
        if (Config.MenuHotkeyKey != 0)
        {
            bool keyDown = (GetAsyncKeyState(Config.MenuHotkeyKey) & 0x8000) != 0;
            bool modMatch = true;
            if ((Config.MenuHotkeyMod & 1) != 0 && (GetAsyncKeyState(0x10) & 0x8000) == 0) modMatch = false; // Shift
            if ((Config.MenuHotkeyMod & 2) != 0 && (GetAsyncKeyState(0x11) & 0x8000) == 0) modMatch = false; // Ctrl
            if ((Config.MenuHotkeyMod & 4) != 0 && (GetAsyncKeyState(0x12) & 0x8000) == 0) modMatch = false; // Alt

            bool hotkeyPressed = keyDown && modMatch;
            if (hotkeyPressed && !lastMenuHotkeyState)
                this.configWindow.ToggleShadersTab();
            lastMenuHotkeyState = hotkeyPressed;
        }

        // Check animator hotkey toggle
        if (Config.AnimatorHotkeyKey != 0)
        {
            bool keyDown = (GetAsyncKeyState(Config.AnimatorHotkeyKey) & 0x8000) != 0;
            bool modMatch = true;
            if ((Config.AnimatorHotkeyMod & 1) != 0 && (GetAsyncKeyState(0x10) & 0x8000) == 0) modMatch = false; // Shift
            if ((Config.AnimatorHotkeyMod & 2) != 0 && (GetAsyncKeyState(0x11) & 0x8000) == 0) modMatch = false; // Ctrl
            if ((Config.AnimatorHotkeyMod & 4) != 0 && (GetAsyncKeyState(0x12) & 0x8000) == 0) modMatch = false; // Alt

            bool hotkeyPressed = keyDown && modMatch;
            if (hotkeyPressed && !lastAnimatorHotkeyState)
                this.animatorWindow.IsOpen = !this.animatorWindow.IsOpen;
            lastAnimatorHotkeyState = hotkeyPressed;
        }

        var territoryId = (uint)clientState.TerritoryType;
        if (territoryId != lastTerritoryId)
        {
            lastTerritoryId = territoryId;
            CurrentTerritoryId = territoryId;
            CurrentTerritoryName = GetZoneName(territoryId);
            ClearBuiltInWeatherOnZoneChange();
            ApplyZoneConfig(territoryId);
        }

        // Global conditions: BetweenAreas or not logged in -> force pause
        bool globalPause = !clientState.IsLoggedIn || condition[ConditionFlag.BetweenAreas];
        if (globalPause) _sawPauseSinceBoot = true;

        if (globalPause != lastGlobalPause)
        {
            lastGlobalPause = globalPause;
            var pauseFile = GetPauseFilePath();

            if (globalPause)
            {
                if (!File.Exists(pauseFile))
                {
                    File.WriteAllText(pauseFile, "");
                }
            }
            else
            {
                if (File.Exists(pauseFile))
                {
                    File.Delete(pauseFile);
                }
                // Loads happen behind a black screen: discard pending time
                // fade windows and sync states, so values snap instantly
                // instead of gliding into the new zone. In-flight trigger
                // runs die too (their wall clocks are stale; otherwise
                // they flash-complete for a frame post-load).
                try
                {
                    _timeFadeStart.Clear();
                    _fadeFrom.Clear();
                    _timeFadeOutStart.Clear();
                    _fadeOutFrom.Clear();
                    _holdRelease.Clear();
                    _runF.Clear();
                    _runLastTick.Clear();
                    _triggerStarts.Clear();
                    _triggerWasDown.Clear();
                    _pinWasUp.Clear();
                    _phasePings.Clear();
                    _retriggerFrom.Clear();
                    _runEndAt.Clear();
                    _runReleasing.Clear();
                    _bandLive.Clear();
                    _bandShown.Clear();
                    _bandLastTick.Clear();
                    _timeWasOn.Clear();
                    _tlWinner = "";
                    _tlFactor = 0f;
                    _tlLastSec = 0;
                    _tlSince.Clear();
                    Service.Log.Warning($"[ReshadeController:{DynamicCanvasWindow.BuildTag}] resume: run state reset");
                    var rd = this.configWindow.GetActiveDynamicData();
                    if (rd != null)
                    {
                        foreach (var tn in rd.Nodes)
                        {
                            if (!IsTimeNode(tn)) continue;
                            _timeWasOn[tn.Id] = TimeNodeOn(rd, tn.Id);
                        }
                    }
                }
                catch { }
            }
        }

        // Wall latches on zone change, per door node: entering a listed
        // (or home) zone latches on (fade-in ramps prog), anything else
        // resets — unless a Teleport/Return was cast recently. Tracked
        // every frame since TerritoryType can flap through 0 mid-load.
        try
        {
            if (objectTable.LocalPlayer is { } tpc && tpc.IsCasting)
            {
                int aid = (int)tpc.CastActionId;
                if (aid == 5 || aid == 6) _lastTeleportCast = DateTime.UtcNow;
            }
        }
        catch { }
        uint wtid = 0;
        try { wtid = clientState.TerritoryType; } catch { }
        if (wtid != _wallTerritory)
        {
            // First sighting after plugin boot while already live in-world
            // is a hot-enable, not a zone entry: adopt the territory
            // silently instead of firing entry actions (which would latch
            // home doors the player never walked into). A boot that saw any
            // loading state first keeps entry actions, so loading into a
            // listed room still latches.
            bool coldAdopt = _wallTerritory == 0 && !_sawPauseSinceBoot && !globalPause;
            _wallTerritory = wtid;
            if (wtid != 0 && !coldAdopt)
            {
                bool teleported = (DateTime.UtcNow - _lastTeleportCast).TotalSeconds < 60;
                var wdyn = this.configWindow.GetActiveDynamicData();
                foreach (var id in _wallLatched.Keys.ToList())
                {
                    var wn = wdyn?.Nodes.Find(n => n.Id == id.Split('\0')[0]);
                    if (wn == null || teleported || !WallAnyHome(wn, wtid)) DropWallNode(id);
                }
                // Entering a listed (or home) zone latches the door on
                // instantly (prog snapped, no fade-in). Fade-in still
                // applies to walk-through crossings. Any home door on the
                // node latches the shared latch.
                if (!teleported && wdyn != null)
                {
                    foreach (var wn in wdyn.Nodes)
                    {
                        if (!IsWallNode(wn)) continue;
                        if (!WallAnyHome(wn, wtid)) continue;
                        // Gated doors don't auto-latch on zone entry.
                        if (!GatesSatisfied(wdyn, wn)) continue;
                        _wallLatched[wn.Id] = true;
                        _wallProg[wn.Id] = 1f;
                    }
                }
                foreach (var id in _wallProg.Keys.Where(k => !_wallLatched.ContainsKey(k)).ToList())
                    DropWallNode(id);
                foreach (var id in _wallPrev.Keys.Where(k => !_wallLatched.ContainsKey(k)).ToList())
                    DropWallNode(id);
            }
        }

        // Don't process QoLBar or zone logic while globally paused
        if (globalPause) return;

        // Check QoLBar condition set if linked
        if (conditionSetIndex >= 0)
        {
            try
            {
                if (getConditionSets == null) ConnectIPC();
                if (getConditionSets == null || checkConditionSet == null) return;

                var sets = getConditionSets.InvokeFunc();
                if (sets == null || conditionSetIndex >= sets.Length) return;

                bool state = checkConditionSet.InvokeFunc(conditionSetIndex);
                if (state == lastState) return;

                lastState = state;
                var pauseFile = GetPauseFilePath();

                if (state)
                {
                    if (!File.Exists(pauseFile))
                    {
                        File.WriteAllText(pauseFile, "");
                    }
                }
                else
                {
                    if (File.Exists(pauseFile))
                    {
                        File.Delete(pauseFile);
                    }
                }
            }
            catch { }
        }

        // Update shader animations (legacy tab + stage-2 dynamic engine share
        // one atomically-written anim file; empty content deletes it).
        var dynData = this.configWindow.GetActiveDynamicData();
        int eorzeaSeconds = GetEorzeaSeconds();
        // Time fade edges: stamp the window start on gated-off -> on.
        // Only nodes with a fade time can use it.
        if (dynData != null)
        {
            bool anyChain = DynamicTimeline.ResolveChainFrames(dynData) != null;
            foreach (var tn in dynData.Nodes)
            {
                if (!IsTimeNode(tn) || ChainHeadFadeSec(dynData, tn) <= 0) continue;
                bool on = TimeNodeOn(dynData, tn.Id);
                bool was = _timeWasOn.TryGetValue(tn.Id, out bool w) && w;
                _timeWasOn[tn.Id] = on;
                if (on && !was)
                {
                    _timeFadeStart[tn.Id] = DateTime.UtcNow;
                    // Freeze the blend-from values now; the live snapshot
                    // keeps moving and must not feed the ramp.
                    _fadeFrom[tn.Id] = new Dictionary<string, string>(_prevMirrored, StringComparer.OrdinalIgnoreCase);
                    _timeFadeOutStart.Remove(tn.Id);
                    _fadeOutFrom.Remove(tn.Id);
                }
                else if (!on && was && anyChain)
                {
                    // Dying chain: hold orphaned keys/techs through the
                    // fade-out window, then release.
                    _timeFadeOutStart[tn.Id] = DateTime.UtcNow;
                    _fadeOutFrom[tn.Id] = new Dictionary<string, string>(_prevMirrored, StringComparer.OrdinalIgnoreCase);
                }
            }
            foreach (var id in _timeWasOn.Keys.Where(k => dynData.Nodes.All(n => n.Id != k)).ToList())
            {
                _timeWasOn.Remove(id);
                _timeFadeStart.Remove(id);
                _fadeFrom.Remove(id);
                _timeFadeOutStart.Remove(id);
                _fadeOutFrom.Remove(id);
            }
        }
        else
        {
            _timeWasOn.Clear();
            _timeFadeStart.Clear();
            _fadeFrom.Clear();
            _timeFadeOutStart.Clear();
            _fadeOutFrom.Clear();
        }
        // Perf readout (/reshade perf): per-section last-frame cost of the
        // dynamic engine. Stopwatch only runs with a dynamic preset active.
        System.Diagnostics.Stopwatch? pfSw = dynData != null ? System.Diagnostics.Stopwatch.StartNew() : null;
        if (pfSw == null) { _pfOverlays = _pfContent = _pfToggles = _pfMirror = _pfWrite = 0; _pfKeys = _pfTechs = 0; }
        BuildTriggerOverlay(dynData, DateTime.UtcNow, out var trigU, out var trigT, out var envU, out var envT);
        BuildWallOverlay(dynData, trigU, trigT, envU, envT);
        BuildBandOverlay(dynData, trigU, trigT, envU, envT);
        BuildTimerOverlay(dynData, DateTime.UtcNow);
        BuildPresetOverlay(dynData);
        string legacy = BuildLegacyAnimContent(eorzeaSeconds);
        if (pfSw != null) { _pfOverlays = pfSw.ElapsedTicks; pfSw.Restart(); }
        float tlDt = 0f;
        try { tlDt = (float)_.UpdateDelta.TotalSeconds; } catch { }
        if (tlDt < 0f || tlDt > 0.5f) tlDt = Math.Min(Math.Max(tlDt, 0f), 0.5f);
        // Time Lock override: arbitrate the winning lock, then evaluate at
        // live + locked seconds and merge by the lock crossfade. Paused
        // output holds (single path), so resume never replays a stale lock.
        int? tlSec = null;
        if (dynData != null)
        {
            try { tlSec = TickTimeLock(dynData, tlDt); } catch { tlSec = null; }
        }
        string dynamic;
        Dictionary<string, string> mirroredUniforms;
        Dictionary<string, bool>? mirroredToggles = null;
        bool writeHandled = false;
        if (dynData != null && !IsPaused && tlSec.HasValue)
        {
            string liveContent = BuildDynamicAnimContent(dynData, eorzeaSeconds, out var liveMir, trigU, false);
            var liveFlags = new Dictionary<string, bool>(_curBaseFlags, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, bool>? liveTg = FlushDynamicToggles(dynData, eorzeaSeconds, trigT, false);
            string lockContent = BuildDynamicAnimContent(dynData, tlSec.Value, out var lockMir, trigU, false);
            foreach (var kvp in _curBaseFlags.ToList())
                _curBaseFlags[kvp.Key] = kvp.Value && liveFlags.TryGetValue(kvp.Key, out bool lf) && lf;
            Dictionary<string, bool>? lockTg = FlushDynamicToggles(dynData, tlSec.Value, trigT, false);
            var merged = MergeTimeLockContent(liveContent, liveMir, lockContent, lockMir, _tlFactor);
            dynamic = merged.Content;
            mirroredUniforms = merged.Mirrored;
            // Toggles are binary: locked set past halfway, live before.
            bool useLock = _tlFactor >= 0.5f;
            var pick = useLock ? lockTg : liveTg;
            var other = useLock ? liveTg : lockTg;
            var mergedTg = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (pick != null) foreach (var kvp in pick) mergedTg[kvp.Key] = kvp.Value;
            if (other != null) foreach (var kvp in other) if (!mergedTg.ContainsKey(kvp.Key)) mergedTg[kvp.Key] = kvp.Value;
            mirroredToggles = mergedTg;
            WriteToggleDiffs(mergedTg);
            // Lock path always full-writes while engaged (transient by
            // nature): sync emit trackers to the merged state so the later
            // single-path deltas continue seamlessly.
            if ((DateTime.UtcNow - _lastAnimWrite).TotalMilliseconds >= 25)
            {
                _lastAnimWrite = DateTime.UtcNow;
                _lastEmitPreset = (dynData != null) ? (this.configWindow.SelectedPreset ?? "") : "";
                _lastEmitEnabled = dynData != null && dynData.Enabled;
                _writesSinceFull = 0;
                _lastWritten.Clear();
                foreach (var kvp in mirroredUniforms) _lastWritten[kvp.Key] = kvp.Value;
                _lastLegacyWritten = legacy;
                _lastWasEmpty = false;
                try
                {
                    var animFile = GetAnimationSignalPath();
                    if (merged.Content.Length == 0)
                    {
                        if (File.Exists(animFile)) File.Delete(animFile);
                        _lastWasEmpty = true;
                    }
                    else
                    {
                        var tmp = animFile + ".tmp";
                        File.WriteAllText(tmp, legacy + merged.Content);
                        File.Move(tmp, animFile, true);
                    }
                }
                catch { }
            }
            if (pfSw != null) { _pfContent = 0; _pfToggles = pfSw.ElapsedTicks; pfSw.Restart(); }
            _pfKeys = mirroredUniforms.Count;
            _pfTechs = mirroredToggles?.Count ?? 0;
            writeHandled = true;
        }
        else
        {
            dynamic = BuildDynamicAnimContent(dynData, eorzeaSeconds, out mirroredUniforms, trigU);
            if (pfSw != null) { _pfContent = pfSw.ElapsedTicks; pfSw.Restart(); }
            if (dynData != null)
                mirroredToggles = FlushDynamicToggles(dynData, eorzeaSeconds, trigT);
            if (pfSw != null) { _pfToggles = pfSw.ElapsedTicks; pfSw.Restart(); }
            _pfKeys = mirroredUniforms.Count;
            _pfTechs = mirroredToggles?.Count ?? 0;
        }
        // Paused shaders hold outputs (panels + signal files) while the sim
        // keeps ticking underneath, so resume picks up the current state
        // instead of replaying stale frames. Toggle signals already gate
        // themselves inside FlushDynamicToggles. Mirror only feeds panels,
        // so skip it with the window closed (re-mirrors on reopen).
        if (dynData != null && !IsPaused && this.configWindow.IsOpen)
            this.configWindow.MirrorDynamicState(mirroredUniforms, mirroredToggles);
        // Snapshot for next frame's time fade (blend-from values +
        // base-only provenance). Kept across frozen frames so re-ON still
        // ramps from pre-freeze. Reference-assign: nothing mutates these
        // dicts after this point until the next build re-news them.
        try
        {
            if (mirroredUniforms.Count > 0)
            {
                _prevMirrored = mirroredUniforms;
                _prevBaseOnly = _curBaseFlags;
            }
            if (mirroredToggles != null && mirroredToggles.Count > 0)
                _prevToggles = mirroredToggles;
        }
        catch { }
        if (pfSw != null) { _pfMirror = pfSw.ElapsedTicks; pfSw.Restart(); }
        // Manual pause holds outputs: touch neither files nor emit
        // trackers (the sim above keeps ticking for a live resume).
        if (!writeHandled && !IsPaused)
        {
            string curPreset = (dynData != null) ? (this.configWindow.SelectedPreset ?? "") : "";
            bool curEnabled = dynData != null && dynData.Enabled;
            bool legChanged = !string.Equals(legacy, _lastLegacyWritten, StringComparison.Ordinal);
            if (dynamic.Length == 0 && legacy.Length == 0)
            {
                // Empty: delete once (coalesced like the old gate), reset
                // emit trackers so the next content full-emits naturally.
                if (!_lastWasEmpty)
                {
                    try { var af0 = GetAnimationSignalPath(); if (File.Exists(af0)) File.Delete(af0); } catch { }
                    _lastWasEmpty = true;
                }
                _lastWritten.Clear();
                _lastLegacyWritten = "\0";
                _writesSinceFull = 0;
                _lastEmitPreset = "";
                _lastEmitEnabled = false;
            }
            else
            {
                if ((DateTime.UtcNow - _lastAnimWrite).TotalMilliseconds < 25) return;
                _lastWasEmpty = false;
                string toWrite = (_emitFull || legChanged) ? legacy + dynamic : dynamic;
                if (toWrite.Length == 0)
                {
                    // Forced but empty (fresh enable on empty content):
                    // ensure absence like the empty branch.
                    try { var af1 = GetAnimationSignalPath(); if (File.Exists(af1)) File.Delete(af1); } catch { }
                    _lastWasEmpty = true;
                }
                else
                {
                    try
                    {
                        var animFile = GetAnimationSignalPath();
                        var tmp = animFile + ".tmp";
                        File.WriteAllText(tmp, toWrite);
                        File.Move(tmp, animFile, true);
                    }
                    catch { }
                    _lastWasEmpty = false;
                }
                _lastAnimWrite = DateTime.UtcNow;
                _lastEmitPreset = curPreset;
                _lastEmitEnabled = curEnabled;
                if (_emitFull)
                {
                    _writesSinceFull = 0;
                    _lastWritten.Clear();
                    foreach (var kvp in mirroredUniforms) _lastWritten[kvp.Key] = kvp.Value;
                }
                else
                {
                    _writesSinceFull++;
                    foreach (var k in _emitChanged)
                        if (mirroredUniforms.TryGetValue(k, out var vv)) _lastWritten[k] = vv;
                }
                _lastLegacyWritten = legacy;
            }
        }
        if (pfSw != null) _pfWrite = pfSw.ElapsedTicks;
    }

    private DateTime _lastAnimWrite = DateTime.MinValue;
    // Perf readout (/reshade perf): last-frame section costs in Stopwatch
    // ticks + output sizes. Zero when idle (no dynamic preset).
    private long _pfOverlays, _pfContent, _pfToggles, _pfMirror, _pfWrite;
    private int _pfKeys, _pfTechs;

    private string BuildLegacyAnimContent(int eorzeaSeconds)
    {
        if (Config.ShaderAnimations.Count == 0) return "";

        var sb = new StringBuilder();

        foreach (var anim in Config.ShaderAnimations)
        {
            if (!anim.Enabled || anim.TimePoints.Count < 2 || string.IsNullOrEmpty(anim.EffectName))
                continue;

            var sorted = anim.TimePoints.OrderBy(tp => tp.TimeSeconds).ToList();
            var values = InterpolateTimePoints(sorted, eorzeaSeconds);
            if (values == null) continue;

            foreach (var uv in values)
            {
                if (uv.Type == "float" && uv.FloatValues != null && uv.FloatValues.Length > 0)
                    sb.AppendLine($"{anim.EffectName}|{uv.Name}|float|{string.Join(",", uv.FloatValues.Select(f => f.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)))}");
                else if (uv.Type == "int" && uv.IntValues != null && uv.IntValues.Length > 0)
                    sb.AppendLine($"{anim.EffectName}|{uv.Name}|int|{string.Join(",", uv.IntValues)}");
            }
        }

        return sb.ToString();
    }

    // Time-node fade: OFF->ON edge per node, window starts, and the last
    // emitted values (blend-from). Manual slider grabs skip the fade.
    private readonly Dictionary<string, bool> _timeWasOn = new();
    private readonly Dictionary<string, DateTime> _timeFadeStart = new();
    // Fade-out: dying chains hold orphaned keys/techs, ramping toward
    // start values (or holding), until the window ends and toggles release.
    private readonly Dictionary<string, DateTime> _timeFadeOutStart = new();
    private readonly Dictionary<string, Dictionary<string, string>> _fadeOutFrom = new();
    private Dictionary<string, string> _prevMirrored = new(StringComparer.OrdinalIgnoreCase);
    // Per emitted key: was it carried only by the pooled primary (base)
    // rather than live chain/trigger content? Authored starts apply when
    // forced, or when the previous value is base-only (or missing).
    private Dictionary<string, bool> _prevBaseOnly = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, bool> _curBaseFlags = new(StringComparer.OrdinalIgnoreCase);
    // Last frame's fully resolved technique states (timeline + overrides +
    // triggers). Drives the auto-Start rule: an effect that's ON glides
    // from live, one that's OFF punches from its authored start.
    private Dictionary<string, bool> _prevToggles = new(StringComparer.OrdinalIgnoreCase);
    // Time Lock override: winning node id (newest activation among wired
    // + satisfied locks), output crossfade 0..1, frozen lock seconds for
    // the fade-out tail, and per-node activation times for recency.
    private string _tlWinner = "";
    private float _tlFactor;
    private int _tlLastSec;
    private float _tlLastFadeOut = 1f;
    private readonly Dictionary<string, DateTime> _tlSince = new();
    // One-shot diagnostic: proves pair mode engaged (else the wrap snap is
    // either a stale build, wrong curve, or this flag never firing).
    private bool _tlPairLogged;
    // Entry blend-from per location/band visit (node id -> key -> From, null
    // = live base). Captured once at entry so the From can't switch
    // mid-visit; cleared on exit.
    private readonly Dictionary<string, Dictionary<string, DynamicUniformValue?>> _locFrom = new();
    private readonly Dictionary<string, Dictionary<string, DynamicUniformValue?>> _bandFrom = new();
    // Zone release ramps per node (factor + tick): going quiet glides out at
    // the slowest zone FadeOut instead of cutting (0 = cut, as before).
    private readonly Dictionary<string, (float F, DateTime Tick)> _locRel = new(StringComparer.Ordinal);
    // Midpoint shaping per key (location blends with UseMidpoint): pinned
    // at factor 0.5, so the merge runs start -> mid -> peak.
    private readonly Dictionary<string, DynamicUniformValue> _midU = new(StringComparer.OrdinalIgnoreCase);

    // Two-leg blend through Mid at f=0.5 (midpoint shaping).
    private static string DynLerpMid(DynamicUniformValue from, DynamicUniformValue mid, DynamicUniformValue to, float f)
    {
        float g = Math.Clamp(f, 0f, 1f);
        return g < 0.5f
            ? DynLerpValue(from, mid, g * 2f)
            : DynLerpValue(mid, to, (g - 0.5f) * 2f);
    }

    // Ambient layer full intent this frame: key -> (From, To, F), claimed
    // keys included (output merge still respects envelope claims).
    private readonly Dictionary<string, (DynamicUniformValue? From, DynamicUniformValue To, float F)> _ambU = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (bool S, float F)> _ambT = new(StringComparer.OrdinalIgnoreCase);
    // Overlay keys in release state this frame (envelope releases, wall
    // unlatches, falling bands). The merge lands these on ambient.
    private readonly HashSet<string> _releaseU = new(StringComparer.OrdinalIgnoreCase);

    // Auto-Start rule: an effect counts as live when any of its techniques
    // ("Tech@File") resolved ON last frame. Unknown files fall back to
    // driven-provenance; on any doubt report live (glide = snap-free).
    private bool FxLive(string file, string key)
    {
        try
        {
            bool anyKnown = false;
            foreach (var kvp in _prevToggles)
            {
                int at = kvp.Key.LastIndexOf('@');
                if (at < 0) continue;
                if (!string.Equals(kvp.Key.Substring(at + 1), file, StringComparison.OrdinalIgnoreCase)) continue;
                anyKnown = true;
                if (kvp.Value) return true;
            }
            if (anyKnown) return false;
            return !_prevBaseOnly.TryGetValue(key, out var wbo) || !wbo;
        }
        catch { return true; }
    }

    // Newest run owns shared trigger keys: a trigger fired over a running
    // one takes priority from its first frame (ties = later node order, as
    // before). Runs offer per key; the flush below commits the winners.
    private static void OfferTriggerUniform(
        Dictionary<string, (string Node, DateTime Since, DynamicUniformValue? From, DynamicUniformValue To, float F, bool Rel)> win,
        string nodeId, DateTime since, string key,
        DynamicUniformValue? from, DynamicUniformValue to, float f, bool rel)
    {
        try
        {
            if (!win.TryGetValue(key, out var w) || since >= w.Since)
                win[key] = (nodeId, since, from, to, f, rel);
        }
        catch { }
    }
    // Trigger key ownership for winner-change crossfades (node id per key).
    private readonly Dictionary<string, string> _trigWinner = new(StringComparer.OrdinalIgnoreCase);
    // Winner-change crossfades: key -> (screen snapshot, start time). Morphs
    // the cut when ownership jumps between live runs (takeover or handback).
    private readonly Dictionary<string, (string Value, DateTime T0)> _xfade = new(StringComparer.OrdinalIgnoreCase);
    private const float TrigXfadeSec = 0.3f;
    private static void OfferTriggerToggle(
        Dictionary<string, (DateTime Since, bool S, float F)> win,
        DateTime since, string key, bool s, float f)
    {
        try
        {
            if (!win.TryGetValue(key, out var w) || since >= w.Since)
                win[key] = (since, s, f);
        }
        catch { }
    }

    // Proportional attack seeding for grace/takeover edges: measure live
    // progress across each key's start->peak span (min over ticked keys and
    // components) and start the factor there instead of 0, solving each
    // key's From so output(f0) still equals the screen exactly. Attacks run
    // at constant value-rate with time proportional to distance left; keys
    // without live values keep full-time behavior. Scalar f0 takes the min
    // (slow stragglers, never violent jumps).
    private void SeedProportionalAttack(DynamicAnimNode node, DynamicKeyframe? kf,
        out Dictionary<string, string> fromMap, out float f0)
    {
        fromMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        f0 = 0f;
        try
        {
            if (kf == null) return;
            var prog = new List<float>();
            var entries = new List<(string Uk, string[] V, string[] P)>();
            foreach (var uk in kf.TickedUniforms)
            {
                int sep = uk.IndexOf('\0');
                if (sep < 0) continue;
                var file = uk.Substring(0, sep);
                var uname = uk.Substring(sep + 1);
                if (!_prevMirrored.TryGetValue(uk, out var liveStr)) continue;
                if (!kf.Uniforms.TryGetValue(file, out var m) || !m.TryGetValue(uname, out var peak)) continue;
                var vs = liveStr.Split(',');
                var ps = peak.Value.Split(',');
                if (vs.Length != ps.Length) { fromMap[uk] = liveStr; continue; }
                string[]? ss = null;
                if (node.StartValues.TryGetValue(file, out var sm) && sm.TryGetValue(uname, out var sv))
                    ss = sv.Value.Split(',');
                if (ss != null && ss.Length == ps.Length)
                {
                    for (int i = 0; i < ps.Length; i++)
                    {
                        if (!TryParseF(vs[i], out var lv) || !TryParseF(ps[i], out var pk) || !TryParseF(ss[i], out var st)) continue;
                        if (Math.Abs(pk - st) < 1e-6f) continue;
                        float praw = (lv - st) / (pk - st);
                        // In-range progress shortens the trip; behind-start
                        // or beyond-peak needs the full trip (from screen),
                        // never an instant-complete teleport to peak.
                        prog.Add(praw >= 0f && praw <= 1f ? praw : 0f);
                    }
                }
                entries.Add((uk, vs, ps));
            }
            if (prog.Count > 0)
            {
                f0 = prog.Min();
                if (f0 < 0f) f0 = 0f;
                if (f0 > 1f) f0 = 1f;
            }
            foreach (var (uk, vs, ps) in entries)
            {
                if (fromMap.ContainsKey(uk)) continue;
                var outs = new string[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    if (!TryParseF(vs[i], out var lv) || !TryParseF(ps[i], out var pk))
                    { outs[i] = vs[i].Trim(); continue; }
                    float fv = f0 >= 0.999f ? lv : (lv - f0 * pk) / (1f - f0);
                    outs[i] = fv.ToString("G9", System.Globalization.CultureInfo.InvariantCulture);
                }
                fromMap[uk] = string.Join(",", outs);
            }
        }
        catch { f0 = 0f; }
    }
    private static bool TryParseF(string s, out float v)
        => float.TryParse(s.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);

    // Color uniforms (ui_type=color) arrive instantly when idle but glide
    // from live when driven (snap-if-idle, glide-if-live): lerping hues
    // drags through middle tones, which is fine for a live handoff but
    // wrong out of nothing. The envelope factor / strength slider still
    // does the visible fading either way.
    private bool IsColorUniform(string file, string uname)
    {
        try
        {
            return this.configWindow.TryGetUniformInfo(file, uname, out var uinfo)
                && string.Equals(uinfo.UiType, "color", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // Entry snapshot for location/band blends: per key, the blend-from to
    // hold for the whole visit. Idle FX punches from authored (else the
    // live screen values); live FX glides from the live base (null).
    private Dictionary<string, DynamicUniformValue?> CaptureLocFrom(DynamicKeyframe kf, DynamicAnimNode node)
    {
        var map = new Dictionary<string, DynamicUniformValue?>(StringComparer.OrdinalIgnoreCase);
        foreach (var uk in kf.TickedUniforms)
        {
            int sep = uk.IndexOf('\0');
            if (sep < 0) continue;
            var file = uk.Substring(0, sep);
            var uname = uk.Substring(sep + 1);
            DynamicUniformValue? fromUv = null;
            if (!FxLive(file, uk))
            {
                if (node.StartValues.TryGetValue(file, out var sm) && sm.TryGetValue(uname, out var sv))
                    fromUv = sv;
                else if (_prevMirrored.TryGetValue(uk, out var snap)
                    && kf.Uniforms.TryGetValue(file, out var m) && m.TryGetValue(uname, out var uv))
                    fromUv = new DynamicUniformValue { Value = snap, BaseType = uv.BaseType };
            }
            map[uk] = fromUv;
        }
        return map;
    }
    // Frozen blend-from values per fading node, captured at its OFF->ON
    // edge. Blending from the live snapshot every frame would compound
    // (exponential approach) instead of ramping over the fade time.
    private readonly Dictionary<string, Dictionary<string, string>> _fadeFrom = new();

    private readonly Dictionary<string, bool> _lastSentDynToggles = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _dynToggleWindowStart = DateTime.MinValue;
    private int _dynToggleSentThisWindow;
    private const int DynTogglePerSecond = 30;

    // Trigger runs: node id -> envelope start (UtcNow). Fired by hotkey edge,
    // retrigger restarts. Evaluated in real seconds every update.
    private readonly Dictionary<string, DateTime> _triggerStarts = new();
    private readonly Dictionary<string, bool> _triggerWasDown = new();
    // Per-run envelope factor (rate-based, linear): fade-in advances it
    // toward 1 at 1/Fi per second, fade-out toward 0 at 1/Fo. Kept across
    // retriggers so attacks resume from live instead of rewinding; hold
    // ramps toward peak instead of jumping to it.
    private readonly Dictionary<string, float> _runF = new();
    private readonly Dictionary<string, DateTime> _runLastTick = new();
    // Retrigger blend-from: live values captured when an edge replaces an
    // active run, so the new attack resumes instead of rewinding to start.
    private readonly Dictionary<string, Dictionary<string, string>> _retriggerFrom = new();
    // Runs currently releasing (fading out): blend from the live timeline
    // instead of authored starts, so the handoff back to base never snaps.
    private readonly HashSet<string> _runReleasing = new();
    // Last run end per node: a fresh edge inside the fade-out duration
    // attacks from live (grace), anything later punches from authored.
    private readonly Dictionary<string, DateTime> _runEndAt = new();
    // HP/MP band factors per node (0 when out of band). Level state like
    // location blends: gates and titlebar read it, no envelope runs.
    private readonly Dictionary<string, float> _bandLive = new();

    // Band-mode trigger (HP/MP with separated Start/Max).
    private static bool IsBandNode(DynamicAnimNode n)
        => (n.TriggerKind == 9 || n.TriggerKind == 10)
        && Math.Abs(n.ThresholdPct - n.ThresholdMax) >= 0.0001f;
    // Slew state: displayed factor ramps toward the band target at
    // FadeInSec (rising) / FadeOutSec (falling) instead of snapping.
    private readonly Dictionary<string, float> _bandShown = new();
    private readonly Dictionary<string, DateTime> _bandLastTick = new();
    // Status-hold release instants (kind 11 + StayWhilePresent): fade-out
    // runs from the moment the status drops.
    private readonly Dictionary<string, DateTime> _holdRelease = new();
    private readonly Dictionary<string, bool> _pinWasUp = new();

    // Trigger phase pings: node id -> completed envelope phases for the
    // current run ("delay"/"fadein"/"stay"/"fadeout"). Sticky until the run
    // ends or retriggers, so downstream pin gates read stable levels.
    private readonly Dictionary<string, HashSet<string>> _phasePings = new();

    // Pin-activation action: runs once when the envelope fires. Goes
    // through the game's own chat submit (like typing it), because
    // CommandManager.ProcessCommand only dispatches Dalamud-registered
    // commands and silently drops game commands.
    private static unsafe void RunPinAction(DynamicAnimNode node)
    {
        try
        {
            if (node.PinAction != 1) return;
            var cmd = (node.PinActionCommand ?? "").Trim();
            if (string.IsNullOrEmpty(cmd) || cmd.Length > 500) return;
            if (!cmd.StartsWith("/")) cmd = "/" + cmd;
            var utf8 = FFXIVClientStructs.FFXIV.Client.System.String.Utf8String.FromString(cmd);
            try
            {
                FFXIVClientStructs.FFXIV.Client.UI.UIModule.Instance()->ProcessChatBoxEntry(utf8);
            }
            finally
            {
                utf8->Dtor(true);
            }
            try { Service.Log.Information($"[ReshadeController] pin action sent: {cmd}"); } catch { }
        }
        catch { }
    }

    private void PingPhase(string nodeId, string phase)
    {
        try
        {
            if (!_phasePings.TryGetValue(nodeId, out var set))
                _phasePings[nodeId] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(phase);
        }
        catch { }
    }

    private static bool HasIncomingPins(DynamicPresetData? data, string nodeId)
    {
        if (data == null) return false;
        foreach (var e in data.Edges)
            if (string.Equals(e.To, nodeId, StringComparison.Ordinal)) return true;
        return false;
    }

    // Self-emote tracking via PlayEmote hook (same source as EmoteLog):
    // owner == local player. Guarded; sensor stays false without it.
    private Dalamud.Hooking.Hook<FFXIVClientStructs.FFXIV.Client.Game.Control.EmoteController.Delegates.PlayEmote>? emoteHook;
    private readonly object _emoteLock = new();
    private uint _lastSelfEmoteId;
    private DateTime _lastSelfEmoteTime = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> _emoteSeen = new();

    // Custom chat commands per trigger node (local player only by nature).
    private readonly object _chatLock = new();
    private readonly Dictionary<string, string> _chatCommands = new();
    private readonly Dictionary<string, bool> _chatFired = new();

    // Wall triggers: latched on/off per node, fade progress 0..1, and the
    // previous character position used for plane-crossing tests.
    private readonly Dictionary<string, bool> _wallLatched = new();
    private readonly Dictionary<string, float> _wallProg = new();
    private readonly Dictionary<string, System.Numerics.Vector3> _wallPrev = new();
    private DateTime _wallLastTick = DateTime.MinValue;

    private void SyncChatCommands(DynamicPresetData? data)
    {
        var want = new Dictionary<string, string>();
        if (data != null && data.Enabled)
            foreach (var n in data.Nodes)
                if (IsTriggerNode(n) && n.TriggerKind == 17 && !string.IsNullOrWhiteSpace(n.ChatCommand))
                    want[n.Id] = n.ChatCommand.Trim();
        lock (_chatLock)
        {
            foreach (var id in _chatCommands.Keys.Where(k => !want.ContainsKey(k) || want[k] != _chatCommands[k]).ToList())
            {
                try { this.commandManager.RemoveHandler(_chatCommands[id]); } catch { }
                _chatCommands.Remove(id);
            }
            foreach (var kvp in want)
            {
                if (_chatCommands.ContainsKey(kvp.Key)) continue;
                string nid = kvp.Key;
                try
                {
                    this.commandManager.AddHandler(kvp.Value, new CommandInfo((_, __) => { lock (_chatLock) { _chatFired[nid] = true; } }) { HelpMessage = "animator trigger" });
                    _chatCommands[nid] = kvp.Value;
                }
                catch { }
            }
            foreach (var id in _chatFired.Keys.Where(k => !want.ContainsKey(k)).ToList())
                _chatFired.Remove(id);
        }
    }

    private unsafe void InitEmoteHook(Dalamud.Plugin.Services.IGameInteropProvider gameInterop)
    {
        this.emoteHook = gameInterop.HookFromAddress<FFXIVClientStructs.FFXIV.Client.Game.Control.EmoteController.Delegates.PlayEmote>(FFXIVClientStructs.FFXIV.Client.Game.Control.EmoteController.Addresses.PlayEmote.Value, PlayEmoteDetour);
        this.emoteHook.Enable();
    }

    private unsafe bool PlayEmoteDetour(FFXIVClientStructs.FFXIV.Client.Game.Control.EmoteController* emoteController, uint emoteId, FFXIVClientStructs.FFXIV.Client.Game.Control.EmoteController.PlayEmoteOption* options)
    {
        bool result = this.emoteHook!.Original(emoteController, emoteId, options);
        try
        {
            nint owner = emoteController != null && emoteController->OwnerObject != null
                ? (nint)emoteController->OwnerObject : nint.Zero;
            nint local = nint.Zero;
            try { local = this.objectTable.LocalPlayer?.Address ?? nint.Zero; } catch { }
            if (owner != nint.Zero && owner == local)
            {
                lock (_emoteLock) { _lastSelfEmoteId = emoteId; _lastSelfEmoteTime = DateTime.UtcNow; }
            }
        }
        catch { }
        return result;
    }

    private static bool IsTriggerNode(DynamicAnimNode n)
        => string.Equals(n.Source, "trigger", StringComparison.OrdinalIgnoreCase);

    private static bool IsNodeBound(DynamicPresetData? data, string kfId)
    {
        if (data == null || string.IsNullOrEmpty(kfId)) return false;
        foreach (var n in data.Nodes)
            if ((IsTriggerNode(n) || IsCoordsNode(n) || IsWallNode(n)) && n.KeyframeId == kfId) return true;
        return false;
    }

    private static bool IsCoordsNode(DynamicAnimNode n)
        => string.Equals(n.Source, "coords", StringComparison.OrdinalIgnoreCase);

    private static bool IsWallNode(DynamicAnimNode n)
        => string.Equals(n.Source, "wall", StringComparison.OrdinalIgnoreCase);

    private static bool IsWeatherNode(DynamicAnimNode n)
        => string.Equals(n.Source, "weather", StringComparison.OrdinalIgnoreCase);

    // A weather group is live while the current weather matches it. Any =
    // anything not claimed by sibling selecting groups. 255 = unknown.
    public static bool WeatherGroupLive(DynamicWeatherGroup g, List<DynamicWeatherGroup> all, byte current)
    {
        try
        {
            if (current == 255) return false;
            if (g.Any)
            {
                foreach (var o in all)
                {
                    if (ReferenceEquals(o, g) || o.Any) continue;
                    if (o.WeatherIds.Contains(current)) return false;
                }
                return true;
            }
            return g.WeatherIds.Contains((int)current);
        }
        catch { return false; }
    }

    // Current weather id. Prefers the built-in override (Weather tab,
    // gated by its master switch), then Weatherman's displayed weather
    // (respects forced weather); falls back to the true weather byte
    // (EnvManager+0x26, same address Weatherman itself reads).
    // 255 = unknown.
    public byte GetCurrentWeather()
    {
        try
        {
            if (Config.WeatherControlEnabled && Weather.IsWeatherCustom())
                return Weather.GetCustomWeather();
        }
        catch { }
        try
        {
            ConnectIPC();
            if (weathermanGetDisplayedWeather != null)
                return weathermanGetDisplayedWeather.InvokeFunc();
        }
        catch { }
        try
        {
            unsafe
            {
                var env = FFXIVClientStructs.FFXIV.Client.Graphics.Environment.EnvManager.Instance();
                if (env == null) return 255;
                return *(byte*)((nint)env + 0x26);
            }
        }
        catch { return 255; }
    }

    private static float Smooth01(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    // Envelope phases (real seconds): delay ramps base -> start (no jump
    // into the fade), then start -> peak, stay, peak -> start, release.
    // Missing start values fall back to base, keeping old behavior.

    // Weapon state via UIState (same source as xivFaderPlugin): no condition
    // flag exists for it.
    private static unsafe bool IsWeaponUnsheathed()
    {
        try
        {
            var ui = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
            return ui != null && ui->WeaponState.IsUnsheathed;
        }
        catch { return false; }
    }

    // First person via the world camera's mode int at +0x180 (same tell as
    // Cammy/Hypostasis: 0 = first person; world camera is the manager's
    // first pointer). Offset-read with guards; false on any doubt.
    private static unsafe bool IsFirstPerson()
    {
        try
        {
            var mgr = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager.Instance();
            if (mgr == null) return false;
            nint worldCam = *(nint*)mgr;
            if (worldCam == nint.Zero) return false;
            return *(int*)(worldCam + 0x180) == 0;
        }
        catch { return false; }
    }

    private bool Cond(ConditionFlag flag)
    {
        try { return this.condition != null && this.condition[flag]; }
        catch { return false; }
    }

    // Sensor state per trigger kind. All edge-detected uniformly (false->true
    // fires the one-shot envelope), so states and events share one shape.
    // Public for the canvas kind-12 row (live sensor readout, same
    // truth the engine evaluates).
    public bool QolbarSetLive(int idx)
    {
        try
        {
            if (checkConditionSet == null) ConnectIPC();
            if (checkConditionSet == null) return false;
            return checkConditionSet.InvokeFunc(idx);
        }
        catch { return false; }
    }

    // Sensor truth for the titlebar stamp: state-kind sensors OR with the
    // envelope run so the stamp follows "condition holds", not just the
    // (possibly instant) run. One-shots (15/17) are excluded: their sensor
    // CONSUMES the edge, so only the run state may read them.
    public bool TriggerSensorLive(DynamicPresetData? data, DynamicAnimNode node)
    {
        try
        {
            if (!DynamicAnimNode.SupportsStayHold(node.TriggerKind)) return false;
            if (!GatesSatisfied(data, node)) return false;
            return EvalTriggerSensor(node);
        }
        catch { return false; }
    }

    private bool EvalTriggerSensor(DynamicAnimNode node)
    {
        try
        {
            switch (Math.Clamp(node.TriggerKind, 0, 22))
            {
                case 0:
                {
                    bool wantText = false;
                    try { wantText = ImGui.GetIO().WantTextInput; } catch { }
                    return node.Hotkey != 0 && !wantText && IsKeyDown(node.Hotkey);
                }
                case 1: return Cond(ConditionFlag.InCombat);
                case 2: return Cond(ConditionFlag.Mounted);
                case 3: return Cond(ConditionFlag.Crafting);
                case 4: return Cond(ConditionFlag.Gathering);
                case 5: return IsWeaponUnsheathed();
                case 6: return objectTable.LocalPlayer is { } pd && pd.IsDead;
                case 7:
                    if (objectTable.LocalPlayer is not { } pc) return false;
                    if (!pc.IsCasting) return false;
                    return node.CastActionIds.Count == 0 || node.CastActionIds.Contains((int)pc.CastActionId);
                case 8: return Cond(ConditionFlag.Emoting);
                case 9:
                    if (objectTable.LocalPlayer is not { } ph || ph.MaxHp == 0) return false;
                    float hpp = (float)ph.CurrentHp / ph.MaxHp * 100f;
                    return node.ThresholdRevert ? hpp > node.ThresholdPct : hpp < node.ThresholdPct;
                case 10:
                    if (objectTable.LocalPlayer is not { } pm || pm.MaxMp == 0) return false;
                    float mpp = (float)pm.CurrentMp / pm.MaxMp * 100f;
                    return node.ThresholdRevert ? mpp > node.ThresholdPct : mpp < node.ThresholdPct;
                case 11:
                    if (objectTable.LocalPlayer is not { } ps || ps.StatusList == null) return false;
                    foreach (var s in ps.StatusList)
                    {
                        if (node.StatusIds.Count == 0) return true;
                        if (node.StatusIds.Contains((int)s.StatusId)) return true;
                    }
                    return false;
                case 12:
                {
                    if (getConditionSets == null || checkConditionSet == null) ConnectIPC();
                    if (getConditionSets == null || checkConditionSet == null) return false;
                    var sets = getConditionSets.InvokeFunc();
                    if (sets == null || node.QolbarSet < 0 || node.QolbarSet >= sets.Length) return false;
                    return checkConditionSet.InvokeFunc(node.QolbarSet);
                }
                case 13: return IsFirstPerson();
                case 14: return Cond(ConditionFlag.InThatPosition);
                case 15:
                {
                    uint eid;
                    DateTime et;
                    lock (_emoteLock) { eid = _lastSelfEmoteId; et = _lastSelfEmoteTime; }
                    if (et == DateTime.MinValue) return false;
                    if (!_emoteSeen.TryGetValue(node.Id, out var seenT)) seenT = DateTime.MinValue;
                    if (et <= seenT) return false;
                    _emoteSeen[node.Id] = et;
                    if (node.EmoteIds.Count > 0 && !node.EmoteIds.Contains((int)eid)) return false;
                    return true;
                }
                case 16:
                {
                    var local = objectTable.LocalPlayer;
                    if (local == null) return false;
                    int need = Math.Max(1, node.DeadCount);
                    int n = 0;
                    foreach (var o in objectTable.PlayerObjects)
                    {
                        if (o is not Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter dpc) continue;
                        if (dpc.GameObjectId == local.GameObjectId) continue;
                        if (!dpc.IsDead) continue;
                        if (++n >= need) return true;
                    }
                    return false;
                }
                case 17:
                {
                    lock (_chatLock)
                    {
                        if (_chatFired.Remove(node.Id)) return true;
                    }
                    return false;
                }
                // Location is continuous (distance factor), evaluated outside
                // the edge flow.
                case 18: return false;
                case 20:
                    // Legacy resolution trigger (superseded by the standalone
                    // Resolution node, same semantics).
                    return ResolutionLive(node);
                case 21: return Cond(ConditionFlag.Diving);
                case 22: return Cond(ConditionFlag.InFlight);
                default: return false;
            }
        }
        catch { return false; }
    }

    private static DynamicKeyframe? FindNodeKeyframe(DynamicPresetData data, DynamicAnimNode node)
    {
        var cfg = data.GetConfig(node.Config);
        if (cfg != null)
            foreach (var kf in cfg.Keyframes)
                if (kf.Id == node.KeyframeId) return kf;
        foreach (var c in data.Configs)
            foreach (var kf in c.Keyframes)
                if (kf.Id == node.KeyframeId) return kf;
        return null;
    }

    // Update runs (fire on hotkey edge, prune finished) and build the overlay:
    // ticked settings of bound keyframes with their envelope factors.
    private void BuildTriggerOverlay(
        DynamicPresetData? data,
        DateTime nowUtc,
        out Dictionary<string, (DynamicUniformValue? From, DynamicUniformValue To, float F)> uni,
        out Dictionary<string, (bool S, float F)> tog,
        out HashSet<string> envU,
        out HashSet<string> envT)
    {
        uni = new Dictionary<string, (DynamicUniformValue?, DynamicUniformValue, float)>(StringComparer.OrdinalIgnoreCase);
        tog = new Dictionary<string, (bool, float)>(StringComparer.OrdinalIgnoreCase);
        // Keys owned by active trigger envelopes this frame. Ambient layers
        // (location, wall, band) fill only unclaimed keys, so one-shot
        // events punch over ambient blends instead of losing to them.
        envU = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        envT = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Ambient full intent (claimed keys included) + keys whose runs are
        // releasing this frame. The merge lands releases on ambient value
        // so handoffs back to zones glide instead of cutting.
        _ambU.Clear();
        _ambT.Clear();
        _midU.Clear();
        _releaseU.Clear();
        if (data == null || !data.Enabled) { _triggerStarts.Clear(); _triggerWasDown.Clear(); _pinWasUp.Clear(); _phasePings.Clear(); _holdRelease.Clear(); _bandLive.Clear(); _bandShown.Clear(); _bandLastTick.Clear(); _bandFrom.Clear(); _locFrom.Clear(); _locRel.Clear(); _trigWinner.Clear(); _xfade.Clear(); _retriggerFrom.Clear(); _runF.Clear(); _runLastTick.Clear(); _runEndAt.Clear(); _runReleasing.Clear(); return; }
        SyncChatCommands(data);
        var seen = new HashSet<string>();
        var winU = new Dictionary<string, (string Node, DateTime Since, DynamicUniformValue? From, DynamicUniformValue To, float F, bool Rel)>(StringComparer.OrdinalIgnoreCase);
        var winT = new Dictionary<string, (DateTime Since, bool S, float F)>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in data.Nodes)
        {
            if (!IsTriggerNode(node)) continue;
            seen.Add(node.Id);
            // Gated triggers can't fire while any gate source is OFF.
            // Active runs finish naturally; only new edges are blocked.
            bool down;
            if (node.TriggerKind == 19)
            {
                // Pin activation: the in-pin gate state IS the sensor.
                // Needs at least one incoming edge; rising edge fires.
                bool gateNow = HasIncomingPins(data, node.Id) && GatesSatisfied(data, node);
                bool wasUp = _pinWasUp.TryGetValue(node.Id, out bool pu) && pu;
                _pinWasUp[node.Id] = gateNow;
                down = gateNow && !wasUp;
            }
            else down = GatesSatisfied(data, node) && EvalTriggerSensor(node);
            bool was = _triggerWasDown.TryGetValue(node.Id, out bool w) && w;
            _triggerWasDown[node.Id] = down;
            if (down && !was)
            {
                _runReleasing.Remove(node.Id);
                // Retriggering an active run keeps both factor AND blend-from
                // untouched: output is already lerp(From, peak, f), so keeping
                // f resumes the ramp at the live rate with remaining time
                // proportional to distance. Capturing live as a new From here
                // would double-apply f (live already contains f) and jump
                // toward peak. Live-capture is only for grace attacks, which
                // restart at f == 0 and need a snap-free From.
                string edgeMode;
                if (_triggerStarts.ContainsKey(node.Id))
                {
                    edgeMode = "retrigger";
                }
                else if (node.FadeOutSec > 0
                    && _runEndAt.TryGetValue(node.Id, out var _ret)
                    && (nowUtc - _ret).TotalSeconds <= node.FadeOutSec)
                {
                    // Grace: re-fired shortly after the last run ended, so
                    // attack from live instead of punching from authored,
                    // with time proportional to the distance left.
                    edgeMode = "grace";
                    SeedProportionalAttack(node, FindNodeKeyframe(data, node), out var graceFrom, out var graceF0);
                    _retriggerFrom[node.Id] = graceFrom;
                    _runF[node.Id] = graceF0;
                }
                else
                {
                    edgeMode = "fresh";
                    _retriggerFrom.Remove(node.Id);
                    _runF[node.Id] = 0f;
                    // Takeover: another run already owns shared keys, so the
                    // attack starts from the live screen instead of punching
                    // from authored (otherwise the takeover dips through the
                    // start value first). Idle FX still punches normally.
                    // Live takeover: any driver (run, zone, door, band) already
                    // holds a ticked key, so the attack starts from the live
                    // screen with proportional time instead of punching from
                    // authored. Idle FX still punches normally below.
                    try
                    {
                        var kfn = FindNodeKeyframe(data, node);
                        bool live = false;
                        if (kfn != null)
                        {
                            foreach (var tuk in kfn.TickedUniforms)
                            {
                                int tsep = tuk.IndexOf('\0');
                                if (tsep < 0) continue;
                                if (FxLive(tuk.Substring(0, tsep), tuk)) { live = true; break; }
                            }
                        }
                        if (live)
                        {
                            SeedProportionalAttack(node, kfn, out var takeFrom, out var takeF0);
                            _retriggerFrom[node.Id] = takeFrom;
                            _runF[node.Id] = takeF0;
                        }
                    }
                    catch { }
                }
                try
                {
                    var kf0 = FindNodeKeyframe(data, node);
                    string dbg = "unbound";
                    if (kf0 != null)
                    {
                        var tu0 = kf0.TickedUniforms.FirstOrDefault();
                        if (tu0 != null)
                        {
                            int ssep0 = tu0.IndexOf('\0');
                            string df0 = ssep0 < 0 ? "" : tu0.Substring(0, ssep0);
                            string du0 = ssep0 < 0 ? tu0 : tu0.Substring(ssep0 + 1);
                            string liveV = _prevMirrored.TryGetValue(tu0, out var _lv) ? _lv : "(none)";
                            string authV = "(none)";
                            if (node.StartValues.TryGetValue(df0, out var _sm0) && _sm0.TryGetValue(du0, out var _sv0))
                                authV = _sv0.Value;
                            dbg = $"{df0}|{du0} live={liveV} authored={authV}";
                        }
                        else dbg = "no-ticks";
                    }
                    Service.Log.Debug($"[ReshadeController:{DynamicCanvasWindow.BuildTag}] edge node={node.NodeNum} mode={edgeMode} {dbg}");
                }
                catch { }
                _triggerStarts[node.Id] = nowUtc;
                _runLastTick[node.Id] = nowUtc;
                _phasePings.Remove(node.Id); // fresh run re-earns its phases
                _holdRelease.Remove(node.Id); // stale hold release (retrigger)
                if (node.TriggerKind == 19) RunPinAction(node);
            }
            if (!_triggerStarts.TryGetValue(node.Id, out var start)) continue;
            // Unbound nodes dry-run: timers, phases and titlebar still
            // track (so they work as timing relays); only value-driving
            // needs a keyframe.
            var kf = FindNodeKeyframe(data, node);
            double elapsed = (nowUtc - start).TotalSeconds;
            float dly = Math.Max(0, node.DelaySec);
            float fi = Math.Max(0, node.FadeInSec);
            float st = Math.Max(0, node.StaySec);
            float fo = Math.Max(0, node.FadeOutSec);
            if (elapsed < dly && dly > 0)
            {
                // Windup writes nothing: the live world (zones, other runs,
                // base) shows through untouched, so nothing can freeze
                // mid-change. The attack seed below refreshes every frame so
                // it starts from wherever the screen is when the delay ends
                // (live), or punches authored when idle.
                if (kf != null)
                {
                    bool liveD = false;
                    foreach (var uk in kf.TickedUniforms)
                    {
                        int sep = uk.IndexOf('\0');
                        if (sep < 0) continue;
                        if (FxLive(uk.Substring(0, sep), uk)) { liveD = true; break; }
                    }
                    if (liveD)
                    {
                        SeedProportionalAttack(node, kf, out var dFrom, out var dF0);
                        _retriggerFrom[node.Id] = dFrom;
                        _runF[node.Id] = dF0;
                    }
                    else
                    {
                        _retriggerFrom.Remove(node.Id);
                        _runF[node.Id] = 0f;
                    }
                }
                // Tick the clock through the delay: without this the first
                // attack frame eats the whole delay as one clamped step and
                // the fade-in opens with a jump.
                _runLastTick[node.Id] = nowUtc;
                continue;
            }
            PingPhase(node.Id, "delay");
            double t2 = elapsed - dly;
            // Stateful factor: integrates toward phase targets at timer
            // rates, so hold-release and retrigger resume from live.
            float rdt = 0f;
            if (_runLastTick.TryGetValue(node.Id, out var rlt)) rdt = (float)(nowUtc - rlt).TotalSeconds;
            if (rdt < 0f) rdt = 0f;
            if (rdt > 0.5f) rdt = 0.5f;
            _runLastTick[node.Id] = nowUtc;
            float f = _runF.TryGetValue(node.Id, out var rcf) ? Math.Clamp(rcf, 0f, 1f) : 0f;
            // Hold-mode reads its sensor up front: dropping mid-fadein
            // aborts straight to release instead of finishing the ramp.
            bool holdKind = DynamicAnimNode.SupportsStayHold(node.TriggerKind) && node.StayWhilePresent;
            bool holdLive = holdKind && (node.TriggerKind == 19
                ? (HasIncomingPins(data, node.Id) && GatesSatisfied(data, node))
                : EvalTriggerSensor(node));
            if (fi > 0 && t2 < fi && (!holdKind || holdLive))
            {
                f = Math.Min(1f, f + rdt / fi);
            }
            else if (holdKind)
            {
                // Hold ramps toward peak at fade-in rate (never jumps to
                // it); release ramps down from live at fade-out rate.
                PingPhase(node.Id, "fadein");
                if (holdLive)
                {
                    _holdRelease.Remove(node.Id);
                    f = fi <= 0f ? 1f : Math.Min(1f, f + rdt / fi);
                }
                else
                {
                    // Stay completes when the hold ends (same meaning as the
                    // timed path, where "stay" pings as fade-out begins), so
                    // downstream stay pins fire on release, not on grab.
                    // Guarded on the fade-in window: aborting mid-fade-in
                    // completed neither phase, so it pings nothing.
                    if (t2 >= fi) PingPhase(node.Id, "stay");
                    if (!_holdRelease.TryGetValue(node.Id, out var rel)) _holdRelease[node.Id] = rel = nowUtc;
                    if (fo <= 0f)
                    {
                        PingPhase(node.Id, "fadeout");
                        _triggerStarts.Remove(node.Id);
                        _holdRelease.Remove(node.Id);
                        _retriggerFrom.Remove(node.Id);
                        _runF.Remove(node.Id);
                        _runLastTick.Remove(node.Id);
                        _runReleasing.Remove(node.Id);
                        _runEndAt[node.Id] = nowUtc;
                        continue;
                    }
                    _runReleasing.Add(node.Id);
                    f = Math.Max(0f, f - rdt / fo);
                    if (f <= 0f)
                    {
                        PingPhase(node.Id, "fadeout");
                        _triggerStarts.Remove(node.Id);
                        _holdRelease.Remove(node.Id);
                        _retriggerFrom.Remove(node.Id);
                        _runF.Remove(node.Id);
                        _runLastTick.Remove(node.Id);
                        _runReleasing.Remove(node.Id);
                        _runEndAt[node.Id] = nowUtc;
                        continue;
                    }
                }
            }
            else
            {
                PingPhase(node.Id, "fadein");
                t2 -= fi;
                if (t2 < st) f = fi <= 0f ? 1f : Math.Min(1f, f + rdt / fi);
                else
                {
                    PingPhase(node.Id, "stay");
                    t2 -= st;
                    if (fo <= 0f) { PingPhase(node.Id, "fadeout"); _triggerStarts.Remove(node.Id); _retriggerFrom.Remove(node.Id); _runF.Remove(node.Id); _runLastTick.Remove(node.Id); _runReleasing.Remove(node.Id); _runEndAt[node.Id] = nowUtc; continue; }
                    _runReleasing.Add(node.Id);
                    f = Math.Max(0f, f - rdt / fo);
                    if (f <= 0f) { PingPhase(node.Id, "fadeout"); _triggerStarts.Remove(node.Id); _retriggerFrom.Remove(node.Id); _runF.Remove(node.Id); _runLastTick.Remove(node.Id); _runReleasing.Remove(node.Id); _runEndAt[node.Id] = nowUtc; continue; }
                }
            }
            _runF[node.Id] = f;
            // Band clamp: a timed run on a band node can't exceed the band's
            // shown level. Otherwise band exit hands off to a run sitting at
            // peak: a max flash that then ramps down, instead of a monotonic
            // return from the current level.
            if (IsBandNode(node) && _bandShown.TryGetValue(node.Id, out var bsh) && bsh < f) f = bsh;
            if (kf != null)
            {
                foreach (var tk in kf.TickedTechs)
                    if (f > 0 && kf.TechStates.TryGetValue(tk, out bool tst))
                        OfferTriggerToggle(winT, start, tk, tst, f);
                foreach (var uk in kf.TickedUniforms)
                {
                    int sep = uk.IndexOf('\0');
                    if (sep < 0) continue;
                    var file = uk.Substring(0, sep);
                    var uname = uk.Substring(sep + 1);
                    if (kf.Uniforms.TryGetValue(file, out var m) && m.TryGetValue(uname, out var uv))
                    {
                        // Attacks punch from authored/retrigger starts. A release
                        // carries its authored start as the landing for keys
                        // with no base carrier and no ambient driver (the
                        // merge lands pooled releases on ambient/base, never
                        // on this carrier).
                        DynamicUniformValue? startUv = null;
                        if (node.StartValues.TryGetValue(file, out var sm) && sm.TryGetValue(uname, out var sv))
                            startUv = sv;
                        // Grace-attack From (live snapshot at the grace edge).
                        // Never applies while releasing: releases always blend
                        // from the live base, otherwise the handoff glides
                        // toward a stale snapshot and snaps to base when the
                        // attack window expires.
                        if (!_runReleasing.Contains(node.Id)
                            && elapsed < dly + fi && _retriggerFrom.TryGetValue(node.Id, out var rmap1)
                            && rmap1.TryGetValue(uk, out var rval1))
                            startUv = new DynamicUniformValue { Value = rval1, BaseType = uv.BaseType };
                        // Colors arrive when idle, glide from live when driven
                        // (see IsColorUniform).
                        if (IsColorUniform(file, uname) && !FxLive(file, uk)) startUv = uv;
                        OfferTriggerUniform(winU, node.Id, start, uk, startUv, uv, f, _runReleasing.Contains(node.Id));
                    }
                }
            }
        }
        foreach (var id in _triggerWasDown.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _triggerWasDown.Remove(id);
            _triggerStarts.Remove(id);
            _emoteSeen.Remove(id);
            _pinWasUp.Remove(id);
            _holdRelease.Remove(id);
            _bandLive.Remove(id);
            _bandShown.Remove(id);
            _bandLastTick.Remove(id);
            _retriggerFrom.Remove(id);
            _runF.Remove(id);
            _runLastTick.Remove(id);
            _runEndAt.Remove(id);
            _runReleasing.Remove(id);
        }
        foreach (var id in _phasePings.Keys.Where(k => !seen.Contains(k)).ToList())
            _phasePings.Remove(id);
        // Newest run wins shared keys: commit the winners. A winner in
        // release lands on ambient at merge time (smooth handoff to
        // zones); attackers run their own course from the takeover frame.
        // Ownership jumping between live runs morphs the cut (screen snap
        // -> winner) instead of cutting; fresh punches and final releases
        // keep their direct paths.
        foreach (var kvp in winU)
        {
            uni[kvp.Key] = (kvp.Value.From, kvp.Value.To, kvp.Value.F);
            if (kvp.Value.Rel) _releaseU.Add(kvp.Key);
            if (_trigWinner.TryGetValue(kvp.Key, out var prevNode) && prevNode != kvp.Value.Node
                && _prevMirrored.TryGetValue(kvp.Key, out var snap))
                _xfade[kvp.Key] = (snap, nowUtc);
            _trigWinner[kvp.Key] = kvp.Value.Node;
        }
        foreach (var k in _trigWinner.Keys.Where(k => !winU.ContainsKey(k)).ToList())
        {
            _trigWinner.Remove(k);
            _xfade.Remove(k);
        }
        foreach (var kvp in winT)
            tog[kvp.Key] = (kvp.Value.S, kvp.Value.F);
        // Freeze this frame's envelope claims before ambient layers run.
        foreach (var k in uni.Keys) envU.Add(k);
        foreach (var k in tog.Keys) envT.Add(k);
        // Location triggers: continuous blend, no envelope or runs. The
        // blend-from is captured once at entry and held for the visit so
        // it can't switch mid-blend. Going quiet (exit, gate, unbind) ramps
        // the contribution out at the slowest zone FadeOut instead of
        // cutting (0 = cut, as before); keys another driver owns are left
        // alone so the handoff can't fight.
        foreach (var node in data.Nodes)
        {
            if (!(IsTriggerNode(node) || IsCoordsNode(node)) || node.TriggerKind != 18) continue;
            var kf = FindNodeKeyframe(data, node);
            bool gated = !GatesSatisfied(data, node);
            float lf = 0;
            bool lsk = false;
            if (!gated && kf != null)
            {
                var lb = LocationBlend(node);
                lf = lb.F;
                lsk = lb.Skirt;
            }
            if (!gated && kf != null && (lf > 0 || lsk))
            {
                if (!_locFrom.TryGetValue(node.Id, out var locMap))
                {
                    locMap = CaptureLocFrom(kf, node);
                    _locFrom[node.Id] = locMap;
                }
                EmitLocationBand(kf, node, uni, tog, lf, lsk, locMap, envU, envT, false, false, true);
                _locRel[node.Id] = (lf, nowUtc);
                continue;
            }
            // Quiet but was contributing: release ramp (needs a keyframe to
            // aim at; otherwise forget it).
            if (kf == null || !_locFrom.TryGetValue(node.Id, out var relMap)
                || !_locRel.TryGetValue(node.Id, out var relSt))
            {
                _locFrom.Remove(node.Id);
                _locRel.Remove(node.Id);
                continue;
            }
            float fo = 0;
            try { foreach (var z in LocZones(node)) fo = Math.Max(fo, Math.Max(0, z.FadeOutSec)); } catch { }
            float rdt = (float)(nowUtc - relSt.Tick).TotalSeconds;
            if (rdt < 0) rdt = 0;
            if (rdt > 0.5f) rdt = 0.5f;
            float rf = fo <= 0 ? 0 : Math.Max(0, relSt.F - rdt / fo);
            if (rf <= 0)
            {
                _locFrom.Remove(node.Id);
                _locRel.Remove(node.Id);
                continue;
            }
            _locRel[node.Id] = (rf, nowUtc);
            EmitLocationBand(kf, node, uni, tog, rf, false, relMap, envU, envT, true, true, true);
        }
        foreach (var id in _locFrom.Keys.Where(k => data.Nodes.All(n => n.Id != k)).ToList())
        {
            _locFrom.Remove(id);
            _locRel.Remove(id);
        }
        // Prune simple-zone fade state for deleted nodes/zones.
        foreach (var k in _locFade.Keys.ToList())
        {
            int sp = k.IndexOf('\0');
            string nid = sp < 0 ? k : k.Substring(0, sp);
            string zid = sp < 0 ? "" : k.Substring(sp + 1);
            var nn = data.Nodes.Find(n => n.Id == nid);
            if (nn == null) { _locFade.Remove(k); continue; }
            bool zoneGone = true;
            try { foreach (var zz in LocZones(nn)) if (zz.Id == zid) { zoneGone = false; break; } }
            catch { zoneGone = false; }
            if (zoneGone) { _locFade.Remove(k); _locVis.Remove(k); _locVisFade.Remove(k); }
        }
    }

    // Wall latch/progress live in memory by node id. Reset on zone change
    // unless the destination is whitelisted (doors chained across loads).
    private uint _wallTerritory;
    // True once any loading/offline state was seen since boot. Tells a
    // hot-enable (straight into a live world) apart from a real boot
    // (loading screens first) for zone-entry actions.
    private bool _sawPauseSinceBoot;
    private DateTime _lastTeleportCast = DateTime.MinValue;
    // Multi-door: one latch/prog per node shared by all its doors; prev
    // player pos stays per node. WallDoors falls back to the legacy
    // node-level fields (stable id "legacy") until the UI migrates them
    // into Doors.
    private const string LegacyDoorId = "legacy";
    private static string WallKey(string nodeId, string doorId)
        => nodeId + "\0" + doorId;
    private static List<DynamicDoor> WallDoors(DynamicAnimNode node)
    {
        try
        {
            if (node.Doors != null && node.Doors.Count > 0) return node.Doors;
        }
        catch { }
        List<uint> kz;
        try { kz = node.KeepZones != null ? new List<uint>(node.KeepZones) : new List<uint>(); }
        catch { kz = new List<uint>(); }
        return new List<DynamicDoor> { new DynamicDoor {
            Id = LegacyDoorId, LocTerritory = node.LocTerritory,
            LocX = node.LocX, LocY = node.LocY, LocZ = node.LocZ,
            BoxYaw = node.BoxYaw, BoxPitch = node.BoxPitch, BoxRoll = node.BoxRoll,
            WallW = node.WallW, WallH = node.WallH, KeepZones = kz } };
    }
    // One latch per node shared by all its doors: crossing any door
    // toggles it, so entering through one and leaving through another
    // turns it on then off like a single doorway with many entrances.
    private static bool WallAnyHome(DynamicAnimNode node, uint wtid)
    {
        try
        {
            // KeepZones is node-wide; home territory is per door.
            if (node.KeepZones.Contains(wtid)) return true;
            foreach (var d in WallDoors(node))
                if (d.LocTerritory != 0 && d.LocTerritory == wtid) return true;
            return false;
        }
        catch { return false; }
    }
    // Frozen live values captured at each wall latch (blend-from for the
    // attack when Force is off — same provenance rule as the time fade).
    private readonly Dictionary<string, Dictionary<string, string>> _wallFrom = new();
    private bool WallFrozenLive(string nodeId, string key)
    {
        try
        {
            if (!_wallFrom.TryGetValue(nodeId, out var map) || !map.ContainsKey(key)) return false;
            return !_prevBaseOnly.TryGetValue(key, out var wbo) || !wbo;
        }
        catch { return false; }
    }
    private void DropWallNode(string id)
    {
        _wallLatched.Remove(id);
        _wallProg.Remove(id);
        _wallPrev.Remove(id);
        _wallFrom.Remove(id);
    }

    private void BuildWallOverlay(
        DynamicPresetData? data,
        Dictionary<string, (DynamicUniformValue? From, DynamicUniformValue To, float F)> uni,
        Dictionary<string, (bool S, float F)> tog,
        HashSet<string>? skipU = null,
        HashSet<string>? skipT = null)
    {
        if (data == null || !data.Enabled)
        {
            _wallLatched.Clear();
            _wallProg.Clear();
            _wallPrev.Clear();
            _wallFrom.Clear();
            _wallLastTick = DateTime.MinValue;
            return;
        }
        var nowUtc = DateTime.UtcNow;
        float dt = 0;
        if (_wallLastTick != DateTime.MinValue)
            dt = (float)(nowUtc - _wallLastTick).TotalSeconds;
        _wallLastTick = nowUtc;
        if (dt < 0) dt = 0;
        if (dt > 0.5f) dt = 0.5f;
        bool loading = false;
        try { loading = this.condition != null && this.condition[ConditionFlag.BetweenAreas]; } catch { }
        System.Numerics.Vector3? cur = null;
        try { cur = this.objectTable.LocalPlayer?.Position; } catch { }
        var seen = new HashSet<string>();
        foreach (var node in data.Nodes)
        {
            if (!IsWallNode(node)) continue;
            seen.Add(node.Id);
            if (loading || cur == null)
            {
                _wallLatched[node.Id] = false;
                _wallProg[node.Id] = 0;
                if (cur != null) _wallPrev[node.Id] = cur.Value;
                continue;
            }
            var curV = cur.Value;
            if (!_wallPrev.TryGetValue(node.Id, out var prev)) { _wallPrev[node.Id] = curV; prev = curV; }
            _wallPrev[node.Id] = curV;
            // Any door crossing toggles the one shared latch.
            bool anyCrossed = false;
            foreach (var d in WallDoors(node))
            {
                // Wall center is stored in world coords so rotation pivots in
                // place (rotating used to swing the door around the origin when
                // the center was stored in the rotated local frame).
                var c = new System.Numerics.Vector3(d.LocX, d.LocY, d.LocZ);
                float w = Math.Max(0.5f, d.WallW);
                float h = Math.Max(0.5f, d.WallH);
                var lp0 = EulerBox.Rotate(prev - c, d.BoxYaw, d.BoxPitch, d.BoxRoll, true);
                var lp1 = EulerBox.Rotate(curV - c, d.BoxYaw, d.BoxPitch, d.BoxRoll, true);
                float segLen = (curV - prev).Length();
                if (segLen <= 25f)
                {
                    if ((lp0.Z < 0 && lp1.Z >= 0) || (lp0.Z > 0 && lp1.Z <= 0))
                    {
                        float denom = Math.Abs(lp0.Z) + Math.Abs(lp1.Z);
                        if (denom > 0.0001f)
                        {
                            float t = Math.Abs(lp0.Z) / denom;
                            float qx = lp0.X + (lp1.X - lp0.X) * t;
                            float qy = lp0.Y + (lp1.Y - lp0.Y) * t;
                            if (Math.Abs(qx) <= w / 2 && Math.Abs(qy) <= h / 2) { anyCrossed = true; break; }
                        }
                    }
                }
            }
            if (!_wallLatched.TryGetValue(node.Id, out bool latched)) latched = false;
            bool gateOk = GatesSatisfied(data, node);
            if (!latched && anyCrossed && gateOk)
            {
                // Rising edge: freeze live screen values for the attack
                // blend (used when this key isn't forced to authored).
                try { _wallFrom[node.Id] = new Dictionary<string, string>(_prevMirrored, StringComparer.OrdinalIgnoreCase); }
                catch { }
            }
            // Gated doors can't latch: crossings ignored and the latch
            // forced off so progress fades out at the normal rate.
            if (!gateOk) latched = false;
            else if (anyCrossed) latched = !latched;
            _wallLatched[node.Id] = latched;
            if (!_wallProg.TryGetValue(node.Id, out float prog)) prog = 0;
            float target = latched ? 1 : 0;
            float rate = latched
                ? (node.FadeInSec > 0 ? dt / node.FadeInSec : 999f)
                : (node.FadeOutSec > 0 ? dt / node.FadeOutSec : 999f);
            if (prog < target) prog = Math.Min(target, prog + rate);
            else if (prog > target) prog = Math.Max(target, prog - rate);
            _wallProg[node.Id] = prog;
            if (prog <= 0) continue;
            var kf = FindNodeKeyframe(data, node);
            if (kf == null) continue;
            foreach (var tk in kf.TickedTechs)
                if (kf.TechStates.TryGetValue(tk, out bool tst))
                {
                    if (latched) _ambT[tk] = (tst, prog);
                    if (skipT == null || !skipT.Contains(tk))
                        tog[tk] = (tst, prog);
                }
            foreach (var uk in kf.TickedUniforms)
            {
                int sep = uk.IndexOf('\0');
                if (sep < 0) continue;
                // Unlatch releases land on ambient at merge time (owned keys
                // only — envelope-owned keys follow the envelope's state).
                if (!latched && (skipU == null || !skipU.Contains(uk))) _releaseU.Add(uk);
                var file = uk.Substring(0, sep);
                var uname = uk.Substring(sep + 1);
                if (kf.Uniforms.TryGetValue(file, out var m) && m.TryGetValue(uname, out var uv))
                {
                    // Same rule as the time-chain fade: an idle FX (or no
                    // live history) attacks from the authored start;
                    // otherwise the attack glides from the frozen live
                    // values. Releases always hand back to the live base.
                    DynamicUniformValue? startUv = null;
                    bool idle = !FxLive(file, uk);
                    if (latched
                        && node.StartValues.TryGetValue(file, out var sm) && sm.TryGetValue(uname, out var sv)
                        && (idle || !WallFrozenLive(node.Id, uk)))
                    {
                        startUv = sv;
                    }
                    else if (latched && !idle && _wallFrom.TryGetValue(node.Id, out var wmap)
                        && wmap.TryGetValue(uk, out var wval))
                    {
                        startUv = new DynamicUniformValue { Value = wval, BaseType = uv.BaseType };
                    }
                    else if (!latched
                        && node.StartValues.TryGetValue(file, out var sm2) && sm2.TryGetValue(uname, out var sv2))
                    {
                        // Release carrier for carrier-less keys (see above).
                        startUv = sv2;
                    }
                    // Colors arrive when idle, glide from live when driven
                    // (see IsColorUniform; idle is already computed above).
                    if (idle && IsColorUniform(file, uname)) startUv = uv;
                    if (latched) _ambU[uk] = (startUv, uv, prog);
                    if (skipU == null || !skipU.Contains(uk))
                        uni[uk] = (startUv, uv, prog);
                }
            }
        }
        foreach (var id in _wallLatched.Keys.Where(k => !seen.Contains(k.Split('\0')[0])).ToList())
            DropWallNode(id);
    }

    // HP/MP band mode (Start != Max): continuous factor like location
    // blends, reusing the same overlay shape. Binary (equal) nodes keep
    // the legacy timed envelope and skip this entirely.
    private void BuildBandOverlay(
        DynamicPresetData? data,
        Dictionary<string, (DynamicUniformValue? From, DynamicUniformValue To, float F)> uni,
        Dictionary<string, (bool S, float F)> tog,
        HashSet<string>? skipU = null,
        HashSet<string>? skipT = null)
    {
        if (data == null || !data.Enabled) return;
        foreach (var node in data.Nodes)
        {
            if (!IsTriggerNode(node)) continue;
            if (node.TriggerKind != 9 && node.TriggerKind != 10) continue;
            if (Math.Abs(node.ThresholdPct - node.ThresholdMax) < 0.0001f) continue;
            float pct;
            try
            {
                var p = objectTable.LocalPlayer;
                if (node.TriggerKind == 9)
                {
                    if (p == null || p.MaxHp == 0) { _bandLive.Remove(node.Id); continue; }
                    pct = (float)p.CurrentHp / p.MaxHp * 100f;
                }
                else
                {
                    if (p == null || p.MaxMp == 0) { _bandLive.Remove(node.Id); continue; }
                    pct = (float)p.CurrentMp / p.MaxMp * 100f;
                }
            }
            catch { _bandLive.Remove(node.Id); _bandFrom.Remove(node.Id); continue; }
            float denom = node.ThresholdMax - node.ThresholdPct;
            float target = Smooth01(Math.Clamp((pct - node.ThresholdPct) / denom, 0f, 1f));
            // Slew toward the target at envelope-timer rates (0 = snap).
            var nowB = DateTime.UtcNow;
            float dt = 0f;
            if (_bandLastTick.TryGetValue(node.Id, out var lt)) dt = (float)(nowB - lt).TotalSeconds;
            _bandLastTick[node.Id] = nowB;
            if (dt < 0) dt = 0;
            if (dt > 0.5f) dt = 0.5f;
            float prevShown = _bandShown.TryGetValue(node.Id, out var ps) ? ps : 0f;
            float shown = _bandShown.TryGetValue(node.Id, out var s) ? s : target;
            if (target > shown)
                shown = node.FadeInSec <= 0 ? target : Math.Min(target, shown + dt / node.FadeInSec);
            else if (target < shown)
                shown = node.FadeOutSec <= 0 ? target : Math.Max(target, shown - dt / node.FadeOutSec);
            _bandShown[node.Id] = shown;
            _bandLive[node.Id] = shown;
            if (shown <= 0) { _bandFrom.Remove(node.Id); continue; }
            var kf = FindNodeKeyframe(data, node);
            if (kf == null) { _bandFrom.Remove(node.Id); continue; }
            if (prevShown <= 0 && !_bandFrom.TryGetValue(node.Id, out var bandMap))
            {
                bandMap = CaptureLocFrom(kf, node);
                _bandFrom[node.Id] = bandMap;
            }
            else _bandFrom.TryGetValue(node.Id, out bandMap);
            // Falling bands land on ambient at merge time (owned keys only).
            if (shown < prevShown)
                foreach (var buk in kf.TickedUniforms)
                    if (skipU == null || !skipU.Contains(buk)) _releaseU.Add(buk);
            EmitLocationBand(kf, node, uni, tog, shown, false, bandMap, skipU, skipT, shown < prevShown);
        }
        foreach (var id in _bandLive.Keys.Where(k => data.Nodes.All(n => n.Id != k)).ToList())
        {
            _bandLive.Remove(id);
            _bandFrom.Remove(id);
        }
    }

    // Timer node state, keyed by node id (gate edge memory) and
    // "nodeId\0timerId" (per-timer start time + output level).
    private readonly Dictionary<string, bool> _timerGateWas = new();
    private readonly Dictionary<string, DateTime> _timerStart = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _timerLevel = new(StringComparer.Ordinal);
    // StaySec == 0 means "activation ping": the output holds ON this long
    // so downstream edge detectors see at least a few frames of it.
    private const float TimerPingSec = 0.25f;

    private static string TimerKey(string nodeId, string timerId)
        => nodeId + "\0" + timerId;

    public bool TimerOutputOn(string nodeId, string timerId)
    {
        try { return _timerLevel.TryGetValue(TimerKey(nodeId, timerId), out bool on) && on; }
        catch { return false; }
    }

    private bool TimerAnyOut(string nodeId)
    {
        try
        {
            string pfx = nodeId + "\0";
            foreach (var kvp in _timerLevel)
                if (kvp.Key.StartsWith(pfx, StringComparison.Ordinal) && kvp.Value) return true;
            return false;
        }
        catch { return false; }
    }

    public bool IsTimerActive(string nodeId)
    {
        try
        {
            string pfx = nodeId + "\0";
            foreach (var kvp in _timerLevel)
                if (kvp.Key.StartsWith(pfx, StringComparison.Ordinal) && kvp.Value) return true;
            foreach (var kvp in _timerStart)
                if (kvp.Key.StartsWith(pfx, StringComparison.Ordinal)) return true;
            return false;
        }
        catch { return false; }
    }

    // Timer nodes: a rising edge on the in-pin gate (needs at least one
    // incoming edge, like pin-activation triggers) starts every timer on
    // the node. Each output goes ON DelaySec after the edge and OFF
    // StaySec later. Pure pin levels — no uniforms, no toggles.
    private void BuildTimerOverlay(DynamicPresetData? data, DateTime nowUtc)
    {
        if (data == null || !data.Enabled)
        {
            _timerGateWas.Clear();
            _timerStart.Clear();
            _timerLevel.Clear();
            return;
        }
        var seen = new HashSet<string>();
        foreach (var node in data.Nodes)
        {
            if (!IsTimerNode(node)) continue;
            seen.Add(node.Id);
            bool gateNow = HasIncomingPins(data, node.Id) && GatesSatisfied(data, node);
            bool wasUp = _timerGateWas.TryGetValue(node.Id, out bool w) && w;
            _timerGateWas[node.Id] = gateNow;
            if (gateNow && !wasUp)
            {
                // Starter ping: (re)start every timer from now.
                foreach (var t in node.Timers)
                    _timerStart[TimerKey(node.Id, t.Id)] = nowUtc;
            }
            else if (!gateNow && node.StopIfStarterOff)
            {
                // Starter dropped with Stop armed: cancel everything.
                string pfx = node.Id + "\0";
                foreach (var k in _timerStart.Keys.Where(k => k.StartsWith(pfx, StringComparison.Ordinal)).ToList())
                    _timerStart.Remove(k);
                foreach (var k in _timerLevel.Keys.Where(k => k.StartsWith(pfx, StringComparison.Ordinal)).ToList())
                    _timerLevel.Remove(k);
            }
            foreach (var t in node.Timers)
            {
                string key = TimerKey(node.Id, t.Id);
                if (!_timerStart.TryGetValue(key, out var start))
                {
                    _timerLevel.Remove(key);
                    continue;
                }
                float delay = Math.Max(0, t.DelaySec);
                float stay = Math.Max(0, t.StaySec);
                double elapsed = (nowUtc - start).TotalSeconds;
                if (elapsed < delay)
                {
                    _timerLevel[key] = false;
                    continue;
                }
                double window = stay > 0 ? delay + stay : delay + TimerPingSec;
                if (elapsed <= window)
                {
                    _timerLevel[key] = true;
                    continue;
                }
                _timerStart.Remove(key);
                _timerLevel.Remove(key);
            }
        }
        foreach (var id in _timerGateWas.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _timerGateWas.Remove(id);
            string pfx = id + "\0";
            foreach (var k in _timerStart.Keys.Where(k => k.StartsWith(pfx, StringComparison.Ordinal)).ToList())
                _timerStart.Remove(k);
            foreach (var k in _timerLevel.Keys.Where(k => k.StartsWith(pfx, StringComparison.Ordinal)).ToList())
                _timerLevel.Remove(k);
        }
    }

    // Preset nodes: a rising edge on the in-pin gate (needs at least one
    // incoming edge, like pin-activation triggers) switches ReShade to the
    // node's preset once. No action on release (sticky until something
    // else switches).
    private readonly Dictionary<string, bool> _presetGateWas = new();

    private void BuildPresetOverlay(DynamicPresetData? data)
    {
        if (data == null || !data.Enabled)
        {
            _presetGateWas.Clear();
            return;
        }
        var seen = new HashSet<string>();
        foreach (var node in data.Nodes)
        {
            if (!IsPresetNode(node)) continue;
            seen.Add(node.Id);
            bool gateNow = HasIncomingPins(data, node.Id) && GatesSatisfied(data, node);
            bool wasUp = _presetGateWas.TryGetValue(node.Id, out bool w) && w;
            _presetGateWas[node.Id] = gateNow;
            if (gateNow && !wasUp && !string.IsNullOrEmpty(node.PresetPath))
            {
                try { configWindow.ApplyPresetPath(node.PresetPath); } catch { }
            }
        }
        foreach (var id in _presetGateWas.Keys.Where(k => !seen.Contains(k)).ToList())
            _presetGateWas.Remove(id);
    }

    private void EmitLocationBand(
        DynamicKeyframe kf,
        DynamicAnimNode node,
        Dictionary<string, (DynamicUniformValue? From, DynamicUniformValue To, float F)> uni,
        Dictionary<string, (bool S, float F)> tog,
        float f,
        bool skirt,
        Dictionary<string, DynamicUniformValue?>? fromMap,
        HashSet<string>? skipU = null,
        HashSet<string>? skipT = null,
        bool releasing = false,
        bool yieldOccupied = false,
        bool useMid = false)
    {
        foreach (var tk in kf.TickedTechs)
        {
            // Skirt feathers uniforms only; techniques follow the feather so
            // they don't pop at the skirt/main boundary (values there are
            // ~base, so the early enable is invisible).
            if (yieldOccupied && tog.ContainsKey(tk)) continue;
            if (kf.TechStates.TryGetValue(tk, out bool tst))
            {
                // A releasing layer isn't a landing target (landing on a
                // falling value would double-apply the fade).
                if (!releasing) _ambT[tk] = (tst, f);
                if (skipT == null || !skipT.Contains(tk))
                    tog[tk] = (tst, f);
            }
        }
        foreach (var uk in kf.TickedUniforms)
        {
            int sep = uk.IndexOf('\0');
            if (sep < 0) continue;
            if (yieldOccupied && uni.ContainsKey(uk)) continue;
            // Blend-from held since visit entry (null = live base). Without
            // a map (defensive), glide from live.
            DynamicUniformValue? startUv = null;
            if (fromMap != null && fromMap.TryGetValue(uk, out var fv)) startUv = fv;
            var file = uk.Substring(0, sep);
            var uname = uk.Substring(sep + 1);
            if (kf.Uniforms.TryGetValue(file, out var m) && m.TryGetValue(uname, out var uv))
            {
                // Colors arrive when idle, glide from live when driven
                // (see IsColorUniform).
                if (IsColorUniform(file, uname) && !FxLive(file, uk)) startUv = uv;
                if (!releasing) _ambU[uk] = (startUv, uv, f);
                if (skipU == null || !skipU.Contains(uk))
                {
                    uni[uk] = skirt ? (null, startUv ?? uv, f) : (startUv, uv, f);
                    // Midpoint shaping (location only, non-colors): recorded
                    // only when actually driving, so it can never bend a
                    // trigger-owned curve.
                    if (useMid && node.UseMidpoint && !IsColorUniform(file, uname)
                        && node.MidValues.TryGetValue(file, out var mm) && mm.TryGetValue(uname, out var mv))
                        _midU[uk] = mv;
                }
            }
        }
    }

    // Location zones for a node, falling back to the legacy node-level
    // fields (stable id "legacy") until the UI migrates them into Zones.
    private static List<DynamicZone> LocZones(DynamicAnimNode node)
    {
        try
        {
            if (node.Zones != null && node.Zones.Count > 0) return node.Zones;
        }
        catch { }
        return new List<DynamicZone> { new DynamicZone {
            Id = LegacyDoorId, LocTerritory = node.LocTerritory,
            LocX = node.LocX, LocY = node.LocY, LocZ = node.LocZ,
            LocShape = node.LocShape, RadiusStart = node.RadiusStart, RadiusMax = node.RadiusMax,
            BoxOX = node.BoxOX, BoxOY = node.BoxOY, BoxOZ = node.BoxOZ,
            BoxIX = node.BoxIX, BoxIY = node.BoxIY, BoxIZ = node.BoxIZ,
            BoxOCX = node.BoxOCX, BoxOCY = node.BoxOCY, BoxOCZ = node.BoxOCZ,
            BoxYaw = node.BoxYaw, BoxPitch = node.BoxPitch, BoxRoll = node.BoxRoll } };
    }

    // Blend factor + skirt flag for one zone (sphere or box).
    private static (float F, bool Skirt) ZoneBlend(DynamicZone z, System.Numerics.Vector3 tpos, uint territory)
    {
        try
        {
            if (z.LocTerritory != 0 && z.LocTerritory != territory) return (0, false);
            // Simple shapes: binary in/out with no falloff — a 3D door
            // trigger that stays on while inside.
            if (z.LocShape == 2)
            {
                float qx = tpos.X - z.LocX;
                float qy = tpos.Y - z.LocY;
                float qz = tpos.Z - z.LocZ;
                float qd = MathF.Sqrt(qx * qx + qy * qy + qz * qz);
                return (qd <= Math.Max(0, z.RadiusMax) ? 1 : 0, false);
            }
            if (z.LocShape == 3)
            {
                var qd = new System.Numerics.Vector3(
                    tpos.X - z.LocX,
                    tpos.Y - z.LocY,
                    tpos.Z - z.LocZ);
                var ql = EulerBox.Rotate(qd, z.BoxYaw, z.BoxPitch, z.BoxRoll, true);
                bool inside = Math.Abs(ql.X) <= Math.Max(0, z.BoxIX)
                    && Math.Abs(ql.Y) <= Math.Max(0, z.BoxIY)
                    && Math.Abs(ql.Z) <= Math.Max(0, z.BoxIZ);
                return (inside ? 1 : 0, false);
            }
            if (z.LocShape == 1)
            {
                float ax(float d, float o, float i)
                {
                    d = Math.Abs(d); o = Math.Max(0, o); i = Math.Max(0, i);
                    if (o <= i) return d <= i ? 1 : 0;
                    if (d >= o) return 0;
                    if (d <= i) return 1;
                    return 1 - Smooth01((d - i) / (o - i));
                }
                // Box axis with its own outer center: 1 inside inner,
                // 0 outside outer, smooth between (continuous at both faces).
                float bax(float x, float off, float o, float i)
                {
                    o = Math.Max(0, o); i = Math.Max(0, i);
                    float lo = off - o, hi = off + o;
                    if (x < lo || x > hi) return 0;
                    if (x >= -i && x <= i) return 1;
                    if (x > i)
                    {
                        float g = hi - i;
                        if (g <= 0) return 0;
                        return 1 - Smooth01((x - i) / g);
                    }
                    float g2 = -i - lo;
                    if (g2 <= 0) return 0;
                    return 1 - Smooth01(((-i) - x) / g2);
                }
                var dl = new System.Numerics.Vector3(
                    tpos.X - z.LocX,
                    tpos.Y - z.LocY,
                    tpos.Z - z.LocZ);
                var local = EulerBox.Rotate(dl, z.BoxYaw, z.BoxPitch, z.BoxRoll, true);
                var offL = EulerBox.Rotate(
                    new System.Numerics.Vector3(z.BoxOCX, z.BoxOCY, z.BoxOCZ),
                    z.BoxYaw, z.BoxPitch, z.BoxRoll, true);
                float f = Math.Min(bax(local.X, offL.X, z.BoxOX, z.BoxIX),
                    Math.Min(bax(local.Y, offL.Y, z.BoxOY, z.BoxIY),
                             bax(local.Z, offL.Z, z.BoxOZ, z.BoxIZ)));
                return (f, false);
            }
            float dx = tpos.X - z.LocX;
            float dy = tpos.Y - z.LocY;
            float dz = tpos.Z - z.LocZ;
            float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            float rs = Math.Max(0, z.RadiusStart);
            float rm = Math.Max(0, z.RadiusMax);
            if (rs <= rm) return (dist <= rm ? 1 : 0, false);
            // Onset exactly at RadiusStart, full at RadiusMax, nothing
            // outside: the old feather band (onset at Start+skirt) contradicted
            // what the two rows promise. Authored attacks punch at Start like
            // trigger punches do; live attacks glide from the base.
            float f2 = dist <= rm ? 1 : 1 - Smooth01((dist - rm) / (rs - rm));
            if (f2 <= 0) return (0, false);
            return (f2, false);
        }
        catch { return (0, false); }
    }

    // Blend factor (+ unused skirt flag, kept for shape) for location
    // nodes: the max over all zones, so one node can cover several areas
    // (mixed shapes welcome).
    // Camera-visibility debounce per zone (zone key -> shown/pending/since):
    // frustum edges flicker during pans, so a new state must hold still for
    // a settle window before it takes effect. Time-based (not frame-counted)
    // because several callers sample the blend every frame.
    private readonly Dictionary<string, (bool Shown, bool Pending, DateTime Since)> _locVis = new(StringComparer.Ordinal);
    private const int LocVisSettleMs = 50;

    // Camera frustum test for one world point (spheres only upstream),
    // clipped to the configured visible play area (game-window fractions).
    // Degenerate bounds or unknown window size fall back to full-window.
    private static bool CamSeesPoint(System.Numerics.Vector3 p, float L, float T, float R, float B)
    {
        try
        {
            var gg = Service.GameGui;
            if (gg == null) return true;
            if (!(L < R && T < B)) { L = 0; T = 0; R = 1; B = 1; }
            if (!gg.WorldToScreen(p, out var sp, out _)) return false;
            if (!TryGetGameClientSize(out int w, out int h)) return true;
            float nx = sp.X / w;
            float ny = sp.Y / h;
            return nx >= L && nx <= R && ny >= T && ny <= B;
        }
        catch { return true; }
    }

    // Coverage-fraction smoothing per zone (key -> current + tick).
    private readonly Dictionary<string, (float F, DateTime Tick)> _locVisFade = new(StringComparer.Ordinal);

    // Visibility amount for sphere zones (0..1): binary gate, or the
    // visible fraction of the RadiusMax sphere when coverage is on.
    // A zero reduction disables the gate shell (always 1, no projection).
    private float ZoneVisAmount(string nodeId, DynamicZone z)
    {
        try
        {
            if (z.LocVisMaxReduction <= 0) return 1f;
            if (z.LocVisMode != 2 || !z.LocVisCoverage)
                return ZoneVisible(nodeId, z) ? 1f : 0f;
            var cfg = this.Config;
            float L = cfg.ViewAreaLeft, T = cfg.ViewAreaTop, R = cfg.ViewAreaRight, B = cfg.ViewAreaBottom;
            var c = new System.Numerics.Vector3(z.LocX, z.LocY, z.LocZ);
            float r = Math.Max(0, z.RadiusMax);
            float target;
            var cam = CameraPosition();
            if (cam != null && System.Numerics.Vector3.Distance(cam.Value, c) <= r) target = 1f;
            else
            {
                // Fibonacci lattice over the sphere; fraction in frustum.
                const int M = 32;
                double golden = Math.PI * (3.0 - Math.Sqrt(5.0));
                int inside = 0;
                for (int i = 0; i < M; i++)
                {
                    double y = 1.0 - (i / (double)(M - 1)) * 2.0;
                    double rr = Math.Sqrt(Math.Max(0, 1 - y * y));
                    double th = golden * i;
                    var p = c + r * new System.Numerics.Vector3((float)(Math.Cos(th) * rr), (float)y, (float)(Math.Sin(th) * rr));
                    if (CamSeesPoint(p, L, T, R, B)) inside++;
                }
                target = (float)inside / M;
            }
            // Glide the fraction so edge crossings sweep instead of stepping.
            string fkey = nodeId + "\0" + z.Id;
            var nowF = DateTime.UtcNow;
            float dtF = 0f;
            float curF = target;
            if (_locVisFade.TryGetValue(fkey, out var fs))
            {
                dtF = (float)(nowF - fs.Tick).TotalSeconds;
                curF = fs.F;
            }
            if (dtF < 0) dtF = 0;
            if (dtF > 0.5f) dtF = 0.5f;
            float ft = Math.Max(0, z.LocVisFadeSec);
            if (ft <= 0) curF = target;
            else if (target > curF) curF = Math.Min(target, curF + dtF / ft);
            else if (target < curF) curF = Math.Max(target, curF - dtF / ft);
            _locVisFade[fkey] = (curF, nowF);
            return curF;
        }
        catch { return 1f; }
    }

    // Visibility gate for sphere zones: mode 1 = center in view, mode 2 =
    // any part of the RadiusMax sphere (camera inside counts as visible).
    private bool ZoneVisible(string nodeId, DynamicZone z)
    {
        try
        {
            var cfg = this.Config;
            float L = cfg.ViewAreaLeft, T = cfg.ViewAreaTop, R = cfg.ViewAreaRight, B = cfg.ViewAreaBottom;
            var c = new System.Numerics.Vector3(z.LocX, z.LocY, z.LocZ);
            bool raw;
            if (z.LocVisMode == 2)
            {
                float r = Math.Max(0, z.RadiusMax);
                var cam = CameraPosition();
                if (cam != null && System.Numerics.Vector3.Distance(cam.Value, c) <= r) raw = true;
                else
                {
                    raw = CamSeesPoint(c, L, T, R, B)
                        || CamSeesPoint(c + new System.Numerics.Vector3(r, 0, 0), L, T, R, B)
                        || CamSeesPoint(c - new System.Numerics.Vector3(r, 0, 0), L, T, R, B)
                        || CamSeesPoint(c + new System.Numerics.Vector3(0, r, 0), L, T, R, B)
                        || CamSeesPoint(c - new System.Numerics.Vector3(0, r, 0), L, T, R, B)
                        || CamSeesPoint(c + new System.Numerics.Vector3(0, 0, r), L, T, R, B)
                        || CamSeesPoint(c - new System.Numerics.Vector3(0, 0, r), L, T, R, B);
                }
            }
            else raw = CamSeesPoint(c, L, T, R, B);
            string key = nodeId + "\0" + z.Id;
            var nowV = DateTime.UtcNow;
            if (!_locVis.TryGetValue(key, out var st))
            {
                _locVis[key] = (raw, raw, nowV);
                return raw;
            }
            if (raw == st.Shown)
            {
                _locVis[key] = (st.Shown, st.Shown, st.Since);
                return st.Shown;
            }
            if (st.Pending != raw) { _locVis[key] = (st.Shown, raw, nowV); return st.Shown; }
            if ((nowV - st.Since).TotalMilliseconds >= LocVisSettleMs) { _locVis[key] = (raw, raw, nowV); return raw; }
            return st.Shown;
        }
        catch { return true; }
    }

    // Simple-zone fade state: zone key -> (current factor, last tick).
    // Per-key ticks keep the slew wall-clock-correct no matter how many
    // callers sample the blend per frame (engine, gates, titlebars).
    private readonly Dictionary<string, (float F, DateTime Tick)> _locFade = new(StringComparer.Ordinal);
    public (float F, bool Skirt) LocationBlend(DynamicAnimNode node)
    {
        try
        {
            var tp = TrackPosition(node);
            if (tp == null) return (0, false);
            uint terr = 0;
            try { terr = clientState.TerritoryType; } catch { }
            float best = 0;
            bool skirt = false;
            var nowL = DateTime.UtcNow;
            foreach (var z in LocZones(node))
            {
                var (raw, s) = ZoneBlend(z, tp.Value, terr);
                float f = raw;
                // Camera visibility scales sphere zones (boxes ignore it).
                // Max reduction sets the floor: hidden lands on (1 - R), so
                // 100% goes to 0 (legacy) and lower values only ever shave.
                if (f > 0 && (z.LocShape == 0 || z.LocShape == 2) && z.LocVisMode != 0)
                {
                    float R = Math.Clamp(z.LocVisMaxReduction / 100f, 0f, 1f);
                    float vis = ZoneVisAmount(node.Id, z);
                    f *= (1f - R) + R * vis;
                }
                // Slew toward the (possibly visibility-gated) target at the
                // zone's fade rates (0 = snap). Positional blends with no
                // fade times stay direct.
                if (z.FadeInSec > 0 || z.FadeOutSec > 0)
                {
                    string key = node.Id + "\0" + z.Id;
                    float dtL = 0f;
                    float cur = f;
                    if (_locFade.TryGetValue(key, out var st))
                    {
                        dtL = (float)(nowL - st.Tick).TotalSeconds;
                        cur = st.F;
                    }
                    if (dtL < 0) dtL = 0;
                    if (dtL > 0.5f) dtL = 0.5f;
                    float fi = Math.Max(0, z.FadeInSec);
                    float fo = Math.Max(0, z.FadeOutSec);
                    if (f > cur) cur = fi <= 0 ? f : Math.Min(f, cur + dtL / fi);
                    else if (f < cur) cur = fo <= 0 ? f : Math.Max(f, cur - dtL / fo);
                    _locFade[key] = (cur, nowL);
                    f = cur;
                }
                if (f > best) { best = f; skirt = s; }
            }
            return (best, skirt);
        }
        catch { return (0, false); }
    }

    public static float LocationDistRaw(uint territoryId, System.Numerics.Vector3? playerPos, DynamicZone z)
    {
        try
        {
            if (z.LocTerritory != 0 && z.LocTerritory != territoryId) return -1;
            if (playerPos == null) return -1;
            var p = playerPos.Value;
            float dx = p.X - z.LocX;
            float dy = p.Y - z.LocY;
            float dz = p.Z - z.LocZ;
            return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        }
        catch { return -1; }
    }

    // Live node status for the animator titlebars. Trigger = envelope run
    // active (incl. delay/stay/fade); wall = fade progress above zero;
    // location (kind 18) blends are read via LocationBlend by the caller.
    // Dictionaries are also written on the framework thread, so guard.
    public bool IsTriggerRunning(string nodeId)
    {
        try
        {
            return _triggerStarts.ContainsKey(nodeId)
                || (_bandLive.TryGetValue(nodeId, out var bf) && bf > 0);
        }
        catch { return false; }
    }

    public bool IsWallRunning(string nodeId)
    {
        try { return _wallProg.TryGetValue(nodeId, out float p) && p > 0.001f; }
        catch { return false; }
    }

    // Gate logic: an edge A -> B where B is a trigger/coords/door node
    // means B can only fire while every incoming source is ON. Mirrors the
    // titlebar status (trigger envelope / location blend / door progress);
    // time nodes never block. One level only — chains settle frame by frame.
    // Live state of a gate source. Time sources recurse through the time
    // chain (an OFF time node blocks); everything else is instant state.
    private bool NodeGateLive(DynamicPresetData? data, DynamicAnimNode src, HashSet<string>? visiting = null)
    {
        try
        {
            if (IsTimeGateNode(src))
                return TimeGateLive(src, _eorzeaNow);
            if (src.TriggerKind == 18 && (IsTriggerNode(src) || IsCoordsNode(src)))
            {
                // Location sources read live only while their own gates
                // hold too (gated-off stillness must propagate downstream).
                var (lf, _) = LocationBlend(src);
                return lf > 0.001f && GatesSatisfied(data, src);
            }
            if (IsTimeNode(src))
                return TimeNodeOn(data, src.Id, visiting ?? new HashSet<string>());
            if (string.Equals(src.Source, "wall", StringComparison.OrdinalIgnoreCase))
                return _wallProg.TryGetValue(src.Id, out float p) && p > 0.001f;
            if (src.TriggerKind == 18)
                return LocationBlend(src).F > 0.001f;
            if (string.Equals(src.Source, "trigger", StringComparison.OrdinalIgnoreCase))
                return _triggerStarts.ContainsKey(src.Id)
                    || (_bandLive.TryGetValue(src.Id, out var bf) && bf > 0);
            if (string.Equals(src.Source, "coords", StringComparison.OrdinalIgnoreCase))
                return false; // dormant without kind 18
            if (IsTimerNode(src))
                return TimerAnyOut(src.Id);
            if (IsDlssNode(src))
                return GetDlssLive();
            if (IsPresetNode(src))
                return HasIncomingPins(data, src.Id) && GatesSatisfied(data, src);
            if (IsResNode(src))
                return ResolutionLive(src);
            if (IsTimeLockNode(src))
                return HasIncomingPins(data, src.Id) && GatesSatisfied(data, src);
            if (IsSpotNode(src))
                return SpotLive(src);
            return true;
        }
        catch { return true; }
    }

    private static bool IsTimeNode(DynamicAnimNode n)
        => !string.Equals(n.Source, "trigger", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(n.Source, "coords", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(n.Source, "wall", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(n.Source, "weather", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(n.Source, "timegate", StringComparison.OrdinalIgnoreCase)
        && !IsTimerNode(n)
        && !IsDlssNode(n)
        && !IsPresetNode(n)
        && !IsResNode(n)
        && !IsTimeLockNode(n)
        && !IsSpotNode(n);

    private static bool IsDlssNode(DynamicAnimNode n)
        => string.Equals(n.Source, "dlss", StringComparison.OrdinalIgnoreCase);

    private static bool IsPresetNode(DynamicAnimNode n)
        => string.Equals(n.Source, "preset", StringComparison.OrdinalIgnoreCase);

    private static bool IsResNode(DynamicAnimNode n)
        => string.Equals(n.Source, "res", StringComparison.OrdinalIgnoreCase);

    private static bool IsTimeLockNode(DynamicAnimNode n)
        => string.Equals(n.Source, "timelock", StringComparison.OrdinalIgnoreCase);

    private static bool IsSpotNode(DynamicAnimNode n)
        => string.Equals(n.Source, "spot", StringComparison.OrdinalIgnoreCase);

    private static bool IsTimerNode(DynamicAnimNode n)
        => string.Equals(n.Source, "timer", StringComparison.OrdinalIgnoreCase);

    private static bool IsTimeGateNode(DynamicAnimNode n)
        => string.Equals(n.Source, "timegate", StringComparison.OrdinalIgnoreCase);

    // Time gate window test. Equal bounds = always on.
    public static bool TimeGateLive(DynamicAnimNode n, int eorzeaSeconds)
    {
        int on = Math.Clamp(n.TimeGateOn, 0, 86399);
        int off = Math.Clamp(n.TimeGateOff, 0, 86399);
        if (on == off) return true;
        int t = Math.Clamp(eorzeaSeconds, 0, 86399);
        return on < off ? (t >= on && t < off) : (t >= on || t < off);
    }

    // Eorzea time cached once per update for gate evaluation.
    private int _eorzeaNow;

    // A time node is ON while every incoming edge's source is ON (All-On).
    // OFF propagates downstream: chain successors of an OFF node read OFF
    // too, so the timeline cuts there. Cycle-guarded (creation already
    // forbids cycles, this is belt and braces).
    public bool TimeNodeOn(DynamicPresetData? data, string nodeId, HashSet<string>? visiting = null)
    {
        try
        {
            if (data == null) return true;
            visiting ??= new HashSet<string>();
            if (!visiting.Add(nodeId)) return true;
            try
            {
                foreach (var e in data.Edges)
                {
                    if (!string.Equals(e.To, nodeId, StringComparison.Ordinal)) continue;
                    if (!string.IsNullOrEmpty(e.FromPin))
                    {
                        if (!EdgeLive(data, e, visiting)) return false;
                        continue;
                    }
                    var src = data.Nodes.Find(n => n.Id == e.From);
                    if (src == null) continue;
                    if (!NodeGateLive(data, src, visiting)) return false;
                }
                return true;
            }
            finally { visiting.Remove(nodeId); }
        }
        catch { return true; }
    }

    // Per-edge live state shared by gate modes and time-node ANDing.
    // Pin edges (weather group / envelope phase) read the pin; whole-node
    // edges read the source node's live state.
    private bool EdgeLive(DynamicPresetData? data, DynamicAnimEdge e, HashSet<string>? visiting = null)
    {
        try
        {
            if (data == null) return true;
            // Weather group pin: live while the current weather matches.
            // Dangling (group deleted) reads satisfied so stale wires
            // can't wedge a node off.
            if (!string.IsNullOrEmpty(e.FromPin) && e.FromPin.StartsWith("w:", StringComparison.Ordinal))
            {
                try
                {
                    var srcNode = data.Nodes.Find(n => n.Id == e.From);
                    var grp = srcNode?.WeatherGroups.Find(gr => ("w:" + gr.Id) == e.FromPin);
                    if (grp == null || srcNode == null) return true;
                    return WeatherGroupLive(grp, srcNode.WeatherGroups, GetCurrentWeather());
                }
                catch { return true; }
            }
            // Timer pin: live while that timer's output is ON. Dangling
            // (timer deleted) reads satisfied so stale wires can't wedge
            // a node off.
            if (!string.IsNullOrEmpty(e.FromPin) && e.FromPin.StartsWith("t:", StringComparison.Ordinal))
            {
                try
                {
                    var srcNode = data.Nodes.Find(n => n.Id == e.From);
                    var t = srcNode?.Timers.Find(tr => ("t:" + tr.Id) == e.FromPin);
                    if (t == null || srcNode == null) return true;
                    return TimerOutputOn(srcNode.Id, t.Id);
                }
                catch { return true; }
            }
            // Phase pin: true once that envelope phase completed.
            if (!string.IsNullOrEmpty(e.FromPin))
            {
                try { return _phasePings.TryGetValue(e.From, out var set) && set.Contains(e.FromPin); }
                catch { return false; }
            }
            return true; // whole-node: resolved by the caller (needs src)
        }
        catch { return true; }
    }

    // Public for the canvas status stamps (same gate truth as the engine).
    public bool GatesSatisfied(DynamicPresetData? data, DynamicAnimNode node)
    {
        try
        {
            if (data == null) return true;
            int on = 0, total = 0;
            foreach (var e in data.Edges)
            {
                if (!string.Equals(e.To, node.Id, StringComparison.Ordinal)) continue;
                if (!string.IsNullOrEmpty(e.FromPin))
                {
                    total++;
                    if (EdgeLive(data, e)) on++;
                    continue;
                }
                var src = data.Nodes.Find(n => n.Id == e.From);
                if (src == null) continue;
                total++;
                if (NodeGateLive(data, src)) on++;
            }
            if (total == 0) return true;
            return Math.Clamp(node.InPinMode, 0, 3) switch
            {
                1 => on == 0,      // All Off
                2 => on >= 1,      // One On
                3 => on < total,   // One Off
                _ => on == total,  // All On
            };
        }
        catch { return true; }
    }

    // Prime the sent-map from known-live state (snapshot time, animate
    // enable, preset parse): avoids a redundant full-state flood for values
    // already live, which is what killed the game (hundreds of simultaneous
    // first-enable compiles collapsing into one frame).
    public void PrimeDynToggles(Dictionary<string, bool> liveStates)
    {
        _lastSentDynToggles.Clear();
        foreach (var kvp in liveStates)
            _lastSentDynToggles[kvp.Key] = kvp.Value;
    }

    private static readonly Comparison<DynamicKeyframe> ByKeyframeTime =
        (x, y) => x.TimeSeconds.CompareTo(y.TimeSeconds);

    private static string DynProtoType(string baseType)
    {
        if (baseType.StartsWith("float", StringComparison.OrdinalIgnoreCase)
            || baseType.StartsWith("half", StringComparison.OrdinalIgnoreCase))
            return "float";
        return "int";
    }

    private static readonly System.Text.RegularExpressions.Regex ProtoNumsRx =
        new(@"\(([^)]+)\)", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string DynLerpValue(DynamicUniformValue a, DynamicUniformValue b, float t)
    {
        string proto = DynProtoType(string.IsNullOrEmpty(a.BaseType) ? b.BaseType : a.BaseType);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        // Scalar fast path: most uniforms are single numbers. Byte-identical
        // to the slow path below (same F4/round formatting, same bool
        // threshold); anything else falls through.
        string av0 = a.Value ?? "", bv0 = b.Value ?? "";
        if (av0.IndexOf(',') < 0 && bv0.IndexOf(',') < 0 && av0.IndexOf('(') < 0 && bv0.IndexOf('(') < 0
            && float.TryParse(av0, System.Globalization.NumberStyles.Float, inv, out float fa0)
            && float.TryParse(bv0, System.Globalization.NumberStyles.Float, inv, out float fb0))
        {
            float v0 = fa0 + (fb0 - fa0) * t;
            if (proto != "float")
            {
                if (a.BaseType.StartsWith("bool", StringComparison.OrdinalIgnoreCase)
                    || b.BaseType.StartsWith("bool", StringComparison.OrdinalIgnoreCase))
                    return v0 > 0.5f ? "1" : "0";
                return ((int)Math.Round(v0)).ToString(inv);
            }
            return v0.ToString("F4", inv);
        }
        float[] ParseNums(string s)
        {
            var m = ProtoNumsRx.Match(s ?? "");
            string raw = m.Success ? m.Groups[1].Value : (s ?? "");
            var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var nums = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                float.TryParse(parts[i], System.Globalization.NumberStyles.Float, inv, out nums[i]);
            return nums;
        }
        var na = ParseNums(a.Value ?? "");
        var nb = ParseNums(b.Value ?? "");
        int count = Math.Max(Math.Max(na.Length, nb.Length), 1);
        var outVals = new string[count];
        for (int i = 0; i < count; i++)
        {
            float fa = i < na.Length ? na[i] : (nb.Length > 0 ? nb[0] : 0f);
            float fb = i < nb.Length ? nb[i] : (na.Length > 0 ? na[0] : 0f);
            float v = fa + (fb - fa) * t;
            outVals[i] = proto == "float"
                ? v.ToString("F4", inv)
                : ((int)Math.Round(v)).ToString(inv);
        }
        if (proto == "int" && (a.BaseType.StartsWith("bool", StringComparison.OrdinalIgnoreCase)
            || b.BaseType.StartsWith("bool", StringComparison.OrdinalIgnoreCase)))
        {
            for (int i = 0; i < count; i++)
                outVals[i] = (i < count && float.TryParse(outVals[i], System.Globalization.NumberStyles.Float, inv, out float fv) && fv > 0.5f) ? "1" : "0";
        }
        return string.Join(",", outVals);
    }

    private static DynamicKeyframe? FindPrimaryFrame(DynamicPresetData? data, List<DynamicKeyframe> frames)
    {
        var hit = frames.FirstOrDefault(f => f.IsPrimary);
        if (hit != null) return hit;
        if (data == null) return null;
        var primary = data.GetConfig("Primary") ?? data.Configs.FirstOrDefault();
        return primary?.Keyframes.FirstOrDefault(k => k.IsPrimary);
    }

    private static DynamicUniformValue? StoredUniform(DynamicKeyframe f, string file, string uname)
    {
        if (f.Uniforms.TryGetValue(file, out var m) && m.TryGetValue(uname, out var v)) return v;
        return null;
    }

    // Chain head's fade seconds: chained time nodes inherit the head's
    // setting (their own row is hidden, like Curve).
    private static float ChainHeadFadeSec(DynamicPresetData? data, DynamicAnimNode node)
    {
        try
        {
            if (data == null) return node.TimeFadeSec;
            var seen = new HashSet<string>();
            var cur = node;
            while (cur != null && seen.Add(cur.Id))
            {
                DynamicAnimNode? prev = null;
                foreach (var e in data.Edges)
                {
                    if (e.To != cur.Id || !string.IsNullOrEmpty(e.FromPin)) continue;
                    var s = data.Nodes.Find(n => n.Id == e.From);
                    if (s != null && IsTimeNode(s)) { prev = s; break; }
                }
                if (prev == null) return cur.TimeFadeSec;
                cur = prev;
            }
            return node.TimeFadeSec;
        }
        catch { return node.TimeFadeSec; }
    }

    // All uniform keys a frame drives (full: everything stored).
    private static List<string> DrivenUniformKeys(DynamicKeyframe f)
    {
        var keys = new List<string>();
        try
        {
            if (f.IsPrimary || !f.Sparse)
            {
                foreach (var kvp in f.Uniforms)
                    foreach (var u in kvp.Value.Keys)
                        keys.Add(kvp.Key + "\0" + u);
            }
            else
            {
                foreach (var k in f.TickedUniforms)
                    if (k.Contains('\0')) keys.Add(k);
            }
        }
        catch { }
        return keys;
    }

    // All tech keys a frame drives.
    private static List<string> DrivenTechKeys(DynamicKeyframe f)
    {
        try
        {
            if (f.IsPrimary || !f.Sparse) return f.TechStates.Keys.ToList();
            return f.TickedTechs.ToList();
        }
        catch { return new List<string>(); }
    }

    // Fade-out claims: sparse ticks only. A full frame is base content
    // covered by fallback — holding it would force every toggle on (a
    // primary-bound node going off floods 600+ ONs). Only when no primary
    // exists anywhere is full content exclusive (then hold it).
    private static List<string> FadeOutUniformKeys(DynamicKeyframe f, bool hasPrimary)
    {
        try
        {
            if (!f.IsPrimary && f.Sparse) return DrivenUniformKeys(f);
            if (hasPrimary) return new List<string>();
            return DrivenUniformKeys(f);
        }
        catch { return new List<string>(); }
    }

    private static List<string> FadeOutTechKeys(DynamicKeyframe f, bool hasPrimary)
    {
        try
        {
            if (!f.IsPrimary && f.Sparse) return DrivenTechKeys(f);
            if (hasPrimary) return new List<string>();
            return DrivenTechKeys(f);
        }
        catch { return new List<string>(); }
    }

    // Active fade-out window 0..1 for a node (cleans up expired). The
    // window only exists while chains exist; legacy mode is untouched.
    private bool FadeOutFactor(DynamicPresetData? data, DynamicAnimNode node, DateTime now, out float f)
    {
        f = 0f;
        try
        {
            if (data == null) return false;
            float fadeSec = ChainHeadFadeSec(data, node);
            if (fadeSec <= 0) return false;
            if (!_timeFadeOutStart.TryGetValue(node.Id, out var st)) return false;
            double el = (now - st).TotalSeconds;
            if (el >= fadeSec)
            {
                _timeFadeOutStart.Remove(node.Id);
                _fadeOutFrom.Remove(node.Id);
                return false;
            }
            if (!_fadeOutFrom.ContainsKey(node.Id)) return false;
            f = Smooth01((float)(el / fadeSec));
            return true;
        }
        catch { return false; }
    }

    // Blend factors per keyframe for time nodes inside their fade window
    // (OFF->ON ramp). Keyed by keyframe id, min wins if shared (with the
    // winning node, whose frozen blend-from map is used).
    private Dictionary<string, (float F, string Node)> TimeFadeFactors(DynamicPresetData? data)
    {
        var map = new Dictionary<string, (float F, string Node)>();
        try
        {
            if (data == null) return map;
        var now = DateTime.UtcNow;
            foreach (var n in data.Nodes)
            {
                if (!IsTimeNode(n) || string.IsNullOrEmpty(n.KeyframeId)) continue;
                float fadeSec = ChainHeadFadeSec(data, n);
                if (fadeSec <= 0) continue;
                if (!_timeFadeStart.TryGetValue(n.Id, out var st)) continue;
                double el = (now - st).TotalSeconds;
                if (el >= fadeSec)
                {
                    _timeFadeStart.Remove(n.Id);
                    _fadeFrom.Remove(n.Id);
                    continue;
                }
                float f = Smooth01((float)(el / fadeSec));
                if (!map.TryGetValue(n.KeyframeId, out var cur) || f < cur.F) map[n.KeyframeId] = (f, n.Id);
            }
        }
        catch { }
        return map;
    }

    // A frame carries a setting if it's full (primary or legacy) and stores
    // it, or sparse and ticks it. No primary anywhere = legacy mode: every
    // frame carries everything it stores.
    // Per-build carrier sets: which keys each pool frame carries. Full
    // frames map to null (carries everything stored — checked inline, no
    // precompute garbage for base content); sparse frames map to their
    // ticked keys present in storage (node-bound maps to the shared empty
    // set). Depends only on data shape, not time: one build pass replaces
    // thousands of per-key closure/concat/node-scan calls.
    private static readonly HashSet<string> NoCarryKeys =
        new(StringComparer.OrdinalIgnoreCase);

    private static void BuildCarrySets(DynamicPresetData? data, List<DynamicKeyframe> pool,
        bool hasPrimary, out Dictionary<DynamicKeyframe, HashSet<string>?> carryU,
        out Dictionary<DynamicKeyframe, HashSet<string>?> carryT)
    {
        carryU = new Dictionary<DynamicKeyframe, HashSet<string>?>(pool.Count);
        carryT = new Dictionary<DynamicKeyframe, HashSet<string>?>(pool.Count);
        foreach (var f in pool)
        {
            bool fullF = !hasPrimary || f.IsPrimary || !f.Sparse;
            if (fullF) { carryU[f] = null; carryT[f] = null; continue; }
            if (IsNodeBound(data, f.Id)) { carryU[f] = NoCarryKeys; carryT[f] = NoCarryKeys; continue; }
            HashSet<string>? su = null, st = null;
            foreach (var k in f.TickedUniforms)
            {
                int s2 = k.IndexOf('\0');
                if (s2 < 0) continue;
                if (f.Uniforms.TryGetValue(k.Substring(0, s2), out var m) && m.ContainsKey(k.Substring(s2 + 1)))
                    (su ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(k);
            }
            foreach (var k in f.TickedTechs)
                if (f.TechStates.ContainsKey(k))
                    (st ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(k);
            carryU[f] = su ?? NoCarryKeys;
            carryT[f] = st ?? NoCarryKeys;
        }
    }

    private static bool CarriesU(Dictionary<DynamicKeyframe, HashSet<string>?> sets,
        DynamicKeyframe f, string key, string file, string uname)
    {
        if (!sets.TryGetValue(f, out var set)) return false;
        if (set == null) return StoredUniform(f, file, uname) != null;
        return set.Contains(key);
    }

    private static bool CarriesT(Dictionary<DynamicKeyframe, HashSet<string>?> sets,
        DynamicKeyframe f, string techKey)
    {
        if (!sets.TryGetValue(f, out var set)) return false;
        if (set == null) return f.TechStates.ContainsKey(techKey);
        return set.Contains(techKey);
    }

    // (Superseded by BuildCarrySets + CarriesU/CarriesT above: same rule,
    // evaluated once per build instead of per key. Kept documented here:
    // full frames carry everything stored; sparse frames carry ticked keys
    // present in storage unless node-bound.)

    // Time Lock arbitration: newest activation among wired + satisfied
    // locks wins (ties -> list order). Returns the locked Eorzea seconds
    // to evaluate at, or null when the lock has no influence (no winner
    // and fade fully out). The crossfade slews toward 1 on engage (fade
    // in) and back to 0 on release (fade out); zero = snap.
    private int? TickTimeLock(DynamicPresetData? data, float dt)
    {
        try
        {
            string best = "";
            DateTime bestAt = DateTime.MinValue;
            if (data != null)
            {
                foreach (var n in data.Nodes)
                {
                    if (!IsTimeLockNode(n)) continue;
                    if (HasIncomingPins(data, n.Id) && GatesSatisfied(data, n))
                    {
                        if (!_tlSince.ContainsKey(n.Id)) _tlSince[n.Id] = DateTime.UtcNow;
                    }
                    else _tlSince.Remove(n.Id);
                }
                foreach (var id in _tlSince.Keys.Where(k => data.Nodes.All(n => n.Id != k)).ToList())
                    _tlSince.Remove(id);
                foreach (var n in data.Nodes)
                {
                    if (!_tlSince.TryGetValue(n.Id, out var at)) continue;
                    if (best == "" || at > bestAt) { best = n.Id; bestAt = at; }
                }
            }
            else _tlSince.Clear();
            if (best != "" && data != null)
            {
                var wn = data.Nodes.Find(n => n.Id == best);
                float fi = Math.Max(0f, wn?.TimeLockFadeIn ?? 1f);
                _tlWinner = best;
                _tlLastFadeOut = Math.Max(0f, wn?.TimeLockFadeOut ?? 1f);
                _tlLastSec = Math.Clamp(wn?.TimeLockTimeSec ?? 0, 0, 86399);
                if (fi <= 0) _tlFactor = 1f;
                else _tlFactor = Math.Min(1f, _tlFactor + dt / fi);
                return _tlLastSec;
            }
            if (_tlFactor > 0f)
            {
                float fo = Math.Max(0f, _tlLastFadeOut);
                if (fo <= 0) _tlFactor = 0f;
                else _tlFactor = Math.Max(0f, _tlFactor - dt / fo);
                if (_tlFactor > 0f) return _tlLastSec;
            }
            _tlWinner = "";
            return null;
        }
        catch { return _tlFactor > 0f ? _tlLastSec : (int?)null; }
    }

    // Merges the live-time and locked-time evaluations by the lock
    // crossfade (output space, so big jumps never sweep through
    // intermediate looks). Keys on one side only pass through.
    private static (string Content, Dictionary<string, string> Mirrored) MergeTimeLockContent(
        string liveContent, Dictionary<string, string> liveMir,
        string lockContent, Dictionary<string, string> lockMir, float f)
    {
        var mir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        try
        {
            var lockLines = new Dictionary<string, (string File, string Uname, string Proto, string Val)>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lockContent.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                var parts = t.Split('|');
                if (parts.Length < 4) continue;
                lockLines[parts[0] + "\0" + parts[1]] = (parts[0], parts[1], parts[2], string.Join("|", parts.Skip(3)));
            }
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in liveContent.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                var parts = t.Split('|');
                if (parts.Length < 4) continue;
                string key = parts[0] + "\0" + parts[1];
                string proto = parts[2];
                liveMir.TryGetValue(key, out var lv);
                lv ??= string.Join("|", parts.Skip(3));
                string outVal = lv;
                if (lockLines.TryGetValue(key, out var lk) && lockMir.TryGetValue(key, out var kv))
                    outVal = DynLerpValue(
                        new DynamicUniformValue { Value = lv, BaseType = proto },
                        new DynamicUniformValue { Value = kv, BaseType = lk.Proto },
                        Math.Clamp(f, 0f, 1f));
                mir[key] = outVal;
                emitted.Add(key);
                sb.AppendLine($"{parts[0]}|{parts[1]}|{proto}|{outVal}");
            }
            foreach (var kvp in lockLines)
            {
                if (emitted.Contains(kvp.Key)) continue;
                string outVal = kvp.Value.Val;
                if (lockMir.TryGetValue(kvp.Key, out var kv2)) outVal = kv2;
                mir[kvp.Key] = outVal;
                sb.AppendLine($"{kvp.Value.File}|{kvp.Value.Uname}|{kvp.Value.Proto}|{outVal}");
            }
        }
        catch { }
        return (sb.ToString(), mir);
    }

    // Delta gate for one formatted line (single path only): true = format
    // now (changed, or forced full); records the key for post-write sync.
    // Unchanged keys stay out of the file — the addon sticky-holds them.
    private bool EmitChangedLine(string key, string value)
    {
        if (_lastWritten.TryGetValue(key, out var pv) && pv == value) return false;
        _emitChanged.Add(key);
        return true;
    }
    private readonly Dictionary<string, string> _lastWritten = new(StringComparer.OrdinalIgnoreCase);
    private string _lastLegacyWritten = "\0";
    private string _lastEmitPreset = "";
    private bool _lastEmitEnabled;
    private int _writesSinceFull;
    private bool _lastWasEmpty = true;
    // Per-build scratch (single path only): keys formatted this build, and
    // whether it emitted full. Cleared at build start; the timelock double
    // build doesn't use them (it always full-writes while engaged).
    private readonly List<string> _emitChanged = new();
    private bool _emitFull;

    private string BuildDynamicAnimContent(DynamicPresetData? data, int eorzeaSeconds, out Dictionary<string, string> mirrored, Dictionary<string, (DynamicUniformValue? From, DynamicUniformValue To, float F)>? trigOverlay = null, bool deltaOk = true)
    {
        mirrored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _emitChanged.Clear();
        _emitFull = false;
        // Delta/full decision up front (single path only; the timelock
        // double build formats full and ignores all of this).
        string emitPreset = (data != null) ? (this.configWindow.SelectedPreset ?? "") : "";
        bool emitEnabled = data != null && data.Enabled;
        bool forceFullFrame = deltaOk && (!string.Equals(emitPreset, _lastEmitPreset, StringComparison.OrdinalIgnoreCase)
            || emitEnabled != _lastEmitEnabled
            || _writesSinceFull >= 80);
        if (deltaOk) _emitFull = forceFullFrame;
        var frames = DynamicTimeline.EvalFrames(data, n => TimeNodeOn(data, n.Id));
        if (frames == null || frames.Count == 0) return "";
        // A chain exists but gating cut it entirely: freeze the timeline
        // (hold last values) instead of falling back to the moving base
        // timeline. Trigger envelopes still drive (off live values).
        bool frozen = DynamicTimeline.ResolveChainFrames(data) != null
            && DynamicTimeline.ResolveChainFrames(data, n => TimeNodeOn(data, n.Id)) == null;
        var primary = FindPrimaryFrame(data, frames);
        var pool = new List<DynamicKeyframe>(frames);
        if (primary != null && !pool.Any(f => f.Id == primary.Id)) pool.Add(primary);
        bool hasPrimary = primary != null;
        // Time order once per build: carriers below stay sorted without a
        // per-key OrderBy (identical order to the old filter+sort).
        pool.Sort(ByKeyframeTime);
        // Carrier membership once per build (see BuildCarrySets): the loops
        // below become set lookups instead of per-key scans.
        BuildCarrySets(data, pool, hasPrimary, out var carryU, out var carryT);
        var fadeByFrame = TimeFadeFactors(data);
        // Chain-start transfer function for segment blending (CurveMode on
        // the walk head; 24H Brightness Curve needs sidecar data). Hoisted:
        // resolving the walk per key per frame would be wasteful.
        int segCurve = 0;
        DynamicDaylight? segDaylight = null;
        try
        {
            if (data != null)
            {
                segDaylight = ResolveDaylight(data);
                var segStart = DynamicTimeline.ResolveChainStartNode(data, n => TimeNodeOn(data, n.Id));
                if (segStart != null) segCurve = Math.Clamp(segStart.CurveMode, 0, 2);
            }
        }
        catch { }
        // Per-frame daylight hoists: the global brightness position and the
        // per-segment normalization scans are identical for every key, so
        // compute once instead of per key (96 + 64 bin samples each).
        float pairGlobalF = float.NaN;
        Dictionary<(int t0, int t1), (float mn, float mx)>? segDlCache = null;
        try
        {
            if (segCurve == 2 && segDaylight != null && segDaylight.Values != null && segDaylight.Values.Count > 0)
            {
                segDlCache = new Dictionary<(int t0, int t1), (float mn, float mx)>();
                if (frames.Count == 2) pairGlobalF = DynamicTimeline.DaylightGlobalFactor(segDaylight, eorzeaSeconds);
            }
        }
        catch { pairGlobalF = float.NaN; segDlCache = null; }
        // Keys with a live driver this frame (non-primary carrier, manual
        // grab, or trigger envelope). Fade-out yields to all of these.
        var liveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        _curBaseFlags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        // Key universe, split once: the main loop needs (key, file, uname)
        // and re-splitting every key every frame showed up hot. Override and
        // overlay keys (few) split once at insert.
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keyParts = new List<(string key, string file, string uname)>();
        void AddKey(string k, string f, string u) { if (keys.Add(k)) keyParts.Add((k, f, u)); }
        void AddKeySplit(string k)
        {
            if (!keys.Add(k)) return;
            int s = k.IndexOf('\0');
            if (s < 0) return;
            keyParts.Add((k, k.Substring(0, s), k.Substring(s + 1)));
        }
        foreach (var f in pool)
        {
            bool full = !hasPrimary || f.IsPrimary || !f.Sparse;
            if (full)
            {
                foreach (var kvp in f.Uniforms)
                    foreach (var u in kvp.Value.Keys)
                        AddKey(kvp.Key + "\0" + u, kvp.Key, u);
            }
            else
            {
                foreach (var k in f.TickedUniforms)
                    if (k.Contains('\0')) AddKeySplit(k);
            }
        }
        foreach (var okey in this.configWindow.DynOverrideUniforms.Keys)
            AddKeySplit(okey);
        // Trigger overlays must also work for uniforms with no base carrier:
        // node-bound sparse frames never carry (by design), so without this
        // their envelope is silently skipped and the effect pops instead of
        // fading (e.g. Drunk_Strength, which Primary never snapshot).
        if (trigOverlay != null)
            foreach (var okey in trigOverlay.Keys)
                AddKeySplit(okey);

        // Per-frame scratch: carrier filter reuses one list to avoid a
        // ToList per key (contents rebuilt, order stays time-sorted).
        var carrierScratch = new List<DynamicKeyframe>(pool.Count);
        // One clock for the whole build (crossfade progress identical for
        // every key instead of drifting by call order).
        DateTime nowUtcBuild = DateTime.UtcNow;
        foreach (var (key, file, uname) in keyParts)
        {
            carrierScratch.Clear();
            if (!frozen)
            {
                foreach (var f in pool)
                    if (CarriesU(carryU, f, key, file, uname))
                        carrierScratch.Add(f);
            }
            var carriers = carrierScratch;
            if (carriers.Count == 0)
            {
                // No base value: drive purely from the trigger envelope.
                // Delay phase (From == null) holds the start value; the fade
                // lerps start -> peak like the normal path. Live envelope
                // content is never base-only.
                if (trigOverlay != null && trigOverlay.TryGetValue(key, out var oo))
                {
                    string oproto = DynProtoType(oo.To.BaseType);
                    string oout = oo.From == null
                        ? oo.To.Value
                        : DynLerpValue(oo.From, oo.To, Math.Clamp(oo.F, 0f, 1f));
                    // Releasing with an ambient driver: land on ambient.
                    if (_releaseU.Contains(key) && _ambU.TryGetValue(key, out var amb0))
                    {
                        DynamicUniformValue ambFrom0 = amb0.From
                            ?? (_prevMirrored.TryGetValue(key, out var pmv)
                                ? new DynamicUniformValue { Value = pmv, BaseType = amb0.To.BaseType }
                                : amb0.To);
                        string ambVal0 = DynLerpValue(ambFrom0, amb0.To, Math.Clamp(amb0.F, 0f, 1f));
                        oout = DynLerpValue(
                            new DynamicUniformValue { Value = ambVal0, BaseType = amb0.To.BaseType },
                            oo.To, Math.Clamp(oo.F, 0f, 1f));
                    }
                    // Midpoint shaping: bend through Mid at f=0.5. Releases
                    // bend the ambient landing, not the raw carrier.
                    if (_midU.TryGetValue(key, out var midUv0))
                    {
                        DynamicUniformValue from0;
                        if (_releaseU.Contains(key) && _ambU.TryGetValue(key, out var ambM))
                        {
                            DynamicUniformValue ambFromM = ambM.From
                                ?? (_prevMirrored.TryGetValue(key, out var pmvM) ? new DynamicUniformValue { Value = pmvM, BaseType = ambM.To.BaseType } : ambM.To);
                            string ambValM = DynLerpValue(ambFromM, ambM.To, Math.Clamp(ambM.F, 0f, 1f));
                            from0 = new DynamicUniformValue { Value = ambValM, BaseType = ambM.To.BaseType };
                        }
                        else from0 = oo.From
                            ?? (_prevMirrored.TryGetValue(key, out var pmv0) ? new DynamicUniformValue { Value = pmv0, BaseType = oo.To.BaseType } : oo.To);
                        oout = DynLerpMid(from0, midUv0, oo.To, oo.F);
                    }
                    // Winner-change crossfade: morph screen -> new owner.
                    if (_xfade.TryGetValue(key, out var xf0))
                    {
                        float xh0 = (float)(nowUtcBuild - xf0.T0).TotalSeconds / TrigXfadeSec;
                        if (xh0 >= 1f) _xfade.Remove(key);
                        else
                            oout = DynLerpValue(
                                new DynamicUniformValue { Value = xf0.Value, BaseType = oo.To.BaseType },
                                new DynamicUniformValue { Value = oout, BaseType = oo.To.BaseType }, Smooth01(xh0));
                    }
                    if (!deltaOk || forceFullFrame || EmitChangedLine(key, oout))
                        sb.AppendLine($"{file}|{uname}|{oproto}|{oout}");
                    mirrored[key] = oout;
                    _curBaseFlags[key] = false;
                    liveKeys.Add(key);
                }
                // Manual override with no other carrier (e.g. trimmed-away
                // base): emit it directly, exactly as the old full-primary
                // path did (override wins outright, flagged base-driven).
                else if (this.configWindow.DynOverrideUniforms.TryGetValue(key, out var ov2))
                {
                    string proto2 = DynProtoType(ov2.BaseType);
                    if (!deltaOk || forceFullFrame || EmitChangedLine(key, ov2.Value))
                        sb.AppendLine($"{file}|{uname}|{proto2}|{ov2.Value}");
                    mirrored[key] = ov2.Value;
                    _curBaseFlags[key] = true;
                    liveKeys.Add(key);
                }
                continue;
            }
            // Twins (same TimeSeconds, e.g. chain frame + pooled primary at
            // 0): prefer the first so chain frames win ties and pc/nc agree.
            // (LastOrDefault picked the pooled primary and every morning
            // lerped Primary->Day instead of Night->Day.) Allocation-free
            // twin of the old Where/Max/First chain (carriers arrive sorted).
            int pcTime = carriers[carriers.Count - 1].TimeSeconds;
            bool anyLe = false;
            foreach (var f in carriers)
            {
                int ft = f.TimeSeconds;
                if (ft <= eorzeaSeconds && (!anyLe || ft > pcTime)) { pcTime = ft; anyLe = true; }
            }
            if (!anyLe) pcTime = carriers[carriers.Count - 1].TimeSeconds;
            DynamicKeyframe pc = carriers[0], nc = carriers[0];
            bool pcSet = false, ncSet = false;
            foreach (var f in carriers)
            {
                if (!pcSet && f.TimeSeconds == pcTime) { pc = f; pcSet = true; }
                if (!ncSet && f.TimeSeconds >= eorzeaSeconds) { nc = f; ncSet = true; }
                if (pcSet && ncSet) break;
            }
            var a = StoredUniform(pc, file, uname);
            var b = StoredUniform(nc, file, uname);
            if (a == null || b == null) continue;
            // Day/night pair: exactly 2 chain frames carrying this key with
            // 24H Brightness Curve selected. Blends by live-curve position
            // between global extrema (no segment snapping); convention is
            // night look on the earlier keyframe. Longer chains and
            // primary-shared keys keep per-segment pacing.
            bool isPair = segCurve == 2 && frames.Count == 2 && carriers.Count == 2;
            float t = DynamicTimeline.SegmentFactor(pc.TimeSeconds, nc.TimeSeconds, eorzeaSeconds, segCurve, segDaylight, isPair, segDlCache);
            // Pair upgrade: both chain frames define this key, so they own it
            // outright by global brightness position — even when the pooled
            // primary also carries it (it otherwise owns the wrap hours and
            // snaps at the extrema). Falls back silently without data.
            if (segCurve == 2 && !isPair && frames.Count == 2 && segDaylight != null)
            {
                var cf0 = frames[0];
                var cf1 = frames[1];
                if (CarriesU(carryU, cf0, key, file, uname) && CarriesU(carryU, cf1, key, file, uname))
                {
                    var e0 = StoredUniform(cf0, file, uname);
                    var e1 = StoredUniform(cf1, file, uname);
                    float pf = pairGlobalF;
                    if (e0 != null && e1 != null && !float.IsNaN(pf))
                    {
                        bool swap = cf0.TimeSeconds > cf1.TimeSeconds;
                        a = swap ? e1 : e0;
                        b = swap ? e0 : e1;
                        pc = swap ? cf1 : cf0;
                        nc = swap ? cf0 : cf1;
                        t = pf;
                        if (!_tlPairLogged)
                        {
                            _tlPairLogged = true;
                            try { Service.Log.Warning($"[ReshadeController:{DynamicCanvasWindow.BuildTag}] day/night pair driving by global brightness (primary bypassed)"); } catch { }
                        }
                    }
                }
            }
            bool overridden = this.configWindow.DynOverrideUniforms.TryGetValue(key, out var ov);
            if (overridden)
                a = b = new DynamicUniformValue { Value = ov.Value, BaseType = ov.BaseType };
            string proto = DynProtoType(string.IsNullOrEmpty(a.BaseType) ? b.BaseType : a.BaseType);
            string outVal = DynLerpValue(a, b, t);
            bool anyNonPrimary = primary == null;
            if (primary != null)
                foreach (var f in carriers)
                    if (f.Id != primary.Id) { anyNonPrimary = true; break; }
            if (anyNonPrimary
                || overridden || (trigOverlay != null && trigOverlay.ContainsKey(key)))
                liveKeys.Add(key);
            bool keyBaseOnly = primary != null;
            if (primary != null)
                foreach (var f in carriers)
                    if (f.Id != primary.Id) { keyBaseOnly = false; break; }
            if (trigOverlay != null && trigOverlay.ContainsKey(key)) keyBaseOnly = false;
            _curBaseFlags[key] = keyBaseOnly;
            // Time fade: ramp from last-driven values instead of snapping
            // when a gated time node switches back on. Manual grabs skip it.
            if (!overridden)
            {
                float ff = 1f;
                string fadeNode = "";
                bool hasFade = false;
                if (fadeByFrame.TryGetValue(pc.Id, out var f1)) { ff = f1.F; fadeNode = f1.Node; hasFade = true; }
                if (pc.Id != nc.Id && fadeByFrame.TryGetValue(nc.Id, out var f2)) { if (!hasFade || f2.F < ff) { ff = f2.F; fadeNode = f2.Node; } hasFade = true; }
                if (hasFade && ff < 1f && fadeNode != "")
                {
                    // Authored start wins when the FX is idle, or when there
                    // is no live previous value (fresh key, or base-only
                    // history); otherwise glide from frozen. Missing keys
                    // snap.
                    var fnode = data?.Nodes.Find(n => n.Id == fadeNode);
                    DynamicUniformValue? authoredUv = null;
                    if (fnode != null && fnode.StartValues.TryGetValue(file, out var ssm0)
                        && ssm0.TryGetValue(uname, out var suv0) && !string.IsNullOrWhiteSpace(suv0.Value))
                        authoredUv = suv0;
                    bool idle = !FxLive(file, key);
                    string? frozenVal = null;
                    if (_fadeFrom.TryGetValue(fadeNode, out var fromMap))
                        fromMap.TryGetValue(key, out frozenVal);
                    bool frozenLive = frozenVal != null
                        && (!_prevBaseOnly.TryGetValue(key, out var wbo) || !wbo);
                    string? fromStr = null;
                    string fromBase = b.BaseType;
                    if (authoredUv != null && (idle || !frozenLive))
                    {
                        fromStr = authoredUv.Value;
                        fromBase = authoredUv.BaseType;
                    }
                    else if (frozenLive)
                    {
                        fromStr = frozenVal;
                    }
                    if (fromStr != null)
                    {
                        var fromUv = new DynamicUniformValue { Value = fromStr, BaseType = fromBase };
                        var curUv = new DynamicUniformValue { Value = outVal, BaseType = b.BaseType };
                        outVal = DynLerpValue(fromUv, curUv, Math.Clamp(ff, 0f, 1f));
                    }
                }
            }
            if (trigOverlay != null && trigOverlay.TryGetValue(key, out var o))
            {
                var baseUv = new DynamicUniformValue { Value = outVal, BaseType = string.IsNullOrEmpty(a.BaseType) ? b.BaseType : a.BaseType };
                var fromUv = o.From ?? baseUv;
                // Releasing runs land on ambient (or base) — never on the
                // authored carrier, which only serves carrier-less keys.
                if (_releaseU.Contains(key))
                {
                    fromUv = baseUv;
                    if (_ambU.TryGetValue(key, out var amb))
                    {
                        var ambFrom = amb.From ?? baseUv;
                        string ambVal = DynLerpValue(ambFrom, amb.To, Math.Clamp(amb.F, 0f, 1f));
                        fromUv = new DynamicUniformValue { Value = ambVal, BaseType = amb.To.BaseType };
                    }
                }
                outVal = DynLerpValue(fromUv, o.To, Math.Clamp(o.F, 0f, 1f));
                // Midpoint shaping: bend through Mid at f=0.5 (from already
                // carries the ambient landing when releasing).
                if (_midU.TryGetValue(key, out var midUv))
                    outVal = DynLerpMid(fromUv, midUv, o.To, o.F);
                // Winner-change crossfade: morph screen -> new owner.
                if (_xfade.TryGetValue(key, out var xf))
                {
                    float xh = (float)(nowUtcBuild - xf.T0).TotalSeconds / TrigXfadeSec;
                    if (xh >= 1f) _xfade.Remove(key);
                    else
                    {
                        string xb = string.IsNullOrEmpty(a.BaseType) ? b.BaseType : a.BaseType;
                        outVal = DynLerpValue(
                            new DynamicUniformValue { Value = xf.Value, BaseType = xb },
                            new DynamicUniformValue { Value = outVal, BaseType = xb }, Smooth01(xh));
                    }
                }
            }
            if (!deltaOk || forceFullFrame || EmitChangedLine(key, outVal))
                sb.AppendLine($"{file}|{uname}|{proto}|{outVal}");
            mirrored[key] = outVal;
        }
        // Fade-out layer: dying chains hold orphaned keys, ramping toward
        // start values (or holding), until toggles release. Appended after
        // normal lines (last wins); live-driven keys yield to new drivers.
        if (data != null)
        {
            var now2 = DateTime.UtcNow;
            foreach (var n in data.Nodes)
            {
                if (!IsTimeNode(n)) continue;
                if (!FadeOutFactor(data, n, now2, out float of)) continue;
                var bf = FindNodeKeyframe(data, n);
                if (bf == null) continue;
                foreach (var key in FadeOutUniformKeys(bf, hasPrimary))
                {
                    if (liveKeys.Contains(key)) continue;
                    if (!_fadeOutFrom.TryGetValue(n.Id, out var ofrom)
                        || !ofrom.TryGetValue(key, out var ostart)) continue;
                    int ssep = key.IndexOf('\0');
                    if (ssep < 0) continue;
                    var ofile = key.Substring(0, ssep);
                    var ouname = key.Substring(ssep + 1);
                    string otarget = ostart;
                    string otBase = "float";
                    if (this.configWindow.TryGetUniformInfo(ofile, ouname, out var oinfo)
                        && !string.IsNullOrEmpty(oinfo.BaseType))
                        otBase = oinfo.BaseType;
                    if (n.StartValues.TryGetValue(ofile, out var ossm)
                        && ossm.TryGetValue(ouname, out var osuv)
                        && !string.IsNullOrWhiteSpace(osuv.Value))
                    {
                        otarget = osuv.Value;
                        otBase = osuv.BaseType;
                    }
                    var oFromUv = new DynamicUniformValue { Value = ostart, BaseType = otBase };
                    var oToUv = new DynamicUniformValue { Value = otarget, BaseType = otBase };
                    string oVal = DynLerpValue(oFromUv, oToUv, Math.Clamp(of, 0f, 1f));
                    if (!deltaOk || forceFullFrame || EmitChangedLine(key, oVal))
                        sb.AppendLine($"{ofile}|{ouname}|{DynProtoType(otBase)}|{oVal}");
                    mirrored[key] = oVal;
                }
            }
        }
        return sb.ToString();
    }

    private Dictionary<string, bool>? FlushDynamicToggles(DynamicPresetData data, int eorzeaSeconds, Dictionary<string, (bool S, float F)>? trigToggles = null, bool writeFile = true)
    {
        if (IsPaused) return null; // never fight the global pause
        var frames = DynamicTimeline.EvalFrames(data, n => TimeNodeOn(data, n.Id));
        if (frames == null || frames.Count == 0) return null;
        // Fully gated chain: freeze the timeline toggles (hold last
        // states). Trigger toggles still drive below.
        bool frozen = DynamicTimeline.ResolveChainFrames(data) != null
            && DynamicTimeline.ResolveChainFrames(data, n => TimeNodeOn(data, n.Id)) == null;
        var primary = FindPrimaryFrame(data, frames);
        var pool = new List<DynamicKeyframe>(frames);
        if (primary != null && !pool.Any(f => f.Id == primary.Id)) pool.Add(primary);
        bool hasPrimary = primary != null;
        pool.Sort(ByKeyframeTime);
        var ordered = pool;
        BuildCarrySets(data, pool, hasPrimary, out var carryUT, out var carryTT);
        var techKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in pool)
        {
            bool full = !hasPrimary || f.IsPrimary || !f.Sparse;
            if (full)
            {
                foreach (var k in f.TechStates.Keys) techKeys.Add(k);
            }
            else
            {
                foreach (var k in f.TickedTechs) techKeys.Add(k);
            }
        }
        // Techs with a live driver (non-primary carrier, grab, trigger).
        // Fade-out holds yield to all of these.
        var liveTechs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var effective = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        // Pair curve mode for the toggle path (mirrors the uniform upgrade
        // below): a 2-frame 24H Brightness Curve chain owns techs both
        // frames define, flipping at brightness midpoint instead of
        // keyframe times (so the pooled primary can't own the wrap hours).
        int segCurveT = 0;
        try
        {
            var segStartT = DynamicTimeline.ResolveChainStartNode(data, n => TimeNodeOn(data, n.Id));
            if (segStartT != null) segCurveT = Math.Clamp(segStartT.CurveMode, 0, 2);
        }
        catch { }
        float pairF = float.NaN;
        if (segCurveT == 2 && frames.Count == 2)
        {
            try
            {
                var pairDl = ResolveDaylight(data);
                if (pairDl != null) pairF = DynamicTimeline.DaylightGlobalFactor(pairDl, eorzeaSeconds);
            }
            catch { pairF = float.NaN; }
        }
        if (!frozen)
        {
            foreach (var tk in techKeys)
            {
                if (!float.IsNaN(pairF))
                {
                    var pf0 = frames[0];
                    var pf1 = frames[1];
                    if (CarriesT(carryTT, pf0, tk) && CarriesT(carryTT, pf1, tk)
                        && pf0.TechStates.TryGetValue(tk, out bool se) && pf1.TechStates.TryGetValue(tk, out bool sl))
                    {
                        bool earlyIsNight = pf0.TimeSeconds <= pf1.TimeSeconds;
                        effective[tk] = (pairF >= 0.5f) ? (earlyIsNight ? sl : se) : (earlyIsNight ? se : sl);
                        if (primary == null || pf0.Id != primary.Id || pf1.Id != primary.Id) liveTechs.Add(tk);
                        continue;
                    }
                }
                // Same twin rule as uniforms: latest applicable time, first
                // carrier there, so chain frames beat the pooled primary.
                // Single allocation-free pass (ordered arrives sorted).
                int ct = -1;
                DynamicKeyframe? tcarrier = null;
                DynamicKeyframe? lastCarrying = null;
                foreach (var f in ordered)
                {
                    if (!CarriesT(carryTT, f, tk)) continue;
                    lastCarrying = f;
                    if (f.TimeSeconds <= eorzeaSeconds && f.TimeSeconds > ct)
                    {
                        ct = f.TimeSeconds;
                        tcarrier = f;
                    }
                }
                if (tcarrier == null) tcarrier = lastCarrying;
                if (tcarrier != null && tcarrier.TechStates.TryGetValue(tk, out bool st))
                {
                    effective[tk] = st;
                    if (primary == null || tcarrier.Id != primary.Id) liveTechs.Add(tk);
                }
            }
        }
        foreach (var kvp in this.configWindow.DynOverrideToggles)
        {
            effective[kvp.Key] = kvp.Value;
            liveTechs.Add(kvp.Key);
        }
        if (trigToggles != null)
            foreach (var kvp in trigToggles)
                if (kvp.Value.F > 0) { effective[kvp.Key] = kvp.Value.S; liveTechs.Add(kvp.Key); }
        // Fade-out holds: keep orphaned techs on until the ramp ends, then
        // the normal path releases them (invisible switch at ~start value).
        if (data != null)
        {
            var now3 = DateTime.UtcNow;
            foreach (var n in data.Nodes)
            {
                if (!IsTimeNode(n)) continue;
                if (!FadeOutFactor(data, n, now3, out _)) continue;
                var bf = FindNodeKeyframe(data, n);
                if (bf == null) continue;
                foreach (var tk in FadeOutTechKeys(bf, hasPrimary))
                {
                    if (liveTechs.Contains(tk)) continue;
                    effective[tk] = true;
                }
            }
        }
        var now = DateTime.UtcNow;
        if (writeFile) WriteToggleDiffs(effective);
        return effective;
    }

    // Budgeted, collapse-to-latest toggle file write. Runs once per frame
    // on the merged map; pure-compute evaluations skip it.
    private void WriteToggleDiffs(Dictionary<string, bool> effective)
    {
        var now = DateTime.UtcNow;
        if ((now - _dynToggleWindowStart).TotalSeconds >= 1)
        {
            _dynToggleWindowStart = now;
            _dynToggleSentThisWindow = 0;
        }
        int budget = DynTogglePerSecond - _dynToggleSentThisWindow;
        if (budget <= 0) return;
        var diffs = new List<KeyValuePair<string, bool>>();
        foreach (var kvp in effective)
        {
            if (!_lastSentDynToggles.TryGetValue(kvp.Key, out bool sent) || sent != kvp.Value)
                diffs.Add(kvp);
            if (diffs.Count >= budget) break;
        }
        if (diffs.Count == 0) return;
        try
        {
            var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? "";
            var toggleFile = Path.Combine(dir, "ffxiv_reshade_toggle");
            var lines = new List<string>();
            foreach (var kvp in diffs)
            {
                int at = kvp.Key.LastIndexOf('@');
                string tech = at >= 0 ? kvp.Key.Substring(0, at) : "";
                string file = at >= 0 ? kvp.Key.Substring(at + 1) : kvp.Key;
                lines.Add($"{file}|{tech}|{(kvp.Value ? "1" : "0")}");
            }
            var tmp = toggleFile + ".tmp";
            File.WriteAllText(tmp, string.Join("\n", lines) + "\n");
            File.Move(tmp, toggleFile, true);
            foreach (var kvp in diffs)
                _lastSentDynToggles[kvp.Key] = kvp.Value;
            _dynToggleSentThisWindow += diffs.Count;
        }
        catch { }
    }

    private unsafe int GetEorzeaSeconds()
    {
        // Built-in time freeze wins so the timeline follows the forced
        // clock; then external Weatherman; then true framework time.
        try
        {
            if (Config.WeatherControlEnabled && Weather.IsTimeCustom())
                return (int)(Weather.GetTime() % 86400);
        }
        catch { }
        try
        {
            ConnectIPC();
            if (weathermanIsTimeCustom != null && weathermanIsTimeCustom.InvokeFunc())
            {
                if (weathermanGetDisplayedTimeString != null)
                {
                    var timeStr = weathermanGetDisplayedTimeString.InvokeFunc();
                    if (!string.IsNullOrEmpty(timeStr))
                    {
                        var parts = timeStr.Split(':');
                        if (parts.Length == 3)
                        {
                            int h = int.Parse(parts[0]);
                            int m = int.Parse(parts[1]);
                            int s = int.Parse(parts[2]);
                            return (h * 3600) + (m * 60) + s;
                        }
                    }
                }
            }
        }
        catch { }

        try
        {
            var framework = Framework.Instance();
            if (framework == null) return 0;
            return (int)(framework->ClientTime.EorzeaTime % 86400);
        }
        catch { return 0; }
    }

    private static List<UniformValue>? InterpolateTimePoints(List<TimePoint> sorted, int currentSeconds)
    {
        if (sorted.Count < 2) return null;

        TimePoint? prev = null;
        TimePoint? next = null;

        for (int i = 0; i < sorted.Count; i++)
        {
            if (sorted[i].TimeSeconds <= currentSeconds)
                prev = sorted[i];
            if (sorted[i].TimeSeconds >= currentSeconds && next == null)
                next = sorted[i];
        }

        if (prev == null) prev = sorted[^1];
        if (next == null) next = sorted[0];

        if (prev == next) return prev.UniformValues;

        int prevTime = prev.TimeSeconds;
        int nextTime = next.TimeSeconds;
        float t;

        if (prevTime <= nextTime)
        {
            if (nextTime == prevTime) t = 0;
            else t = (float)(currentSeconds - prevTime) / (nextTime - prevTime);
        }
        else
        {
            int totalWrap = 86400 - prevTime + nextTime;
            int elapsed = (currentSeconds - prevTime + 86400) % 86400;
            t = totalWrap == 0 ? 0 : (float)elapsed / totalWrap;
        }

        t = Math.Clamp(t, 0f, 1f);
        // Smoothstep for smoother transitions
        t = t * t * (3f - 2f * t);

        var result = new List<UniformValue>();
        foreach (var prevVal in prev.UniformValues)
        {
            var nextVal = next.UniformValues.Find(u => u.Name == prevVal.Name);
            if (nextVal == null) { result.Add(prevVal); continue; }

            var interpolated = new UniformValue { Name = prevVal.Name, Type = prevVal.Type };

            if (prevVal.Type == "float" && prevVal.FloatValues != null && nextVal.FloatValues != null)
            {
                int count = Math.Min(prevVal.FloatValues.Length, nextVal.FloatValues.Length);
                interpolated.FloatValues = new float[count];
                for (int i = 0; i < count; i++)
                    interpolated.FloatValues[i] = prevVal.FloatValues[i] + (nextVal.FloatValues[i] - prevVal.FloatValues[i]) * t;
            }
            else if (prevVal.Type == "int" && prevVal.IntValues != null && nextVal.IntValues != null)
            {
                int count = Math.Min(prevVal.IntValues.Length, nextVal.IntValues.Length);
                interpolated.IntValues = new int[count];
                for (int i = 0; i < count; i++)
                    interpolated.IntValues[i] = (int)(prevVal.IntValues[i] + (nextVal.IntValues[i] - prevVal.IntValues[i]) * t);
            }
            else
            {
                interpolated = prevVal;
            }

            result.Add(interpolated);
        }

        return result;
    }

    private void ApplyZoneConfig(uint territoryId)
    {
        var zonePreset = Config.GetPresetForZone(territoryId);

        // Write preset path signal (queued: the addon's poll lock can
        // refuse any single attempt; see QueuePresetSignal).
        var presetPath = zonePreset?.PresetPath ?? Config.DefaultPresetPath;
        QueuePresetSignal(presetPath ?? "");

        // Write pause signal
        var pauseFile = GetPauseFilePath();
        bool shouldPause = zonePreset?.ConditionSetIndex >= 0;
        if (shouldPause)
        {
            try
            {
                if (getConditionSets == null) ConnectIPC();
                if (getConditionSets != null && checkConditionSet != null)
                {
                    var sets = getConditionSets.InvokeFunc();
                    if (sets != null && zonePreset!.ConditionSetIndex < sets.Length)
                    {
                        shouldPause = checkConditionSet.InvokeFunc(zonePreset!.ConditionSetIndex);
                    }
                }
            }
            catch { }
        }

        if (shouldPause && !File.Exists(pauseFile))
        {
            File.WriteAllText(pauseFile, "");
        }
        else if (!shouldPause && File.Exists(pauseFile))
        {
            File.Delete(pauseFile);
        }
    }

    public void RefreshConditionSets()
    {
        try
        {
            ConnectIPC();
            if (getConditionSets == null)
            {
                ConditionSetNames = Array.Empty<string>();
                return;
            }
            ConditionSetNames = getConditionSets.InvokeFunc() ?? Array.Empty<string>();
        }
        catch
        {
            ConditionSetNames = Array.Empty<string>();
        }
    }

    private void ConnectIPC()
    {
        try
        {
            getConditionSets = this.pluginInterface.GetIpcSubscriber<string[]>("QoLBar.GetConditionSets");
            checkConditionSet = this.pluginInterface.GetIpcSubscriber<int, bool>("QoLBar.CheckConditionSet");
        }
        catch
        {
            getConditionSets = null;
            checkConditionSet = null;
        }

        // Weatherman registers its IPC gates on its first framework tick,
        // which can be AFTER our first check. A failed subscribe must NOT
        // latch forever: null subscribers are retried (throttled), so a
        // late-loading Weatherman is picked up and the patch-fight refusal
        // keeps working.
        if (!weathermanChecked || (DateTime.UtcNow >= ipcRetryAt &&
            (weathermanIsTimeCustom == null || weathermanGetDisplayedTimeString == null ||
             weathermanGetDisplayedWeather == null || weathermanIsWeatherCustom == null)))
        {
            if (weathermanIsTimeCustom == null)
            {
                try
                {
                    weathermanIsTimeCustom = this.pluginInterface.GetIpcSubscriber<bool>("Weatherman.IsTimeCustom");
                    weathermanIsTimeCustom.InvokeFunc();
                }
                catch { weathermanIsTimeCustom = null; }
            }

            if (weathermanGetDisplayedTimeString == null)
            {
                try
                {
                    weathermanGetDisplayedTimeString = this.pluginInterface.GetIpcSubscriber<string>("Weatherman.GetDisplayedTimeString");
                    weathermanGetDisplayedTimeString.InvokeFunc();
                }
                catch { weathermanGetDisplayedTimeString = null; }
            }

            if (weathermanGetDisplayedWeather == null)
            {
                try
                {
                    weathermanGetDisplayedWeather = this.pluginInterface.GetIpcSubscriber<byte>("Weatherman.GetDisplayedWeather");
                    weathermanGetDisplayedWeather.InvokeFunc();
                }
                catch { weathermanGetDisplayedWeather = null; }
            }

            if (weathermanIsWeatherCustom == null)
            {
                try
                {
                    weathermanIsWeatherCustom = this.pluginInterface.GetIpcSubscriber<bool>("Weatherman.IsWeatherCustom");
                    weathermanIsWeatherCustom.InvokeFunc();
                }
                catch { weathermanIsWeatherCustom = null; }
            }

            weathermanChecked = true;
            ipcRetryAt = DateTime.UtcNow.AddSeconds(30);
        }
    }

    public string GetZoneName(uint territoryId)
    {
        try
        {
            var sheet = dataManager.GetExcelSheet<TerritoryType>();
            if (sheet == null) return $"Zone {territoryId}";
            var t = sheet.GetRow((ushort)territoryId);
            var name = t.PlaceNameZone.Value.Name.ToString();
            if (string.IsNullOrEmpty(name))
                name = t.PlaceName.Value.Name.ToString();
            if (string.IsNullOrEmpty(name))
                return $"Zone {territoryId}";
            return name;
        }
        catch
        {
            return $"Zone {territoryId}";
        }
    }

    private void OnCommand(string command, string args)
    {
        var parts = args.Trim().ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            this.configWindow.Toggle();
            return;
        }

        var pauseFile = GetPauseFilePath();

        switch (parts[0])
        {
            case "help":
                this.chatGui.Print("[Reshade] Commands:");
                this.chatGui.Print("  /reshade - Open config window");
                this.chatGui.Print("  /reshade pause - Pause shaders");
                this.chatGui.Print("  /reshade resume - Resume shaders");
                this.chatGui.Print("  /reshade toggle - Toggle shaders");
                this.chatGui.Print("  /reshade status - Show status");
                this.chatGui.Print("  /reshade perf - Show engine cost");
                this.chatGui.Print("  /reshade preset <path> - Swap preset");
                break;

            case "pause":
                if (File.Exists(pauseFile))
                    this.chatGui.Print("[Reshade] Already paused.");
                else
                {
                    File.WriteAllText(pauseFile, "");
                    this.chatGui.Print("[Reshade] Shaders paused.");
                }
                break;

            case "resume":
                if (!File.Exists(pauseFile))
                    this.chatGui.Print("[Reshade] Already running.");
                else
                {
                    File.Delete(pauseFile);
                    this.chatGui.Print("[Reshade] Shaders resumed.");
                }
                break;

            case "toggle":
                if (File.Exists(pauseFile))
                {
                    File.Delete(pauseFile);
                    this.chatGui.Print("[Reshade] Shaders resumed.");
                }
                else
                {
                    File.WriteAllText(pauseFile, "");
                    this.chatGui.Print("[Reshade] Shaders paused.");
                }
                break;

            case "status":
                var paused = File.Exists(pauseFile);
                this.chatGui.Print($"[Reshade] Shaders: {(paused ? "PAUSED" : "Running")}");
                this.chatGui.Print($"[Reshade] Current zone: {CurrentTerritoryName} ({CurrentTerritoryId})");
                break;

            case "perf":
                try
                {
                    double f = 1000000.0 / System.Diagnostics.Stopwatch.Frequency;
                    double ov = _pfOverlays * f, co = _pfContent * f, tg = _pfToggles * f,
                        mi = _pfMirror * f, wr = _pfWrite * f;
                    this.chatGui.Print($"[Reshade] perf µs last frame: overlays {ov:F0} | content {co:F0} | toggles {tg:F0} | mirror {mi:F0} | write {wr:F0} | total {ov + co + tg + mi + wr:F0} ({_pfKeys} keys, {_pfTechs} techs)");
                }
                catch { }
                break;

            case "preset":
                // Everything after "preset" is the path (quotes optional):
                // absolute, or relative to reshade-presets\.
                string want = args.Trim();
                if (want.StartsWith("preset", StringComparison.OrdinalIgnoreCase))
                    want = want.Substring(6).Trim();
                want = want.Trim().Trim('"').Trim();
                if (string.IsNullOrEmpty(want))
                {
                    this.chatGui.Print("[Reshade] Usage: /reshade preset \"xlAnimPresets\\xl_Realism.ini\"");
                    break;
                }
                string full = want;
                try
                {
                    if (!Path.IsPathRooted(full))
                    {
                        var gd = Path.GetDirectoryName(Environment.ProcessPath) ?? "";
                        var rel = Path.Combine(gd, "reshade-presets", full);
                        if (File.Exists(rel)) full = rel;
                        else
                        {
                            var rel2 = Path.Combine(gd, full);
                            if (File.Exists(rel2)) full = rel2;
                        }
                    }
                    if (!File.Exists(full))
                    {
                        this.chatGui.Print($"[Reshade] Preset not found: {want}");
                        break;
                    }
                    if (string.Equals(this.configWindow.SelectedPreset, full, StringComparison.OrdinalIgnoreCase))
                    {
                        this.chatGui.Print($"[Reshade] Already on {Path.GetFileName(full)}.");
                        break;
                    }
                    this.configWindow.ApplyPresetPath(full);
                    this.chatGui.Print($"[Reshade] Preset: {Path.GetFileName(full)}");
                }
                catch { this.chatGui.Print($"[Reshade] Preset not found: {want}"); }
                break;

            default:
                this.chatGui.Print($"[Reshade] Unknown command: {parts[0]}. Use /reshade help");
                break;
        }
    }

    public static string GetPauseFilePath()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
            throw new InvalidOperationException("Cannot determine game path.");
        var dir = Path.GetDirectoryName(processPath)!;
        return Path.Combine(dir, "ffxiv_reshade_pause");
    }

    public static string GetPresetSignalPath()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
            throw new InvalidOperationException("Cannot determine game path.");
        var dir = Path.GetDirectoryName(processPath)!;
        return Path.Combine(dir, "ffxiv_reshade_preset");
    }

    public static string GetAnimationSignalPath()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
            throw new InvalidOperationException("Cannot determine game path.");
        var dir = Path.GetDirectoryName(processPath)!;
        return Path.Combine(dir, "ffxiv_reshade_anim");
    }

    public int GetEorzeaSecondsPublic() => GetEorzeaSeconds();

    public bool IsWeathermanControllingTime()
    {
        try
        {
            if (Config.WeatherControlEnabled && Weather.IsTimeCustom())
                return true;
        }
        catch { }
        try
        {
            ConnectIPC();
            if (weathermanIsTimeCustom != null)
                return weathermanIsTimeCustom.InvokeFunc();
        }
        catch { }
        return false;
    }

    // True while an EXTERNAL Weatherman holds its own time override
    // (excludes the built-in freeze, unlike IsWeathermanControllingTime).
    public bool IsExternalWeathermanTimeCustom()
    {
        try
        {
            ConnectIPC();
            if (weathermanIsTimeCustom != null)
                return weathermanIsTimeCustom.InvokeFunc();
        }
        catch { }
        return false;
    }
    // True while an EXTERNAL Weatherman has its own weather override live.
    // The built-in override refuses to engage while this holds (no patch
    // fights); the user must clear the external override first.
    public bool IsExternalWeathermanWeatherCustom()
    {
        try
        {
            ConnectIPC();
            if (weathermanIsWeatherCustom != null)
                return weathermanIsWeatherCustom.InvokeFunc();
        }
        catch { }
        return false;
    }
}
