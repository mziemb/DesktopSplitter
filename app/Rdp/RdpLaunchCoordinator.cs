namespace DesktopSplitter.Rdp;

/// <summary>Testable preflight boundary: no client launch until fresh topology and storage are validated.</summary>
public sealed class RdpLaunchCoordinator
{
    private readonly Func<Task<IReadOnlyList<RdpMonitor>>> _read;
    private readonly Func<bool> _systemBusy;
    private readonly Action _validateStorage;
    private readonly Action<string> _launch;
    private readonly Action<IReadOnlyList<RdpMonitor>, IReadOnlyList<string>, string> _remember;
    public RdpLaunchCoordinator(Func<Task<IReadOnlyList<RdpMonitor>>> read, Func<bool> systemBusy,
        Action validateStorage, Action<string> launch,
        Action<IReadOnlyList<RdpMonitor>, IReadOnlyList<string>, string> remember)
    { _read = read; _systemBusy = systemBusy; _validateStorage = validateStorage; _launch = launch; _remember = remember; }

    public async Task<string?> LaunchAsync(RdpConnection connection, IReadOnlyList<RdpMonitor> expected,
        IReadOnlyList<string> ids, string primary)
    {
        var fresh = await _read();
        if (_systemBusy() || RdpLayoutPolicy.SnapshotKey(fresh) != RdpLayoutPolicy.SnapshotKey(expected))
            throw new InvalidOperationException("Displays changed after selection. Refresh and choose again; no connection was started.");
        string content = RdpLayoutPolicy.BuildFile(connection, fresh, ids, primary);
        _validateStorage();
        if (_systemBusy()) throw new InvalidOperationException("Apply/Revert started. Refresh after it finishes; no connection was started.");
        _launch(content);
        try { _remember(fresh, ids, primary); }
        catch (Exception ex) { return ex.Message; }
        return null;
    }
}
