using DesktopSplitter.Models;

namespace DesktopSplitter.Services;

public static class MonitorSettingsMigration
{
    /// <summary>Never infer identity from a legacy GDI alias or friendly name. Called only after explicit confirmation.</summary>
    public static void ConfirmSelection(AppSettings settings, string stableId, bool ownsLegacyHiddenDisplay)
    {
        if (string.IsNullOrWhiteSpace(stableId)) throw new InvalidOperationException("Identity is not confirmed.");
        if (settings.HasConfirmedIdentity) return;
        settings.PhysicalStableMonitorId = stableId;
        settings.IdentitySchemaVersion = 1;
        settings.ApplyOnStartup = false;
        settings.SplitActiveOnExit = false;
        if (ownsLegacyHiddenDisplay) settings.SpecializedStableMonitorId = stableId;
        // Legacy aliases remain intact for diagnostics and the exact original is backed up by SettingsStore.
    }
}
