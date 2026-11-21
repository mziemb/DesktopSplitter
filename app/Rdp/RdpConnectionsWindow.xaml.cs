using System.Windows;
using System.Windows.Controls;

namespace DesktopSplitter.Rdp;

public partial class RdpConnectionsWindow : Window
{
    private readonly RdpConnectionStore _store;
    private string? _editingId;
    public RdpConnectionsWindow(RdpConnectionStore store)
    {
        InitializeComponent();
        _store = store;
        Refresh();
    }
    private void Refresh() => Connections.ItemsSource = _store.Load().Connections.OrderBy(x => x.Name).ToList();
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Connections.SelectedItem is not RdpConnection item) return;
        _editingId = item.Id;
        ConnectionName.Text = item.Name; Host.Text = item.Host; Port.Text = item.Port.ToString(); UserName.Text = item.UserName;
    }
    private void New_Click(object sender, RoutedEventArgs e)
    {
        Connections.SelectedItem = null; _editingId = null;
        ConnectionName.Clear(); Host.Clear(); UserName.Clear(); Port.Text = "3389"; ConnectionName.Focus();
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(Port.Text, out int port)) throw new InvalidOperationException("Enter a numeric port.");
            var connection = new RdpConnection { Id = _editingId ?? Guid.NewGuid().ToString("N"),
                Name = ConnectionName.Text.Trim(), Host = Host.Text.Trim(), Port = port, UserName = UserName.Text.Trim() };
            connection.Validate();
            _store.Update(catalog =>
            {
                var old = catalog.Connections.SingleOrDefault(x => x.Id == connection.Id);
                if (old is not null)
                {
                    // A different endpoint starts with no remembered layout for the old server.
                    if (old.Host == connection.Host && old.Port == connection.Port && old.UserName == connection.UserName)
                        connection.Layouts = old.Layouts;
                    catalog.Connections.Remove(old);
                }
                catalog.Connections.Add(connection);
            });
            Refresh(); _editingId = connection.Id;
            Connections.SelectedItem = Connections.Items.Cast<RdpConnection>().Single(x => x.Id == connection.Id);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "RDP connection", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_editingId is null || MessageBox.Show(this, "Delete this saved connection and its remembered layouts? Running sessions will stay open.",
            "Delete RDP connection", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { _store.Update(c => c.Connections.RemoveAll(x => x.Id == _editingId)); Refresh(); New_Click(sender, e); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "RDP connection", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
