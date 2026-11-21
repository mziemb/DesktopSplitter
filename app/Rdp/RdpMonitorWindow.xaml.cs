using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopSplitter.Services;

namespace DesktopSplitter.Rdp;

public partial class RdpMonitorWindow : Window
{
    private readonly RdpConnection _connection;
    private readonly RdpConnectionStore _store;
    private readonly RdpDesktopService _desktop;
    private readonly Func<bool> _systemBusy;
    private readonly List<(RdpMonitor Monitor, CheckBox Check)> _rows = new();
    private RdpDesktopSnapshot? _snapshot;
    private bool _loading, _connecting, _closed, _requiresRefresh;
    private string _hint = string.Empty;

    public RdpMonitorWindow(RdpConnection connection, RdpConnectionStore store, RdpDesktopService desktop, Func<bool> systemBusy)
    {
        InitializeComponent();
        _connection = connection; _store = store; _desktop = desktop; _systemBusy = systemBusy;
        Heading.Text = $"{connection.Name} — {connection.Address}";
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _closed = true;
        Closing += (_, e) => { if (_connecting) e.Cancel = true; };
    }

    private async Task RefreshAsync()
    {
        if (_loading || _connecting || _closed) return;
        _loading = true; _requiresRefresh = false; ConnectButton.IsEnabled = false; RefreshButton.IsEnabled = false;
        MonitorRows.Children.Clear(); Map.Children.Clear(); _rows.Clear(); Primary.ItemsSource = null; _snapshot = null;
        Status.Text = "Reading current Windows and RDP monitors…";
        try
        {
            if (_systemBusy()) throw new InvalidOperationException("Wait for Apply/Revert to finish, then refresh.");
            var snapshot = await _desktop.ReadAsync();
            if (_closed) return;
            _snapshot = snapshot;
            RdpRememberedLayout? saved = null;
            try
            {
                saved = RdpLayoutPolicy.Recall(_connection, snapshot.Monitors);
                _hint = saved is null ? "No saved layout for this monitor set. Choose monitors to connect." : "Suggested: your last launched layout for this monitor set.";
            }
            catch (InvalidOperationException ex) { _hint = ex.Message + " Choose a new layout; the previous one has been retained."; }
            foreach (var monitor in snapshot.Monitors)
            {
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var check = new CheckBox { Content = $"{monitor.Label}  [RDP {monitor.RdpId}]", IsEnabled = monitor.StableId.Length > 0,
                    IsChecked = saved?.SelectedIds.Contains(monitor.StableId) == true, VerticalAlignment = VerticalAlignment.Center };
                if (monitor.StableId.Length == 0)
                {
                    var confirm = new Button { Content = "Confirm identity…", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(8, 0, 0, 0) };
                    DockPanel.SetDock(confirm, Dock.Right); row.Children.Add(confirm);
                    confirm.Click += async (_, _) =>
                    {
                        if (MessageBox.Show(this, "Confirm a new stable identity for this monitor?\n\n" + monitor.Label +
                            "\n\nTo reconnect it to an existing ID instead, use Identity details in the main window.", "Confirm monitor identity",
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                        try
                        {
                            if (_systemBusy()) throw new InvalidOperationException("Wait for Apply/Revert to finish.");
                            confirm.IsEnabled = false;
                            MonitorRows.IsEnabled = false; RefreshButton.IsEnabled = false;
                            await Task.Run(() => MonitorIdentityStore.Confirm(snapshot.Sources.Single(m => m.DeviceName == monitor.GdiName), null));
                            await RefreshAsync();
                        }
                        catch (Exception ex) { Status.Text = ex.Message; confirm.IsEnabled = true; }
                        finally { MonitorRows.IsEnabled = true; RefreshButton.IsEnabled = true; }
                    };
                }
                row.Children.Add(check); MonitorRows.Children.Add(row); _rows.Add((monitor, check));
                check.Checked += (_, _) => SelectionChanged(); check.Unchecked += (_, _) => SelectionChanged();
            }
            SelectionChanged();
            if (saved is not null) Primary.SelectedItem = snapshot.Monitors.Single(m => m.StableId == saved.PrimaryId);
        }
        catch (Exception ex) { _hint = Status.Text = ex.Message; }
        finally { _loading = false; RefreshButton.IsEnabled = true; ValidateSelection(); }
    }

    private void SelectionChanged()
    {
        string? previous = (Primary.SelectedItem as RdpMonitor)?.StableId;
        var selected = _rows.Where(x => x.Check.IsChecked == true).Select(x => x.Monitor).ToList();
        Primary.ItemsSource = selected;
        Primary.SelectedItem = selected.FirstOrDefault(m => m.StableId == previous) ?? selected.FirstOrDefault(m => m.LocalPrimary) ?? selected.FirstOrDefault();
        DrawMap(); ValidateSelection();
    }

    private void ValidateSelection()
    {
        ConnectButton.IsEnabled = false;
        if (_loading || _connecting || _requiresRefresh || _snapshot is null) return;
        try
        {
            if (_systemBusy()) throw new InvalidOperationException("Wait for Apply/Revert to finish, then refresh.");
            _ = RdpLayoutPolicy.Select(_snapshot.Monitors, SelectedIds(), (Primary.SelectedItem as RdpMonitor)?.StableId ?? string.Empty);
            ConnectButton.IsEnabled = true; Status.Text = _hint;
        }
        catch (Exception ex) { Status.Text = _hint + "\n" + ex.Message; }
    }
    private List<string> SelectedIds() => _rows.Where(x => x.Check.IsChecked == true).Select(x => x.Monitor.StableId).ToList();

    private void DrawMap()
    {
        Map.Children.Clear();
        if (_snapshot is null || _snapshot.Monitors.Count == 0) return;
        var monitors = _snapshot.Monitors;
        int left = monitors.Min(m => m.X), top = monitors.Min(m => m.Y);
        double scale = Math.Min(780.0 / Math.Max(1, monitors.Max(m => m.Right) - left), 150.0 / Math.Max(1, monitors.Max(m => m.Bottom) - top));
        foreach (var m in monitors)
        {
            bool selected = _rows.Any(x => x.Monitor == m && x.Check.IsChecked == true);
            var tile = new Border { Width = m.Width * scale, Height = m.Height * scale, Background = selected ? Brushes.SteelBlue : Brushes.DimGray,
                BorderBrush = (Primary.SelectedItem as RdpMonitor)?.StableId == m.StableId && m.StableId.Length > 0 ? Brushes.Gold : Brushes.White,
                BorderThickness = new Thickness(2), ToolTip = m.Label, Cursor = System.Windows.Input.Cursors.Hand,
                Child = new TextBlock { Text = $"{(m.StableId.Length > 0 ? m.StableId : "Confirm ID")}\nRDP {m.RdpId}\n{m.Width} × {m.Height}",
                    Foreground = Brushes.White, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 } };
            Canvas.SetLeft(tile, 10 + (m.X - (double)left) * scale); Canvas.SetTop(tile, 10 + (m.Y - (double)top) * scale);
            tile.MouseLeftButtonUp += (_, _) =>
            {
                var row = _rows.Single(x => x.Monitor == m);
                if (row.Check.IsEnabled && !_connecting) row.Check.IsChecked = row.Check.IsChecked != true;
            };
            Map.Children.Add(tile);
        }
    }
    private void Primary_Changed(object sender, SelectionChangedEventArgs e) { if (Map is not null) { DrawMap(); ValidateSelection(); } }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null || _connecting) return;
        _connecting = true; ConnectButton.IsEnabled = false; RefreshButton.IsEnabled = false; CancelButton.IsEnabled = false;
        MonitorRows.IsEnabled = false; Primary.IsEnabled = false;
        string primary = (Primary.SelectedItem as RdpMonitor)?.StableId ?? string.Empty;
        var ids = SelectedIds();
        try
        {
            Status.Text = "Checking that the selected displays are still available…";
            var coordinator = new RdpLaunchCoordinator(async () => (await _desktop.ReadAsync()).Monitors, _systemBusy,
                () => { _ = _store.Load(); }, _desktop.Launch,
                (fresh, selected, chosenPrimary) => _store.Update(catalog =>
                    RdpLayoutPolicy.Remember(catalog.Connections.Single(c => c.Id == _connection.Id), fresh, selected, chosenPrimary)));
            string? warning = await coordinator.LaunchAsync(_connection, _snapshot.Monitors, ids, primary);
            if (warning is not null) MessageBox.Show(this, "The RDP client started, but the layout could not be remembered:\n" + warning,
                "RDP layout", MessageBoxButton.OK, MessageBoxImage.Warning);
            _connecting = false; Close();
        }
        catch (Exception ex) { _requiresRefresh = true; Status.Text = ex.Message; }
        finally
        {
            _connecting = false; RefreshButton.IsEnabled = true; CancelButton.IsEnabled = true;
            MonitorRows.IsEnabled = true; Primary.IsEnabled = true;
            // A failed preflight requires an explicit refresh before another launch.
        }
    }
}
