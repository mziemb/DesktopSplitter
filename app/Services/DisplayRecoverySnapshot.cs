using System.IO;
using System.Text.Json;
using DesktopSplitter.Interop;
using DesktopSplitter.Models;

namespace DesktopSplitter.Services;

internal sealed record SavedDisplay(
    string StableId, string DeviceName, string Label, string MonitorDevicePath,
    uint AdapterLow, int AdapterHigh, uint TargetId, uint SourceAdapterLow, int SourceAdapterHigh, uint SourceId,
    int X, int Y, int Width, int Height, uint Rotation, uint RefreshMillihertz, bool IsVirtual, bool IsPrimary)
{
    public static SavedDisplay Capture(MonitorInfo m) => new(
        m.StableMonitorId, m.DeviceName, m.DisplayLabel, m.MonitorDevicePath,
        m.AdapterIdLow, m.AdapterIdHigh, m.TargetId, m.SourceAdapterIdLow, m.SourceAdapterIdHigh, m.SourceId,
        m.PositionX, m.PositionY, m.CurrentWidth, m.CurrentHeight, m.Rotation,
        m.CurrentRefreshMillihertz, m.IsDeskSplitVirtual, m.IsPrimary);
}

internal enum DisplayRecoveryPhase { Applying, Active, Reverting, AwaitingConfirmation }

internal sealed record DisplayRecoverySnapshot(
    int SchemaVersion, string PrimaryId, string SplitMonitorId, bool SplitWasPrimary, SavedDisplay[] Displays,
    DisplayRecoveryPhase Phase = DisplayRecoveryPhase.Applying)
{
    public static DisplayRecoverySnapshot Capture(IReadOnlyList<MonitorInfo> active, string splitMonitorId)
    {
        foreach (var monitor in active)
            _ = MonitorIdentityResolver.Resolve(monitor.StableMonitorId, active);

        MonitorInfo? primary = active.SingleOrDefault(m => m.IsPrimary);
        if (primary is null || primary.IsDeskSplitVirtual || primary.PositionX != 0 || primary.PositionY != 0)
            throw new DisplayArrangeException("The original physical primary at (0,0) could not be confirmed.");

        _ = MonitorIdentityResolver.Resolve(splitMonitorId, active);
        return new(1, primary.StableMonitorId, splitMonitorId, primary.StableMonitorId == splitMonitorId,
                   active.Select(SavedDisplay.Capture).ToArray());
    }

    public string AppliedPrimary(string splitId, IReadOnlyList<MonitorInfo> segments, int primarySegment)
        => PrimaryId == splitId ? segments[primarySegment].StableMonitorId : PrimaryId;

    public bool NeedsStartupRecovery(bool compositorRunning, bool hasVirtualDisplays)
        => Phase != DisplayRecoveryPhase.Active || !compositorRunning || !hasVirtualDisplays;

    public IReadOnlyList<DisplayArranger.MonitorPlacement>? RestoreLayout(IReadOnlyList<MonitorInfo> live)
    {
        var physical = Displays.Where(d => !d.IsVirtual).ToDictionary(d => d.StableId);
        if (live.Count != physical.Count || live.Select(m => m.StableMonitorId).Distinct().Count() != live.Count ||
            live.Any(m => m.IsDeskSplitVirtual || !physical.ContainsKey(m.StableMonitorId)))
            return null;

        return live.Select(m =>
        {
            SavedDisplay saved = physical[m.StableMonitorId];
            return new DisplayArranger.MonitorPlacement(m.DeviceName, saved.Width, saved.Height,
                saved.X, saved.Y, saved.Rotation, saved.StableId == PrimaryId, saved.StableId);
        }).ToList();
    }
}

internal sealed class DisplayTopologyStability
{
    private SavedDisplay[]? previous;

    public bool Observe(IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0)
        {
            previous = null;
            return false;
        }

        var sample = monitors.Select(SavedDisplay.Capture)
            .OrderBy(m => m.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.TargetId).ToArray();
        bool stable = previous is not null && sample.SequenceEqual(previous);
        previous = sample;
        return stable;
    }
}

internal sealed class DisplayRecoveryStore(string filePath)
{
    public static DisplayRecoveryStore ForUser { get; } =
        new(Path.Combine(SettingsStore.Directory, "display-recovery.json"));

    public DisplayRecoverySnapshot? Load()
    {
        if (!File.Exists(filePath)) return null;
        var snapshot = JsonSerializer.Deserialize<DisplayRecoverySnapshot>(File.ReadAllBytes(filePath));
        if (snapshot is null || snapshot.SchemaVersion != 1 || string.IsNullOrEmpty(snapshot.PrimaryId) ||
            !Enum.IsDefined(snapshot.Phase) || snapshot.Displays is null ||
            snapshot.Displays.Any(d => string.IsNullOrEmpty(d.StableId) || d.Width <= 0 || d.Height <= 0) ||
            snapshot.Displays.Select(d => d.StableId).Distinct().Count() != snapshot.Displays.Length ||
            snapshot.Displays.Count(d =>
                d.StableId == snapshot.PrimaryId && !d.IsVirtual && d.X == 0 && d.Y == 0) != 1)
            throw new InvalidDataException("Invalid display recovery snapshot. Recovery evidence has been retained.");
        return snapshot;
    }

    public DisplayRecoverySnapshot Begin(Func<DisplayRecoverySnapshot> capture)
    {
        if (Load() is { } existing) return existing;
        var snapshot = capture();
        Write(snapshot, overwrite: false);
        return snapshot;
    }

    public void SetPhase(DisplayRecoverySnapshot snapshot, DisplayRecoveryPhase phase)
    {
        if (snapshot.Phase != phase) Write(snapshot with { Phase = phase }, overwrite: true);
    }

    public T RunApply<T>(DisplayRecoverySnapshot snapshot, Func<T> apply, Action revert, Action<string> log,
        bool confirmationRequired = false)
    {
        SetPhase(snapshot, DisplayRecoveryPhase.Applying);
        try
        {
            T result = apply();
            SetPhase(snapshot with { Phase = DisplayRecoveryPhase.Applying }, confirmationRequired
                ? DisplayRecoveryPhase.AwaitingConfirmation : DisplayRecoveryPhase.Active);
            return result;
        }
        catch (Exception applyError)
        {
            log($"Apply failed during display changes: {applyError.Message}. Restoring the original desktop.");
            try
            {
                revert();
            }
            catch (Exception recoveryError)
            {
                throw new AggregateException("Apply failed and recovery is incomplete.", applyError, recoveryError);
            }
            throw;
        }
    }

    private void Write(DisplayRecoverySnapshot snapshot, bool overwrite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        string temp = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, snapshot);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, filePath, overwrite);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public void Complete() => File.Delete(filePath);
}
