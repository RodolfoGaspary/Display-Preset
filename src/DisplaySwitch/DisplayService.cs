using System.Runtime.InteropServices;
using static DisplaySwitch.Ccd;

namespace DisplaySwitch;

public sealed class DisplayException : Exception
{
    public DisplayException(string message) : base(message) { }
}

internal static class DisplayService
{
    /// <summary>Set when the last apply succeeded but could not be written to Windows'
    /// persistence database, meaning it will not survive a restart. Null when all is well.</summary>
    public static string? LastApplyWasTemporary { get; private set; }


    /// <summary>Reads the display configuration. Only active paths are captured -- restoring an
    /// active-only path list tells Windows to switch every other output off, which is exactly
    /// what "just the TV" means.</summary>
    public static (DISPLAYCONFIG_PATH_INFO[] Paths, DISPLAYCONFIG_MODE_INFO[] Modes) Query(
        uint flags = QDC_ONLY_ACTIVE_PATHS)
    {
        var rc = GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount);
        if (rc != ERROR_SUCCESS)
            throw new DisplayException($"GetDisplayConfigBufferSizes failed: {DescribeError(rc)}");

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

        rc = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        if (rc != ERROR_SUCCESS)
            throw new DisplayException($"QueryDisplayConfig failed: {DescribeError(rc)}");

        // The call reports how many entries it actually filled; the rest is garbage.
        Array.Resize(ref paths, (int)pathCount);
        Array.Resize(ref modes, (int)modeCount);
        return (paths, modes);
    }

    public static (string DevicePath, string FriendlyName) GetTargetName(LUID adapterId, uint targetId)
    {
        var req = new DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DEVICE_INFO_GET_TARGET_NAME,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                adapterId = adapterId,
                id = targetId,
            },
        };
        var rc = DisplayConfigGetDeviceInfo(ref req);
        if (rc != ERROR_SUCCESS) return ($"unknown:{adapterId.Value:x}:{targetId}", "");
        return (req.monitorDevicePath ?? "", req.monitorFriendlyDeviceName ?? "");
    }

    public static string GetAdapterPath(LUID adapterId)
    {
        var req = new DISPLAYCONFIG_ADAPTER_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DEVICE_INFO_GET_ADAPTER_NAME,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_ADAPTER_NAME>(),
                adapterId = adapterId,
            },
        };
        var rc = DisplayConfigGetDeviceInfo(ref req);
        return rc == ERROR_SUCCESS ? req.adapterDevicePath ?? "" : "";
    }

    private static bool TryGetSourceMode(
        DISPLAYCONFIG_MODE_INFO[] modes, uint idx, out DISPLAYCONFIG_SOURCE_MODE mode)
    {
        mode = default;
        if (idx == DISPLAYCONFIG_PATH_MODE_IDX_INVALID || idx >= modes.Length) return false;
        if (modes[idx].infoType != DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE) return false;
        mode = modes[idx].sourceMode;
        return true;
    }

    /// <summary>Describes a layout precisely enough to recognise it again: which monitors are on,
    /// at what resolution, refresh rate and desktop position.</summary>
    public static string ComputeFingerprint(
        DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes)
    {
        var lines = new List<string>();
        foreach (var path in paths)
        {
            if ((path.flags & DISPLAYCONFIG_PATH_ACTIVE) == 0) continue;

            var (devicePath, _) = GetTargetName(path.targetInfo.adapterId, path.targetInfo.id);
            var geometry = TryGetSourceMode(modes, path.sourceInfo.modeInfoIdx, out var src)
                ? $"{src.width}x{src.height}+{src.position.x}+{src.position.y}"
                : "no-mode";
            lines.Add($"{devicePath}|{geometry}|{path.targetInfo.refreshRate.AsHz:0.###}");
        }
        lines.Sort(StringComparer.Ordinal);
        return string.Join(";", lines);
    }

    public static string CurrentFingerprint()
    {
        var (paths, modes) = Query();
        return ComputeFingerprint(paths, modes);
    }

    public static Preset Capture(string name)
    {
        var (paths, modes) = Query();
        if (paths.Length == 0)
            throw new DisplayException("No active displays were reported -- nothing to save.");

        var preset = new Preset
        {
            Name = name,
            SavedUtc = DateTime.UtcNow,
            PathsBase64 = Convert.ToBase64String(ToBytes(paths)),
            ModesBase64 = Convert.ToBase64String(ToBytes(modes)),
            Fingerprint = ComputeFingerprint(paths, modes),
        };

        // Record every adapter LUID with its stable device path. LUIDs are reassigned on reboot
        // and driver restarts, so they must be re-resolved before the preset is applied.
        var seen = new HashSet<ulong>();
        void RecordAdapter(LUID luid)
        {
            if (!seen.Add(luid.Value)) return;
            preset.Adapters.Add(new AdapterRef { Luid = luid.Value, DevicePath = GetAdapterPath(luid) });
        }

        foreach (var path in paths)
        {
            RecordAdapter(path.sourceInfo.adapterId);
            RecordAdapter(path.targetInfo.adapterId);

            if ((path.flags & DISPLAYCONFIG_PATH_ACTIVE) == 0) continue;

            var (devicePath, friendly) = GetTargetName(path.targetInfo.adapterId, path.targetInfo.id);
            var hasMode = TryGetSourceMode(modes, path.sourceInfo.modeInfoIdx, out var src);
            preset.Targets.Add(new TargetRef
            {
                DevicePath = devicePath,
                FriendlyName = friendly,
                Width = hasMode ? src.width : 0,
                Height = hasMode ? src.height : 0,
                RefreshHz = path.targetInfo.refreshRate.AsHz,
                X = hasMode ? src.position.x : 0,
                Y = hasMode ? src.position.y : 0,
                IsPrimary = hasMode && src.position is { x: 0, y: 0 },
            });
        }

        foreach (var mode in modes) RecordAdapter(mode.adapterId);

        return preset;
    }

    public static void Apply(Preset preset)
    {
        var paths = FromBytes<DISPLAYCONFIG_PATH_INFO>(Convert.FromBase64String(preset.PathsBase64));
        var modes = FromBytes<DISPLAYCONFIG_MODE_INFO>(Convert.FromBase64String(preset.ModesBase64));

        if (paths.Length == 0)
            throw new DisplayException($"Preset \"{preset.Name}\" contains no display paths.");

        RemapAdapterIds(preset, paths, modes);

        // Validate before applying so a stale preset fails loudly instead of blanking the desktop.
        var rc = SetDisplayConfig(
            (uint)paths.Length, paths, (uint)modes.Length, modes,
            SDC_VALIDATE | SDC_USE_SUPPLIED_DISPLAY_CONFIG);
        if (rc != ERROR_SUCCESS)
        {
            throw new DisplayException(
                $"Windows rejected preset \"{preset.Name}\": {DescribeError(rc)}\n\n" +
                "If you have plugged, unplugged or swapped a monitor since saving it, " +
                "set the layout up by hand once and save the preset again.");
        }

        // SDC_ALLOW_CHANGES is intentionally NOT set here. With it, Windows is free to
        // substitute its own timings and quietly drops the 144 Hz PC-mode signal to TV-mode.
        //
        // SDC_SAVE_TO_DATABASE, on the other hand, is required: it records the layout in the
        // database Windows replays at boot and on wake. Without it the switch reverts on the
        // next restart, because Windows still holds its own older entry for this monitor set.
        const uint applyFlags = SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG;
        rc = SetDisplayConfig(
            (uint)paths.Length, paths, (uint)modes.Length, modes, applyFlags | SDC_SAVE_TO_DATABASE);

        if (rc != ERROR_SUCCESS)
        {
            // Some drivers refuse the persist flag for a given topology. Applying without it is
            // still better than not switching at all -- the layout just will not survive a reboot.
            var persistError = rc;
            rc = SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, applyFlags);
            if (rc != ERROR_SUCCESS)
            {
                throw new DisplayException(
                    $"Applying preset \"{preset.Name}\" failed: {DescribeError(rc)}");
            }
            LastApplyWasTemporary =
                $"Windows would not persist this layout ({DescribeError(persistError)}), " +
                "so it may revert after a restart.";
            return;
        }

        LastApplyWasTemporary = null;
    }

    /// <summary>Rewrites the saved adapter LUIDs to the values this boot is using, matching them
    /// up by adapter device path.</summary>
    private static void RemapAdapterIds(
        Preset preset, DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes)
    {
        if (preset.Adapters.Count == 0) return;

        // Current LUID for each adapter device path present right now.
        var currentByPath = new Dictionary<string, LUID>(StringComparer.OrdinalIgnoreCase);
        var (livePaths, liveModes) = Query(QDC_ALL_PATHS);
        void Note(LUID luid)
        {
            var devicePath = GetAdapterPath(luid);
            if (!string.IsNullOrEmpty(devicePath)) currentByPath.TryAdd(devicePath, luid);
        }
        foreach (var p in livePaths) { Note(p.sourceInfo.adapterId); Note(p.targetInfo.adapterId); }
        foreach (var m in liveModes) Note(m.adapterId);

        var remap = new Dictionary<ulong, LUID>();
        foreach (var adapter in preset.Adapters)
        {
            if (string.IsNullOrEmpty(adapter.DevicePath)) continue;
            if (currentByPath.TryGetValue(adapter.DevicePath, out var live) && live.Value != adapter.Luid)
                remap[adapter.Luid] = live;
        }
        if (remap.Count == 0) return;

        LUID Fix(LUID luid) => remap.TryGetValue(luid.Value, out var live) ? live : luid;

        for (var i = 0; i < paths.Length; i++)
        {
            paths[i].sourceInfo.adapterId = Fix(paths[i].sourceInfo.adapterId);
            paths[i].targetInfo.adapterId = Fix(paths[i].targetInfo.adapterId);
        }
        for (var i = 0; i < modes.Length; i++)
            modes[i].adapterId = Fix(modes[i].adapterId);
    }

    private static byte[] ToBytes<T>(T[] array) where T : struct
        => MemoryMarshal.AsBytes(new ReadOnlySpan<T>(array)).ToArray();

    private static T[] FromBytes<T>(byte[] bytes) where T : struct
        => MemoryMarshal.Cast<byte, T>(bytes).ToArray();
}
