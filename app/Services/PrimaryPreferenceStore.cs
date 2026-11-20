using System.IO;
using System.Text.Json;
using DesktopSplitter.Models;

namespace DesktopSplitter.Services;

/// <summary>Separate atomic file: background preference updates cannot overwrite UI settings.</summary>
public sealed class PrimaryPreferenceStore
{
    public static PrimaryPreferenceStore Default { get; } = new(Path.Combine(SettingsStore.Directory, "primary-preferences.json"));
    private readonly string _path;
    private readonly object _gate = new();
    public string FilePath => _path;
    private static void Validate(PrimaryMonitorState state)
    {
        if (state.SchemaVersion != 1 || string.IsNullOrEmpty(state.PreSplitId) || string.IsNullOrEmpty(state.DesiredId) || string.IsNullOrEmpty(state.SplitPhysicalId))
            throw new InvalidDataException("Unknown primary preference schema/identity; retained for recovery.");
    }
    public PrimaryPreferenceStore(string path) => _path = path;
    public PrimaryMonitorState? Read() { lock (_gate) return ReadCore(); }
    private PrimaryMonitorState? ReadCore()
    {
        if (!File.Exists(_path)) return null;
        var state = JsonSerializer.Deserialize<PrimaryMonitorState>(File.ReadAllText(_path))
            ?? throw new InvalidDataException("Invalid primary preferences; refusing to replace them.");
        Validate(state);
        return state;
    }
    public void Write(PrimaryMonitorState state)
    {
        lock (_gate)
        {
            Validate(state);
            _ = ReadCore(); // Corrupt existing data is never silently overwritten.
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (File.Exists(_path) && !File.Exists(_path + ".bak")) File.Copy(_path, _path + ".bak");
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, true);
        }
    }
}
