using System.Diagnostics;
using System.Runtime.InteropServices;
namespace DesktopSplitter.Services;

/// <summary>Windows reports topology changes without their cause. Do not learn an OS fallback as user intent.</summary>
public static class PrimaryChangeIntent
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput { public uint Size; public uint Time; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput input);
    private delegate bool ChildVisitor(IntPtr hwnd, IntPtr context);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hwnd, ChildVisitor visitor, IntPtr context);
    public static bool IsSettingsInteraction(string processName, uint inputAgeMilliseconds, IReadOnlyList<string>? hostedProcesses = null)
        => inputAgeMilliseconds <= 5000 && (processName.Equals("SystemSettings", StringComparison.OrdinalIgnoreCase) ||
            (processName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) &&
             hostedProcesses?.Any(p => p.Equals("SystemSettings", StringComparison.OrdinalIgnoreCase)) == true));
    public static bool ObserveSettingsInteraction()
    {
        try {
            IntPtr foreground = GetForegroundWindow();
            GetWindowThreadProcessId(foreground, out uint pid);
            var input = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
            if (pid == 0 || !GetLastInputInfo(ref input)) return false;
            using var process = Process.GetProcessById((int)pid);
            uint age = unchecked((uint)Environment.TickCount - input.Time);
            string name = process.ProcessName;
            if (IsSettingsInteraction(name, age)) return true;
            if (age > 5000 || !name.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)) return false;
            var hosted = new List<string>();
            EnumChildWindows(foreground, (child, _) => {
                try {
                    GetWindowThreadProcessId(child, out uint childPid);
                    using var owner = Process.GetProcessById((int)childPid);
                    if (owner.ProcessName.Equals("SystemSettings", StringComparison.OrdinalIgnoreCase)) { hosted.Add(owner.ProcessName); return false; }
                } catch { }
                return true;
            }, IntPtr.Zero);
            return IsSettingsInteraction(name, age, hosted);
        } catch { return false; }
    }
}
