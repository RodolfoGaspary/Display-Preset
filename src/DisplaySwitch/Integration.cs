using Microsoft.Win32;

namespace DisplaySwitch;

/// <summary>Desktop shortcuts and run-at-login registration -- both strictly user-initiated
/// from the tray menu.</summary>
internal static class Integration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "DisplaySwitch";

    public static string ExePath => Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "DisplaySwitch.exe");

    /// <summary>The --startup flag marks a logon launch, which is the only time the tray is
    /// allowed to force the startup layout.</summary>
    public static string RunCommand => $"\"{ExePath}\" --startup";

    public static bool IsRunAtLogin()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is not null;
    }

    public static void SetRunAtLogin(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("Could not open the Run registry key.");
        if (enabled) key.SetValue(RunValue, RunCommand);
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    /// <summary>Brings an existing run-at-login entry up to date -- an entry written by an older
    /// build, or one left behind after the exe moved, would otherwise never pass --startup.</summary>
    public static void RepairRunAtLogin()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(RunValue) is not string existing) return;
        if (existing == RunCommand) return;

        // Only touch it if it still refers to this app; never clobber someone else's entry.
        if (existing.Contains("DisplaySwitch", StringComparison.OrdinalIgnoreCase))
            key.SetValue(RunValue, RunCommand);
    }

    /// <summary>Creates a .lnk on the desktop that applies one preset. Returns the path created.</summary>
    public static string CreateDesktopShortcut(string presetName)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var safeName = string.Concat(presetName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var linkPath = Path.Combine(desktop, $"Display - {safeName}.lnk");

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell is not available on this system.");
        dynamic shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Could not create WScript.Shell.");

        dynamic link = shell.CreateShortcut(linkPath);
        link.TargetPath = ExePath;
        link.Arguments = $"apply \"{presetName}\"";
        link.WorkingDirectory = Path.GetDirectoryName(ExePath) ?? "";
        link.IconLocation = $"{ExePath},0";
        link.Description = $"Switch displays to the \"{presetName}\" layout";
        link.Save();

        return linkPath;
    }
}
