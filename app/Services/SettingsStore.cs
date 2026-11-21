using System.IO;
using System.Text.Json;
using DesktopSplitter.Models;

namespace DesktopSplitter.Services;

/// <summary>Reads/writes %APPDATA%\DesktopSplitter\settings.json (last applied UI state).</summary>
public static class SettingsStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopSplitter");

    public static string FilePath => Path.Combine(Directory, "settings.json");

    public static AppSettings? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, Options);
        }
        catch
        {
            // A corrupt settings file must never stop the app from starting.
            return null;
        }
    }

    public static void Save(AppSettings settings) => SaveTo(FilePath, settings);

    // The same file transaction is tested against a temporary directory without user settings.
    internal static void SaveTo(string filePath, AppSettings settings)
    {
        lock (Gate)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            // Retain exact legacy bytes before the first write, including preference-only writes.
            if (File.Exists(filePath) && !File.Exists(filePath + ".pre-identity.bak"))
            {
                var old = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(filePath), Options);
                if (old is null) throw new InvalidDataException("Invalid settings; refusing to overwrite.");
                if (!old.HasConfirmedIdentity) File.Copy(filePath, filePath + ".pre-identity.bak");
            }
            string temp = filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
            File.Move(temp, filePath, true);
        }
    }
}
