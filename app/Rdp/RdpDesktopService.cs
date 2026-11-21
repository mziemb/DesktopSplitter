using System.Diagnostics;
using System.IO;
using System.Text;
using DesktopSplitter.Interop;
using DesktopSplitter.Models;

namespace DesktopSplitter.Rdp;

public sealed record RdpDesktopSnapshot(IReadOnlyList<RdpMonitor> Monitors, IReadOnlyList<MonitorInfo> Sources);

/// <summary>Reads mstsc's actual IDs, never DISPLAYn minus one or enumeration order.</summary>
public interface IRdpDesktopService
{
    Task<RdpDesktopSnapshot> ReadWindowsAsync();
    Task<RdpDesktopSnapshot> ResolveAsync(RdpDesktopSnapshot before);
    Task<RdpDesktopSnapshot> ReadAsync();
    void Launch(string content);
}

public sealed class RdpDesktopService : IRdpDesktopService
{
    public static string ClientPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mstsc.exe");

    public Task<RdpDesktopSnapshot> ReadWindowsAsync() => Task.Run(() =>
    {
        var sources = DisplayConfig.Enumerate();
        string sourceId = string.Empty;
        string stateError = string.Empty;
        try
        {
            var state = Services.PrimaryPreferenceStore.Default.Read();
            if (sources.Any(m => m.IsDeskSplitVirtual))
            {
                sourceId = state?.SplitPhysicalId ?? string.Empty;
                if (sourceId.Length == 0)
                {
                    var settings = Services.SettingsStore.Load();
                    if (settings?.HasConfirmedIdentity == true) sourceId = settings.PhysicalStableMonitorId;
                }
                if (sourceId.Length == 0) stateError = "The split physical source could not be identified. Restore or confirm its identity before connecting.";
            }
        }
        catch (Exception ex) { stateError = "Split state could not be verified: " + ex.Message; }
        var monitors = sources.Select(m => new RdpMonitor(m.StableMonitorId, m.Caption, m.DeviceName, -1,
            m.PositionX, m.PositionY, m.CurrentWidth, m.CurrentHeight, m.IsPrimary)
            { WindowsNumber = m.Index, BlockedReason = stateError.Length > 0 ? stateError :
                sourceId.Length > 0 && m.StableMonitorId == sourceId ? "Split physical display — select its virtual monitors instead" : string.Empty }).ToList();
        return new RdpDesktopSnapshot(monitors, sources);
    });

    public async Task<RdpDesktopSnapshot> ReadAsync()
    {
        var before = await ReadWindowsAsync();
        return await ResolveAsync(before);
    }

    public async Task<RdpDesktopSnapshot> ResolveAsync(RdpDesktopSnapshot before)
    {
        // Do not hide the probe at startup: modern mstsc needs a realized UI to expose its text.
        string listing = await Task.Run(() => RdpMonitorProbe.ReadAsync(new ProcessStartInfo(ClientPath, "/l") { UseShellExecute = false }));
        var after = await ReadWindowsAsync();
        if (RdpLayoutPolicy.SnapshotKey(before.Monitors) != RdpLayoutPolicy.SnapshotKey(after.Monitors))
            throw new InvalidOperationException("Displays changed while reading RDP IDs. Refresh and choose again.");
        return new RdpDesktopSnapshot(RdpLayoutPolicy.BindClientIds(after.Monitors, RdpLayoutPolicy.ParseMonitorList(listing)), after.Sources);
    }

    public void Launch(string content)
    {
        string directory = Path.Combine(Services.SettingsStore.Directory, "RdpSessions");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".rdp");
        File.WriteAllText(path, content, Encoding.Unicode);
        var start = new ProcessStartInfo(ClientPath) { UseShellExecute = false };
        start.ArgumentList.Add(path);
        try { using var process = Process.Start(start) ?? throw new InvalidOperationException("Remote Desktop Connection did not start."); }
        catch { File.Delete(path); throw; }
        // Keep the file for mstsc reconnect/handoff. It contains no password.
        // Each launch has a different path, so active sessions cannot overwrite one another.
    }

}
