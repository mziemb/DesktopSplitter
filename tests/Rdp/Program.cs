using DesktopSplitter.Rdp;

int passed = 0;
void Test(string name, Action action) { action(); Console.WriteLine("PASS " + name); passed++; }
void Check(bool value) { if (!value) throw new Exception("Assertion failed"); }
void Fails(Action action) { try { action(); } catch (Exception e) when (e is InvalidOperationException or System.IO.InvalidDataException or System.Text.Json.JsonException) { return; } throw new Exception("Expected rejection"); }
RdpMonitor M(string id, int rdp, int x, int y = 0, int w = 1920, int h = 1080) => new(id, id, @"\\.\DISPLAY" + rdp, rdp, x, y, w, h, x == 0);
var monitors = new[] { M("MON-001", 7, 0), M("MON-002", 3, 1920), M("VIRTUAL-A/0", 9, 3840) };
RdpConnection C(string name = "Work") => new() { Name = name, Host = "rdp.example.test", UserName = @"DOMAIN\user" };

Test("RDP IDs come from numeric client records, with inclusive bounds and negative coordinates", () => {
    var list = RdpLayoutPolicy.ParseMonitorList("Available monitors:\r\n7: 1920 x 1080; (0, 0, 1919, 1079)\r\n3: 1080 × 1920; (-1080, -200, -1, 1719)\r\n");
    Check(list[0] == new RdpListedMonitor(7, 0, 0, 1920, 1080)); Check(list[1].X == -1080 && list[1].Height == 1920);
});
Test("monitor-list parser also accepts exclusive rectangle bounds", () => Check(RdpLayoutPolicy.ParseMonitorList("4: 1920 x 1080; (0, 0, 1920, 1080)").Count == 1));
Test("duplicate, malformed and unrecognized monitor lists fail closed", () => {
    Fails(() => RdpLayoutPolicy.ParseMonitorList("no records"));
    Fails(() => RdpLayoutPolicy.ParseMonitorList("0: 1920 x 1080; (0, 0, 100, 100)"));
    Fails(() => RdpLayoutPolicy.ParseMonitorList("0: 1 x 1; (0, 0, 0, 0)\n0: 1 x 1; (1, 0, 1, 0)"));
});
Test("actual RDP list binds by geometry rather than Windows name or list order", () => {
    var listed = new[] { new RdpListedMonitor(20, 3840, 0, 1920, 1080), new RdpListedMonitor(15, 1920, 0, 1920, 1080), new RdpListedMonitor(8, 0, 0, 1920, 1080) };
    var bound = RdpLayoutPolicy.BindClientIds(monitors, listed); Check(bound[0].RdpId == 8 && bound[1].RdpId == 15 && bound[2].RdpId == 20);
});
Test("incomplete, mismatched and mirrored client mappings are rejected", () => {
    var listed = new[] { new RdpListedMonitor(0, 0, 0, 1920, 1080) };
    Fails(() => RdpLayoutPolicy.BindClientIds(monitors, listed));
    Fails(() => RdpLayoutPolicy.BindClientIds(new[] { M("A", 0, 1) }, listed));
    Fails(() => RdpLayoutPolicy.BindClientIds(new[] { M("A", 0, 0), M("B", 1, 0) },
        new[] { listed[0], new RdpListedMonitor(1, 1920, 0, 1920, 1080) }));
});
Test("remote primary is first in selectedmonitors, without assuming GDI indexes", () => {
    string file = RdpLayoutPolicy.BuildFile(C(), monitors, new[] { "MON-001", "MON-002" }, "MON-002");
    Check(file.Contains("selectedmonitors:s:3,7\r\n")); Check(file.Contains("use multimon:i:1\r\n")); Check(!file.Contains("password"));
});
Test("single chosen monitor uses exactly its RDP ID", () => Check(RdpLayoutPolicy.BuildFile(C(), monitors, new[] { "MON-002" }, "MON-002").Contains("selectedmonitors:s:3\r\n")));
Test("empty, duplicate, missing and non-selected primary selections are rejected", () => {
    Fails(() => RdpLayoutPolicy.Select(monitors, Array.Empty<string>(), ""));
    Fails(() => RdpLayoutPolicy.Select(monitors, new[] { "MON-001", "MON-001" }, "MON-001"));
    Fails(() => RdpLayoutPolicy.Select(monitors, new[] { "missing" }, "missing"));
    Fails(() => RdpLayoutPolicy.Select(monitors, new[] { "MON-001" }, "MON-002"));
});
Test("gaps, overlap and corner-only contact are rejected", () => {
    Fails(() => RdpLayoutPolicy.Select(monitors, new[] { "MON-001", "VIRTUAL-A/0" }, "MON-001"));
    Fails(() => RdpLayoutPolicy.Select(new[] { M("A", 1, 0), M("B", 2, 100) }, new[] { "A", "B" }, "A"));
    Fails(() => RdpLayoutPolicy.Select(new[] { M("A", 1, 0), M("B", 2, 1920, 1080) }, new[] { "A", "B" }, "A"));
});
Test("portrait stacked virtual segments share usable edges", () => {
    var v = new[] { M("V/0", 3, -1080, 0, 1080, 640), M("V/1", 4, -1080, 640, 1080, 640), M("V/2", 5, -1080, 1280, 1080, 640) };
    Check(RdpLayoutPolicy.Select(v, v.Select(m => m.StableId), "V/1")[0].RdpId == 4);
});
Test("remembered sets ignore GDI and RDP renumbering and enumeration order", () => {
    var c = C(); RdpLayoutPolicy.Remember(c, monitors, new[] { "MON-001", "MON-002" }, "MON-002");
    var changed = monitors.Reverse().Select((m, i) => m with { GdiName = "DISPLAY" + i, RdpId = i + 10 }).ToArray();
    var saved = RdpLayoutPolicy.Recall(c, changed)!;
    Check(saved.PrimaryId == "MON-002"); Check(RdpLayoutPolicy.BuildFile(c, changed, saved.SelectedIds, saved.PrimaryId).Contains("selectedmonitors:s:11,12"));
});
Test("different monitor sets and virtual adapter instances keep separate suggestions", () => {
    var c = C(); RdpLayoutPolicy.Remember(c, monitors, new[] { "MON-001" }, "MON-001");
    Check(RdpLayoutPolicy.Recall(c, monitors.Take(2).ToArray()) is null);
    Check(RdpLayoutPolicy.Recall(c, monitors.Select(m => m.StableId == "VIRTUAL-A/0" ? m with { StableId = "VIRTUAL-B/0" } : m).ToArray()) is null);
});
Test("each connection remembers its own layout for the same monitor set", () => {
    var a = C("A"); var b = C("B");
    RdpLayoutPolicy.Remember(a, monitors, new[] { "MON-001" }, "MON-001");
    RdpLayoutPolicy.Remember(b, monitors, new[] { "MON-002" }, "MON-002");
    Check(RdpLayoutPolicy.Recall(a, monitors)!.PrimaryId != RdpLayoutPolicy.Recall(b, monitors)!.PrimaryId);
});
Test("unplug and reconnect restores earlier monitor-set suggestion", () => {
    var c = C(); RdpLayoutPolicy.Remember(c, monitors, new[] { "MON-002" }, "MON-002");
    var fewer = monitors.Take(1).ToArray(); RdpLayoutPolicy.Remember(c, fewer, new[] { "MON-001" }, "MON-001");
    Check(c.Layouts.Count == 2 && RdpLayoutPolicy.Recall(c, monitors)!.PrimaryId == "MON-002");
});
Test("changed geometry invalidates an impossible suggestion without deleting it", () => {
    var c = C(); RdpLayoutPolicy.Remember(c, monitors, new[] { "MON-001", "MON-002" }, "MON-001");
    Fails(() => RdpLayoutPolicy.Recall(c, monitors.Select(m => m.StableId == "MON-002" ? m with { X = 3000 } : m).ToArray()));
    Check(c.Layouts.Count == 1);
});
Test("unconfirmed and duplicate stable IDs never authorize a layout", () => {
    Fails(() => RdpLayoutPolicy.MonitorSetKey(new[] { M("", 0, 0) }));
    Fails(() => RdpLayoutPolicy.MonitorSetKey(new[] { M("A", 0, 0), M("A", 1, 1920) }));
});
Test("topology snapshot detects transient address and geometry changes", () => {
    string key = RdpLayoutPolicy.SnapshotKey(monitors);
    Check(key == RdpLayoutPolicy.SnapshotKey(monitors.Reverse().ToArray()));
    Check(key != RdpLayoutPolicy.SnapshotKey(monitors.Select(m => m with { RdpId = m.RdpId + 1 }).ToArray()));
    Check(key != RdpLayoutPolicy.SnapshotKey(monitors.Take(2).ToArray()));
});
Test("IPv6 and custom ports are rendered safely", () => {
    var c = C(); c.Host = "2001:db8::1"; c.Port = 3390; c.Validate(); Check(c.Address == "[2001:db8::1]:3390");
});
Test("connection fields reject line injection and invalid endpoints", () => {
    var c = C(); c.Host = "host\r\nselectedmonitors:s:0"; Fails(c.Validate);
    c = C(); c.UserName = "user\nauthentication level:i:0"; Fails(c.Validate);
    c = C(); c.Port = 65536; Fails(c.Validate);
    c = C(); c.Host = "https://example.test"; Fails(c.Validate);
});
Test("launch revalidates current IDs before starting the client", () => {
    int launched = 0, remembered = 0;
    var runner = new RdpLaunchCoordinator(() => Task.FromResult<IReadOnlyList<RdpMonitor>>(monitors), () => false,
        () => { }, content => { Check(content.Contains("selectedmonitors:s:3")); launched++; }, (_, _, _) => remembered++);
    Check(runner.LaunchAsync(C(), monitors, new[] { "MON-002" }, "MON-002").GetAwaiter().GetResult() is null);
    Check(launched == 1 && remembered == 1);
});
Test("disconnect, renumber, resize or Apply during selection prevents launch and persistence", () => {
    foreach (var fresh in new[] { monitors.Take(2).ToArray(), monitors.Select(m => m with { RdpId = m.RdpId + 1 }).ToArray(),
        monitors.Select(m => m with { Width = m.Width + 1 }).ToArray() }) {
        int mutations = 0;
        var runner = new RdpLaunchCoordinator(() => Task.FromResult<IReadOnlyList<RdpMonitor>>(fresh), () => false,
            () => { }, _ => mutations++, (_, _, _) => mutations++);
        Fails(() => runner.LaunchAsync(C(), monitors, new[] { "MON-001" }, "MON-001").GetAwaiter().GetResult()); Check(mutations == 0);
    }
    int started = 0;
    var busy = new RdpLaunchCoordinator(() => Task.FromResult<IReadOnlyList<RdpMonitor>>(monitors), () => true,
        () => { }, _ => started++, (_, _, _) => started++);
    Fails(() => busy.LaunchAsync(C(), monitors, new[] { "MON-001" }, "MON-001").GetAwaiter().GetResult()); Check(started == 0);
});
Test("client startup failure does not save a successful layout", () => {
    int remembered = 0;
    var runner = new RdpLaunchCoordinator(() => Task.FromResult<IReadOnlyList<RdpMonitor>>(monitors), () => false,
        () => { }, _ => throw new InvalidOperationException("start failed"), (_, _, _) => remembered++);
    Fails(() => runner.LaunchAsync(C(), monitors, new[] { "MON-001" }, "MON-001").GetAwaiter().GetResult()); Check(remembered == 0);
});
Test("unreadable settings prevents launching; post-launch save failure is reported without another launch", () => {
    int started = 0;
    var runner = new RdpLaunchCoordinator(() => Task.FromResult<IReadOnlyList<RdpMonitor>>(monitors), () => false,
        () => throw new InvalidOperationException("storage failed"), _ => started++, (_, _, _) => { });
    Fails(() => runner.LaunchAsync(C(), monitors, new[] { "MON-001" }, "MON-001").GetAwaiter().GetResult()); Check(started == 0);
    runner = new RdpLaunchCoordinator(() => Task.FromResult<IReadOnlyList<RdpMonitor>>(monitors), () => false,
        () => { }, _ => started++, (_, _, _) => throw new InvalidOperationException("disk full"));
    Check(runner.LaunchAsync(C(), monitors, new[] { "MON-001" }, "MON-001").GetAwaiter().GetResult() == "disk full"); Check(started == 1);
});
string directory = Path.Combine(Path.GetTempPath(), "ds-rdp-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    string path = Path.Combine(directory, "connections.json"); var store = new RdpConnectionStore(path);
    Test("catalog and per-set preferences survive application restart", () => {
        var c = C(); RdpLayoutPolicy.Remember(c, monitors, new[] { "MON-002" }, "MON-002");
        store.Update(x => x.Connections.Add(c));
        var restarted = new RdpConnectionStore(path).Load(); Check(RdpLayoutPolicy.Recall(restarted.Connections.Single(), monitors)!.PrimaryId == "MON-002");
    });
    Test("updates preserve other connections and create a backup", () => {
        store.Update(x => x.Connections.Add(C("Second"))); Check(store.Load().Connections.Count == 2 && File.Exists(path + ".bak"));
    });
    Test("invalid update leaves original bytes unchanged", () => {
        string original = File.ReadAllText(path); Fails(() => store.Update(x => x.Connections[0].Host = "bad\nvalue")); Check(File.ReadAllText(path) == original);
    });
    Test("delete removes only one connection and its layouts", () => {
        string id = store.Load().Connections[0].Id; store.Update(x => x.Connections.RemoveAll(c => c.Id == id)); Check(store.Load().Connections.Single().Name == "Second");
    });
    Test("corrupt and unknown-schema catalogs cannot be overwritten", () => {
        File.WriteAllText(path, "broken"); Fails(() => store.Update(x => x.Connections.Clear())); Check(File.ReadAllText(path) == "broken");
        File.WriteAllText(path, "{\"SchemaVersion\":999,\"Connections\":[]}"); Fails(() => store.Update(x => x.Connections.Clear())); Check(File.ReadAllText(path).Contains("999"));
    });
}
finally { Directory.Delete(directory, true); }
Console.WriteLine($"{passed} RDP tests passed; no mstsc, network, credentials or display configuration APIs invoked.");
