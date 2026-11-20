using DesktopSplitter.Models;
namespace DesktopSplitter.Services;

/// <summary>A failed preference write must not make the reconciler undo the user's new choice.</summary>
public sealed class PrimaryPreferenceWriteback
{
    private PrimaryMonitorState? _expected, _desired;
    public void Commit(PrimaryMonitorState expected, PrimaryMonitorState desired, Action<PrimaryMonitorState> write)
    {
        _expected = expected; _desired = desired;
        write(desired);
        _expected = _desired = null;
    }
    public PrimaryMonitorState? Flush(PrimaryMonitorState? current, Action<PrimaryMonitorState> write)
    {
        if (_desired is null) return current;
        if (current != _expected) { _expected = _desired = null; return current; } // A newer Apply/Revert wins.
        var desired = _desired;
        write(desired);
        _expected = _desired = null;
        return desired;
    }
}
