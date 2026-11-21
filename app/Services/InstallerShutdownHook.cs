using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace DesktopSplitter.Services;

/// <summary>Installer exit endpoint that also works before a window is shown.</summary>
public sealed class InstallerShutdownHook : IDisposable
{
    public const string MessageName = "DesktopSplitter.InstallerExit.v1";
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
    private readonly HwndSource _source;
    private readonly Func<bool> _isBusy;
    private readonly Action _queueExit;
    private readonly uint _message;
    private bool _disposed;

    public InstallerShutdownHook(IntPtr hwnd, Func<bool> isBusy, Action queueExit)
    {
        _source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("Installer shutdown HWND is unavailable.");
        _isBusy = isBusy;
        _queueExit = queueExit;
        _message = RegisterWindowMessage(MessageName);
        if (_message == 0) throw new InvalidOperationException("Cannot register installer shutdown message.");
        _source.AddHook(Receive);
    }
    private IntPtr Receive(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed || (uint)message != _message) return IntPtr.Zero;
        handled = true;
        if (_isBusy()) return new IntPtr(2);
        _queueExit(); // Queue on the dispatcher so the installer receives ACK first.
        return new IntPtr(1);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.RemoveHook(Receive);
    }
}
