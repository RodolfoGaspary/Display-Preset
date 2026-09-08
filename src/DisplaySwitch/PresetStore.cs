using System.Text.Json;

namespace DisplaySwitch;

public static class PresetStore
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DisplaySwitch");

    public static string FilePath { get; } = Path.Combine(Folder, "presets.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    public static string BackupPath { get; } = Path.Combine(Folder, "presets.backup.json");

    /// <summary>Reads the store. A missing file means "no layouts yet", but a file that exists
    /// and cannot be read is an error and must be reported.
    ///
    /// Returning an empty store on a read failure would be silent data loss: every mutation is
    /// load-modify-save, so one transient lock or a truncated file would be written straight
    /// back as an empty store and take every saved layout with it.</summary>
    public static PresetFile Load()
    {
        if (!File.Exists(FilePath)) return new PresetFile();

        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<PresetFile>(json, Options) ?? new PresetFile();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fall back to the previous good copy rather than pretending there is nothing saved.
            if (File.Exists(BackupPath))
            {
                try
                {
                    var backup = File.ReadAllText(BackupPath);
                    if (JsonSerializer.Deserialize<PresetFile>(backup, Options) is { } recovered)
                        return recovered;
                }
                catch (Exception inner) when (inner is IOException or JsonException or UnauthorizedAccessException)
                {
                    // Nothing usable; report the original failure below.
                }
            }

            throw new DisplayException(
                $"Could not read your saved layouts from:\n{FilePath}\n\n{ex.Message}\n\n" +
                "Nothing has been changed. Fix or move that file and try again.");
        }
    }

    public static void Save(PresetFile file)
    {
        Directory.CreateDirectory(Folder);

        // Keep the last good copy before replacing it, so a bad write is always recoverable.
        try
        {
            if (File.Exists(FilePath)) File.Copy(FilePath, BackupPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missing backup is not a reason to refuse the save.
        }

        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, Options));
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>Merges presets from another store file, adding only names that are not already
    /// present. Never overwrites or deletes an existing layout. Returns what it added and skipped.</summary>
    public static (List<string> Added, List<string> Skipped) Import(string path)
    {
        var json = File.ReadAllText(path);
        var incoming = JsonSerializer.Deserialize<PresetFile>(json, Options)
            ?? throw new DisplayException($"{path} is not a DisplaySwitch preset file.");

        var file = Load();
        var added = new List<string>();
        var skipped = new List<string>();

        foreach (var preset in incoming.Presets)
        {
            var exists = file.Presets.Any(p =>
                string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
            if (exists) skipped.Add(preset.Name);
            else { file.Presets.Add(preset); added.Add(preset.Name); }
        }

        var adoptedStartup = false;
        if (string.IsNullOrWhiteSpace(file.StartupPresetName)
            && !string.IsNullOrWhiteSpace(incoming.StartupPresetName))
        {
            file.StartupPresetName = incoming.StartupPresetName;
            adoptedStartup = true;
        }

        if (added.Count > 0 || adoptedStartup) Save(file);
        return (added, skipped);
    }

    public static void Upsert(Preset preset)
    {
        var file = Load();
        var idx = file.Presets.FindIndex(p =>
            string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) file.Presets[idx] = preset;
        else file.Presets.Add(preset);
        Save(file);
    }

    public static void SetStartupPreset(string? name)
    {
        var file = Load();
        file.StartupPresetName = name;
        Save(file);
    }

    public static void Remove(string name)
    {
        var file = Load();
        file.Presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        Save(file);
    }

    public static Preset? Find(string name)
        => Load().Presets.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
