namespace DesktopSplitter.Models;

/// <summary>Logical preferences, never GDI addresses. PreSplitId is immutable for a split session.</summary>
public sealed record PrimaryMonitorState
{
    public int SchemaVersion { get; init; } = 1;
    public string PreSplitId { get; init; } = string.Empty;
    public string SplitPhysicalId { get; init; } = string.Empty;
    public string DesiredId { get; init; } = string.Empty;
    public bool DesiredIsVirtual { get; init; }
    public bool ManualOverride { get; init; }
    public bool SplitActive { get; init; }
    public bool RevertPending { get; init; }

    public static PrimaryMonitorState BeginApply(PrimaryMonitorState? previous, string currentPrimaryId, string splitId)
    {
        string original = previous is { SplitActive: true } or { RevertPending: true } ? previous.PreSplitId : currentPrimaryId;
        if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(splitId)) throw new InvalidOperationException("Primary identity is not confirmed.");
        bool keepManual = previous?.SplitActive == true && previous.ManualOverride;
        return new PrimaryMonitorState { PreSplitId = original, SplitPhysicalId = splitId, SplitActive = true,
            DesiredId = keepManual ? previous!.DesiredId : original,
            DesiredIsVirtual = keepManual && previous!.DesiredIsVirtual, ManualOverride = keepManual };
    }
    public PrimaryMonitorState CompleteApply(string firstVirtualId, IReadOnlyCollection<string> activeIds, bool hidePhysical = false)
    {
        bool keep = ManualOverride && (!hidePhysical || DesiredId != SplitPhysicalId) && (!DesiredIsVirtual || activeIds.Contains(DesiredId));
        string id = keep ? DesiredId : PreSplitId == SplitPhysicalId ? firstVirtualId : PreSplitId;
        if (id.Length == 0) throw new InvalidOperationException("Applied primary identity is unknown.");
        return this with { DesiredId = id, DesiredIsVirtual = keep ? DesiredIsVirtual : PreSplitId == SplitPhysicalId, ManualOverride = keep };
    }
    public PrimaryMonitorState CompleteTeardown(bool deliberate) => deliberate ? BeginRevert() : this;
    public PrimaryMonitorState BeginRevert() => this with { SplitActive = false, RevertPending = true,
        DesiredId = PreSplitId, DesiredIsVirtual = false, ManualOverride = false };
}

public sealed record PrimaryObservation(string Id, bool IsPrimary, bool IsVirtual, int Width, int Height);
public sealed record PrimaryDecision(PrimaryMonitorState State, string TargetId, string Reason);

/// <summary>Pure reconciliation policy. Lifecycle/fallback changes cannot overwrite user intent.</summary>
public sealed class PrimaryPreferenceTracker
{
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(3);
    private string _topology = string.Empty;
    private string _lastPrimary = string.Empty;
    private bool _wasSuspended;
    private string _pendingChange = "";
    private DateTime _pendingAt;
    private DateTime _quietUntil;
    public void Invalidate(DateTime now) { _quietUntil = now + SettleTime; _pendingChange = ""; }
    public void OwnSwitch(DateTime now) { Invalidate(now); _lastPrimary = string.Empty; }

    public PrimaryDecision Observe(PrimaryMonitorState state, IReadOnlyList<PrimaryObservation> displays,
        DateTime now, bool displayEvent, bool lifecycleEvent, bool suspended)
    {
        if (displays.Count == 0 || displays.Any(d => d.Id.Length == 0) ||
            displays.Select(d => d.Id).Distinct().Count() != displays.Count || displays.Count(d => d.IsPrimary) != 1)
        { Invalidate(now); _topology = ""; _lastPrimary = ""; return new(state, "", "Unknown or ambiguous display identity; preference retained"); }
        string topology = string.Join(";", displays.OrderBy(d => d.Id).Select(d => $"{d.Id}:{d.Width}x{d.Height}"));
        string primary = displays.Single(d => d.IsPrimary).Id;
        bool changed = topology != _topology;
        if (changed || lifecycleEvent || suspended != _wasSuspended) Invalidate(now);
        string oldPrimary = _lastPrimary;
        _topology = topology; _lastPrimary = primary; _wasSuspended = suspended;
        string desired = state.RevertPending ? state.PreSplitId : state.DesiredId;
        if (desired.Length == 0) return new(state, "", "No pending primary preference");
        var target = displays.SingleOrDefault(d => d.Id == desired);

        if (suspended && state.SplitActive)
        {
            // Temporary secure-desktop fallback never becomes the user's saved preference.
            var physical = target is { IsVirtual: false } ? target
                : displays.FirstOrDefault(d => d.Id == state.PreSplitId && !d.IsVirtual)
                ?? displays.FirstOrDefault(d => d.Id == state.SplitPhysicalId && !d.IsVirtual)
                ?? displays.FirstOrDefault(d => d.IsPrimary && !d.IsVirtual)
                ?? displays.Where(d => !d.IsVirtual).OrderBy(d => d.Id).FirstOrDefault();
            return new(state, physical is not null && physical.Id != primary ? physical.Id : "", "Temporary secure-desktop primary");
        }
        bool settled = now >= _quietUntil;
        if (!state.RevertPending && settled && !changed && !lifecycleEvent && displayEvent &&
            oldPrimary.Length > 0 && (primary != oldPrimary || (_pendingChange == primary && now - _pendingAt <= SettleTime)) && primary != desired)
        {
            _pendingChange = "";
            var chosen = displays.Single(d => d.IsPrimary);
            if (!state.SplitActive && chosen.IsVirtual) return new(state, "", "A retiring virtual monitor cannot become the idle primary preference");
            return new(state with { DesiredId = primary, DesiredIsVirtual = chosen.IsVirtual, ManualOverride = true },
                "", "Primary changed on a settled desktop; remembering the new split preference");
        }
        if (target is null) return new(state, "", "Preferred primary is offline; waiting for the same stable identity");
        if (!state.SplitActive && state.RevertPending && primary == desired)
            return new(state with { RevertPending = false }, "", "Pre-split primary restoration confirmed");
        if (!settled || primary == desired) return new(state, "", "Waiting for topology to settle / primary already correct");
        if (!state.RevertPending && !displayEvent && primary != oldPrimary && oldPrimary.Length > 0) {
            _pendingChange = primary; _pendingAt = now;
            return new(state, "", "Waiting briefly for the display-change notification");
        }
        if (_pendingChange == primary && now - _pendingAt < SettleTime)
            return new(state, "", "Waiting briefly for the display-change notification");
        return new(state, desired, "Restoring the remembered primary");
    }
}
