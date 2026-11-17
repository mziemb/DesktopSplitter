using System.IO;
using System.Text.Json;
using DesktopSplitter.Interop;
using DesktopSplitter.Models;

namespace DesktopSplitter.Services;

/// <summary>Serializes discovery and durable catalog writes. Corruption is an error, never a fresh catalog.</summary>
public static class MonitorIdentityStore
{
    private static readonly object Gate = new();
    private static string CatalogPath => Path.Combine(SettingsStore.Directory, "monitor-identities.json");
    private static MonitorIdentityCatalog Load()
    {
        var catalog = File.Exists(CatalogPath)
            ? JsonSerializer.Deserialize<MonitorIdentityCatalog>(File.ReadAllText(CatalogPath))
                ?? throw new InvalidDataException("Invalid monitor identity catalog.") : new();
        if (catalog.SchemaVersion != 1) throw new InvalidDataException("Unsupported monitor identity catalog schema.");
        return catalog;
    }
    private static void Save(MonitorIdentityCatalog catalog)
    {
        Directory.CreateDirectory(SettingsStore.Directory);
        string temp = CatalogPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, CatalogPath, true);
    }
    public static MonitorIdentity Observe(MonitorInfo m) => new()
    {
        MonitorDevicePath = m.MonitorDevicePath, AdapterPath = m.AdapterPath,
        Manufacturer = m.Edid?.Manufacturer ?? m.EdidManufacturer,
        ProductCode = m.Edid?.ProductCode ?? m.EdidProductCode,
        SerialNumber = m.Edid?.SerialNumber ?? 0,
        SegmentIndex = m.IsDeskSplitVirtual ? m.DeskSplitMonitorIndex : -1,
    };
    public static void Identify(IReadOnlyList<MonitorInfo> monitors)
    {
        lock (Gate)
        {
            var catalog = Load();
            var observations = monitors.Select(Observe).ToList();
            bool changed = false;
            for (int i = 0; i < monitors.Count; i++)
            {
                var observed = observations[i];
                var known = catalog.Match(observed, observations);
                // Never register a new physical ID during discovery. A human confirms the binding.
                if (known is null && monitors[i].IsDeskSplitVirtual && observed.IsVirtual &&
                    !catalog.HasPossibleConflict(observed) && observed.AdapterPath.Length > 0)
                { known = catalog.Register(observed); changed = true; }
                if (known is not null)
                {
                    observed.StableMonitorId = known.StableMonitorId;
                    observed.VirtualAdapterId = known.VirtualAdapterId;
                    monitors[i].Identity = observed;
                }
                else monitors[i].IdentityConflict = "Unconfirmed or ambiguous identity — select Identity details / Confirm.";
            }
            if (changed) Save(catalog);
        }
    }
    public static IReadOnlyList<MonitorIdentity> PhysicalRecords()
    { lock (Gate) return Load().Monitors.Where(x => !x.IsVirtual).ToList(); }
    public static void Confirm(MonitorInfo monitor, string? existingId)
    {
        lock (Gate)
        {
            var connected = DisplayConfig.EnumerateConnected();
            var selectedEvidence = Observe(monitor);
            var current = connected.Where(x => MonitorIdentity.Same(x.MonitorDevicePath, monitor.MonitorDevicePath) &&
                selectedEvidence.Compatible(Observe(x))).ToList();
            if (current.Count != 1) throw new InvalidOperationException("Selected device changed; refresh before confirming.");
            var catalog = Load();
            var observed = Observe(current[0]);
            // Refuse concurrent live reuse of an ID selected for a different monitor.
            if (existingId is not null && connected.Any(x =>
                x.StableMonitorId == existingId && !MonitorIdentity.Same(x.MonitorDevicePath, monitor.MonitorDevicePath)))
                throw new InvalidOperationException("The selected ID is still connected on another device path.");
            var match = catalog.Match(observed, connected.Select(Observe).ToList());
            if (match is not null && (existingId is null || match.StableMonitorId == existingId))
            { monitor.Identity = observed; observed.StableMonitorId = match.StableMonitorId; return; }
            monitor.Identity = catalog.Register(observed, existingId);
            Save(catalog);
        }
    }
    public static MonitorInfo Resolve(string stableId, bool requireActive = true)
    {
        return MonitorIdentityResolver.Resolve(stableId, DisplayConfig.EnumerateConnected(), requireActive);
    }
}
