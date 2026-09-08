namespace DisplaySwitch;

/// <summary>An adapter LUID as it was at save time, keyed by its stable device path.</summary>
public sealed class AdapterRef
{
    public ulong Luid { get; set; }
    public string DevicePath { get; set; } = "";
}

/// <summary>A monitor that was active in the preset. Informational + used for the summary text.</summary>
public sealed class TargetRef
{
    public string DevicePath { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    public uint Width { get; set; }
    public uint Height { get; set; }
    public double RefreshHz { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public bool IsPrimary { get; set; }
}

public sealed class Preset
{
    public string Name { get; set; } = "";
    public DateTime SavedUtc { get; set; }

    /// <summary>Raw byte image of the DISPLAYCONFIG_PATH_INFO array (active paths only).</summary>
    public string PathsBase64 { get; set; } = "";

    /// <summary>Raw byte image of the DISPLAYCONFIG_MODE_INFO array.</summary>
    public string ModesBase64 { get; set; } = "";

    public List<AdapterRef> Adapters { get; set; } = new();
    public List<TargetRef> Targets { get; set; } = new();

    /// <summary>Stable description of the layout, used to tell which preset is currently live.</summary>
    public string Fingerprint { get; set; } = "";

    public string Summary()
    {
        if (Targets.Count == 0) return "(no monitors recorded)";
        var parts = Targets.Select(t =>
        {
            var name = string.IsNullOrWhiteSpace(t.FriendlyName) ? "Display" : t.FriendlyName;
            var star = t.IsPrimary ? "*" : "";
            return $"{name}{star} {t.Width}x{t.Height}@{t.RefreshHz:0.##}Hz";
        });
        return string.Join(", ", parts);
    }
}

public sealed class PresetFile
{
    public List<Preset> Presets { get; set; } = new();

    /// <summary>Layout to force at logon, or null to leave whatever Windows restored.
    /// Windows can pick the wrong stored layout when displays wake in a different order at
    /// boot, so this is the deterministic override.</summary>
    public string? StartupPresetName { get; set; }
}
