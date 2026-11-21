using System.Text.Json.Serialization;

namespace DesktopSplitter.Models;

/// <summary>Persisted evidence, never an address. IDs belong to this installation's catalog.</summary>
public sealed class MonitorIdentity
{
    [JsonPropertyName("stableMonitorId")] public string StableMonitorId { get; set; } = "";
    [JsonPropertyName("monitorDevicePath")] public string MonitorDevicePath { get; set; } = "";
    [JsonPropertyName("adapterPath")] public string AdapterPath { get; set; } = "";
    [JsonPropertyName("manufacturer")] public string Manufacturer { get; set; } = "";
    [JsonPropertyName("productCode")] public int ProductCode { get; set; } = -1;
    [JsonPropertyName("serialNumber")] public uint SerialNumber { get; set; }
    [JsonPropertyName("virtualAdapterId")] public string VirtualAdapterId { get; set; } = "";
    [JsonPropertyName("segmentIndex")] public int SegmentIndex { get; set; } = -1;
    public bool Retired { get; set; }
    [JsonIgnore] public bool IsVirtual => SegmentIndex >= 0;
    [JsonIgnore] public bool HasSerial => SerialNumber != 0 && SerialNumber != uint.MaxValue;
    public static bool Same(string? a, string? b) => !string.IsNullOrEmpty(a) &&
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    public bool SameModel(MonitorIdentity other) => Same(Manufacturer, other.Manufacturer) &&
        ProductCode >= 0 && ProductCode == other.ProductCode;
    public bool Compatible(MonitorIdentity other) => IsVirtual == other.IsVirtual && SameModel(other) &&
        (!HasSerial || !other.HasSerial || SerialNumber == other.SerialNumber);
}

public sealed class MonitorIdentityCatalog
{
    public int SchemaVersion { get; set; } = 1;
    public List<MonitorIdentity> Monitors { get; set; } = new();

    private MonitorIdentity? UniqueBinding(MonitorIdentity? record) => record is not null &&
        Monitors.Count(x => !x.Retired && x.StableMonitorId == record.StableMonitorId) == 1 ? record : null;

    /// <summary>Fail closed, including duplicate persisted records and duplicate live serials.</summary>
    public MonitorIdentity? Match(MonitorIdentity observed, IReadOnlyList<MonitorIdentity> live)
    {
        if (string.IsNullOrEmpty(observed.MonitorDevicePath) || string.IsNullOrEmpty(observed.Manufacturer) ||
            observed.ProductCode < 0) return null;
        if (live.Count(x => MonitorIdentity.Same(x.MonitorDevicePath, observed.MonitorDevicePath)) != 1) return null;
        var exact = Monitors.Where(x => !x.Retired && MonitorIdentity.Same(x.MonitorDevicePath, observed.MonitorDevicePath)).ToList();
        if (exact.Count > 0)
            return UniqueBinding(exact.Count == 1 && exact[0].Compatible(observed) &&
                (!observed.IsVirtual || MonitorIdentity.Same(exact[0].AdapterPath, observed.AdapterPath)) ? exact[0] : null);
        if (observed.IsVirtual)
        {
            var segment = Monitors.Where(x => !x.Retired && x.IsVirtual && x.SegmentIndex == observed.SegmentIndex &&
                MonitorIdentity.Same(x.AdapterPath, observed.AdapterPath) && x.Compatible(observed)).ToList();
            return UniqueBinding(segment.Count == 1 && live.Count(x => x.IsVirtual && x.SegmentIndex == observed.SegmentIndex &&
                MonitorIdentity.Same(x.AdapterPath, observed.AdapterPath)) == 1 ? segment[0] : null);
        }
        if (!observed.HasSerial) return null;
        var serial = Monitors.Where(x => !x.Retired && !x.IsVirtual && x.SameModel(observed) && x.HasSerial &&
            x.SerialNumber == observed.SerialNumber).ToList();
        return UniqueBinding(serial.Count == 1 && live.Count(x => !x.IsVirtual && x.SameModel(observed) &&
            x.SerialNumber == observed.SerialNumber) == 1 ? serial[0] : null);
    }

    public bool HasPossibleConflict(MonitorIdentity observed) => Monitors.Any(x =>
        MonitorIdentity.Same(x.MonitorDevicePath, observed.MonitorDevicePath) ||
        (observed.IsVirtual
            ? x.IsVirtual && x.SegmentIndex == observed.SegmentIndex && MonitorIdentity.Same(x.AdapterPath, observed.AdapterPath)
            : !x.IsVirtual && x.SameModel(observed) && (!x.HasSerial || !observed.HasSerial || x.SerialNumber == observed.SerialNumber)));

    /// <summary>Only explicit user selection may rebind a physical record after ambiguous discovery.</summary>
    public MonitorIdentity Register(MonitorIdentity observed, string? confirmedId = null)
    {
        if (observed.MonitorDevicePath.Length == 0 || observed.Manufacturer.Length == 0 || observed.ProductCode < 0)
            throw new InvalidOperationException("Monitor identity evidence is incomplete; reconnect and refresh.");
        if (confirmedId is not null)
        {
            var existing = Monitors.Single(x => x.StableMonitorId == confirmedId);
            if (existing.IsVirtual || observed.IsVirtual) throw new InvalidOperationException("Cannot rebind a virtual segment.");
            // Explicit confirmation replaces the evidence, never silently merges two records.
            if (Monitors.Any(x => x != existing && MonitorIdentity.Same(x.MonitorDevicePath, observed.MonitorDevicePath)))
                throw new InvalidOperationException("This device path already belongs to another ID.");
            Monitors.Remove(existing);
            observed.StableMonitorId = confirmedId;
        }
        else if (observed.IsVirtual)
        {
            if (observed.AdapterPath.Length == 0) throw new InvalidOperationException("Virtual adapter identity is missing.");
            observed.VirtualAdapterId = Monitors.FirstOrDefault(x => x.IsVirtual &&
                MonitorIdentity.Same(x.AdapterPath, observed.AdapterPath))?.VirtualAdapterId ?? Guid.NewGuid().ToString("N");
            observed.StableMonitorId = $"DSV-{observed.VirtualAdapterId}-{observed.SegmentIndex + 1:00}";
        }
        else
        {
            int next = 1;
            while (Monitors.Any(x => x.StableMonitorId == $"MON-{next:000}")) next++;
            observed.StableMonitorId = $"MON-{next:000}";
        }
        foreach (var prior in Monitors.Where(x => !x.Retired && MonitorIdentity.Same(x.MonitorDevicePath, observed.MonitorDevicePath)))
            prior.Retired = true;
        if (Monitors.Any(x => x.StableMonitorId == observed.StableMonitorId))
            throw new InvalidOperationException("Duplicate logical monitor identity.");
        Monitors.Add(observed);
        return observed;
    }
}
