using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace ReshadeController;

[Serializable]
public class ZonePreset
{
    public uint TerritoryId;
    public string PresetPath = string.Empty;
    public int ConditionSetIndex = -1;
}

[Serializable]
public class UniformValue
{
    public string Name = string.Empty;
    public string Type = "float";
    public float[] FloatValues = { 0f };
    public int[] IntValues = { 0 };
    public bool[] BoolValues = { false };
}

[Serializable]
public class TimePoint
{
    public int TimeSeconds;
    public List<UniformValue> UniformValues = new();
}

[Serializable]
public class ShaderAnimation
{
    public bool Enabled = false;
    public string EffectName = string.Empty;
    public string EffectPath = string.Empty;
    public List<TimePoint> TimePoints = new();
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public string DefaultPresetPath = string.Empty;
    public int DefaultConditionSet = -1;
    public int ToggleHotkeyKey = 0;
    public int ToggleHotkeyMod = 0;
    public int MenuHotkeyKey = 0;
    public int MenuHotkeyMod = 0;
    public int AnimatorHotkeyKey = 0;
    public int AnimatorHotkeyMod = 0;
    // Preset forced while OccupiedInCutSceneEvent holds. Empty = (none).
    public string CutscenePresetPath = "";
    // Preset forced while BoundByDuty holds. Empty = (none).
    public string DutyPresetPath = "";
    // Animator window: hide the background so nodes float over the game.
    public bool AnimatorNoBackground;
    // Animator window: keep the canvas grid while the background is off.
    public bool AnimatorKeepGrid;
    public float WindowBgOpacity = 1f;
    public List<ZonePreset> ZonePresets = new();
    public List<ShaderAnimation> ShaderAnimations = new();
    public List<string> FavoritePresets = new();
    public List<string> FavoriteEffects = new();
    public Dictionary<string, string> TechniqueNicknames = new();
    public float ShaderSplitX = -1;
    // Visible play area as game-window fractions (for the coords Visible
    // gate): areas covered by side menus etc. are treated as off-screen.
    public float ViewAreaLeft = 0f;
    public float ViewAreaTop = 0f;
    public float ViewAreaRight = 1f;
    public float ViewAreaBottom = 1f;
    public bool ViewAreaOutline;

    // Built-in weather/time override (Weather tab). Master switch gates
    // everything: while WeatherControlEnabled is false no patches are
    // applied and the override manager never even scans signatures.
    public bool WeatherControlEnabled;
    public bool WeatherCustomOn;
    public byte ForcedWeatherId;
    public bool TimeCustomOn;
    public int ForcedTimeSeconds;
    public bool DayCustomOn;
    public int ForcedDay = 1;
    // Seconds per full 24h time-play cycle (Weather tab play button).
    public int WeatherPlayCycleSeconds = 60;
    // When true, DLSS5 Neural Rendering is forced ON inside cutscenes and
    // restored OFF outside of them (automation owns NR while enabled).
    public bool DlssInCutscenes;
    // Developer settings (daylight recording etc.). Hidden unless on.
    public bool DeveloperMode;

    public void Save()
    {
        Service.PluginInterface.SavePluginConfig(this);
    }

    public ZonePreset? GetPresetForZone(uint territoryId)
    {
        return ZonePresets.Find(z => z.TerritoryId == territoryId);
    }

    public ZonePreset GetOrCreatePreset(uint territoryId)
    {
        var existing = GetPresetForZone(territoryId);
        if (existing != null) return existing;

        var preset = new ZonePreset { TerritoryId = territoryId };
        ZonePresets.Add(preset);
        return preset;
    }
}
