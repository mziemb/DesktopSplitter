namespace DesktopSplitter.Models;

/// <summary>The shared fail-closed operation boundary, independent of enumeration and Windows APIs.</summary>
public static class MonitorIdentityResolver
{
    public static MonitorInfo Resolve(string stableId, IReadOnlyList<MonitorInfo> connected, bool requireActive = true)
    {
        if (string.IsNullOrEmpty(stableId)) throw new InvalidOperationException("Monitor identity is not confirmed. Select and confirm the monitor.");
        var matches = connected.Where(x => x.StableMonitorId == stableId).ToList();
        if (matches.Count != 1 || (requireActive && (!matches[0].IsActive || matches[0].DeviceName.Length == 0)))
            throw new InvalidOperationException($"Monitor {stableId} is missing, inactive or ambiguous. Re-select the device.");
        if (requireActive && connected.Count(x => x.IsActive && MonitorIdentity.Same(x.DeviceName, matches[0].DeviceName)) != 1)
            throw new InvalidOperationException("Cloned displays share a GDI source; extend the desktop before Apply.");
        return matches[0];
    }
}
