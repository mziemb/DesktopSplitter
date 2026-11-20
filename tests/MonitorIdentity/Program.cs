using System.Text.Json;
using DesktopSplitter.Models;
using DesktopSplitter.Services;

int passed = 0;
void Test(string name, Action body) { body(); Console.WriteLine("PASS " + name); passed++; }
void Check(bool value) { if (!value) throw new Exception("Assertion failed"); }
MonitorIdentity Physical(string path = "panel-a", uint serial = 123) => new()
{ MonitorDevicePath = path, AdapterPath = "gpu", Manufacturer = "SAM", ProductCode = 42, SerialNumber = serial };
MonitorIdentity Virtual(string adapter, int index, string path) => new()
{ MonitorDevicePath = path, AdapterPath = adapter, Manufacturer = "DSP", ProductCode = index,
  SerialNumber = 0xD5000001u + (uint)index, SegmentIndex = index };
MonitorIdentityCatalog NewCatalog() => new();

Test("GDI renumbering does not participate in matching", () => {
    var c = NewCatalog(); var saved = c.Register(Physical()); var live = Physical();
    Check(c.Match(live, new[] { live })?.StableMonitorId == saved.StableMonitorId);
    Check(!JsonSerializer.Serialize(saved).Contains("DISPLAY"));
});
Test("identical models on separate paths retain explicit bindings", () => {
    var c = NewCatalog(); var a = c.Register(Physical("a", 0)); var b = c.Register(Physical("b", 0));
    Check(a.StableMonitorId != b.StableMonitorId);
    var live = new[] { Physical("b", 0), Physical("a", 0) };
    Check(c.Match(live[0], live)?.StableMonitorId == b.StableMonitorId);
    Check(c.Match(live[1], live)?.StableMonitorId == a.StableMonitorId);
});
Test("missing serial permits corroborated same path only", () => {
    var c = NewCatalog(); var a = c.Register(Physical("a", 0)); var same = Physical("a", 0); var moved = Physical("b", 0);
    Check(c.Match(same, new[] { same }) == a); Check(c.Match(moved, new[] { moved }) is null);
});
Test("unplug reconnect retains ID without deleting offline records", () => {
    var c = NewCatalog(); var a = c.Register(Physical());
    Check(c.Match(Physical(), Array.Empty<MonitorIdentity>()) is null);
    var reconnected = Physical(); Check(c.Match(reconnected, new[] { reconnected }) == a);
});
Test("unique serial reconnect on different port", () => {
    var c = NewCatalog(); var a = c.Register(Physical()); var moved = Physical("new-port");
    Check(c.Match(moved, new[] { moved }) == a);
});
Test("enumeration order has no influence", () => {
    var c = NewCatalog(); c.Register(Physical("a", 1)); c.Register(Physical("b", 2));
    var live = new[] { Physical("b", 2), Physical("a", 1) };
    var first = c.Match(live[0], live)?.StableMonitorId;
    Check(c.Match(live[0], live.Reverse().ToArray())?.StableMonitorId == first);
});
Test("virtual adapters and segments are separate logical identities", () => {
    var c = NewCatalog(); var a = c.Register(Virtual("adapter-a", 0, "a0"));
    var b = c.Register(Virtual("adapter-b", 0, "b0")); var next = c.Register(Virtual("adapter-a", 1, "a1"));
    Check(a.StableMonitorId != b.StableMonitorId && a.StableMonitorId != next.StableMonitorId);
    Check(a.VirtualAdapterId == next.VirtualAdapterId && a.VirtualAdapterId != b.VirtualAdapterId);
    var replug = Virtual("adapter-a", 0, "new-a0");
    Check(c.Match(replug, new[] { replug }) == a);
    Check(c.Match(replug, Array.Empty<MonitorIdentity>()) is null);
});
Test("restart round trip preserves stable catalog", () => {
    var c = NewCatalog(); var a = c.Register(Physical());
    var restored = JsonSerializer.Deserialize<MonitorIdentityCatalog>(JsonSerializer.Serialize(c))!;
    var live = Physical(); Check(restored.Match(live, new[] { live })?.StableMonitorId == a.StableMonitorId);
});
Test("legacy aliases never authorize automatic Apply", () => {
    var s = JsonSerializer.Deserialize<AppSettings>("{\"physicalDevice\":\"\\\\\\\\.\\\\DISPLAY3\",\"applyOnStartup\":true}")!;
    Check(!s.HasConfirmedIdentity);
    MonitorSettingsMigration.ConfirmSelection(s, "MON-001", false);
    Check(s.HasConfirmedIdentity && !s.ApplyOnStartup && !s.SplitActiveOnExit);
    Check(s.PhysicalDevice.Contains("DISPLAY3"));
});
Test("confirmed settings cannot be overwritten by migration", () => {
    var s = new AppSettings { IdentitySchemaVersion = 1, PhysicalStableMonitorId = "MON-001" };
    MonitorSettingsMigration.ConfirmSelection(s, "MON-999", false);
    Check(s.PhysicalStableMonitorId == "MON-001");
});
Test("hidden monitor uses evidence without GDI or active flag", () => {
    var c = NewCatalog(); var a = c.Register(Physical()); var hidden = Physical();
    Check(c.Match(hidden, new[] { hidden }) == a);
});
Test("duplicate serial on changed ports is ambiguous", () => {
    var c = NewCatalog(); c.Register(Physical());
    var live = new[] { Physical("new-a"), Physical("new-b") };
    Check(c.Match(live[0], live) is null && c.Match(live[1], live) is null);
});
Test("reused path with changed reliable serial is rejected", () => {
    var c = NewCatalog(); c.Register(Physical()); var other = Physical(serial: 456);
    Check(c.Match(other, new[] { other }) is null);
});
Test("duplicate persisted records stop matching", () => {
    var c = NewCatalog(); c.Register(Physical()); c.Monitors.Add(Physical()); var live = Physical();
    Check(c.Match(live, new[] { live }) is null);
});
Test("incomplete evidence cannot acquire a logical identity", () => {
    var c = NewCatalog(); bool blocked = false;
    try { c.Register(new MonitorIdentity()); } catch (InvalidOperationException) { blocked = true; }
    Check(blocked);
});
Test("DISPLAY3 to DISPLAY2 resolves the same stable ID to a fresh address", () => {
    var id = Physical(); id.StableMonitorId = "MON-001";
    var old = new MonitorInfo { Identity = id, DeviceName = @"\\.\DISPLAY3" };
    var now = new MonitorInfo { Identity = id, DeviceName = @"\\.\DISPLAY2" };
    Check(MonitorIdentityResolver.Resolve("MON-001", new[] { old }).DeviceName.EndsWith("DISPLAY3"));
    Check(MonitorIdentityResolver.Resolve("MON-001", new[] { now }).DeviceName.EndsWith("DISPLAY2"));
});
Test("hidden target resolves for recovery but cannot be used as an active GDI source", () => {
    var id = Physical(); id.StableMonitorId = "MON-001";
    var hidden = new MonitorInfo { Identity = id, IsActive = false };
    Check(MonitorIdentityResolver.Resolve("MON-001", new[] { hidden }, false) == hidden);
    bool blocked = false;
    try { MonitorIdentityResolver.Resolve("MON-001", new[] { hidden }); } catch (InvalidOperationException) { blocked = true; }
    Check(blocked);
});
Test("missing or ambiguous ID blocks before a mutation callback", () => {
    var id = Physical(); id.StableMonitorId = "MON-001";
    var a = new MonitorInfo { Identity = id, DeviceName = "DISPLAY1" };
    var b = new MonitorInfo { Identity = id, DeviceName = "DISPLAY2" };
    int mutations = 0;
    foreach (var candidates in new[] { Array.Empty<MonitorInfo>(), new[] { a, b } }) {
        try { MonitorIdentityResolver.Resolve("MON-001", candidates); mutations++; } catch (InvalidOperationException) { }
    }
    Check(mutations == 0);
});
Test("cloned GDI sources do not authorize mutations", () => {
    var a = Physical(); a.StableMonitorId = "MON-001";
    var b = Physical("b"); b.StableMonitorId = "MON-002";
    bool blocked = false;
    try { MonitorIdentityResolver.Resolve("MON-001", new[] {
        new MonitorInfo { Identity = a, DeviceName = "DISPLAY1" }, new MonitorInfo { Identity = b, DeviceName = "DISPLAY1" } });
    } catch (InvalidOperationException) { blocked = true; }
    Check(blocked);
});
Test("legacy settings backup preserves exact original bytes across later saves", () => {
    string dir = Path.Combine(Path.GetTempPath(), "ds-identity-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
        string path = Path.Combine(dir, "settings.json");
        string original = "{ \"physicalDevice\": \"DISPLAY3\", \"applyOnStartup\": true }";
        File.WriteAllText(path, original);
        var s = new AppSettings();
        MonitorSettingsMigration.ConfirmSelection(s, "MON-001", false);
        SettingsStore.SaveTo(path, s); SettingsStore.SaveTo(path, s);
        Check(File.ReadAllText(path + ".pre-identity.bak") == original);
        Check(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))!.HasConfirmedIdentity);
    } finally { Directory.Delete(dir, true); }
});
Test("explicit replacement retires old binding without deleting evidence", () => {
    var c = NewCatalog(); var old = c.Register(Physical()); var replacement = c.Register(Physical(serial: 999));
    Check(old.Retired && c.Monitors.Contains(old));
    var live = Physical(serial: 999);
    Check(c.Match(live, new[] { live }) == replacement);
});
Test("duplicate logical IDs cannot match even if only one panel is connected", () => {
    var c = NewCatalog(); var a = c.Register(Physical("a", 1)); var b = c.Register(Physical("b", 2));
    b.StableMonitorId = a.StableMonitorId; var live = Physical("a", 1);
    Check(c.Match(live, new[] { live }) is null);
});
Test("default hotkey and Windows Escape parse without changing their meaning", () => {
    Check(RevertHotkey.Parse(RevertHotkey.DefaultGesture) == new RevertHotkey(7, 'R'));
    Check(RevertHotkey.Parse(" windows + escape ").ToString() == "Win+Esc");
    Check(RevertHotkey.Parse("shift+ctrl+alt+r").ToString() == RevertHotkey.DefaultGesture);
});
Test("invalid or explicitly reserved hotkeys are rejected", () => {
    foreach (string input in new[] { "Esc", "Ctrl++R", "Ctrl+Ctrl+R", "Ctrl+F12", "Ctrl+Shift+Esc", "Ctrl+F25", "Banana+R" }) {
        bool rejected = false; try { RevertHotkey.Parse(input); } catch (FormatException) { rejected = true; }
        Check(rejected);
    }
});
Test("occupied hotkey preserves old registration and saved settings", () => {
    var backend = new FakeHotkeyBackend(); using var hotkey = new HotkeyRegistration(backend);
    hotkey.Change(RevertHotkey.Parse(RevertHotkey.DefaultGesture), () => { });
    int? old = hotkey.ActiveId; backend.Reject = true; bool persisted = false;
    try { hotkey.Change(RevertHotkey.Parse("Ctrl+Alt+Q"), () => persisted = true); } catch (InvalidOperationException) { }
    Check(!persisted && hotkey.ActiveId == old && backend.Registered.Count == 1);
});
Test("save failure releases candidate and preserves old shortcut", () => {
    var backend = new FakeHotkeyBackend(); using var hotkey = new HotkeyRegistration(backend);
    hotkey.Change(RevertHotkey.Parse(RevertHotkey.DefaultGesture), () => { }); int? old = hotkey.ActiveId;
    try { hotkey.Change(RevertHotkey.Parse("Ctrl+Alt+Q"), () => throw new IOException("disk")); } catch (IOException) { }
    Check(hotkey.ActiveId == old && backend.Registered.Count == 1);
});
Test("successful rebind ignores queued messages from previous shortcut", () => {
    var backend = new FakeHotkeyBackend(); using var hotkey = new HotkeyRegistration(backend);
    hotkey.Change(RevertHotkey.Parse(RevertHotkey.DefaultGesture), () => { }); int old = hotkey.ActiveId!.Value;
    var next = RevertHotkey.Parse("Ctrl+Alt+Q"); hotkey.Change(next, () => { });
    Check(!hotkey.Matches(old, 7, 'R') && hotkey.Matches(hotkey.ActiveId!.Value, next.Modifiers, next.VirtualKey));
    Check(backend.Registered.Count == 1);
});
Test("disable and dispose unregister and remain idempotent", () => {
    var backend = new FakeHotkeyBackend(); var hotkey = new HotkeyRegistration(backend);
    hotkey.Change(RevertHotkey.Parse(RevertHotkey.DefaultGesture), () => { });
    hotkey.Change(null, () => { }); Check(hotkey.ActiveId is null && backend.Registered.Count == 0);
    hotkey.Dispose(); hotkey.Dispose();
});
Test("legacy settings receive a default and custom shortcut survives persistence", () => {
    var legacy = JsonSerializer.Deserialize<AppSettings>("{}")!;
    Check(legacy.GlobalRevertHotkeyEnabled && legacy.GlobalRevertHotkey == RevertHotkey.DefaultGesture);
    legacy.GlobalRevertHotkey = "Ctrl+Alt+Q"; legacy.GlobalRevertHotkeyEnabled = false;
    var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(legacy))!;
    Check(!restored.GlobalRevertHotkeyEnabled && restored.GlobalRevertHotkey == "Ctrl+Alt+Q");
});
Test("Revert requests during Apply coalesce and repeated Revert cannot overlap", () => {
    var queue = new RevertRequestQueue();
    Check(queue.Request(true) == RevertRequestDisposition.Queued);
    Check(queue.Request(true) == RevertRequestDisposition.Queued);
    Check(queue.TakePending() && !queue.TakePending());
    Check(queue.Request(false) == RevertRequestDisposition.Start);
    Check(queue.Request(true) == RevertRequestDisposition.AlreadyReverting);
    queue.CompleteRevert(); Check(queue.Request(false) == RevertRequestDisposition.Start);
});
Test("three stacked tiles a portrait Samsung and exposes two horizontal dividers", () => {
    var layout = LayoutDefinition.FromKind(LayoutKind.ThreeHorizontal);
    var sizes = layout.ComputeDefaultSegmentSizes(1080, 1920);
    Check(layout.Columns == 1 && layout.Rows == 3 && layout.SegmentCount == 3);
    Check(layout.VerticalSplitLines.Count == 0 && layout.HorizontalSplitLines.SequenceEqual(new[] { 0, 1 }));
    Check(sizes.All(s => s.Width == 1080 && s.Height == 640));
    var rects = LayoutGeometry.ComputePhysRects(layout, 1080, 1920, sizes);
    Check(rects.SequenceEqual(new[] { new PixelRect(0, 0, 1080, 640), new PixelRect(0, 640, 1080, 640), new PixelRect(0, 1280, 1080, 640) }));
    Check(layout.PreviewBoxes.Count == 3 && layout.PreviewBoxes.Select(p => p.Y).SequenceEqual(layout.PreviewBoxes.Select(p => p.Y).OrderBy(y => y)));
});
Test("three stacked preserves coverage after resizing and persists without renumbering older layouts", () => {
    var layout = LayoutDefinition.FromKind(LayoutKind.ThreeHorizontal);
    var rects = LayoutGeometry.ComputePhysRects(layout, 1080, 1921, new[] { (1080, 640), (1080, 700), (1080, 640) });
    Check(rects[0].Y == 0 && rects[1].Y == rects[0].H && rects[2].Y == rects[1].Y + rects[1].H);
    Check(rects.Sum(r => r.H) == 1921 && rects.All(r => r.X == 0 && r.W == 1080));
    var saved = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new AppSettings { Layout = LayoutKind.ThreeHorizontal }))!;
    Check(LayoutDefinition.FromKind(saved.Layout) == layout);
    Check((int)LayoutKind.TwoHorizontal == 1 && (int)LayoutKind.ThreeColumns == 2 && (int)LayoutKind.SixteenNineCenter == 6);
});

Test("splitting the primary selects segment one; splitting a secondary preserves the primary", () => {
    var active = new[] { "P", "S", "V1", "V2" };
    Check(PrimaryMonitorState.BeginApply(null, "P", "P").CompleteApply("V1", active).DesiredId == "V1");
    Check(PrimaryMonitorState.BeginApply(null, "P", "S").CompleteApply("V1", active).DesiredId == "P");
});
Test("reapply preserves the original primary even after a physical manual override", () => {
    var first = PrimaryMonitorState.BeginApply(null, "P", "S").CompleteApply("V1", new[] { "P", "S", "V1" });
    var manual = first with { DesiredId = "Q", ManualOverride = true };
    var again = PrimaryMonitorState.BeginApply(manual, "Q", "S").CompleteApply("V1", new[] { "P", "S", "Q", "V1" });
    Check(again.PreSplitId == "P" && again.DesiredId == "Q");
    Check(again.BeginRevert().DesiredId == "P");
});
var primaryTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
List<PrimaryObservation> Desktop(string primary, params string[] ids) => ids.Select(id =>
    new PrimaryObservation(id, id == primary, id.StartsWith("V"), 1920, 1080)).ToList();
PrimaryMonitorState Session(string desired = "P") => new() { PreSplitId = "P", SplitPhysicalId = "S", DesiredId = desired, DesiredIsVirtual = desired.StartsWith("V"), SplitActive = true };
Test("offline primary and Windows fallback never overwrite preference; reconnect restores original ID", () => {
    var tracker = new PrimaryPreferenceTracker(); var state = Session();
    tracker.Observe(state, Desktop("P", "P", "S", "V1"), primaryTime, false, false, false);
    var offline = tracker.Observe(state, Desktop("S", "S", "V1"), primaryTime.AddSeconds(10), true, true, false);
    Check(offline.State.DesiredId == "P" && offline.TargetId == "");
    tracker.Observe(state, Desktop("S", "P", "S", "V1"), primaryTime.AddSeconds(20), true, true, false);
    var returned = tracker.Observe(state, Desktop("S", "P", "S", "V1"), primaryTime.AddSeconds(24), false, false, false);
    Check(returned.TargetId == "P" && !returned.State.ManualOverride);
});
Test("manual Windows primary on a settled topology is retained across sleep and wake", () => {
    var tracker = new PrimaryPreferenceTracker(); var state = Session("V1");
    tracker.Observe(state, Desktop("V1", "P", "Q", "V1"), primaryTime, false, false, false);
    var manual = tracker.Observe(state, Desktop("Q", "P", "Q", "V1"), primaryTime.AddSeconds(10), true, false, false);
    Check(manual.State.DesiredId == "Q" && manual.State.PreSplitId == "P" && manual.State.ManualOverride);
    tracker.Observe(manual.State, Desktop("P", "P", "Q", "V1"), primaryTime.AddSeconds(20), true, true, false);
    var wake = tracker.Observe(manual.State, Desktop("P", "P", "Q", "V1"), primaryTime.AddSeconds(24), false, false, false);
    Check(wake.TargetId == "Q");
    Check(manual.State.BeginRevert().DesiredId == "P");
});
Test("manual virtual segment choice survives secure desktop and resumes that segment", () => {
    var tracker = new PrimaryPreferenceTracker(); var state = Session("V2") with { ManualOverride = true };
    tracker.Observe(state, Desktop("V2", "P", "V1", "V2"), primaryTime, false, false, false);
    var secure = tracker.Observe(state, Desktop("V2", "P", "V1", "V2"), primaryTime.AddSeconds(10), true, true, true);
    Check(secure.TargetId == "P" && secure.State.DesiredId == "V2");
    tracker.Observe(state, Desktop("P", "P", "V1", "V2"), primaryTime.AddSeconds(12), true, false, false);
    Check(tracker.Observe(state, Desktop("P", "P", "V1", "V2"), primaryTime.AddSeconds(16), false, false, false).TargetId == "V2");
});
Test("Revert while original primary is offline preserves a pending restoration across restart", () => {
    var pending = Session("V1").BeginRevert();
    var restored = JsonSerializer.Deserialize<PrimaryMonitorState>(JsonSerializer.Serialize(pending))!;
    var tracker = new PrimaryPreferenceTracker();
    Check(tracker.Observe(restored, Desktop("S", "S"), primaryTime, true, true, false).State.RevertPending);
    tracker.Observe(restored, Desktop("S", "P", "S"), primaryTime.AddSeconds(10), true, true, false);
    Check(tracker.Observe(restored, Desktop("S", "P", "S"), primaryTime.AddSeconds(14), false, false, false).TargetId == "P");
    Check(!tracker.Observe(restored, Desktop("P", "P", "S"), primaryTime.AddSeconds(15), true, false, false).State.RevertPending);
});
Test("own primary switch notification cannot be learned as a user override", () => {
    var tracker = new PrimaryPreferenceTracker(); var state = Session("V1");
    tracker.Observe(state, Desktop("V1", "P", "V1"), primaryTime, false, false, false);
    tracker.OwnSwitch(primaryTime.AddSeconds(10));
    var own = tracker.Observe(state, Desktop("P", "P", "V1"), primaryTime.AddSeconds(11), true, false, false);
    Check(own.State.DesiredId == "V1" && !own.State.ManualOverride);
});
Test("unknown and duplicate identities block reconciliation without losing state", () => {
    var tracker = new PrimaryPreferenceTracker(); var state = Session();
    Check(tracker.Observe(state, Desktop("", "", "Q"), primaryTime, true, false, false).TargetId == "");
    Check(tracker.Observe(state, Desktop("P", "P", "P"), primaryTime, true, false, false).State == state);
});
Test("enumeration order and GDI renumbering cannot change logical primary", () => {
    var tracker = new PrimaryPreferenceTracker(); var state = Session();
    tracker.Observe(state, Desktop("P", "P", "S"), primaryTime, false, false, false);
    var reordered = tracker.Observe(state, Desktop("S", "S", "P"), primaryTime.AddSeconds(10), false, true, false);
    Check(reordered.State.DesiredId == "P");
    Check(tracker.Observe(state, Desktop("S", "S", "P"), primaryTime.AddSeconds(14), false, false, false).TargetId == "P");
});
Test("delayed display notification can still recognize a settled manual change", () => {
    var tracker = new PrimaryPreferenceTracker(); var state = Session();
    tracker.Observe(state, Desktop("P", "P", "Q"), primaryTime, false, false, false);
    Check(tracker.Observe(state, Desktop("Q", "P", "Q"), primaryTime.AddSeconds(10), false, false, false).TargetId == "");
    Check(tracker.Observe(state, Desktop("Q", "P", "Q"), primaryTime.AddSeconds(11), true, false, false).State.DesiredId == "Q");
});
Test("new layout dropping a preferred virtual segment falls back to the session default", () => {
    var state = Session("V3") with { ManualOverride = true };
    Check(state.CompleteApply("V1", new[] { "P", "S", "V1", "V2" }).DesiredId == "P");
});
Test("primary preference store is durable and corruption is not silently overwritten", () => {
    string dir = Path.Combine(Path.GetTempPath(), "ds-primary-" + Guid.NewGuid());
    Directory.CreateDirectory(dir);
    try {
        string path = Path.Combine(dir, "primary.json"); var store = new PrimaryPreferenceStore(path);
        var state = Session("V2") with { ManualOverride = true };
        store.Write(state); Check(new PrimaryPreferenceStore(path).Read() == state);
        store.Write(state.BeginRevert()); Check(File.Exists(path + ".bak"));
        File.WriteAllText(path, "broken"); bool refused = false;
        try { store.Write(state); } catch (JsonException) { refused = true; }
        Check(refused && File.ReadAllText(path) == "broken");
    } finally { Directory.Delete(dir, true); }
});

Test("blank enumeration followed by a late fallback cannot become a manual preference", () => {
    var tracker = new PrimaryPreferenceTracker(); var state = Session();
    tracker.Observe(state, Desktop("P", "P", "Q"), primaryTime, false, false, false);
    tracker.Observe(state, Array.Empty<PrimaryObservation>(), primaryTime.AddSeconds(10), true, true, false);
    var back = tracker.Observe(state, Desktop("Q", "P", "Q"), primaryTime.AddSeconds(30), true, false, false);
    Check(back.State.DesiredId == "P" && !back.State.ManualOverride);
});
Test("only a recent Windows Settings interaction qualifies as automatic manual intent", () => {
    Check(PrimaryChangeIntent.IsSettingsInteraction("SystemSettings", 200));
    Check(!PrimaryChangeIntent.IsSettingsInteraction("SystemSettings", 8000));
    Check(!PrimaryChangeIntent.IsSettingsInteraction("explorer", 200));
});
Test("manual intent while the former primary is offline still retains the original for Revert", () => {
    var tracker = new PrimaryPreferenceTracker(); var state = Session();
    tracker.Observe(state, Desktop("R", "R", "Q"), primaryTime, false, true, false);
    var manual = tracker.Observe(state, Desktop("Q", "R", "Q"), primaryTime.AddSeconds(10), true, false, false);
    Check(manual.State.DesiredId == "Q" && manual.State.PreSplitId == "P");
    Check(manual.State.BeginRevert().DesiredId == "P");
});
Test("after Revert the physical preference is still restored on later reconnect", () => {
    var state = Session().BeginRevert() with { RevertPending = false };
    var tracker = new PrimaryPreferenceTracker();
    tracker.Observe(state, Desktop("P", "P", "Q"), primaryTime, false, false, false);
    tracker.Observe(state, Desktop("Q", "Q"), primaryTime.AddSeconds(10), false, true, false);
    tracker.Observe(state, Desktop("Q", "P", "Q"), primaryTime.AddSeconds(20), false, true, false);
    Check(tracker.Observe(state, Desktop("Q", "P", "Q"), primaryTime.AddSeconds(24), false, false, false).TargetId == "P");
});

Test("automatic exit teardown preserves manual split preference for the next startup", () => {
    var saved = Session("Q") with { ManualOverride = true };
    var exit = saved.CompleteTeardown(deliberate: false);
    var restarted = JsonSerializer.Deserialize<PrimaryMonitorState>(JsonSerializer.Serialize(exit))!;
    var reapplied = PrimaryMonitorState.BeginApply(restarted, "P", "S").CompleteApply("V1", new[] { "P", "S", "Q", "V1" });
    Check(reapplied.PreSplitId == "P" && reapplied.DesiredId == "Q" && reapplied.ManualOverride);
    Check(saved.CompleteTeardown(deliberate: true).DesiredId == "P");
});
Test("repeated power-off events keep automatic fallback out of the saved preference", () => {
    var tracker = new PrimaryPreferenceTracker(); var state = Session();
    tracker.Observe(state, Desktop("P", "P", "Q"), primaryTime, false, false, false);
    for (int seconds = 10; seconds < 30; seconds++) {
        var off = tracker.Observe(state, Desktop("Q", "P", "Q"), primaryTime.AddSeconds(seconds), false, true, false);
        Check(off.State.DesiredId == "P" && off.TargetId == "");
    }
    tracker.Observe(state, Desktop("Q", "P", "Q"), primaryTime.AddSeconds(30), false, true, false);
    Check(tracker.Observe(state, Desktop("Q", "P", "Q"), primaryTime.AddSeconds(34), false, false, false).TargetId == "P");
});

Test("manual physical-source primary survives reapply unless that source will be hidden", () => {
    var manual = Session("S") with { ManualOverride = true };
    Check(manual.CompleteApply("V1", new[] { "P", "S", "V1" }).DesiredId == "S");
    Check(manual.CompleteApply("V1", new[] { "P", "S", "V1" }, hidePhysical: true).DesiredId == "P");
});

Console.WriteLine($"{passed} tests passed; no Windows display APIs were called.");

sealed class FakeHotkeyBackend : IHotkeyBackend
{
    public bool Reject { get; set; }
    public HashSet<int> Registered { get; } = new();
    public bool TryRegister(int id, RevertHotkey gesture, out int error)
    { error = Reject ? 1409 : 0; return !Reject && Registered.Add(id); }
    public void Unregister(int id) => Registered.Remove(id);
}
