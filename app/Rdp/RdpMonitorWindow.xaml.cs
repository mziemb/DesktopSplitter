using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopSplitter.Services;

namespace DesktopSplitter.Rdp;

public partial class RdpMonitorWindow : Window
{
    private readonly RdpConnection _connection;
    private readonly RdpConnectionStore _store;
    private readonly IRdpDesktopService _desktop;
    private readonly Func<bool> _systemBusy;
    private readonly List<(RdpMonitor Monitor, CheckBox Check)> _rows = new();
    private RdpDesktopSnapshot? _snapshot;
    private bool _loading, _connecting, _closed, _requiresRefresh;
    private string _hint = string.Empty;

    public RdpMonitorWindow(RdpConnection connection, RdpConnectionStore store, IRdpDesktopService desktop, Func<bool> systemBusy)
    {
        InitializeComponent();
        _connection = connection; _store = store; _desktop = desktop; _systemBusy = systemBusy;
        Heading.Text = $"{connection.Name} — {connection.Address}";
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _closed = true;
        Closing += (_, e) => { if (_connecting) e.Cancel = true; };
    }

    private string _probeError = string.Empty;
    private async Task RefreshAsync()
    {
        if (_loading || _connecting || _closed) return;
        _loading = true; _requiresRefresh = false; _probeError = string.Empty;
        ConnectButton.IsEnabled = false; RefreshButton.IsEnabled = false;
        MonitorRows.IsEnabled = false; Primary.IsEnabled = false;
        Status.Text = "Reading the current Windows display layout…";
        try
        {
            if (_systemBusy()) throw new InvalidOperationException("Wait for Apply/Revert to finish, then refresh.");
            _snapshot = await _desktop.ReadWindowsAsync();
            if (_closed) return;
            RenderSnapshot(); // Windows geometry is visible even if mstsc probing fails.
            Status.Text = "Windows layout loaded. Reading RDP monitor IDs…";
            try
            {
                var resolved = await _desktop.ResolveAsync(_snapshot);
                if (_closed) return;
                _snapshot = resolved;
                RenderSnapshot();
            }
            catch (Exception ex) { _probeError = ex.Message; }
        }
        catch (Exception ex) { _probeError = ex.Message; }
        finally
        {
            _loading = false; RefreshButton.IsEnabled = true;
            MonitorRows.IsEnabled = true; Primary.IsEnabled = true;
            ValidateSelection();
        }
    }

    private void RenderSnapshot()
    {
        MonitorRows.Children.Clear(); Map.Children.Clear(); _rows.Clear(); Primary.ItemsSource = null;
        if (_snapshot is null) return;
        var snapshot = _snapshot;
        RdpRememberedLayout? saved = null;
        try
        {
            // Resolve remembered choices only after fresh RDP IDs are available.
            if (snapshot.Monitors.All(m => m.RdpId >= 0)) saved = RdpLayoutPolicy.Recall(_connection, snapshot.Monitors);
            _hint = saved is null ? "Choose monitors for this connection. The split physical display is unavailable." : "Suggested: your last launched layout for this monitor set.";
        }
        catch (InvalidOperationException ex) { _hint = ex.Message + " Choose a new layout; the previous one has been retained."; }
        foreach (var monitor in snapshot.Monitors)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            string detail = monitor.BlockedReason.Length > 0 ? " — " + monitor.BlockedReason : monitor.RdpId >= 0 ? $"  [RDP {monitor.RdpId}]" : " — RDP ID pending";
            var check = new CheckBox { Content = new TextBlock { Text = monitor.Label + detail, TextWrapping = TextWrapping.Wrap, MaxWidth = 660 },
                IsEnabled = monitor.CanSelect, IsChecked = monitor.CanSelect && saved?.SelectedIds.Contains(monitor.StableId) == true,
                VerticalAlignment = VerticalAlignment.Center };
            if (monitor.StableId.Length == 0 && monitor.BlockedReason.Length == 0)
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
                        confirm.IsEnabled = false; MonitorRows.IsEnabled = false; RefreshButton.IsEnabled = false;
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
        if (_loading || _connecting || _requiresRefresh) return;
        if (_probeError.Length > 0) { Status.Text = _probeError; return; }
        if (_snapshot is null) return;
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
        double extentWidth = Math.Max(1, monitors.Max(m => m.Right) - left);
        double extentHeight = Math.Max(1, monitors.Max(m => m.Bottom) - top);
        double scale = Math.Min(860.0 / extentWidth, 230.0 / extentHeight);
        double offsetX = (900 - extentWidth * scale) / 2, offsetY = (260 - extentHeight * scale) / 2;
        foreach (var m in monitors)
        {
            bool selected = _rows.Any(x => x.Monitor == m && x.Check.IsChecked == true);
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock { Text = m.WindowsNumber > 0 && m.WindowsNumber < int.MaxValue ? m.WindowsNumber.ToString() : "?",
                Foreground = Brushes.White, TextAlignment = TextAlignment.Center, FontSize = 28, FontWeight = FontWeights.SemiBold });
            if (m.BlockedReason.Length > 0) label.Children.Add(new TextBlock { Text = "Split source / unavailable", Foreground = Brushes.LightGray,
                TextAlignment = TextAlignment.Center, FontSize = 10, TextWrapping = TextWrapping.Wrap });
            var tile = new Border { Width = m.Width * scale, Height = m.Height * scale,
                Background = !m.CanSelect ? Brushes.DarkSlateGray : selected ? Brushes.SteelBlue : Brushes.DimGray,
                BorderBrush = (Primary.SelectedItem as RdpMonitor)?.StableId == m.StableId && m.CanSelect ? Brushes.Gold : Brushes.LightGray,
                BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(4), Opacity = m.BlockedReason.Length > 0 ? 0.5 : 1,
                ToolTip = m.Label + (m.BlockedReason.Length > 0 ? "\n" + m.BlockedReason : ""),
                Cursor = m.CanSelect ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.No, Child = label };
            Canvas.SetLeft(tile, offsetX + (m.X - (double)left) * scale); Canvas.SetTop(tile, offsetY + (m.Y - (double)top) * scale);
            tile.MouseLeftButtonUp += (_, _) =>
            {
                var row = _rows.Single(x => x.Monitor == m);
                if (row.Check.IsEnabled && !_loading && !_connecting) row.Check.IsChecked = row.Check.IsChecked != true;
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
