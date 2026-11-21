using System.IO;
using System.Text.Json;

namespace DesktopSplitter.Rdp;

/// <summary>Separate from split/recovery settings; a corrupt catalog is never overwritten.</summary>
public sealed class RdpConnectionStore
{
    private static readonly object Gate = new();
    private readonly string _path;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public RdpConnectionStore(string path) => _path = path;

    public RdpCatalog Load()
    {
        lock (Gate)
        {
            if (!File.Exists(_path)) return new();
            var value = JsonSerializer.Deserialize<RdpCatalog>(File.ReadAllText(_path))
                ?? throw new InvalidDataException("Invalid RDP connection catalog.");
            Validate(value);
            return value;
        }
    }

    private static void Validate(RdpCatalog value)
    {
        if (value.SchemaVersion != 1 || value.Connections is null || value.Connections.Any(x => x is null) ||
            value.Connections.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != value.Connections.Count)
            throw new InvalidDataException("Unsupported or ambiguous RDP connection catalog. The original file was retained.");
        foreach (var connection in value.Connections)
        {
            connection.Validate();
            if (connection.Layouts is null || connection.Layouts.Any(x => x is null || x.SelectedIds is null ||
                x.SelectedIds.Count == 0 || x.SelectedIds.Any(string.IsNullOrWhiteSpace) ||
                x.SelectedIds.Distinct(StringComparer.Ordinal).Count() != x.SelectedIds.Count ||
                !x.SelectedIds.Contains(x.PrimaryId) || x.MonitorSetKey is null || x.MonitorSetKey.Length != 64 || !x.MonitorSetKey.All(Uri.IsHexDigit)) ||
                connection.Layouts.Select(x => x.MonitorSetKey).Distinct().Count() != connection.Layouts.Count)
                throw new InvalidDataException("Invalid saved RDP layout. The original file was retained.");
        }
    }

    public void Update(Action<RdpCatalog> update)
    {
        lock (Gate)
        {
            var catalog = Load();
            update(catalog);
            Validate(catalog);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(catalog, Options));
                if (File.Exists(_path)) File.Replace(temp, _path, _path + ".bak");
                else File.Move(temp, _path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
