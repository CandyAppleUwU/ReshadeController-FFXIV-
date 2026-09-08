using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using ECommons.EzHookManager;

namespace ReshadeController;

// Built-in minimal weather/time override, ported from the edited Weatherman
// fork (Services/MemoryManager.cs). Weatherman 2.x does NOT write game
// variables; it patches the render functions so true state (and BGM logic)
// stays untouched. This class copies only the current-zone subset:
//
//   weather: RenderWeatherPatch + RenderSunlightShadowPatch
//   time:    RenderTimePatch (time-of-day seconds)
//   date:    RenderMoonPatch ((day-1) * 86400)
//
// Skipped for v1 (noted in UI): weather-sound hook, BGM day/night hook,
// per-zone persistence, blacklists. Zone weather order comes from the zone
// .lvb (LvbFile.cs); unknown weathers fall back to index 0, same as upstream.
//
// GATING (two levels, both must pass before anything touches memory):
//   1. The master switch (Config.WeatherControlEnabled) + the external-
//      Weatherman IPC refusals in the UI. The manager itself never acts
//      unless told to.
//   2. No ECommons object exists until the first enable attempt (lazy).
//      Before constructing anything, the expected original bytes are
//      verified with our own sig scan + read. If the bytes are already
//      patched (external Weatherman) or the sigs moved (game update), the
//      enable fails with a Status string and ZERO ECommons log spam —
//      ECommons verifies (noisily) inside its own constructors, so it is
//      only ever constructed after our silent pre-check passes.
public sealed class WeatherOverride : IDisposable
{
    public const int SecondsInDay = 86400;

    private sealed class PatchDef
    {
        public string Sig = "";
        public int Offset;
        public byte[] Orig = Array.Empty<byte>();
    }

    // (sig, offset of patched bytes from match, expected original bytes)
    private static readonly PatchDef SunDef = new()
    {
        Sig = "49 0F BE 40 ?? 84 C0", Offset = 0,
        Orig = new byte[] { 0x49, 0x0F, 0xBE, 0x40, 0x24 },
    };
    private static readonly PatchDef WeatherDef = new()
    {
        Sig = "48 89 5C 24 ?? 57 48 83 EC 30 80 B9 ?? ?? ?? ?? ?? 49 8B F8 0F 29 74 24 ?? 48 8B D9 0F 28 F1", Offset = 0x55,
        Orig = new byte[] { 0x0F, 0xB6, 0x50, 0x26 },
    };
    private static readonly PatchDef TimeDef = new()
    {
        Sig = "48 89 5C 24 ?? 57 48 83 EC 30 4C 8B 15", Offset = 0x19,
        Orig = new byte[] { 0x4D, 0x8B, 0x8A, 0x78, 0x17, 0x00, 0x00 },
    };
    private static readonly PatchDef MoonDef = new()
    {
        Sig = "48 89 5C 24 ?? 57 48 83 EC 30 4C 8B 15", Offset = 0x12C,
        Orig = new byte[] { 0x49, 0x8B, 0x8A, 0x78, 0x17, 0x00, 0x00 },
    };

    // ECommons patch bytes (must pair with the Defs above).
    private const string SunPatchHex = "B8 00 00 00 00";
    private const string WeatherPatchHex = "B2 00 90 90";
    private const string TimePatchHex = "49 C7 C1 00 00 00 00";
    private const string MoonPatchHex = "48 C7 C1 00 00 00 00";

    private readonly ISigScanner scanner;

    private EzPatchWithPointer<uint>? sunPatch;
    private EzPatchWithPointer<byte>? weatherPatch;
    private EzPatchWithPointer<uint>? timePatch;
    private EzPatchWithPointer<uint>? moonPatch;

    public bool Available { get; private set; }
    public string Status { get; private set; } = "Disabled";

    private bool disposed;
    private uint[] weatherIndexMap = new uint[255];

    public WeatherOverride(ISigScanner scanner)
    {
        this.scanner = scanner;
    }

    // Silent pre-check: resolve the sig and compare the bytes ECommons
    // would verify. No objects, no logs on either outcome.
    private bool BytesAreOriginal(PatchDef def)
    {
        try
        {
            nint addr = scanner.ScanText(def.Sig) + def.Offset;
            for (int i = 0; i < def.Orig.Length; i++)
                if (Marshal.ReadByte(addr, i) != def.Orig[i])
                    return false;
            return true;
        }
        catch { return false; }
    }

    // Lazily build the ECommons objects after the silent pre-check passes
    // for all four sites. Returns false with Status set on any problem.
    private bool EnsurePatches()
    {
        try
        {
            if (sunPatch != null && weatherPatch != null && timePatch != null && moonPatch != null)
            {
                Available = !sunPatch.Disposed && !weatherPatch.Disposed && !timePatch.Disposed && !moonPatch.Disposed;
                return Available;
            }
        }
        catch { }

        if (!BytesAreOriginal(SunDef) || !BytesAreOriginal(WeatherDef) ||
            !BytesAreOriginal(TimeDef) || !BytesAreOriginal(MoonDef))
        {
            Status = "Render bytes already patched (external override?) or signatures moved (game update?) — clear any external override and retry.";
            Available = false;
            return false;
        }

        try
        {
            sunPatch = new(SunDef.Sig, SunDef.Offset, new("49 0F BE 40 24", SunPatchHex), 1, autoEnable: false);
            weatherPatch = new(WeatherDef.Sig, WeatherDef.Offset, new("0F B6 50 26", WeatherPatchHex), 1, autoEnable: false);
            timePatch = new(TimeDef.Sig, TimeDef.Offset, new("4D 8B 8A 78 17 00 00", TimePatchHex), 3, autoEnable: false);
            moonPatch = new(MoonDef.Sig, MoonDef.Offset, new("49 8B 8A 78 17 00 00", MoonPatchHex), 3, autoEnable: false);
        }
        catch (Exception ex)
        {
            Status = "Patch setup failed: " + ex.Message;
            Available = false;
            return false;
        }

        Available = true;
        Status = "Ready";
        return true;
    }

    private void DropPatches()
    {
        try { sunPatch?.Dispose(); } catch { }
        try { weatherPatch?.Dispose(); } catch { }
        try { timePatch?.Dispose(); } catch { }
        try { moonPatch?.Dispose(); } catch { }
        sunPatch = null;
        weatherPatch = null;
        timePatch = null;
        moonPatch = null;
        Available = false;
    }

    // Build the weather-id -> zone-index map for the sunlight patch.
    // Mirrors Weatherman's ZoneToWeatherIndexMap (unknown ids stay 0).
    private void BuildIndexMap(ushort territory)
    {
        weatherIndexMap = new uint[255];
        try
        {
            var list = GetZoneWeathers(territory);
            for (int i = 0; i < list.Count; i++)
                weatherIndexMap[list[i]] = (uint)i;
        }
        catch { }
    }

    public List<byte> GetZoneWeathers(ushort territory)
    {
        var weathers = new List<byte>();
        try
        {
            var terr = Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().GetRow(territory);
            var file = Service.DataManager.GetFile<LvbFile>($"bg/{terr.Bg}.lvb");
            if (file?.weatherIds == null) return weathers;
            foreach (var w in file.weatherIds)
                if (w > 0 && w < 255)
                    weathers.Add((byte)w);
            weathers.Sort();
        }
        catch { }
        return weathers;
    }

    // ---- weather (current zone) ----

    public bool EnableWeather(byte weatherId, ushort territory)
    {
        if (!EnsurePatches()) return false;
        try
        {
            BuildIndexMap(territory);
            if (!IsWeatherCustom())
            {
                weatherPatch!.Enable();
                sunPatch!.Enable();
            }
            if (!IsWeatherCustom())
            {
                Status = "Weather enable failed: patch rejected (external override engaged between check and apply?) — retry.";
                DropPatches();
                return false;
            }
            return SetWeather(weatherId);
        }
        catch (Exception ex)
        {
            Status = "Weather enable failed: " + ex.Message;
            return false;
        }
    }

    public bool SetWeather(byte weatherId)
    {
        try
        {
            if (weatherPatch == null || sunPatch == null || !IsWeatherCustom()) return false;
            weatherPatch.PointerValue = weatherId;
            if (sunPatch.Enabled)
                sunPatch.PointerValue = weatherIndexMap[weatherId];
            return true;
        }
        catch { return false; }
    }

    public void DisableWeather()
    {
        try
        {
            // Restore order matters: sunlight first so no frame renders
            // mixed old/new state, then weather.
            try { if (sunPatch != null && sunPatch.Enabled) sunPatch.Disable(); } catch { }
            try { if (weatherPatch != null && weatherPatch.Enabled) weatherPatch.Disable(); } catch { }
        }
        catch { }
    }

    public bool IsWeatherCustom()
    {
        try { return weatherPatch != null && weatherPatch.Enabled; }
        catch { return false; }
    }

    public byte GetCustomWeather()
    {
        try
        {
            if (weatherPatch != null) return weatherPatch.PointerValue;
        }
        catch { }
        return 0;
    }

    // ---- time (freeze time-of-day) ----

    public bool EnableTime(uint timeOfDaySeconds)
    {
        if (!EnsurePatches()) return false;
        try
        {
            if (!IsTimeCustom()) timePatch!.Enable();
            if (!IsTimeCustom())
            {
                Status = "Time enable failed: patch rejected (external override engaged between check and apply?) — retry.";
                return false;
            }
            return SetTime(timeOfDaySeconds);
        }
        catch (Exception ex)
        {
            Status = "Time enable failed: " + ex.Message;
            return false;
        }
    }

    public bool SetTime(uint timeOfDaySeconds)
    {
        try
        {
            if (timePatch == null || !IsTimeCustom()) return false;
            timePatch.PointerValue = timeOfDaySeconds % SecondsInDay;
            return true;
        }
        catch { return false; }
    }

    public void DisableTime()
    {
        try { if (timePatch != null && timePatch.Enabled) timePatch.Disable(); }
        catch { }
    }

    public bool IsTimeCustom()
    {
        try { return timePatch != null && timePatch.Enabled; }
        catch { return false; }
    }

    public uint GetTime()
    {
        try
        {
            if (timePatch != null) return timePatch.PointerValue;
        }
        catch { }
        return 0;
    }

    // ---- date (day of month 1..32, moon patch) ----

    public bool EnableDay(uint day)
    {
        if (!EnsurePatches()) return false;
        try
        {
            if (!IsDayCustom()) moonPatch!.Enable();
            if (!IsDayCustom())
            {
                Status = "Day enable failed: patch rejected (external override engaged between check and apply?) — retry.";
                return false;
            }
            return SetDay(day);
        }
        catch (Exception ex)
        {
            Status = "Day enable failed: " + ex.Message;
            return false;
        }
    }

    public bool SetDay(uint day)
    {
        try
        {
            if (moonPatch == null || !IsDayCustom()) return false;
            if (day < 1) day = 1;
            if (day > 32) day = 32;
            moonPatch.PointerValue = (day - 1) * (uint)SecondsInDay;
            return true;
        }
        catch { return false; }
    }

    public void DisableDay()
    {
        try { if (moonPatch != null && moonPatch.Enabled) moonPatch.Disable(); }
        catch { }
    }

    public bool IsDayCustom()
    {
        try { return moonPatch != null && moonPatch.Enabled; }
        catch { return false; }
    }

    public uint GetDay()
    {
        try
        {
            if (moonPatch != null) return moonPatch.PointerValue / (uint)SecondsInDay + 1;
        }
        catch { }
        return 1;
    }

    public void DisableAll()
    {
        DisableWeather();
        DisableTime();
        DisableDay();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { DisableAll(); } catch { }
        DropPatches();
    }
}
