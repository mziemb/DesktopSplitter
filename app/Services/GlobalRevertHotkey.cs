using System.Runtime.InteropServices;
using System.Windows.Interop;
using DesktopSplitter.Models;

namespace DesktopSplitter.Services;

/// <summary>A message-only HWND remains alive when the main window is hidden in the tray.</summary>
public sealed class GlobalRevertHotkey : IDisposable, IHotkeyBackend
{
    private readonly HwndSource _source;
    private bool _disposed;
    private readonly HotkeyRegistration _registration;
    public event EventHandler? Pressed;
    public RevertHotkey? ActiveGesture => _registration.ActiveGesture;

    public GlobalRevertHotkey()
    {
        _source = new HwndSource(new HwndSourceParameters("DesktopSplitter.RevertHotkey")
        { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 });
        _registration = new HotkeyRegistration(this);
        _source.AddHook(WndProc);
    }

    public void Configure(RevertHotkey? gesture, Action persist) => _registration.Change(gesture, persist);

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312)
        {
            long data = lParam.ToInt64();
            if (_registration.Matches(wParam.ToInt32(), (uint)(data & 0x000F), (uint)((data >> 16) & 0xFFFF)))
            { handled = true; Pressed?.Invoke(this, EventArgs.Empty); }
        }
        return IntPtr.Zero;
    }

    bool IHotkeyBackend.TryRegister(int id, RevertHotkey gesture, out int error)
    {
        bool ok = RegisterHotKey(_source.Handle, id, gesture.Modifiers | 0x4000 /* MOD_NOREPEAT */, gesture.VirtualKey);
        error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }
    void IHotkeyBackend.Unregister(int id) => UnregisterHotKey(_source.Handle, id);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _registration.Dispose();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
