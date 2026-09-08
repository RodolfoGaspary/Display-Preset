using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DisplaySwitch;

internal static class Program
{
    private const int AttachParentProcess = -1;
    private const int StdOutputHandle = -11;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        if (args.Length == 0) return RunTray();

        // Written into the Run key, so the tray can tell a logon launch from a manual one and
        // only force the startup layout in the former case.
        if (args.Length == 1 && args[0].Equals("--startup", StringComparison.OrdinalIgnoreCase))
            return RunTray(launchedAtLogon: true);

        AttachToParentConsole();
        try
        {
            return RunCommand(args);
        }
        catch (Exception ex)
        {
            Report(ex.Message, isError: true);
            return 1;
        }
    }

    private static int RunTray(bool launchedAtLogon = false)
    {
        // One tray icon is enough; a second launch just hands control back to the first.
        // Global\ so the check still holds across session and container boundaries. A second
        // tray icon quietly writing to a different, virtualised copy of the preset store is
        // exactly the confusion this prevents.
        using var mutex = new Mutex(
            initiallyOwned: true, @"Global\DisplaySwitch.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            // At logon, just step aside quietly rather than greeting the user with a dialog.
            if (launchedAtLogon) return 0;

            MessageBox.Show(
                "Display Switch is already running -- look for the monitor icon in the notification area "
                + "(you may need to expand the hidden-icons arrow).",
                "Display Switch", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Application.Run(new TrayApp(launchedAtLogon));
        return 0;
    }

    private static int RunCommand(string[] args)
    {
        var command = args[0].ToLowerInvariant().TrimStart('-', '/');
        var rest = args.Skip(1).ToArray();

        switch (command)
        {
            case "toggle":
                return Toggle();

            case "apply":
            case "switch":
                if (rest.Length == 0) { Report("Usage: DisplaySwitch apply <name>", true); return 2; }
                return Apply(string.Join(' ', rest));

            case "save":
                if (rest.Length == 0) { Report("Usage: DisplaySwitch save <name>", true); return 2; }
                return SaveCurrent(string.Join(' ', rest));

            case "delete":
                if (rest.Length == 0) { Report("Usage: DisplaySwitch delete <name>", true); return 2; }
                PresetStore.Remove(string.Join(' ', rest));
                Report($"Deleted \"{string.Join(' ', rest)}\".");
                return 0;

            case "list":
                return List();

            case "where":
                Report($"Layouts are stored in:\n  {PresetStore.FilePath}\n" +
                       $"Backup of the previous version:\n  {PresetStore.BackupPath}");
                return 0;

            case "import":
            {
                if (rest.Length == 0) { Report("Usage: DisplaySwitch import <file.json>", true); return 2; }
                var source = string.Join(' ', rest);
                if (!File.Exists(source)) { Report($"No such file: {source}", true); return 1; }

                var (added, skipped) = PresetStore.Import(source);
                var lines = new List<string>();
                lines.Add(added.Count > 0
                    ? "Added: " + string.Join(", ", added)
                    : "Added nothing -- every layout in that file was already here.");
                if (skipped.Count > 0)
                    lines.Add("Left alone (already present): " + string.Join(", ", skipped));
                lines.Add($"Store: {PresetStore.FilePath}");
                Report(string.Join("\n", lines));
                return 0;
            }

            case "startup":
            {
                if (rest.Length == 0)
                {
                    var name = PresetStore.Load().StartupPresetName;
                    Report(name is null
                        ? "No startup layout set -- Windows decides."
                        : $"Startup layout: {name}");
                    return 0;
                }

                var wanted = string.Join(' ', rest);
                if (wanted.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    PresetStore.SetStartupPreset(null);
                    Report("Startup layout cleared.");
                    return 0;
                }

                if (PresetStore.Find(wanted) is null)
                {
                    Report($"No layout named \"{wanted}\".", true);
                    return 1;
                }

                PresetStore.SetStartupPreset(wanted);
                Integration.SetRunAtLogin(true);
                Report($"\"{wanted}\" will be applied at every logon.");
                return 0;
            }

            case "icon":
            {
                var folder = rest.Length > 0 ? string.Join(' ', rest) : Directory.GetCurrentDirectory();
                var written = LogoExporter.Export(folder);
                Report("Wrote:\n  " + string.Join("\n  ", written));
                return 0;
            }

            case "current":
                return Current();

            case "help":
            case "h":
            case "?":
                Report(HelpText);
                return 0;

            default:
                Report($"Unknown command \"{args[0]}\".\n\n{HelpText}", true);
                return 2;
        }
    }

    private static int Toggle()
    {
        var presets = PresetStore.Load().Presets;
        if (presets.Count == 0) { Report("No layouts saved yet. Run: DisplaySwitch save <name>", true); return 1; }
        if (presets.Count == 1) return Apply(presets[0].Name);

        var current = DisplayService.CurrentFingerprint();
        var idx = presets.FindIndex(p => p.Fingerprint == current);
        var next = idx < 0 ? presets[0] : presets[(idx + 1) % presets.Count];
        return Apply(next.Name);
    }

    private static int Apply(string name)
    {
        var preset = PresetStore.Find(name);
        if (preset is null)
        {
            Report($"No layout named \"{name}\". Known layouts: " +
                   string.Join(", ", PresetStore.Load().Presets.Select(p => p.Name)), true);
            return 1;
        }
        DisplayService.Apply(preset);
        Report(DisplayService.LastApplyWasTemporary is { } warning
            ? $"Switched to \"{preset.Name}\".\nWARNING: {warning}"
            : $"Switched to \"{preset.Name}\". Saved to the Windows display database.");
        return 0;
    }

    private static int SaveCurrent(string name)
    {
        var preset = DisplayService.Capture(name);
        PresetStore.Upsert(preset);
        Report($"Saved \"{name}\": {preset.Summary()}");
        return 0;
    }

    private static int List()
    {
        var presets = PresetStore.Load().Presets;
        if (presets.Count == 0) { Report("No layouts saved yet."); return 0; }

        var current = DisplayService.CurrentFingerprint();
        var sb = new StringBuilder();
        foreach (var preset in presets)
        {
            var marker = preset.Fingerprint == current ? "*" : " ";
            sb.AppendLine($"{marker} {preset.Name,-12} {preset.Summary()}");
        }
        sb.Append($"\nStored in {PresetStore.FilePath}");
        Report(sb.ToString());
        return 0;
    }

    private static int Current()
    {
        var (paths, modes) = DisplayService.Query();
        var sb = new StringBuilder("Active displays:\n");
        foreach (var path in paths)
        {
            if ((path.flags & Ccd.DISPLAYCONFIG_PATH_ACTIVE) == 0) continue;
            var (_, friendly) = DisplayService.GetTargetName(path.targetInfo.adapterId, path.targetInfo.id);
            var idx = path.sourceInfo.modeInfoIdx;
            var geometry = idx != Ccd.DISPLAYCONFIG_PATH_MODE_IDX_INVALID && idx < modes.Length
                ? $"{modes[idx].sourceMode.width}x{modes[idx].sourceMode.height} " +
                  $"at ({modes[idx].sourceMode.position.x},{modes[idx].sourceMode.position.y})"
                : "unknown geometry";
            sb.AppendLine($"  {(string.IsNullOrWhiteSpace(friendly) ? "Display" : friendly),-18} " +
                          $"{geometry} @ {path.targetInfo.refreshRate.AsHz:0.##} Hz");
        }

        var match = PresetStore.Load().Presets
            .FirstOrDefault(p => p.Fingerprint == DisplayService.ComputeFingerprint(paths, modes));
        sb.Append(match is null ? "\nThis layout is not saved as a preset." : $"\nMatches preset: {match.Name}");
        Report(sb.ToString());
        return 0;
    }

    private const string HelpText = """
        DisplaySwitch -- one-click monitor layout switching.

          DisplaySwitch                 Run the tray icon (left-click it to toggle layouts)
          DisplaySwitch save <name>     Save the current layout under <name>
          DisplaySwitch apply <name>    Switch to a saved layout
          DisplaySwitch toggle          Cycle to the next saved layout
          DisplaySwitch list            List saved layouts (* marks the active one)
          DisplaySwitch current         Show the displays that are on right now
          DisplaySwitch delete <name>   Forget a saved layout
          DisplaySwitch startup <name>  Auto-restore a layout at logon and after waking
                                        ("none" to switch that off)
          DisplaySwitch where           Show where layouts are stored on disk
          DisplaySwitch import <file>   Merge layouts from another store file (never overwrites)

        Typical setup:
          1. Arrange all three monitors the way you like, then:  DisplaySwitch save PC
          2. Switch to TV-only in Windows display settings, then: DisplaySwitch save TV
          3. From then on: DisplaySwitch toggle  (or left-click the tray icon)
          4. If Windows keeps bringing the wrong layout back: DisplaySwitch startup PC
        """;

    private static bool _hasConsole;

    private static void AttachToParentConsole()
    {
        // A WinExe has no console of its own. If the launcher handed one down -- an inherited
        // console handle, a pipe or a file redirect -- write straight to it; that also keeps an
        // explicit redirect from being overridden by AttachConsole.
        var handle = GetStdHandle(StdOutputHandle);
        if (handle != IntPtr.Zero && handle != new IntPtr(-1))
        {
            _hasConsole = true;
            return;
        }

        // Otherwise borrow the console of the process that launched us, if there is one.
        if (AttachConsole(AttachParentProcess))
        {
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdout);
            _hasConsole = true;
        }
    }

    private static void Report(string message, bool isError = false)
    {
        if (_hasConsole)
        {
            try
            {
                Console.WriteLine(message);
                return;
            }
            catch (IOException) { /* fall through to the dialog */ }
        }

        if (isError)
        {
            MessageBox.Show(message, "Display Switch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
