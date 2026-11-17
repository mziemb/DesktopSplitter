using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using DesktopSplitter.Interop;
using DesktopSplitter.Models;

namespace DesktopSplitter.Services;

public sealed class ApplyException : Exception
{
    public ApplyException(string message) : base(message) { }
    public ApplyException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The dialogs the display-hiding sequence needs. Every method is called from a BACKGROUND
/// thread and blocks until the user answers, so implementations must marshal to the UI thread.
/// </summary>
public interface ISpecializationUi
{
    /// <summary>
    /// The programmatic DisplayConfig SET was refused. Walk the user through doing the same thing
    /// in Windows Settings ("Remove display from desktop"), polling until it takes effect.
    /// Returns true once the monitor really is off the desktop.
    /// </summary>
    bool GuideManualEnable(MonitorInfo physical, SpecializationResult failure, CancellationToken ct);

    /// <summary>
    /// Keep-or-revert countdown, shown once the compositor confirms it is presenting through the
    /// hidden display. Returns true to keep. MUST resolve to false on its own timeout — the app
    /// is the auto-revert authority.
    /// </summary>
    bool ConfirmKeep(MonitorInfo physical, int timeoutSeconds, CancellationToken ct);

    /// <summary>
    /// Undoing the hide programmatically was refused too. Tell the user how to put the monitor
    /// back themselves, and wait until they have (or they dismiss).
    /// </summary>
    void GuideManualDisable(MonitorInfo physical, SpecializationResult failure, CancellationToken ct);
}

/// <summary>
/// The "Keep these display settings?" countdown, mirroring what Windows shows after a resolution
/// change. Called from a BACKGROUND thread and blocks; returns true to keep.
/// MUST resolve to false when the countdown lapses — that timeout is the safety net for a change
/// that left the user unable to see or click anything.
/// </summary>
public interface IApplyConfirmer
{
    bool ConfirmApply(string summary, int timeoutSeconds, CancellationToken ct);
}

/// <summary>
/// The configuration that was live before an apply, so a declined change can be put back exactly.
/// </summary>
public sealed record AppliedSnapshot(
    bool WasApplied,
    string PhysicalDevice,
    LayoutKind Layout,
    IReadOnlyList<(int Width, int Height)> Resolutions,
    bool HidePhysicalDisplay,
    int PrimarySegmentIndex = 0)
{
    public static readonly AppliedSnapshot None =
        new(false, string.Empty, LayoutKind.TwoVertical, Array.Empty<(int, int)>(), false);
}

public sealed class ApplyRequest
{
    public required MonitorInfo Physical { get; init; }
    public required LayoutDefinition Layout { get; init; }

    /// <summary>One entry per layout segment, in layout order.</summary>
    public required IReadOnlyList<(int Width, int Height)> Resolutions { get; init; }

    /// <summary>Try to remove the physical monitor from the desktop once the split is up.</summary>
    public bool HidePhysicalDisplay { get; init; }
    public int PrimarySegmentIndex { get; init; }

    /// <summary>Supplies the hiding dialogs. Without one, hiding is kept automatically.</summary>
    public ISpecializationUi? SpecializationUi { get; init; }

    /// <summary>
    /// Show the keep-or-revert countdown after applying. Set ONLY for user-initiated applies:
    /// startup auto-apply, crash recovery and rollback re-applies must never prompt.
    /// </summary>
    public bool PromptToConfirm { get; init; }

    /// <summary>Supplies that countdown. Without one, no prompt is shown whatever the flag says.</summary>
    public IApplyConfirmer? Confirmer { get; init; }
}

public sealed class ApplyResult
{
    public required IReadOnlyList<MonitorInfo> VirtualMonitors { get; init; }
    public required string ConfigPath { get; init; }
    public required int CompositorPid { get; init; }

    /// <summary>True when the user declined (or ignored) the countdown and we put things back.</summary>
    public bool RolledBack { get; init; }

    /// <summary>What we rolled back to, when <see cref="RolledBack"/> is set.</summary>
    public string RollbackDescription { get; init; } = string.Empty;
}

/// <summary>
/// Orchestrates the full apply / revert sequence described in docs/ARCHITECTURE.md.
/// All methods are synchronous and blocking; call them off the UI thread and pass an
/// <see cref="IProgress{T}"/> created on the UI thread for status updates.
/// </summary>
public static class ApplyService
{
    public const int MinSegmentDimension = LayoutGeometry.MinSegmentDimension;
    public const int MaxSegmentDimension = LayoutGeometry.MaxSegmentDimension;

    private static readonly TimeSpan MonitorArrivalTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CompositorStopTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan EdidMappingRetryTimeout = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>%ProgramData%\DesktopSplitter</summary>
    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DesktopSplitter");

    public static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    // ------------------------------------------------------------------ apply

    /// <summary>
    /// Applies a split. The mutating pipeline runs under <see cref="DisplayOperationGate"/> so an
    /// automatic suspend/resume cannot interleave with it; the confirmation countdown runs OUTSIDE
    /// the gate so a UAC prompt is never made to wait on it.
    /// </summary>
    public static ApplyResult Apply(ApplyRequest request, IProgress<string> log, CancellationToken ct)
    {
        AppliedSnapshot snapshot;
        ApplyResult applied;
        lock (DisplayOperationGate)
        {
            applied = ApplyCore(request, log, ct, out snapshot);
        }

        // g. "Keep these display settings?" — only for user-initiated applies. A change that
        //    leaves the user unable to see or click anything undoes itself when the countdown
        //    lapses, exactly like Windows' own resolution prompt.
        if (!request.PromptToConfirm || request.Confirmer is null) return applied;

        string summary = $"{request.Layout.Name} on {request.Physical.DisplayLabel} " +
                         $"({string.Join(", ", request.Resolutions.Select(r => $"{r.Width}x{r.Height}"))})";

        if (request.Confirmer.ConfirmApply(summary, ConfirmChangesSeconds, ct))
        {
            log.Report("Change kept.");
            return applied;
        }

        log.Report("Change not confirmed — rolling back.");
        string restored = RollBackTo(snapshot, log, ct, request.SpecializationUi);
        return new ApplyResult
        {
            VirtualMonitors = applied.VirtualMonitors,
            ConfigPath = applied.ConfigPath,
            CompositorPid = applied.CompositorPid,
            RolledBack = true,
            RollbackDescription = restored,
        };
    }

    private static ApplyResult ApplyCore(
        ApplyRequest request, IProgress<string> log, CancellationToken ct, out AppliedSnapshot snapshot)
    {
        ValidateRequest(request);

        ct.ThrowIfCancellationRequested();
        AppSettings? preflight = SettingsStore.Load();
        if (!string.IsNullOrEmpty(preflight?.SpecializedDevice) && string.IsNullOrEmpty(preflight.SpecializedStableMonitorId))
            throw new ApplyException("Legacy hidden-monitor identity is unverified; restore it in Windows Settings first.");

        var connectedBefore = DisplayConfig.EnumerateConnected();
        var before = connectedBefore.Where(m => m.IsActive).ToList();
        LogPrimary("before Apply", before, log);
        MonitorInfo physical = MonitorIdentityResolver.Resolve(request.Physical.StableMonitorId, connectedBefore,
            requireActive: request.Physical.StableMonitorId != preflight?.SpecializedStableMonitorId);
        foreach (var monitor in before)
            _ = MonitorIdentityResolver.Resolve(monitor.StableMonitorId, before);

        snapshot = CaptureAppliedSnapshot();
        _ = DriverClient.GetDevicePath();

        var store = DisplayRecoveryStore.ForUser;
        var recovery = store.Begin(() =>
        {
            if (before.Any(m => m.IsDeskSplitVirtual) || CompositorLauncher.IsRunning() ||
                !string.IsNullOrEmpty(preflight?.SpecializedStableMonitorId))
                throw new ApplyException("The active split has no recovery snapshot. Revert it before applying again.");
            return DisplayRecoverySnapshot.Capture(before, physical.StableMonitorId);
        });
        _ = MonitorIdentityResolver.Resolve(recovery.PrimaryId, connectedBefore,
            requireActive: recovery.PrimaryId != preflight?.SpecializedStableMonitorId);
        return store.RunApply(recovery,
            () => ApplyChanges(request, physical, recovery, log, ct),
            () =>
            {
                LogPrimary("after failed Apply", SafeEnumerate(), log);
                RevertCore(log, CancellationToken.None, request.SpecializationUi, deliberate: false);
            },
            log.Report);
    }

    private static ApplyResult ApplyChanges(ApplyRequest request, MonitorInfo physical,
        DisplayRecoverySnapshot recovery, IProgress<string> log, CancellationToken ct)
    {
        LayoutDefinition layout = request.Layout;
        int segmentCount = layout.SegmentCount;
        string previousPrimaryId = recovery.PrimaryId;
        SavedDisplay originalPrimary = recovery.Displays.Single(d => d.StableId == previousPrimaryId);
        log.Report($"Original primary {originalPrimary.StableId} ({originalPrimary.DeviceName}), " +
                   $"first split target was primary: {recovery.SplitWasPrimary}.");

        if (CompositorLauncher.IsRunning())
        {
            log.Report("Stopping the running compositor first...");
            if (!CompositorLauncher.StopAndWait(CompositorStopTimeout, log.Report))
                throw new ApplyException("The previous compositor did not stop.");
        }

        ClearSpecializationIfOurs(log, request.SpecializationUi, ct);
        physical = MonitorIdentityStore.Resolve(physical.StableMonitorId);
        _ = MonitorIdentityStore.Resolve(previousPrimaryId);

        // a. Build and send DESKSPLIT_CONFIG.
        DESKSPLIT_CONFIG cfg = DriverClient.NewConfig();
        cfg.MonitorCount = (uint)segmentCount;
        for (int i = 0; i < segmentCount; i++)
        {
            (int w, int h) = request.Resolutions[i];
            cfg.Monitors[i].ModeCount = 1;
            cfg.Monitors[i].PreferredModeIndex = 0;
            cfg.Monitors[i].Modes[0] = new DESKSPLIT_MODE
            {
                Width = (uint)w,
                Height = (uint)h,
                RefreshMillihertz = physical.MaxRefreshMillihertz,
            };
        }

        log.Report($"Sending IOCTL_DESKSPLIT_SET_CONFIG (0x{DriverClient.IOCTL_DESKSPLIT_SET_CONFIG:X8}) " +
                   $"for {segmentCount} monitor(s) at {physical.MaxRefreshMillihertz} mHz...");
        try
        {
            physical = MonitorIdentityStore.Resolve(physical.StableMonitorId);
            DriverClient.SetConfig(cfg);
            log.Report("IOCTL_DESKSPLIT_SET_CONFIG succeeded.");
        }
        catch (DriverNotFoundException ex)
        {
            throw new ApplyException(ex.Message, ex);
        }
        catch (DriverIoException ex)
        {
            throw new ApplyException(ex.Message, ex);
        }

        // b. Wait for the virtual monitors to arrive.
        log.Report($"Waiting for {segmentCount} virtual monitor(s) to appear (up to {MonitorArrivalTimeout.TotalSeconds:0}s)...");
        IReadOnlyList<MonitorInfo> arrived = WaitForVirtualMonitors(segmentCount, ct);
        foreach (MonitorInfo m in arrived)
        {
            log.Report($"  Found {m.Caption}" +
                       (m.Edid is null ? "  [no EDID]" : $"  [{m.Edid}]"));
        }

        // Map segment i -> the monitor the driver created as DESKSPLIT_CONFIG.Monitors[i],
        // identified by the EDID serial the driver stamps (0xD5000001 + index).
        IReadOnlyList<MonitorInfo> virtualMonitors = MapSegmentsToMonitors(arrived, segmentCount, log, ct);

        // Arrange in one topology and choose the primary by stable identity.
        log.Report("Arranging virtual monitors in the virtual desktop...");
        IReadOnlyList<MonitorInfo> current = DisplayConfig.Enumerate();
        MonitorInfo physicalNow = MonitorIdentityResolver.Resolve(physical.StableMonitorId, current);
        virtualMonitors = virtualMonitors.Select(v => MonitorIdentityResolver.Resolve(v.StableMonitorId, current)).ToList();
        string appliedPrimaryId = recovery.AppliedPrimary(physical.StableMonitorId, virtualMonitors, request.PrimarySegmentIndex);
        MonitorInfo desiredPrimary = MonitorIdentityResolver.Resolve(appliedPrimaryId, current);
        var placements = DisplayArranger.ComputePlacements(physicalNow, layout, virtualMonitors, request.Resolutions,
            current.Where(m => !m.IsDeskSplitVirtual).ToList());
        DisplayArranger.ApplyLayout(current, DisplayArranger.ComposeLayout(current, placements, desiredPrimary.DeviceName),
            log.Report);

        var arranged = DisplayConfig.Enumerate();
        DisplayArranger.VerifyPrimary(appliedPrimaryId, arranged);
        virtualMonitors = virtualMonitors.Select(v => MonitorIdentityResolver.Resolve(v.StableMonitorId, arranged)).ToList();
        physical = MonitorIdentityResolver.Resolve(physical.StableMonitorId, arranged);

        // d. Write %ProgramData%\DesktopSplitter\config.json.
        // physRects mirror the editor: track sizes are normalised onto the monitor's real
        // dimensions (last track absorbs the rounding remainder) rather than being an equal
        // subdivision, so a dragged/typed split lands on screen exactly where it was drawn.
        IReadOnlyList<PixelRect> physRects = LayoutGeometry.ComputePhysRects(
            layout, physical.CurrentWidth, physical.CurrentHeight, request.Resolutions);
        log.Report("Segment rects on the physical monitor: " +
                   string.Join("  ", physRects.Select((r, i) => $"[{i + 1}] {r}")));
        var compositorConfig = new CompositorConfig
        {
            PhysicalDevice = physical.DeviceName,
            IdentitySchemaVersion = 1,
            PhysicalIdentity = physical.Identity!,
            PhysicalWidth = physical.CurrentWidth,
            PhysicalHeight = physical.CurrentHeight,
            RefreshMillihertz = physical.MaxRefreshMillihertz,
        };
        for (int i = 0; i < segmentCount; i++)
        {
            (int w, int h) = request.Resolutions[i];
            compositorConfig.Segments.Add(new CompositorSegment
            {
                VirtualDevice = virtualMonitors[i].DeviceName,
                Identity = virtualMonitors[i].Identity!,
                Width = w,
                Height = h,
                PhysRect = CompositorRect.From(physRects[i]),
            });
        }
        WriteCompositorConfig(compositorConfig);
        log.Report($"Wrote {ConfigPath}");

        // Windows can only hide a non-primary monitor. Apply the primary selection first.
        (Process compositor, string specializedDevice) = LaunchCompositor(request, log, ct);

        var after = DisplayConfig.Enumerate();
        LogPrimary("after Apply", after, log);
        MonitorInfo appliedPrimary = DisplayArranger.VerifyPrimary(appliedPrimaryId, after);

        // f. Remember the settings for next launch / tray "Apply last".
        AppSettings? previousSettings = SettingsStore.Load();
        SettingsStore.Save(new AppSettings
        {
            IdentitySchemaVersion = 1,
            PhysicalStableMonitorId = physical.StableMonitorId,
            PreviousPrimaryStableMonitorId = previousPrimaryId,
            AppliedPrimaryStableMonitorId = appliedPrimaryId,
            PrimarySegmentIndex = request.PrimarySegmentIndex,
            SpecializedStableMonitorId = specializedDevice,
            PhysicalDevice = physical.DeviceName,
            PhysicalFriendlyName = physical.FriendlyName,
            Layout = layout.Kind,
            Segments = request.Resolutions.Select(r => new SegmentSize { Width = r.Width, Height = r.Height }).ToList(),
            PreviousPrimaryDevice = originalPrimary.DeviceName,
            PreviousPrimaryFriendlyName = originalPrimary.Label,
            AppliedPrimaryDevice = appliedPrimary.DeviceName,
            HidePhysicalDisplay = request.HidePhysicalDisplay,
            SpecializedDevice = string.IsNullOrEmpty(specializedDevice) ? string.Empty : physical.DeviceName,
            LastAppliedUtc = DateTime.UtcNow,
            SplitActiveOnExit = true,
            ApplyOnStartup = previousSettings?.ApplyOnStartup ?? false,
            StartWithWindows = previousSettings?.StartWithWindows ?? false,
            MinimizeToTrayOnClose = previousSettings?.MinimizeToTrayOnClose ?? true,
            SpecializationHashes = previousSettings?.SpecializationHashes ?? new Dictionary<string, string>(),
            SpecializationHashRecipe = previousSettings?.SpecializationHashRecipe ?? string.Empty,
        });
        log.Report($"Saved {SettingsStore.FilePath}");

        return new ApplyResult
        {
            VirtualMonitors = virtualMonitors,
            ConfigPath = ConfigPath,
            CompositorPid = compositor.Id,
        };
    }

    /// <summary>
    /// Records that no split is running. <paramref name="deliberate"/> distinguishes a user
    /// pressing Revert (never auto-apply again) from an automatic teardown at exit.
    /// </summary>
    public static void MarkSplitInactive(bool deliberate)
    {
        try
        {
            AppSettings? settings = SettingsStore.Load();
            if (settings is null) return;
            if (!settings.SplitActiveOnExit && !deliberate) return;

            settings.SplitActiveOnExit = false;
            if (deliberate) settings.ApplyOnStartup = false;
            SettingsStore.Save(settings);
        }
        catch
        {
            // Never let bookkeeping break a revert.
        }
    }

    /// <summary>
    /// Decides whether startup should re-apply the saved configuration. Auto-apply happens when
    /// the user asked for it, or when a split was still running when we last exited — but never
    /// after a deliberate revert, and never when a split is somehow already up.
    /// </summary>
    public static bool ShouldAutoApplyOnStartup(AppSettings? settings, bool splitAlreadyActive)
    {
        if (settings is null || !settings.HasConfirmedIdentity) return false;
        if (splitAlreadyActive) return false;
        if (string.IsNullOrEmpty(settings.PhysicalDevice) || settings.Segments.Count == 0) return false;
        return settings.ApplyOnStartup || settings.SplitActiveOnExit;
    }

    // ------------------------------------------------------------------ confirm / roll back

    /// <summary>Seconds the "Keep these display settings?" countdown runs before auto-reverting.</summary>
    public const int ConfirmChangesSeconds = 15;

    /// <summary>
    /// What is live right now, so a declined apply can be put back. "Applied" means a split is
    /// genuinely running, not merely that settings.json remembers one — a user who reverted
    /// deliberately must not have it resurrected.
    /// </summary>
    public static AppliedSnapshot CaptureAppliedSnapshot()
    {
        AppSettings? settings = SettingsStore.Load();
        if (settings is null || string.IsNullOrEmpty(settings.PhysicalDevice) || settings.Segments.Count == 0)
            return AppliedSnapshot.None;
        if (!HidingStatus.IsSplitActive())
            return AppliedSnapshot.None;

        return new AppliedSnapshot(
            true,
            settings.PhysicalStableMonitorId,
            settings.Layout,
            settings.Segments.Select(s => (s.Width, s.Height)).ToList(),
            settings.HidePhysicalDisplay,
            settings.PrimarySegmentIndex);
    }

    /// <summary>
    /// Restores the configuration captured before the declined apply — re-applying the previous
    /// split, or reverting completely when there was none. Never prompts (that would trap the
    /// user in a loop of countdowns).
    /// </summary>
    private static string RollBackTo(
        AppliedSnapshot snapshot, IProgress<string> log, CancellationToken ct, ISpecializationUi? ui)
    {
        if (!snapshot.WasApplied)
        {
            log.Report("Nothing was applied before this change — reverting completely.");
            Revert(log, ct, ui);
            return "no split (fully reverted)";
        }

        MonitorInfo? physical = DisplayConfig.EnumerateConnected()
            .SingleOrDefault(m => m.StableMonitorId == snapshot.PhysicalDevice);
        LayoutDefinition layout = LayoutDefinition.FromKind(snapshot.Layout);

        if (physical is null || snapshot.Resolutions.Count != layout.SegmentCount)
        {
            log.Report("The previous configuration is no longer valid — reverting completely.");
            Revert(log, ct, ui);
            return "no split (fully reverted)";
        }

        log.Report($"Restoring the previous configuration: {layout.Name} on {physical.DisplayLabel}.");
        Apply(new ApplyRequest
        {
            Physical = physical,
            Layout = layout,
            Resolutions = snapshot.Resolutions,
            HidePhysicalDisplay = snapshot.HidePhysicalDisplay,
            PrimarySegmentIndex = snapshot.PrimarySegmentIndex,
            SpecializationUi = ui,
            PromptToConfirm = false,
        }, log, ct);

        return $"{layout.Name} on {physical.DisplayLabel}";
    }

    // ------------------------------------------------------------------ revert

    public static void Revert(IProgress<string> log, CancellationToken ct, ISpecializationUi? ui = null,
        bool deliberate = true)
    {
        lock (DisplayOperationGate)
        {
            RevertCore(log, ct, ui, deliberate);
        }
    }

    private static void RevertCore(IProgress<string> log, CancellationToken ct, ISpecializationUi? ui,
        bool deliberate = true)
    {
        ct.ThrowIfCancellationRequested();
        var recovery = DisplayRecoveryStore.ForUser.Load();
        AppSettings? state = SettingsStore.Load();
        var before = DisplayConfig.Enumerate();
        LogPrimary("before Revert", before, log);
        try
        {
            bool hasVirtual = before.Any(m => m.IsDeskSplitVirtual);
            if (recovery is null && !hasVirtual && !CompositorLauncher.IsRunning() &&
                string.IsNullOrEmpty(state?.SpecializedDevice) && string.IsNullOrEmpty(state?.SpecializedStableMonitorId))
            {
                MarkSplitInactive(deliberate);
                log.Report("No split or pending display recovery.");
                return;
            }

            string primaryId = recovery?.PrimaryId ?? state?.PreviousPrimaryStableMonitorId ?? string.Empty;
            if (recovery is not null)
                DisplayRecoveryStore.ForUser.SetPhase(recovery, DisplayRecoveryPhase.Reverting);
            if (deliberate) MarkSplitInactive(deliberate: true);

            // Restore a hidden output before removing the virtual displays. Leave a working
            // compositor running if there is currently no physical output to return to.
            string? pendingHide = state?.SpecializedStableMonitorId;
            bool hiddenMissing = !string.IsNullOrEmpty(pendingHide) &&
                !DisplayConfig.EnumerateConnected().Any(m => m.StableMonitorId == pendingHide);
            if (!before.Any(m => !m.IsDeskSplitVirtual) && (string.IsNullOrEmpty(pendingHide) || hiddenMissing))
                throw new ApplyException("No physical display is available. Reconnect a physical monitor and retry Revert.");

            if (!CompositorLauncher.StopAndWait(CompositorStopTimeout, log.Report))
                throw new ApplyException("The compositor did not stop. Retry Revert.");

            if (!hiddenMissing) ClearSpecializationIfOurs(log, ui, ct);

            var ready = DisplayConfig.Enumerate();
            if (!ready.Any(m => !m.IsDeskSplitVirtual))
                throw new ApplyException("No active physical display. Reconnect a physical monitor before removing virtual displays.");

            if (ready.Any(m => m.IsDeskSplitVirtual))
            {
                // Give the shell a physical primary before its virtual primary disappears, when
                // that physical monitor is already active. The authoritative restore runs below.
                if (ready.Any(m => !m.IsDeskSplitVirtual && m.StableMonitorId == primaryId))
                    SwitchPrimary(primaryId, "the original primary before virtual removal", log);
            }

            try
            {
                // ClearConfig is also needed after a failed Apply where no virtual path has arrived yet.
                if (hasVirtual || ready.Any(m => m.IsDeskSplitVirtual) || recovery is not null)
                {
                    DriverClient.ClearConfig();
                    log.Report("IOCTL_DESKSPLIT_SET_CONFIG (zero monitors) succeeded.");
                }
            }
            catch (DriverNotFoundException) when (!hasVirtual && !ready.Any(m => m.IsDeskSplitVirtual))
            {
                log.Report("Virtual display driver is absent and no virtual displays are active.");
            }

            var live = WaitForPhysicalTopology(ct);
            if (string.IsNullOrEmpty(primaryId) || !live.Any(m => m.StableMonitorId == primaryId))
                throw new ApplyException($"Original primary monitor '{primaryId}' is unavailable or unconfirmed. " +
                                         "Windows still has an active physical desktop. Reconnect or confirm it and retry Revert.");

            MonitorInfo primary = MonitorIdentityResolver.Resolve(primaryId, live);
            var saved = recovery?.RestoreLayout(live);
            if (saved is not null)
            {
                if (!DisplayArranger.SnapshotLayout(live).SequenceEqual(saved))
                {
                    try
                    {
                        DisplayArranger.ApplyLayout(live, saved, log.Report);
                    }
                    catch (DisplayArrangeException ex)
                    {
                        log.Report("Saved topology was rejected: " + ex.Message);
                        SwitchPrimary(primaryId, "the original primary", log);
                        throw new ApplyException("The saved topology was rejected. Recovery remains pending.", ex);
                    }
                }
            }
            else if (!primary.IsPrimary || primary.PositionX != 0 || primary.PositionY != 0)
            {
                DisplayArranger.ApplyLayout(live,
                    DisplayArranger.RebaseToPrimary(DisplayArranger.SnapshotLayout(live), primary.DeviceName),
                    log.Report);
            }

            var verified = WaitForPhysicalTopology(ct);
            DisplayArranger.VerifyPrimary(primaryId, verified);
            if (recovery is not null)
            {
                // Resolve aliases again for verification if Windows renumbered the sources.
                var expected = recovery.RestoreLayout(verified);
                if (expected is null || !DisplayArranger.SnapshotLayout(verified).SequenceEqual(expected))
                    throw new ApplyException("The original primary was restored, but the saved physical topology " +
                                             "could not be verified. Check that all original monitors are available and retry Revert.");
            }
            if (hiddenMissing) throw new ApplyException($"Previously hidden monitor {pendingHide} is unavailable.");

            DisplayRecoveryStore.ForUser.Complete();
            MarkSplitInactive(deliberate);
            log.Report(recovery is null
                ? "Virtual displays removed and original primary verified. No saved topology was available."
                : "Reverted. Original primary and physical topology verified.");
        }
        catch (Exception ex)
        {
            log.Report("Display topology recovery incomplete: " + ex.Message);
            throw;
        }
        finally
        {
            LogPrimary("after Revert", SafeEnumerate(), log);
        }
    }

    private static IReadOnlyList<MonitorInfo> WaitForPhysicalTopology(CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        SavedDisplay[]? previous = null;
        while (timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            ct.ThrowIfCancellationRequested();
            var current = SafeEnumerate();
            if (current.Count > 0 && current.All(m => !m.IsDeskSplitVirtual))
            {
                var sample = current.Select(SavedDisplay.Capture).ToArray();
                if (previous is not null && sample.SequenceEqual(previous)) return current;
                previous = sample;
            }
            else
            {
                previous = null;
            }
            ct.WaitHandle.WaitOne(PollInterval);
        }
        throw new ApplyException("Windows display topology did not stabilize after removing the virtual monitors.");
    }

    // ------------------------------------------------------------------ display hiding

    /// <summary>How long to wait for the compositor's first specialized scan-out.</summary>
    private static readonly TimeSpan SpecializedLiveTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Seconds the user gets to keep the hidden state before it auto-reverts.</summary>
    public const int SpecializationConfirmSeconds = 10;

    private static MonitorInfo FindLive(string stableId)
        => MonitorIdentityStore.Resolve(stableId, requireActive: false);

    /// <summary>
    /// Starts the compositor, first specializing the physical monitor when that was asked for.
    ///
    /// Sequence: SET specialization ON -> launch dscomp --specialized -> wait for
    /// Global\DeskSplitSpecializedLive -> keep/revert countdown. Anything short of a confirmed
    /// keep rolls all the way back to a plain windowed compositor, so a failure can never leave
    /// the monitor off the desktop with nothing drawing to it.
    /// </summary>
    private static (Process Compositor, string SpecializedDevice) LaunchCompositor(
        ApplyRequest request, IProgress<string> log, CancellationToken ct)
    {
        if (!request.HidePhysicalDisplay) return (StartPlain(log), string.Empty);

        MonitorInfo physical = FindLive(request.Physical.StableMonitorId);

        // Windows only allows a NON-primary display to be specialized.
        if (physical.IsPrimary)
        {
            log.Report($"WARNING: {physical.DisplayLabel} is still the primary display, and Windows only " +
                       "allows non-primary displays to be hidden. Skipping hiding.");
            return (StartPlain(log), string.Empty);
        }

        if (!MonitorSpecialization.TryGet(physical, out SpecializationState state, out int rc))
        {
            log.Report($"WARNING: could not read monitor specialization (error {rc}). Skipping hiding.");
            return (StartPlain(log), string.Empty);
        }

        if (!state.Available)
        {
            log.Report($"WARNING: {physical.DisplayLabel} cannot be hidden (GET raw 0x{state.RawValue:X}: " +
                       $"monitor={state.AvailableForMonitor}, system={state.AvailableForSystem}). Skipping hiding.");
            return (StartPlain(log), string.Empty);
        }

        bool alreadyHidden = state.EnabledNow;
        if (alreadyHidden)
            log.Report($"{physical.DisplayLabel} is already off the desktop — going straight to acquiring it.");

        // START THE COMPOSITOR FIRST. It polls until the display becomes specialized and then
        // acquires it. If the display were removed from the desktop before anything was ready to
        // scan out to it, the panel would simply go black — which is exactly what happened when
        // this was done in the other order.
        Process compositor = CompositorLauncher.Start(
            CompositorLauncher.SpecializedArgument,
            CompositorLauncher.WaitForTargetArgument, CompositorLauncher.WaitForTargetSeconds.ToString(),
            CompositorLauncher.FrameDeadlineArgument, CompositorLauncher.FrameDeadlineSeconds.ToString());
        log.Report($"Started {compositor.ProcessName}.exe {CompositorLauncher.SpecializedArgument} " +
                   $"{CompositorLauncher.WaitForTargetArgument} {CompositorLauncher.WaitForTargetSeconds} " +
                   $"(pid {compositor.Id}) — it is now waiting to take over the display.");

        // Persist recovery ownership BEFORE removing the target, including failures/crashes during confirmation.
        var ownership = SettingsStore.Load() ?? new AppSettings();
        ownership.SpecializedStableMonitorId = physical.StableMonitorId;
        ownership.SpecializedDevice = physical.DeviceName;
        SettingsStore.Save(ownership);

        if (!alreadyHidden && !SetSpecialized(physical, true, request.SpecializationUi, log, ct))
        {
            log.Report("Display hiding was cancelled — restarting the compositor in windowed mode.");
            CompositorLauncher.StopAndWait(CompositorStopTimeout, log.Report);
            RestoreToDesktop(physical, request.SpecializationUi, log, ct);
            return (StartPlain(log), string.Empty);
        }

        log.Report($"Waiting up to {SpecializedLiveTimeout.TotalSeconds:0}s for " +
                   $"{CompositorLauncher.SpecializedLiveEventName}...");
        CompositorLauncher.SpecializedWait wait =
            CompositorLauncher.WaitForSpecializedLive(compositor, SpecializedLiveTimeout, ct, log.Report);

        bool keep = ShouldKeepHiding(
            wait,
            askUser: () => request.SpecializationUi is null
                           || request.SpecializationUi.ConfirmKeep(physical, SpecializationConfirmSeconds, ct),
            log);

        if (keep) return (compositor, physical.StableMonitorId);

        // Roll back: stop the compositor, put the display back, restart windowed.
        log.Report("Rolling back display hiding...");
        CompositorLauncher.StopAndWait(CompositorStopTimeout, log.Report);
        RestoreToDesktop(physical, request.SpecializationUi, log, ct);

        return (StartPlain(log), string.Empty);
    }

    /// <summary>
    /// The keep-or-roll-back decision, isolated so it can be tested without touching a display.
    ///
    /// The user is only ever asked when the compositor actually confirmed it is presenting
    /// through the hidden display. Every other outcome — no signal, or the compositor dying —
    /// rolls back WITHOUT prompting, because in those cases the user may be looking at a blank
    /// screen and cannot answer.
    /// </summary>
    public static bool ShouldKeepHiding(
        CompositorLauncher.SpecializedWait wait, Func<bool> askUser, IProgress<string> log)
    {
        switch (wait)
        {
            case CompositorLauncher.SpecializedWait.Live:
                log.Report("Compositor confirmed specialized presentation — asking you to confirm.");
                bool keep = askUser();
                log.Report(keep ? "Keeping the hidden display." : "Restoring the display to the desktop.");
                return keep;

            case CompositorLauncher.SpecializedWait.CompositorExited:
                log.Report("The compositor stopped before it could present through the hidden display — " +
                           "rolling back without prompting.");
                return false;

            default:
                log.Report($"No {CompositorLauncher.SpecializedLiveEventName} signal within " +
                           $"{SpecializedLiveTimeout.TotalSeconds:0}s — treating hiding as failed and " +
                           "rolling back without prompting.");
                return false;
        }
    }

    /// <summary>
    /// Adds or removes a monitor from the desktop, trying the routes in order of preference:
    ///
    ///   1. SystemSettingsAdminFlows.exe SpecializeDisplay — what Windows itself runs. Needs a
    ///      validated identity hash, so it is off until that has been proven on this machine.
    ///   2. The guided Windows Settings dialog, which the user completes by hand.
    ///
    /// The in-process DisplayConfig type-13 SET is deliberately NOT attempted: disassembly of every
    /// System32 binary that calls DisplayConfigSetDeviceInfo shows none of them ever builds the
    /// public 0x138-byte packet, so it is a metadata-only definition that cannot succeed.
    /// Returns true once the display actually reached the requested state.
    /// </summary>
    private static bool SetSpecialized(
        MonitorInfo physical, bool enable, ISpecializationUi? ui, IProgress<string> log, CancellationToken ct)
    {
        physical = MonitorIdentityStore.Resolve(physical.StableMonitorId, requireActive: enable);
        string what = enable ? "Removing" : "Restoring";
        log.Report($"{what} {physical.DisplayLabel} {(enable ? "from" : "to")} the desktop...");

        AdminFlowResult admin = SystemSettingsAdminFlowsClient.TryRun(physical, enable, log, ct);
        if (admin.Success)
        {
            log.Report(admin.Message);
            return true;
        }

        if (admin.NotAttempted)
            log.Report($"Automatic route unavailable: {admin.Message}");
        else
            log.Report("Automatic route failed: " + admin.Message);

        log.Report("Using the Windows Settings route.");

        if (ui is null)
        {
            log.Report($"Do it yourself: Settings > System > Display > Advanced display > " +
                       $"select {physical.DisplayLabel} > turn {(enable ? "ON" : "OFF")} " +
                       "\"Remove display from desktop\".");
            return false;
        }

        // While the user works the toggle, watch Windows' own elevated helper so we can learn
        // this monitor's identity hash and do it in one click next time.
        using var watcher = new SpecializationCaptureWatcher(physical, log);
        watcher.Start();

        var failure = new SpecializationResult(false, 0, admin.Message);
        bool reached;
        if (enable)
        {
            reached = ui.GuideManualEnable(physical, failure, ct);
        }
        else
        {
            ui.GuideManualDisable(physical, failure, ct);
            reached = MonitorSpecialization.TryGet(physical, out SpecializationState after, out _)
                      && !after.EnabledNow;
        }

        SpecializeDisplayInvocation? captured = watcher.StopAndCollect();
        if (captured is not null)
        {
            SpecializationHashStore.Learn(physical, captured.Hash, log);
        }
        else if (watcher.ElevationRequired)
        {
            log.Report("Note: Windows ran its display helper, but DesktopSplitter could not read how " +
                       "it was called because that helper runs as administrator. Run DesktopSplitter " +
                       "as administrator once while you do this, and it will remember the setting and " +
                       "be able to do it for you in one click from then on.");
        }

        return reached;
    }

    /// <summary>Puts a monitor back on the desktop. Never leaves the user without a way out.</summary>
    private static void RestoreToDesktop(
        MonitorInfo physical, ISpecializationUi? ui, IProgress<string> log, CancellationToken ct)
    {
        var current = MonitorIdentityStore.Resolve(physical.StableMonitorId, requireActive: false);
        if ((!current.IsActive || !MonitorSpecialization.TryGet(current, out var before, out _) || before.EnabledNow) &&
            !SetSpecialized(current, false, ui, log, ct))
            throw new ApplyException("Restoration failed; hidden monitor recovery identity retained.");
        var timer = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            current = SafeEnumerate().SingleOrDefault(m => m.StableMonitorId == physical.StableMonitorId);
            if (current is not null && MonitorSpecialization.TryGet(current, out var after, out _) && !after.EnabledNow)
                break;
            if (timer.Elapsed >= TimeSpan.FromSeconds(10))
                throw new ApplyException("Restoration not confirmed; hidden monitor recovery identity retained.");
            ct.WaitHandle.WaitOne(PollInterval);
        }
        log.Report($"{physical.DisplayLabel} is back on the desktop.");
        var settings = SettingsStore.Load();
        if (settings is not null && settings.SpecializedStableMonitorId == physical.StableMonitorId)
        {
            settings.SpecializedStableMonitorId = string.Empty;
            settings.SpecializedDevice = string.Empty;
            SettingsStore.Save(settings);
        }
    }

    private static Process StartPlain(IProgress<string> log)
    {
        Process compositor = CompositorLauncher.Start();
        log.Report($"Started {compositor.ProcessName}.exe (pid {compositor.Id}).");
        return compositor;
    }

    /// <summary>
    /// Puts a monitor we specialized back on the desktop. Used by Revert, by failure cleanup and
    /// by startup recovery after a crash.
    /// </summary>
    public static void ClearSpecializationIfOurs(
        IProgress<string> log, ISpecializationUi? ui = null, CancellationToken ct = default)
    {
        AppSettings? settings = SettingsStore.Load();
        string device = settings?.SpecializedStableMonitorId ?? string.Empty;
        if (string.IsNullOrEmpty(device))
        {
            if (!string.IsNullOrEmpty(settings?.SpecializedDevice))
                throw new ApplyException("Unverified legacy hidden monitor: restore through Windows Settings; recovery evidence was retained.");
            return;
        }

        RestoreToDesktop(FindLive(device), ui, log, ct);
    }

    /// <summary>
    /// Called at startup: if we recorded a hidden display but no split is running, a previous
    /// session died without cleaning up. Put the display back.
    /// </summary>
    public static void RecoverInterruptedDisplayOperation(IProgress<string> log)
    {
        lock (DisplayOperationGate)
        {
            bool running = CompositorLauncher.IsRunning();
            var recovery = DisplayRecoveryStore.ForUser.Load();
            if (recovery is null)
            {
                if (!running) ClearSpecializationIfOurs(log);
                return;
            }
            if (!recovery.NeedsStartupRecovery(running, DisplayConfig.Enumerate().Any(m => m.IsDeskSplitVirtual)))
                return;

            log.Report("Recovering the saved pre-split desktop.");
            RevertCore(log, CancellationToken.None, null, deliberate: false);
        }
    }

    // ------------------------------------------------------------------ primary display

    private static void LogPrimary(string stage, IReadOnlyList<MonitorInfo> monitors, IProgress<string> log)
    {
        var primary = monitors.FirstOrDefault(m => m.IsPrimary);
        log.Report(primary is null ? $"Primary {stage}: unavailable."
            : $"Primary {stage}: {primary.DisplayLabel}, {primary.StableMonitorId}, {primary.DeviceName}, " +
              $"target {primary.AdapterIdHigh:X8}:{primary.AdapterIdLow:X8}/{primary.TargetId}, " +
              $"position ({primary.PositionX},{primary.PositionY}).");
    }

    /// <summary>
    /// Serialises every display-mutating operation: Apply, Revert and the automatic
    /// suspend/resume primary switches. Without this an automatic suspend could land in the
    /// middle of an Apply and leave the desktop half-rearranged.
    ///
    /// Deliberately NOT held across the keep-or-revert countdown — a UAC prompt must not have to
    /// wait 15 seconds for a confirmation the user may not even be able to see.
    /// </summary>
    public static readonly object DisplayOperationGate = new();

    /// <summary>
    /// Makes <paramref name="targetDevice"/> primary. Public so the suspend/resume watcher can
    /// hand primary back and forth without duplicating the re-basing logic.
    /// </summary>
    public static bool SwitchPrimaryTo(string targetDevice, string description, IProgress<string> log)
        => SwitchPrimary(targetDevice, description, log);

    private static bool SwitchPrimary(string stableId, string description, IProgress<string> log)
    {
        try
        {
            var current = DisplayConfig.Enumerate();
            var target = MonitorIdentityResolver.Resolve(stableId, current);
            if (target.IsPrimary && target.PositionX == 0 && target.PositionY == 0)
            {
                log.Report($"{description} is already the primary display.");
                return true;
            }

            var rebased = DisplayArranger.RebaseToPrimary(DisplayArranger.SnapshotLayout(current), target.DeviceName);
            log.Report($"Making {description} primary by moving all {rebased.Count} screens " +
                       $"by ({-target.PositionX},{-target.PositionY}).");
            DisplayArranger.ApplyLayout(current, rebased, log.Report);
            DisplayArranger.VerifyPrimary(stableId, DisplayConfig.Enumerate());
            return true;
        }
        catch (Exception ex) when (ex is DisplayArrangeException or DisplayConfigException or InvalidOperationException)
        {
            log.Report($"Primary switch to {stableId} failed: {ex.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------------ helpers

    public static void ValidateRequest(ApplyRequest request)
    {
        if (request.PrimarySegmentIndex < 0 || request.PrimarySegmentIndex >= request.Layout.SegmentCount)
            throw new ApplyException("The primary segment is outside the selected layout.");
        if (request.Resolutions.Count != request.Layout.SegmentCount)
        {
            throw new ApplyException(
                $"Layout '{request.Layout.Name}' needs {request.Layout.SegmentCount} segment resolutions, " +
                $"got {request.Resolutions.Count}.");
        }
        if (request.Layout.SegmentCount > DriverClient.DESKSPLIT_MAX_MONITORS)
        {
            throw new ApplyException(
                $"Layout '{request.Layout.Name}' needs {request.Layout.SegmentCount} monitors but the driver " +
                $"supports at most {DriverClient.DESKSPLIT_MAX_MONITORS}.");
        }
        for (int i = 0; i < request.Resolutions.Count; i++)
        {
            (int w, int h) = request.Resolutions[i];
            if (w < MinSegmentDimension || w > MaxSegmentDimension ||
                h < MinSegmentDimension || h > MaxSegmentDimension)
            {
                throw new ApplyException(
                    $"Segment {i + 1} resolution {w}x{h} is out of range " +
                    $"({MinSegmentDimension}..{MaxSegmentDimension}).");
            }
        }
        if (request.Physical.IsDeskSplitVirtual)
            throw new ApplyException("The selected monitor is a DesktopSplitter virtual monitor; pick a physical one.");
    }

    /// <summary>
    /// Orders the arrived virtual monitors so that index i is the monitor the driver created
    /// from DESKSPLIT_CONFIG.Monitors[i], using the EDID serial (0xD5000001 + index) that the
    /// driver stamps. Falls back to positional \\.\DISPLAYn order with a logged warning when
    /// the EDIDs are unreadable, incomplete or ambiguous.
    /// </summary>
    private static IReadOnlyList<MonitorInfo> MapSegmentsToMonitors(
        IReadOnlyList<MonitorInfo> arrived, int segmentCount,
        IProgress<string> log, CancellationToken ct)
    {
        // Windows caches a monitor's EDID under its devnode shortly after arrival; on the
        // first read it may not be there yet. Retry with fresh enumerations (each one
        // re-reads the EDIDs) before giving up on identity mapping.
        DateTime deadline = DateTime.UtcNow + EdidMappingRetryTimeout;
        string reason;

        while (true)
        {
            if (TryMapByEdid(arrived, segmentCount, out List<MonitorInfo> mapped, out reason))
            {
                log.Report("Mapped segments to virtual monitors by EDID serial (driver monitor index).");
                for (int i = 0; i < mapped.Count; i++)
                {
                    EdidInfo edid = mapped[i].Edid!;
                    log.Report($"  Segment {i + 1} -> {mapped[i].DeviceName} " +
                               $"(serial 0x{edid.SerialNumber:X8}, product 0x{edid.ProductCode:X4})");
                    if (edid.ProductCode != i)
                    {
                        log.Report($"  Note: EDID product code 0x{edid.ProductCode:X4} does not match monitor " +
                                   $"index {i}; trusting the serial.");
                    }
                }
                return mapped;
            }

            if (DateTime.UtcNow >= deadline || ct.IsCancellationRequested) break;
            ct.WaitHandle.WaitOne(PollInterval);

            var refreshed = SafeEnumerate()
                .Where(m => m.IsDeskSplitVirtual && !string.IsNullOrEmpty(m.StableMonitorId))
                .OrderBy(m => DisplayConfig.DeviceIndex(m.DeviceName))
                .ThenBy(m => m.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (refreshed.Count == arrived.Count) arrived = refreshed;
        }

        log.Report($"WARNING: {reason} — falling back to positional \\\\.\\DISPLAYn-order mapping. " +
                   "Segments may end up on the wrong virtual monitor.");
        throw new ApplyException("Virtual segment identity is ambiguous: " + reason);
    }

    private static bool TryMapByEdid(
        IReadOnlyList<MonitorInfo> arrived, int segmentCount,
        out List<MonitorInfo> mapped, out string reason)
    {
        var byIndex = new Dictionary<int, MonitorInfo>();
        bool duplicates = false;

        foreach (MonitorInfo m in arrived)
        {
            int index = m.DeskSplitMonitorIndex;
            if (index < 0 || index >= segmentCount) continue;
            if (string.IsNullOrEmpty(m.StableMonitorId) || !byIndex.TryAdd(index, m)) duplicates = true;
        }

        bool complete = arrived.Select(m => m.Identity?.VirtualAdapterId).Distinct().Count() == 1 && !duplicates
                        && byIndex.Count == segmentCount
                        && Enumerable.Range(0, segmentCount).All(byIndex.ContainsKey);

        if (complete)
        {
            mapped = Enumerable.Range(0, segmentCount).Select(i => byIndex[i]).ToList();
            reason = string.Empty;
            return true;
        }

        mapped = new List<MonitorInfo>();
        reason = duplicates
            ? "two virtual monitors reported the same driver index"
            : byIndex.Count == 0
                ? "no EDID serial in the DesktopSplitter range could be read"
                : $"only {byIndex.Count} of {segmentCount} monitors reported a usable driver index";
        return false;
    }

    private static IReadOnlyList<MonitorInfo> WaitForVirtualMonitors(
        int expected, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow + MonitorArrivalTimeout;
        List<MonitorInfo> found = new();

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            found = SafeEnumerate()
                .Where(m => m.IsDeskSplitVirtual && !string.IsNullOrEmpty(m.StableMonitorId))
                .OrderBy(m => DisplayConfig.DeviceIndex(m.DeviceName))
                .ThenBy(m => m.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (found.Count == expected) return found;

            ct.WaitHandle.WaitOne(PollInterval);
        }

        throw new ApplyException(
            $"Timed out after {MonitorArrivalTimeout.TotalSeconds:0}s waiting for the virtual monitors: " +
            $"expected {expected}, saw {found.Count}. Is the DesktopSplitter driver installed and started " +
            "(Device Manager -> Display adapters)?");
    }

    private static IReadOnlyList<MonitorInfo> SafeEnumerate()
    {
        try { return DisplayConfig.Enumerate(); }
        catch (DisplayConfigException) { return Array.Empty<MonitorInfo>(); }
    }

    /// <summary>
    /// Writes config.json to %ProgramData%\DesktopSplitter. There is deliberately no
    /// fallback location: the compositor's contract is a single fixed path.
    /// </summary>
    public static void WriteCompositorConfig(CompositorConfig config)
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new ApplyException(
                $"Access denied writing {ConfigPath}.{Environment.NewLine}" +
                "Run DesktopSplitter as administrator once — it will create " +
                $"{ConfigDirectory} and grant the current user write access; " +
                "subsequent runs do not need elevation.", ex);
        }
        catch (IOException ex)
        {
            throw new ApplyException($"Could not write {ConfigPath}: {ex.Message}", ex);
        }
    }
}

