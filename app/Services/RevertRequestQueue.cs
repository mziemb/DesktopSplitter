namespace DesktopSplitter.Services;

public enum RevertRequestDisposition { Start, Queued, AlreadyReverting }

/// <summary>Dispatcher-owned state: coalesce repeats and defer Revert until Apply releases the pipeline.</summary>
public sealed class RevertRequestQueue
{
    private bool _pending;
    private bool _reverting;
    public RevertRequestDisposition Request(bool busy)
    {
        if (_reverting) return RevertRequestDisposition.AlreadyReverting;
        if (busy) { _pending = true; return RevertRequestDisposition.Queued; }
        _pending = false;
        _reverting = true;
        return RevertRequestDisposition.Start;
    }
    public bool TakePending() { bool pending = _pending; _pending = false; return pending; }
    public void CompleteRevert() => _reverting = false;
}
