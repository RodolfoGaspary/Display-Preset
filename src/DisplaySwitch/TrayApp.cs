using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DisplaySwitch;

internal sealed class TrayApp : ApplicationContext
{
    /// <summary>How long to let displays wake and enumerate before forcing the layout.</summary>
    private const int SettleMs = 8000;

    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu = new();
    private Icon? _icon;
    private bool _restorePending;
    private bool _storeErrorShown;

    public TrayApp(bool launchedAtLogon = false)
    {
        _tray = new NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _tray.MouseClick += OnTrayClick;
        _menu.Opening += (_, _) => RebuildMenu();

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        RefreshIcon();
        RebuildMenu();

        try
        {
            Integration.RepairRunAtLogin();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            // Not worth blocking startup over.
        }

        if (launchedAtLogon) ScheduleRestore("at startup");

        if (LoadOrWarn().Presets.Count < 2)
        {
            _tray.ShowBalloonTip(
                10_000,
                "Display Switch",
                "Arrange your monitors the way you want, then right-click this icon and pick "
                + "\"Save current layout as...\". Do that once per layout.",
                ToolTipIcon.Info);
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => RefreshIcon();

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // Waking is the common case for the layout coming back wrong: monitors re-handshake in
        // whatever order they manage, and Windows can settle on a different stored arrangement.
        if (e.Mode == PowerModes.Resume) ScheduleRestore("after waking");
    }

    /// <summary>Force the chosen layout once the displays have settled, at logon or after a wake.
    ///
    /// Windows keys its stored layouts by which monitors it can see, and at boot or on resume it
    /// can see them in a different order, or miss a slow one entirely -- an HDMI TV takes several
    /// seconds to hand over its EDID -- so it restores the wrong entry and the primary monitor
    /// and refresh rate come back wrong. Re-applying after the dust settles makes it
    /// deterministic. Nothing happens if the layout is already correct.</summary>
    private void ScheduleRestore(string trigger)
    {
        var file = LoadOrWarn();
        if (string.IsNullOrWhiteSpace(file.StartupPresetName)) return;

        var target = file.Presets.FirstOrDefault(p =>
            string.Equals(p.Name, file.StartupPresetName, StringComparison.OrdinalIgnoreCase));
        if (target is null) return;

        // A wake can raise several events; one pending restore is enough.
        if (_restorePending) return;
        _restorePending = true;

        var timer = new System.Windows.Forms.Timer { Interval = SettleMs };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            _restorePending = false;

            // Nothing to do if Windows already landed on the right layout.
            if (SafeCurrentFingerprint() == target.Fingerprint) return;

            try
            {
                DisplayService.Apply(target);
                RefreshIcon();
                Notify("Display Switch", $"Restored the \"{target.Name}\" layout {trigger}.");
            }
            catch (DisplayException ex)
            {
                // A balloon, never a modal dialog -- nobody wants a stuck dialog at logon.
                Notify("Could not restore your layout", ex.Message);
            }
        };
        timer.Start();
    }

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) Toggle();
    }

    private void Toggle()
    {
        var presets = LoadOrWarn().Presets;
        if (presets.Count == 0)
        {
            Notify("No layouts saved yet", "Right-click the icon and save your current layout first.");
            return;
        }
        if (presets.Count == 1)
        {
            ApplyPreset(presets[0]);
            return;
        }

        var current = SafeCurrentFingerprint();
        var idx = presets.FindIndex(p => p.Fingerprint == current);
        var next = idx < 0 ? presets[0] : presets[(idx + 1) % presets.Count];
        ApplyPreset(next);
    }

    private void ApplyPreset(Preset preset)
    {
        try
        {
            DisplayService.Apply(preset);
            RefreshIcon();
            Notify("Display Switch", DisplayService.LastApplyWasTemporary is { } warning
                ? $"Switched to \"{preset.Name}\". {warning}"
                : $"Switched to \"{preset.Name}\".");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Display Switch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SavePreset(string name)
    {
        try
        {
            var preset = DisplayService.Capture(name);
            PresetStore.Upsert(preset);
            RefreshIcon();
            Notify($"Saved \"{name}\"", preset.Summary());
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Display Switch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Reads the store for display purposes. If the file is there but unreadable, say so
    /// once and carry on showing an empty menu -- never write anything back, or the bad read
    /// would be persisted as "no layouts saved".</summary>
    private PresetFile LoadOrWarn()
    {
        try
        {
            return PresetStore.Load();
        }
        catch (DisplayException ex)
        {
            if (!_storeErrorShown)
            {
                _storeErrorShown = true;
                MessageBox.Show(ex.Message, "Display Switch",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return new PresetFile();
        }
    }

    private static string SafeCurrentFingerprint()
    {
        try { return DisplayService.CurrentFingerprint(); }
        catch (DisplayException) { return ""; }
    }

    private void RefreshIcon()
    {
        var presets = LoadOrWarn().Presets;
        var current = SafeCurrentFingerprint();
        var active = presets.FirstOrDefault(p => p.Fingerprint == current);

        var old = _icon;
        _icon = IconFactory.BuildTray(active is null ? null : IconFactory.DistinctLabel(active, presets));
        _tray.Icon = _icon;
        old?.Dispose();

        var tip = active is null
            ? "Display Switch - current layout is not saved as a preset"
            : $"Display Switch - {active.Name}";
        // NotifyIcon truncates anything past 63 characters.
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    private void RebuildMenu()
    {
        _menu.Items.Clear();

        var presets = LoadOrWarn().Presets;
        var current = SafeCurrentFingerprint();

        if (presets.Count == 0)
        {
            _menu.Items.Add(new ToolStripMenuItem("(no layouts saved yet)") { Enabled = false });
        }
        else
        {
            foreach (var preset in presets)
            {
                var isActive = preset.Fingerprint == current;
                var item = new ToolStripMenuItem(preset.Name)
                {
                    Checked = isActive,
                    ToolTipText = preset.Summary(),
                };
                var captured = preset;
                item.Click += (_, _) => ApplyPreset(captured);
                _menu.Items.Add(item);
            }
        }

        _menu.Items.Add(new ToolStripSeparator());

        var save = new ToolStripMenuItem("Save current layout as...");
        foreach (var preset in presets)
        {
            var captured = preset;
            var overwrite = new ToolStripMenuItem($"Overwrite \"{preset.Name}\"");
            overwrite.Click += (_, _) => SavePreset(captured.Name);
            save.DropDownItems.Add(overwrite);
        }
        if (presets.Count > 0) save.DropDownItems.Add(new ToolStripSeparator());
        var newPreset = new ToolStripMenuItem("New layout...");
        newPreset.Click += (_, _) =>
        {
            var name = InputDialog.Ask(
                "Save layout",
                "Name this layout (for example: PC or TV)",
                presets.Count == 0 ? "PC" : "");
            if (name is not null) SavePreset(name);
        };
        save.DropDownItems.Add(newPreset);
        _menu.Items.Add(save);

        if (presets.Count > 0)
        {
            var delete = new ToolStripMenuItem("Delete layout");
            foreach (var preset in presets)
            {
                var captured = preset;
                var item = new ToolStripMenuItem(captured.Name);
                item.Click += (_, _) =>
                {
                    var answer = MessageBox.Show(
                        $"Delete the layout \"{captured.Name}\"?", "Display Switch",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (answer == DialogResult.Yes)
                    {
                        PresetStore.Remove(captured.Name);
                        RefreshIcon();
                    }
                };
                delete.DropDownItems.Add(item);
            }
            _menu.Items.Add(delete);

            var shortcuts = new ToolStripMenuItem("Create desktop shortcuts");
            shortcuts.Click += (_, _) => CreateShortcuts(presets);
            _menu.Items.Add(shortcuts);
        }

        _menu.Items.Add(new ToolStripSeparator());

        if (presets.Count > 0)
        {
            var startupPreset = LoadOrWarn().StartupPresetName;
            var atStartup = new ToolStripMenuItem("Auto-restore this layout")
            {
                ToolTipText = "Put this layout back at logon and after waking from sleep, "
                              + "if Windows brings it back wrong.",
            };

            var none = new ToolStripMenuItem("(leave it to Windows)")
            {
                Checked = string.IsNullOrWhiteSpace(startupPreset),
            };
            none.Click += (_, _) => PresetStore.SetStartupPreset(null);
            atStartup.DropDownItems.Add(none);
            atStartup.DropDownItems.Add(new ToolStripSeparator());

            foreach (var preset in presets)
            {
                var captured = preset;
                var item = new ToolStripMenuItem(captured.Name)
                {
                    Checked = string.Equals(
                        captured.Name, startupPreset, StringComparison.OrdinalIgnoreCase),
                };
                item.Click += (_, _) =>
                {
                    PresetStore.SetStartupPreset(captured.Name);
                    if (!SafeIsRunAtLogin())
                    {
                        try
                        {
                            Integration.SetRunAtLogin(true);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(ex.Message, "Display Switch",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    }
                };
                atStartup.DropDownItems.Add(item);
            }

            _menu.Items.Add(atStartup);
        }

        var startup = new ToolStripMenuItem("Start with Windows") { Checked = SafeIsRunAtLogin() };
        startup.Click += (_, _) =>
        {
            try
            {
                Integration.SetRunAtLogin(!startup.Checked);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Display Switch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        _menu.Items.Add(startup);

        var openFolder = new ToolStripMenuItem("Open presets folder");
        openFolder.Click += (_, _) =>
        {
            Directory.CreateDirectory(PresetStore.Folder);
            Process.Start(new ProcessStartInfo(PresetStore.Folder) { UseShellExecute = true });
        };
        _menu.Items.Add(openFolder);

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitThread();
        _menu.Items.Add(exit);
    }

    private void CreateShortcuts(List<Preset> presets)
    {
        try
        {
            var created = presets.Select(p => Integration.CreateDesktopShortcut(p.Name)).ToList();
            MessageBox.Show(
                "Created on your desktop:\n\n" + string.Join("\n", created.Select(Path.GetFileName)) +
                "\n\nRight-click a shortcut > Properties > Shortcut key to assign a hotkey.",
                "Display Switch", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not create the shortcuts: {ex.Message}\n\n" +
                $"You can make one by hand pointing at:\n{Integration.ExePath} apply \"<name>\"",
                "Display Switch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static bool SafeIsRunAtLogin()
    {
        try { return Integration.IsRunAtLogin(); }
        catch { return false; }
    }

    private void Notify(string title, string message)
        => _tray.ShowBalloonTip(4000, title, message, ToolTipIcon.None);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _tray.Visible = false;
            _tray.Dispose();
            _menu.Dispose();
            _icon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
