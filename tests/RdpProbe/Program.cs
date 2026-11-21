using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopSplitter.Models;
using DesktopSplitter.Rdp;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--fixture")
        {
            var window = new Window { Title = "Owned WPF monitor-list fixture", Width = 420, Height = 140,
                Content = new TextBlock { Text = "Dostępne monitory:\n7: 1920 x 1080; (0, 0, 1919, 1079)\n3: 1080 x 1920; (-1080, 0, -1, 1919)", Margin = new Thickness(12) } };
            window.Closed += (_, _) => File.WriteAllText(args[1], "closed");
            new Application().Run(window);
            return 0;
        }
        string directory = Path.Combine(Path.GetTempPath(), "ds-rdp-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        ProcessStartInfo Fixture(string marker)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            start.ArgumentList.Add("--fixture"); start.ArgumentList.Add(Path.Combine(directory, marker));
            return start;
        }
        using var unrelated = Process.Start(Fixture("unrelated"))!;
        try
        {
            string text = Task.Run(() => RdpMonitorProbe.ReadAsync(Fixture("owned"))).GetAwaiter().GetResult();
            var monitors = RdpLayoutPolicy.ParseMonitorList(text);
            if (monitors.Count != 2 || monitors[0].Id != 7 || monitors[1].X != -1080) throw new Exception("Incorrect accessible text mapping.");
            Console.WriteLine("PASS read a non-#32770 WPF window using accessibility without clipboard or keyboard input");
            for (int i = 0; i < 20 && !File.Exists(Path.Combine(directory, "owned")); i++) Thread.Sleep(100);
            if (!File.Exists(Path.Combine(directory, "owned"))) throw new Exception("Probe did not close its own window.");
            Console.WriteLine("PASS probe closes the exact process-owned monitor window");
            if (unrelated.HasExited || File.Exists(Path.Combine(directory, "unrelated"))) throw new Exception("Unrelated window was closed.");
            Console.WriteLine("PASS unrelated same-executable window remains open");
            TestPicker(directory);
            Console.WriteLine("6 RDP window tests passed; controlled windows and fake desktop only, no mstsc, credentials or display changes.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            if (!unrelated.HasExited) { unrelated.CloseMainWindow(); if (!unrelated.WaitForExit(3000)) unrelated.Kill(); }
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
    private static void TestPicker(string directory)
    {
        var app = new Application();
        foreach (string key in new[] { "WindowBackground", "MutedBrush", "ForegroundBrush" })
            app.Resources[key] = Brushes.DarkGray;
        var desktop = new FailedProbeDesktop();
        var picker = new RdpMonitorWindow(new RdpConnection { Name = "Fixture", Host = "example.test" },
            new RdpConnectionStore(Path.Combine(directory, "profiles.json")), desktop, () => false);
        Exception? failure = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                var canvas = (Canvas)picker.FindName("Map");
                var status = (TextBlock)picker.FindName("Status");
                var rows = (StackPanel)picker.FindName("MonitorRows");
                if (canvas.Children.Count != 3 || !status.Text.Contains("simulated probe failure")) throw new Exception("A failed probe removed the Windows map.");
                Console.WriteLine("PASS real picker retains Windows monitors after client-ID probe failure");
                var first = (Border)canvas.Children[0]; var second = (Border)canvas.Children[1]; var source = (Border)canvas.Children[2];
                if (!(Canvas.GetLeft(source) < Canvas.GetLeft(first) && Canvas.GetLeft(first) < Canvas.GetLeft(second)) || source.Height <= source.Width)
                    throw new Exception("Picker does not preserve portrait monitor positions/proportions.");
                Console.WriteLine("PASS real picker shows current negative-coordinate portrait layout");
                var blocked = ((DockPanel)rows.Children[2]).Children.OfType<CheckBox>().Single();
                var connect = (Button)picker.FindName("ConnectButton");
                if (blocked.IsEnabled || connect.IsEnabled || desktop.Launched) throw new Exception("Blocked source or unresolved client IDs permit a launch.");
                Console.WriteLine("PASS split source is disabled and unresolved client IDs cannot connect");
            }
            catch (Exception ex) { failure = ex; }
            finally { picker.Close(); }
        };
        timer.Start();
        app.Run(picker);
        if (failure is not null) throw failure;
    }

    private sealed class FailedProbeDesktop : IRdpDesktopService
    {
        public bool Launched { get; private set; }
        public Task<RdpDesktopSnapshot> ReadWindowsAsync() => Task.FromResult(new RdpDesktopSnapshot(new[] {
            new RdpMonitor("A", "Windows display 1", "DISPLAY1", -1, 0, 0, 1920, 1080, true) { WindowsNumber = 1 },
            new RdpMonitor("B", "Windows display 2", "DISPLAY2", -1, 1920, 0, 1920, 1080, false) { WindowsNumber = 2 },
            new RdpMonitor("C", "Windows display 3", "DISPLAY3", -1, -1080, 0, 1080, 1920, false) { WindowsNumber = 3, BlockedReason = "Split source" }
        }, Array.Empty<MonitorInfo>()));
        public Task<RdpDesktopSnapshot> ResolveAsync(RdpDesktopSnapshot before) => Task.FromException<RdpDesktopSnapshot>(new InvalidOperationException("simulated probe failure"));
        public Task<RdpDesktopSnapshot> ReadAsync() => ResolveAsync(null!);
        public void Launch(string content) => Launched = true;
    }
}
