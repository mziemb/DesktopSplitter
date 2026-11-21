using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DesktopSplitter.Rdp;

public sealed class RdpConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 3389;
    public string UserName { get; set; } = string.Empty;
    public List<RdpRememberedLayout> Layouts { get; set; } = new();
    public override string ToString() => Name;

    public void Validate()
    {
        static bool Clean(string? s, int max) => s is not null && s.Length <= max && !s.Any(char.IsControl);
        if (!Guid.TryParseExact(Id, "N", out _) || !Clean(Name, 120) || string.IsNullOrWhiteSpace(Name))
            throw new InvalidOperationException("Enter a connection name (up to 120 characters).");
        if (!Clean(Host, 253) || Host.Length == 0 || Host != Host.Trim() ||
            Uri.CheckHostName(Host.Trim('[', ']')) == UriHostNameType.Unknown ||
            Host.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '"'))
            throw new InvalidOperationException("Enter a host name or IP address, without a port or URL.");
        if (Port is < 1 or > 65535) throw new InvalidOperationException("Port must be between 1 and 65535.");
        if (!Clean(UserName, 256)) throw new InvalidOperationException("User name contains invalid characters.");
    }
    public string Address => $"{(Host.Contains(':') ? "[" + Host.Trim('[', ']') + "]" : Host)}:{Port}";
}

public sealed class RdpRememberedLayout
{
    public string MonitorSetKey { get; set; } = string.Empty;
    public List<string> SelectedIds { get; set; } = new();
    public string PrimaryId { get; set; } = string.Empty;
}

public sealed class RdpCatalog
{
    public int SchemaVersion { get; set; } = 1;
    public List<RdpConnection> Connections { get; set; } = new();
}

public sealed record RdpMonitor(string StableId, string Label, string GdiName, int RdpId,
    int X, int Y, int Width, int Height, bool LocalPrimary)
{
    public int WindowsNumber { get; init; }
    public string BlockedReason { get; init; } = string.Empty;
    public bool CanSelect => StableId.Length > 0 && BlockedReason.Length == 0;
    public long Right => (long)X + Width;
    public long Bottom => (long)Y + Height;
    public override string ToString() => Label;
}

public sealed record RdpListedMonitor(int Id, int X, int Y, int Width, int Height);

public static class RdpLayoutPolicy
{
    // mstsc /l is a localized dialog, not stdout. Only its numeric records are parsed.
    // Windows reports inclusive right/bottom coordinates; accept exclusive fixtures too.
    private static readonly Regex MonitorLine = new(
        @"(?m)^\s*(\d+)\s*:\s*(\d+)\s*[x×]\s*(\d+)\s*;\s*\(\s*(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)\s*\)\s*$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static IReadOnlyList<RdpListedMonitor> ParseMonitorList(string text)
    {
        var result = new List<RdpListedMonitor>();
        foreach (Match match in MonitorLine.Matches(text.Replace("\r", "")))
        {
            int[] n = Enumerable.Range(1, 7).Select(i => int.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture)).ToArray();
            long dx = (long)n[5] - n[3], dy = (long)n[6] - n[4];
            if (n[1] <= 0 || n[2] <= 0 || !((dx == n[1] - 1L && dy == n[2] - 1L) || (dx == n[1] && dy == n[2])))
                throw new InvalidOperationException("RDP monitor coordinates could not be verified.");
            result.Add(new(n[0], n[3], n[4], n[1], n[2]));
        }
        if (result.Count == 0 || result.Count > 16 || result.Select(m => m.Id).Distinct().Count() != result.Count)
            throw new InvalidOperationException("The RDP monitor list is missing, unsupported or ambiguous.");
        return result;
    }

    public static IReadOnlyList<RdpMonitor> BindClientIds(IReadOnlyList<RdpMonitor> current, IReadOnlyList<RdpListedMonitor> listed)
    {
        if (current.Count == 0 || listed.Count != current.Count || listed.Select(m => m.Id).Distinct().Count() != listed.Count)
            throw new InvalidOperationException("Windows and RDP disagree about active monitors. Refresh after display changes finish.");
        var result = current.Select(m =>
        {
            var matches = listed.Where(x => x.X == m.X && x.Y == m.Y && x.Width == m.Width && x.Height == m.Height).ToList();
            if (matches.Count != 1) throw new InvalidOperationException("RDP monitor mapping is ambiguous. Mirrored or overlapping displays are not supported.");
            return m with { RdpId = matches[0].Id };
        }).ToList();
        if (result.Select(m => m.RdpId).Distinct().Count() != result.Count)
            throw new InvalidOperationException("More than one physical target maps to the same RDP monitor.");
        return result;
    }

    public static string MonitorSetKey(IReadOnlyList<RdpMonitor> monitors)
    {
        if (monitors.Count == 0 || monitors.Any(m => string.IsNullOrWhiteSpace(m.StableId)) ||
            monitors.Select(m => m.StableId).Distinct(StringComparer.Ordinal).Count() != monitors.Count)
            throw new InvalidOperationException("Confirm each monitor's identity before saving an RDP layout.");
        string canonical = string.Concat(monitors.Select(m => m.StableId).OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => $"{id.Length}:{id};"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static IReadOnlyList<RdpMonitor> Select(IReadOnlyList<RdpMonitor> monitors, IEnumerable<string> selectedIds, string primaryId)
    {
        _ = MonitorSetKey(monitors);
        if (monitors.Any(m => m.RdpId < 0 || m.Width <= 0 || m.Height <= 0) ||
            monitors.Select(m => m.RdpId).Distinct().Count() != monitors.Count)
            throw new InvalidOperationException("Refresh the RDP monitor list before connecting.");
        var ids = selectedIds.ToList();
        if (ids.Count == 0 || ids.Count > 16 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count || !ids.Contains(primaryId))
            throw new InvalidOperationException("Select at least one monitor and a primary from the selected monitors.");
        var selected = ids.Select(id => monitors.SingleOrDefault(m => m.StableId == id)
            ?? throw new InvalidOperationException("A selected monitor is no longer available.")).ToList();
        if (selected.Any(m => !m.CanSelect)) throw new InvalidOperationException("The split physical display cannot be used for RDP. Select its virtual monitors instead.");
        for (int i = 0; i < selected.Count; i++)
            for (int j = i + 1; j < selected.Count; j++)
                if (selected[i].X < selected[j].Right && selected[j].X < selected[i].Right &&
                    selected[i].Y < selected[j].Bottom && selected[j].Y < selected[i].Bottom)
                    throw new InvalidOperationException("Overlapping or mirrored monitors cannot be selected for RDP.");
        bool Touch(RdpMonitor a, RdpMonitor b) =>
            ((a.Right == b.X || b.Right == a.X) && Math.Max(a.Y, b.Y) < Math.Min(a.Bottom, b.Bottom)) ||
            ((a.Bottom == b.Y || b.Bottom == a.Y) && Math.Max(a.X, b.X) < Math.Min(a.Right, b.Right));
        var visited = new HashSet<string> { selected[0].StableId };
        while (true)
        {
            var next = selected.Where(m => !visited.Contains(m.StableId) && selected.Any(v => visited.Contains(v.StableId) && Touch(m, v))).ToList();
            if (next.Count == 0) break;
            foreach (var m in next) visited.Add(m.StableId);
        }
        if (visited.Count != selected.Count) throw new InvalidOperationException("Selected monitors must share edges. Gaps and corner-only contact are not supported by RDP.");
        return selected.OrderBy(m => m.StableId == primaryId ? 0 : 1).ThenBy(m => m.RdpId).ToList();
    }

    public static RdpRememberedLayout? Recall(RdpConnection connection, IReadOnlyList<RdpMonitor> monitors)
    {
        string key = MonitorSetKey(monitors);
        var saved = connection.Layouts.SingleOrDefault(x => x.MonitorSetKey == key);
        if (saved is null) return null;
        _ = Select(monitors, saved.SelectedIds, saved.PrimaryId);
        return saved;
    }

    public static void Remember(RdpConnection connection, IReadOnlyList<RdpMonitor> monitors, IEnumerable<string> ids, string primary)
    {
        var selected = Select(monitors, ids, primary);
        string key = MonitorSetKey(monitors);
        connection.Layouts.RemoveAll(x => x.MonitorSetKey == key);
        connection.Layouts.Add(new() { MonitorSetKey = key, SelectedIds = selected.Select(m => m.StableId).ToList(), PrimaryId = primary });
    }

    public static string BuildFile(RdpConnection connection, IReadOnlyList<RdpMonitor> monitors, IEnumerable<string> ids, string primary)
    {
        connection.Validate();
        var selected = Select(monitors, ids, primary);
        return $"full address:s:{connection.Address}\r\nusername:s:{connection.UserName}\r\nscreen mode id:i:2\r\nuse multimon:i:1\r\nspan monitors:i:0\r\nselectedmonitors:s:{string.Join(",", selected.Select(m => m.RdpId.ToString(CultureInfo.InvariantCulture)))}\r\ndynamic resolution:i:0\r\nprompt for credentials:i:1\r\nauthentication level:i:2\r\nenablecredsspsupport:i:1\r\n";
    }

    // Includes transient addresses and geometry, so a topology change invalidates an open picker.
    public static string SnapshotKey(IReadOnlyList<RdpMonitor> monitors) => string.Join("|", monitors
        .OrderBy(m => m.StableId, StringComparer.Ordinal).Select(m => $"{m.StableId}:{m.GdiName}:{m.RdpId}:{m.X}:{m.Y}:{m.Width}:{m.Height}:{m.BlockedReason}"));
}
