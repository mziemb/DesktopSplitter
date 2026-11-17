using DesktopSplitter.Interop;
using DesktopSplitter.Models;
using DesktopSplitter.Services;

internal static class PrimaryPreservationTests
{
    public static void Run(Action<string, Action> test, Action<bool> check)
    {
        var main = Screen("main", 1, x: 0, y: 0, primary: true);
        var side = Screen("side", 2, x: 1920, y: -240, rotation: 2);
        MonitorInfo[] physical = [main, side];
        var physicalLayout = DisplayArranger.SnapshotLayout(physical);
        MonitorInfo[] segments = [Screen("segment-1", 7, virtualDisplay: true), Screen("segment-2", 8, virtualDisplay: true)];
        var secondarySplit = DisplayRecoverySnapshot.Capture(physical, "side");
        var primarySplit = DisplayRecoverySnapshot.Capture(physical, "main");

        test("Apply on a secondary display preserves the physical primary at the origin", () =>
        {
            MonitorInfo[] current = [Screen("main", 3, x: -2000), Screen("side", 2, primary: true), .. segments];
            var layout = LayoutDefinition.FromKind(LayoutKind.TwoVertical);
            var virtualLayout = DisplayArranger.ComputePlacements(physicalLayout[1], layout, segments, [(960, 1080), (960, 1080)], physicalLayout);
            string id = secondarySplit.AppliedPrimary("side", segments, primarySegment: 1);
            var desired = DisplayArranger.ComposeLayout(current, virtualLayout, physicalLayout,
                MonitorIdentityResolver.Resolve(id, current).DeviceName);
            check(!secondarySplit.SplitWasPrimary && id == "main");
            check(desired.Single(p => p.IsPrimary) is { X: 0, Y: 0 } p && p.DeviceName == current[0].DeviceName);
            check(desired.Single(p => p.DeviceName == side.DeviceName) is { X: 1920, Y: -240, Rotation: 2 });
        });

        test("Apply on the primary selects the first segment by default and supports segment choice", () =>
        {
            var current = physical.Concat(segments).ToArray();
            var layout = LayoutDefinition.FromKind(LayoutKind.TwoVertical);
            var virtualLayout = DisplayArranger.ComputePlacements(physicalLayout[0], layout, segments, [(960, 1080), (960, 1080)], physicalLayout);
            check(virtualLayout[0].X == side.PositionX + side.CurrentWidth);
            check(primarySplit.SplitWasPrimary && primarySplit.AppliedPrimary("main", segments, 0) == "segment-1");
            var id = primarySplit.AppliedPrimary("main", segments, 1);
            var desired = DisplayArranger.ComposeLayout(current, virtualLayout, physicalLayout,
                MonitorIdentityResolver.Resolve(id, current).DeviceName);
            check(desired.Single(p => p.IsPrimary) is { X: 0, Y: 0 } p && p.DeviceName == segments[1].DeviceName);
            check(desired[1].X - desired[0].X == side.PositionX && desired[1].Y - desired[0].Y == side.PositionY);
        });

        foreach (var snapshot in new[] { secondarySplit, primarySplit })
        {
            test($"Revert after splitting {snapshot.SplitMonitorId} restores original desktop geometry", () =>
            {
                MonitorInfo[] shifted = [Screen("side", 2, primary: true, rotation: 1),
                                         Screen("main", 3, x: -1080, width: 1080)];
                var restored = snapshot.RestoreLayout(shifted)!;
                check(restored[0] is { X: 1920, Y: -240, Width: 1920, Height: 1080, Rotation: 2, IsPrimary: false });
                check(restored[1] is { X: 0, Y: 0, Width: 1920, Height: 1080, Rotation: 1, IsPrimary: true });
                check(restored[1].DeviceName == shifted[1].DeviceName);
                var verified = DisplayArranger.VerifyPrimary(snapshot.PrimaryId,
                    [Screen("main", 3, primary: true), side]);
                check(verified.DeviceName.EndsWith("DISPLAY3"));
            });
        }

        test("DISPLAY1 reused by another screen cannot verify the original primary", () =>
        {
            MonitorInfo[] live = [Screen("different", 1, primary: true), Screen("main", 3, x: -1920)];
            check(secondarySplit.AppliedPrimary("side", segments, 0) == "main");
            Expect<DisplayArrangeException>(() => DisplayArranger.VerifyPrimary(secondarySplit.PrimaryId, live));
        });

        test("Missing original primary leaves the existing physical layout available and fails verification", () =>
        {
            MonitorInfo[] live = [Screen("side", 1, primary: true)];
            check(secondarySplit.RestoreLayout(live) is null);
            Expect<InvalidOperationException>(() => DisplayArranger.VerifyPrimary(secondarySplit.PrimaryId, live));
            check(live.Single().IsPrimary);
        });

        test("A new monitor prevents replaying the original coordinates over an unknown desktop", () =>
        {
            check(primarySplit.RestoreLayout([main, side, Screen("new", 4, x: -1920)]) is null);
        });

        test("Interrupted Apply persists its original snapshot across process restart", () => WithStore(path =>
        {
            var firstProcess = new DisplayRecoveryStore(path);
            firstProcess.Begin(() => primarySplit);
            var restarted = new DisplayRecoveryStore(path).Load()!;
            check(restarted.NeedsStartupRecovery(compositorRunning: true, hasVirtualDisplays: true));
            check(restarted.Displays.SequenceEqual(primarySplit.Displays));
            check(restarted.Displays[0].TargetId != Screen("main", 3).TargetId);
        }));

        test("Restart keeps a completed active split and repairs one orphaned after a reboot", () => WithStore(path =>
        {
            var original = new DisplayRecoveryStore(path);
            original.SetPhase(original.Begin(() => primarySplit), DisplayRecoveryPhase.Active);
            var restarted = new DisplayRecoveryStore(path).Load()!;
            check(!restarted.NeedsStartupRecovery(compositorRunning: true, hasVirtualDisplays: true));
            check(restarted.NeedsStartupRecovery(compositorRunning: false, hasVirtualDisplays: true));
            check(restarted.NeedsStartupRecovery(compositorRunning: false, hasVirtualDisplays: false));
        }));

        test("A partial Apply triggers recovery and retains the snapshot if recovery fails", () => WithStore(path =>
        {
            var store = new DisplayRecoveryStore(path);
            var snapshot = store.Begin(() => primarySplit);
            bool virtualsCreated = false;
            Expect<AggregateException>(() => store.RunApply<int>(snapshot, () =>
            {
                check(store.Load()!.Phase == DisplayRecoveryPhase.Applying);
                virtualsCreated = true;
                throw new InvalidOperationException("Display layout failed after creating virtual monitors.");
            }, () =>
            {
                check(virtualsCreated);
                virtualsCreated = false;
                store.SetPhase(store.Load()!, DisplayRecoveryPhase.Reverting);
                throw new IOException("The original monitor is disconnected.");
            }, _ => { }));
            check(!virtualsCreated);
            check(store.Load() is { Phase: DisplayRecoveryPhase.Reverting, PrimaryId: "main" });
        }));

        test("Repeated Apply after an active split keeps the first snapshot and journals partial failure", () => WithStore(path =>
        {
            var store = new DisplayRecoveryStore(path);
            store.SetPhase(store.Begin(() => secondarySplit), DisplayRecoveryPhase.Active);
            var repeated = store.Begin(() => throw new Exception("Must reuse the first topology"));
            store.SetPhase(repeated, DisplayRecoveryPhase.Applying);
            var interrupted = new DisplayRecoveryStore(path);
            var pending = interrupted.Load()!;
            check(pending.SplitMonitorId == "side" && pending.Displays.SequenceEqual(secondarySplit.Displays));
            check(pending.NeedsStartupRecovery(compositorRunning: false, hasVirtualDisplays: true));
            interrupted.SetPhase(pending, DisplayRecoveryPhase.Reverting);
            check(interrupted.Load()!.RestoreLayout([side]) is null);
            check(interrupted.Load()!.NeedsStartupRecovery(compositorRunning: true, hasVirtualDisplays: true));
            check(interrupted.Load()!.RestoreLayout(physical) is not null);
            interrupted.Complete();
            check(interrupted.Load() is null);
        }));
    }

    private static MonitorInfo Screen(string id, int display, int x = 0, int y = 0, int width = 1920,
        bool primary = false, uint rotation = 1, bool virtualDisplay = false) => new()
    {
        DeviceName = $@"\\.\DISPLAY{display}",
        Identity = new MonitorIdentity { StableMonitorId = id },
        TargetId = (uint)display,
        AdapterIdLow = (uint)(display + 10),
        SourceId = (uint)display,
        PositionX = x,
        PositionY = y,
        CurrentWidth = width,
        CurrentHeight = 1080,
        Rotation = rotation,
        IsActive = true,
        IsPrimary = primary,
        IsDeskSplitVirtual = virtualDisplay,
    };

    private static void WithStore(Action<string> body)
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { body(Path.Combine(directory, "display-recovery.json")); }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void Expect<T>(Action body) where T : Exception
    {
        try { body(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
