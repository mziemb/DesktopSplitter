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
    public static bool IsSettingsInteraction(string processName, uint inputAgeMilliseconds)
        => processName.Equals("SystemSettings", StringComparison.OrdinalIgnoreCase) && inputAgeMilliseconds <= 5000;
    public static bool ObserveSettingsInteraction()
    {
        try {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            var input = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
            if (pid == 0 || !GetLastInputInfo(ref input)) return false;
            using var process = Process.GetProcessById((int)pid);
            return IsSettingsInteraction(process.ProcessName, unchecked((uint)Environment.TickCount - input.Time));
        } catch { return false; }
    }
}
