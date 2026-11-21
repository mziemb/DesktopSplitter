using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DesktopSplitter.Services;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
    private static void Check(bool condition) { if (!condition) throw new Exception("IPC assertion failed"); }
    [STAThread]
    private static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new Window();
        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        Check(hwnd != IntPtr.Zero && !window.IsVisible);
        bool busy = false;
        int exits = 0;
        var pending = new Queue<Action>();
        using var hook = new InstallerShutdownHook(hwnd, () => busy, () => pending.Enqueue(() => ++exits));
        uint message = RegisterWindowMessage(InstallerShutdownHook.MessageName);
        Check(SendMessageTimeout(hwnd, message, IntPtr.Zero, IntPtr.Zero, 2, 1000, out var reply) != IntPtr.Zero && reply.ToUInt64() == 1);
        Check(!window.IsVisible && pending.Count == 1 && exits == 0);
        pending.Dequeue()(); Check(exits == 1);
        Console.WriteLine("PASS hidden WPF HWND acknowledges exit and queues teardown before Show");
        busy = true;
        Check(SendMessageTimeout(hwnd, message, IntPtr.Zero, IntPtr.Zero, 2, 1000, out reply) != IntPtr.Zero && reply.ToUInt64() == 2);
        Check(pending.Count == 0 && exits == 1);
        Console.WriteLine("PASS busy WPF endpoint refuses exit without scheduling teardown");
        busy = false;
        Check(SendMessageTimeout(hwnd, 0, IntPtr.Zero, IntPtr.Zero, 2, 1000, out _) != IntPtr.Zero && pending.Count == 0);
        Console.WriteLine("PASS unrelated message does not request exit");
        hook.Dispose(); hook.Dispose();
        Check(SendMessageTimeout(hwnd, message, IntPtr.Zero, IntPtr.Zero, 2, 1000, out reply) != IntPtr.Zero && reply.ToUInt64() != 1 && pending.Count == 0);
        Console.WriteLine("PASS disposed endpoint no longer acknowledges installer requests");
        window.Close(); app.Shutdown();
        Console.WriteLine("4 WPF installer IPC tests passed; hidden fixture only, no display configuration APIs");
    }
}
