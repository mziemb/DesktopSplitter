using System.IO;
using System.Windows;
using DesktopSplitter.Services;

namespace DesktopSplitter.Rdp;

/// <summary>One modal RDP operation at a time; independent of split/recovery settings.</summary>
public sealed class RdpTrayController : IDisposable
{
    private readonly TrayIconService _tray;
    private readonly Func<bool> _systemBusy;
    private readonly RdpConnectionStore _store = new(Path.Combine(SettingsStore.Directory, "rdp-connections.json"));
    private bool _dialogOpen;

    public RdpTrayController(TrayIconService tray, Func<bool> systemBusy)
    {
        _tray = tray; _systemBusy = systemBusy;
        tray.ManageRdpRequested += Manage;
        tray.RdpConnectionRequested += Connect;
        RefreshMenu();
    }
    private void RefreshMenu()
    {
        try { _tray.SetRdpConnections(_store.Load().Connections); }
        catch (Exception ex) { _tray.ShowBalloon("RDP connections unavailable", ex.Message); }
    }
    private void Manage(object? sender, EventArgs e) => Show(() => new RdpConnectionsWindow(_store));
    private void Connect(string id) => Show(() => new RdpMonitorWindow(
        _store.Load().Connections.Single(c => c.Id == id), _store, new RdpDesktopService(), _systemBusy));
    private void Show(Func<Window> create)
    {
        if (_dialogOpen) return;
        if (_systemBusy()) { _tray.ShowBalloon("RDP connections", "Wait for Apply/Revert to finish."); return; }
        _dialogOpen = true;
        try { create().ShowDialog(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "RDP connections", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _dialogOpen = false; RefreshMenu(); }
    }
    public void Dispose()
    {
        _tray.ManageRdpRequested -= Manage;
        _tray.RdpConnectionRequested -= Connect;
    }
}
