using System.Threading;
using DesktopSplitter.Interop;
using DesktopSplitter.Models;

namespace DesktopSplitter.Services;

public enum SuspensionRequest { Unknown, Active, Suspended }

/// <summary>Reconciles durable primary intent with topology/power/secure-desktop changes.</summary>
public sealed class SplitSuspensionWatcher : IDisposable
{
    public const string SuspendEventName = "DeskSplitSuspendRequest";
    public const string ResumeEventName = "DeskSplitResumeRequest";
    private readonly IProgress<string> _log;
    private readonly object _stateGate = new();
    private readonly PrimaryPreferenceTracker _tracker = new();
    private readonly PrimaryPreferenceWriteback _writeback = new();
    private Run? _run;
    private bool _restartRequested;
    private int _displayUnavailable;
    private EventWaitHandle? _suspend, _resume;
    private int _displayEvent, _lifecycleEvent;
    private bool _suspended;
    private string _lastReason = "";
    private DateTime _lastRecovery;
    private sealed class Run { public readonly ManualResetEventSlim Stop = new(false); public readonly AutoResetEvent Wake = new(false); public Thread? Thread; }
    public SplitSuspensionWatcher(IProgress<string> log) => _log = log;
    public bool IsSuspended => _suspended;
    public bool IsRunning { get { lock (_stateGate) return _run is not null; } }
    public void NotifyDisplayChanged(bool userIntent) { if (userIntent) Interlocked.Exchange(ref _displayEvent, 1); Wake(); }
    public void NotifyLifecycleChanged(bool? unavailable = null) {
        if (unavailable.HasValue) Interlocked.Exchange(ref _displayUnavailable, unavailable.Value ? 1 : 0);
        Interlocked.Exchange(ref _lifecycleEvent, 1);
        Wake();
    }

    private void Wake() { lock (_stateGate) _run?.Wake.Set(); }

    public void Start()
    {
        lock (_stateGate)
        {
            if (_run is not null) { if (_run.Stop.IsSet) _restartRequested = true; return; }
            _tracker.Invalidate(DateTime.UtcNow);
            Interlocked.Exchange(ref _lifecycleEvent, 1);
            var run = new Run();
            run.Thread = new Thread(() => Loop(run)) { IsBackground = true, Name = "DesktopSplitter primary preferences" };
            _run = run; run.Thread.Start();
        }
    }
    public void Stop(bool restorePrimary)
    {
        Run? run;
        lock (_stateGate) { _restartRequested = false; run = _run; run?.Stop.Set(); }
        run?.Thread?.Join(TimeSpan.FromSeconds(3));
        // Intent remains on disk, including an offline pre-split monitor after Revert.
        // The caller's explicit Apply/Revert owns any immediate restoration.
    }
    public SuspensionRequest ReadDesiredState()
    {
        _suspend ??= TryOpen(SuspendEventName); _resume ??= TryOpen(ResumeEventName);
        bool suspend = _suspend?.WaitOne(0) ?? false, resume = _resume?.WaitOne(0) ?? false;
        return suspend && !resume ? SuspensionRequest.Suspended : resume && !suspend ? SuspensionRequest.Active : SuspensionRequest.Unknown;
    }
    private void Loop(Run run)
    {
        try {
            while (!run.Stop.IsSet) {
                try { Reconcile(); } catch (Exception ex) { Report("Primary reconciliation deferred: " + ex.Message); }
                WaitHandle.WaitAny(new WaitHandle[] { run.Stop.WaitHandle, run.Wake }, 1000);
            }
        } finally {
            bool restart;
            lock (_stateGate) {
                _suspend?.Dispose(); _resume?.Dispose(); _suspend = _resume = null;
                if (_run == run) _run = null;
                run.Stop.Dispose(); run.Wake.Dispose();
                restart = _restartRequested; _restartRequested = false;
            }
            if (restart) Start();
        }
    }
    public void Reconcile()
    {
        // Never block a stopping thread behind Apply, and never mutate outside this gate.
        if (!Monitor.TryEnter(ApplyService.DisplayOperationGate)) return;
        try {
            var state = _writeback.Flush(PrimaryPreferenceStore.Default.Read(), PrimaryPreferenceStore.Default.Write);
            if (state is null) {
                var legacy = SettingsStore.Load();
                if (legacy?.HasConfirmedIdentity != true || string.IsNullOrEmpty(legacy.PreviousPrimaryStableMonitorId) || !HidingStatus.IsSplitActive()) return;
                var primary = DisplayConfig.Enumerate().SingleOrDefault(m => m.IsPrimary);
                if (primary is null || primary.StableMonitorId.Length == 0) return;
                state = new PrimaryMonitorState { PreSplitId = legacy.PreviousPrimaryStableMonitorId,
                    SplitPhysicalId = legacy.PhysicalStableMonitorId, DesiredId = primary.StableMonitorId,
                    DesiredIsVirtual = primary.IsDeskSplitVirtual, SplitActive = true };
                PrimaryPreferenceStore.Default.Write(state);
                _log.Report("Migrated confirmed primary identities; current split primary retained. Historical manual intent cannot be inferred.");
            }
            DateTime now = DateTime.UtcNow;
            if (!state.SplitActive && now - _lastRecovery > TimeSpan.FromSeconds(30)) {
                _lastRecovery = now;
                var settings = SettingsStore.Load();
                if (!string.IsNullOrEmpty(settings?.SpecializedStableMonitorId) &&
                    DisplayConfig.EnumerateConnected().Any(m => m.StableMonitorId == settings.SpecializedStableMonitorId)) {
                    try { ApplyService.RecoverStaleSpecialization(_log); }
                    catch (Exception ex) { Report("Hidden-display recovery still pending: " + ex.Message); }
                }
            }
            var live = DisplayConfig.Enumerate();
            SuspensionRequest secure = ReadDesiredState();
            if (!state.SplitActive) _suspended = false;
            else if (secure != SuspensionRequest.Unknown) _suspended = secure == SuspensionRequest.Suspended;
            var observations = live.Select(m => new PrimaryObservation(m.StableMonitorId, m.IsPrimary,
                m.IsDeskSplitVirtual, m.CurrentWidth, m.CurrentHeight)).ToList();
            bool manualIntent = Interlocked.Exchange(ref _displayEvent, 0) != 0;
            // WM_DISPLAYCHANGE is not guaranteed for a primary-only change with unchanged
            // resolution. Polling still needs a recent foreground Settings interaction.
            string desired = state.RevertPending ? state.PreSplitId : state.DesiredId;
            if (!manualIntent && !_suspended && !state.RevertPending && observations.Any(m => m.IsPrimary && m.Id != desired))
                manualIntent = PrimaryChangeIntent.ObserveSettingsInteraction();
            var decision = _tracker.Observe(state, observations, now,
                manualIntent, Interlocked.Exchange(ref _lifecycleEvent, 0) != 0 || Volatile.Read(ref _displayUnavailable) != 0, _suspended);
            if (decision.State != state) {
                _writeback.Commit(state, decision.State, PrimaryPreferenceStore.Default.Write);
                _log.Report($"Primary preference: pre-split={decision.State.PreSplitId}, desired={decision.State.DesiredId}, manual={decision.State.ManualOverride}, revertPending={decision.State.RevertPending}.");
            }
            Report(decision.Reason);
            if (decision.TargetId.Length == 0) return;
            if (live.Any(m => m.StableMonitorId == decision.TargetId && m.IsDeskSplitVirtual) && !CompositorLauncher.IsRunning()) {
                Report("Virtual primary waits for a running compositor."); return;
            }
            _tracker.OwnSwitch(now); // Suppress our delayed WM_DISPLAYCHANGE notifications, including failed attempts.
            ApplyService.SwitchPrimaryTo(decision.TargetId, "the remembered primary " + decision.TargetId, _log);
        } finally { Monitor.Exit(ApplyService.DisplayOperationGate); }
    }
    private void Report(string reason) { if (_lastReason == reason) return; _lastReason = reason; _log.Report(reason); }
    private static EventWaitHandle? TryOpen(string name)
    {
        foreach (string prefix in new[] { @"Global\", @"Local\" }) {
            try { if (EventWaitHandle.TryOpenExisting(prefix + name, out var handle)) return handle; }
            catch (UnauthorizedAccessException) { }
            catch (WaitHandleCannotBeOpenedException) { }
        }
        return null;
    }
    public void Dispose() => Stop(false);
}
