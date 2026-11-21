using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace DesktopSplitter.Rdp;

/// <summary>Read only windows owned by this exact probe process, regardless of window class.</summary>
public static class RdpMonitorProbe
{
    public static async Task<string> ReadAsync(ProcessStartInfo start)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the RDP monitor probe.");
        var windows = new HashSet<IntPtr>();
        var classes = new HashSet<string>();
        var timer = Stopwatch.StartNew();
        long lastAutomation = -2000;
        try
        {
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                var parts = new List<string>();
                EnumWindows((window, _) =>
                {
                    GetWindowThreadProcessId(window, out uint pid);
                    if (pid != process.Id) return true;
                    windows.Add(window);
                    var name = new StringBuilder(128);
                    GetClassName(window, name, name.Capacity); classes.Add(name.ToString());
                    ReadNativeText(window, parts);
                    EnumChildWindows(window, (child, _) => { ReadNativeText(child, parts); return true; }, IntPtr.Zero);
                    return true;
                }, IntPtr.Zero);
                string? listing = FindListing(parts);
                if (listing is not null) return listing;
                // Modern controls may expose text only through accessibility, not child HWNDs.
                if (windows.Count > 0 && timer.ElapsedMilliseconds > 500 && timer.ElapsedMilliseconds - lastAutomation >= 2000)
                {
                    lastAutomation = timer.ElapsedMilliseconds;
                    IntPtr[] owned = windows.ToArray();
                    try
                    {
                        var accessible = await Task.Run(() => ReadAccessibleText(owned, process.Id))
                            .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                        listing = FindListing(accessible);
                        if (listing is not null) return listing;
                    }
                    catch (Exception ex) when (ex is TimeoutException or ElementNotAvailableException or COMException) { }
                }
                if (process.HasExited) break;
                await Task.Delay(100).ConfigureAwait(false);
            }
            throw new InvalidOperationException($"RDP monitor IDs could not be read (PID {process.Id}; window classes: {string.Join(", ", classes)}). Windows monitors remain visible. Refresh to retry; no connection was started.");
        }
        finally
        {
            foreach (IntPtr window in windows)
            {
                GetWindowThreadProcessId(window, out uint owner);
                if (owner == process.Id) PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
            }
        }
    }
    private static string? FindListing(IEnumerable<string> parts)
    {
        foreach (string text in parts)
        {
            try { _ = RdpLayoutPolicy.ParseMonitorList(text); return text; }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException) { }
        }
        string combined = string.Join("\n", parts);
        try { _ = RdpLayoutPolicy.ParseMonitorList(combined); return combined; }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException) { return null; }
    }
    private static List<string> ReadAccessibleText(IntPtr[] windows, int pid)
    {
        var parts = new List<string>();
        foreach (IntPtr window in windows)
        {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner != pid) continue;
            try
            {
                var root = AutomationElement.FromHandle(window);
                var elements = root.FindAll(TreeScope.Subtree, Condition.TrueCondition);
                foreach (AutomationElement element in elements)
                {
                    if (element.Current.ProcessId != pid) continue;
                    parts.Add(element.Current.Name);
                    if (element.TryGetCurrentPattern(TextPattern.Pattern, out object pattern))
                        parts.Add(((TextPattern)pattern).DocumentRange.GetText(32768));
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException) { }
        }
        return parts;
    }
    private static void ReadNativeText(IntPtr window, List<string> parts)
    {
        var text = new StringBuilder(32768);
        if (SendMessageTimeout(window, 0x000D, (IntPtr)text.Capacity, text, 2, 100, out _) != IntPtr.Zero)
            parts.Add(text.ToString());
    }
    private delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wparam, StringBuilder text, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
}
