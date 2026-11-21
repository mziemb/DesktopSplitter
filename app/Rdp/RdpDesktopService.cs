using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DesktopSplitter.Interop;
using DesktopSplitter.Models;

namespace DesktopSplitter.Rdp;

public sealed record RdpDesktopSnapshot(IReadOnlyList<RdpMonitor> Monitors, IReadOnlyList<MonitorInfo> Sources);

/// <summary>Reads mstsc's actual IDs, never DISPLAYn minus one or enumeration order.</summary>
public sealed class RdpDesktopService
{
    public static string ClientPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mstsc.exe");

    public Task<RdpDesktopSnapshot> ReadAsync() => Task.Run(async () =>
    {
        var before = DisplayConfig.Enumerate();
        string listing = await ReadClientMonitorListAsync().ConfigureAwait(false);
        var after = DisplayConfig.Enumerate();
        string Fingerprint(IReadOnlyList<MonitorInfo> monitors) => string.Join("|", monitors.OrderBy(m => m.MonitorDevicePath, StringComparer.Ordinal)
            .Select(m => $"{m.MonitorDevicePath}:{m.StableMonitorId}:{m.DeviceName}:{m.PositionX}:{m.PositionY}:{m.CurrentWidth}:{m.CurrentHeight}"));
        if (Fingerprint(before) != Fingerprint(after)) throw new InvalidOperationException("Displays changed while reading the RDP monitor list. Refresh and choose again.");
        var listed = RdpLayoutPolicy.ParseMonitorList(listing);
        var observations = after.Select(m => new RdpMonitor(m.StableMonitorId, m.Caption, m.DeviceName, -1,
            m.PositionX, m.PositionY, m.CurrentWidth, m.CurrentHeight, m.IsPrimary)).ToList();
        var result = RdpLayoutPolicy.BindClientIds(observations, listed);
        return new RdpDesktopSnapshot(result, after);
    });

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

    private static async Task<string> ReadClientMonitorListAsync()
    {
        var start = new ProcessStartInfo(ClientPath, "/l")
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not read RDP monitor IDs.");
        IntPtr dialog = IntPtr.Zero;
        try
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(6))
            {
                var parts = new List<string>();
                EnumWindows((window, _) =>
                {
                    GetWindowThreadProcessId(window, out uint pid);
                    if (pid != process.Id) return true;
                    var name = new StringBuilder(64);
                    GetClassName(window, name, name.Capacity);
                    if (name.ToString() != "#32770") return true;
                    dialog = window;
                    ShowWindow(window, 0);
                    EnumChildWindows(window, (child, _) =>
                    {
                        var text = new StringBuilder(32768);
                        if (SendMessageTimeout(child, 0x000D, (IntPtr)text.Capacity, text, 2, 200, out _) != IntPtr.Zero)
                            parts.Add(text.ToString());
                        return true;
                    }, IntPtr.Zero);
                    return false;
                }, IntPtr.Zero);
                string text = string.Join("\n", parts);
                if (text.Contains(';') && text.Contains('(')) return text;
                if (process.HasExited) break;
                await Task.Delay(100).ConfigureAwait(false);
            }
            throw new InvalidOperationException("Could not read mstsc /l. Close its monitor-list dialog if still visible and refresh. No connection was started.");
        }
        finally
        {
            // Close only the monitor-list dialog owned by the exact process we created.
            if (dialog != IntPtr.Zero)
            {
                GetWindowThreadProcessId(dialog, out uint owner);
                if (owner == process.Id) PostMessage(dialog, 0x0010, IntPtr.Zero, IntPtr.Zero);
            }
        }
    }

    private delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wparam, StringBuilder text, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
}
