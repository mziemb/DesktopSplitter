using DesktopSplitter.Models;
using static DesktopSplitter.Interop.NativeMethods;

namespace DesktopSplitter.Interop;

public sealed class DisplayArrangeException : Exception
{
    public DisplayArrangeException(string message) : base(message) { }
}

/// <summary>
/// Places the virtual monitors in virtual-desktop space, immediately to the RIGHT of the
/// physical monitor, preserving the layout's relative geometry, and sets each to its
/// configured mode. Also owns primary-display switching, which requires re-basing every
/// monitor's position so the new primary sits at the virtual-desktop origin.
/// Validates and commits the complete active DisplayConfig topology.
/// </summary>
public static class DisplayArranger
{
    public sealed record VirtualPlacement(
        string DeviceName, int Width, int Height, int X, int Y, int Col, int Row, string StableMonitorId);

    /// <summary>
    /// A monitor's desired mode and position in virtual-desktop space.
    /// </summary>
    public sealed record MonitorPlacement(
        string DeviceName, int Width, int Height, int X, int Y, uint Rotation, bool IsPrimary, string StableMonitorId)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;
        public override string ToString()
            => $"{DeviceName} {Width}x{Height}+{X}+{Y}{(IsPrimary ? " [PRIMARY]" : string.Empty)}";
    }

    /// <summary>
    /// Computes desktop-space positions. Column widths / row heights come from the actual
    /// segment resolutions (not the physical monitor), so the virtual desktop stays gap-free
    /// even when segment resolutions differ from their on-screen rects.
    /// </summary>
    public static IReadOnlyList<VirtualPlacement> ComputePlacements(
        MonitorPlacement physical,
        LayoutDefinition layout,
        IReadOnlyList<MonitorInfo> virtualMonitors,
        IReadOnlyList<(int Width, int Height)> resolutions,
        IReadOnlyList<MonitorPlacement> activePhysical)
    {
        if (virtualMonitors.Count != layout.SegmentCount)
            throw new DisplayArrangeException(
                $"Expected {layout.SegmentCount} virtual monitors, found {virtualMonitors.Count}.");
        if (resolutions.Count != layout.SegmentCount)
            throw new DisplayArrangeException(
                $"Expected {layout.SegmentCount} segment resolutions, got {resolutions.Count}.");

        LayoutGeometry.Tracks tracks = LayoutGeometry.ComputeTracks(layout, resolutions);
        int[] colWidth = tracks.ColumnWidths;
        int[] rowHeight = tracks.RowHeights;

        var columnOffsets = new int[colWidth.Length];
        for (int i = 1; i < columnOffsets.Length; i++)
            columnOffsets[i] = columnOffsets[i - 1] + colWidth[i - 1];
        var rowOffsets = new int[rowHeight.Length];
        for (int i = 1; i < rowOffsets.Length; i++)
            rowOffsets[i] = rowOffsets[i - 1] + rowHeight[i - 1];

        var placements = PlaceToRightOf(physical);
        if (placements.Any(v => activePhysical.Any(m =>
                v.X < m.Right && v.X + v.Width > m.X &&
                v.Y < m.Bottom && v.Y + v.Height > m.Y)))
        {
            // The selected physical output may have a neighbor to its right. Anchor the
            // segment group to the right edge of the physical desktop in that case.
            var rightmost = activePhysical.MaxBy(m => m.Right)!;
            placements = PlaceToRightOf(rightmost);
        }
        return placements;

        List<VirtualPlacement> PlaceToRightOf(MonitorPlacement anchor)
        {
            var result = new List<VirtualPlacement>(layout.SegmentCount);
            for (int i = 0; i < layout.SegmentCount; i++)
            {
                LayoutCell cell = layout.Cells[i];
                (int width, int height) = resolutions[i];
                int x = anchor.Right + columnOffsets[cell.Col];
                int y = anchor.Y + rowOffsets[cell.Row];
                MonitorInfo monitor = virtualMonitors[i];
                result.Add(new VirtualPlacement(monitor.DeviceName, width, height, x, y,
                    cell.Col, cell.Row, monitor.StableMonitorId));
            }
            return result;
        }
    }

    /// <summary>
    /// The whole desktop as it should look once the split is up: every monitor that is not one
    /// of <paramref name="placements"/> keeps its rect from before virtual enumeration,
    /// and everything is re-based so <paramref name="primaryDevice"/> is primary at the origin.
    /// Pure function: no display APIs are called.
    /// </summary>
    public static IReadOnlyList<MonitorPlacement> ComposeLayout(
        IReadOnlyList<MonitorInfo> current,
        IReadOnlyList<VirtualPlacement> placements,
        IReadOnlyList<MonitorPlacement> physicalBefore,
        string primaryDevice)
    {
        var segments = placements.ToDictionary(p => p.StableMonitorId);
        var physical = physicalBefore.ToDictionary(p => p.StableMonitorId);
        if (!current.Where(m => !m.IsDeskSplitVirtual).Select(m => m.StableMonitorId)
                    .ToHashSet().SetEquals(physical.Keys))
            throw new DisplayArrangeException("The physical display set changed during Apply.");

        var layout = SnapshotLayout(current)
            .Select(p => segments.TryGetValue(p.StableMonitorId, out VirtualPlacement? v)
                ? p with { Width = v.Width, Height = v.Height, X = v.X, Y = v.Y }
                : physical.TryGetValue(p.StableMonitorId, out MonitorPlacement? saved)
                    ? saved with { DeviceName = p.DeviceName }
                    : throw new DisplayArrangeException($"Unexpected display {p.DeviceName} during Apply."))
            .ToList();

        return RebaseToPrimary(layout, primaryDevice);
    }

    // ------------------------------------------------------------------ primary switching

    /// <summary>
    /// Shifts every monitor so that <paramref name="primaryDevice"/> lands on the virtual-desktop
    /// origin (0,0) and is flagged primary. Windows requires the primary display to be at (0,0),
    /// so promoting a different monitor necessarily re-bases all the others by the same vector —
    /// which leaves every relative offset, and therefore the whole arrangement, untouched.
    /// Pure function: no display APIs are called.
    /// </summary>
    public static IReadOnlyList<MonitorPlacement> RebaseToPrimary(
        IReadOnlyList<MonitorPlacement> placements, string primaryDevice)
    {
        if (placements.Count == 0)
            throw new DisplayArrangeException("Cannot re-base an empty monitor set.");

        MonitorPlacement? origin = placements.FirstOrDefault(
            p => string.Equals(p.DeviceName, primaryDevice, StringComparison.OrdinalIgnoreCase));
        if (origin is null)
        {
            throw new DisplayArrangeException(
                $"Monitor '{primaryDevice}' is not in the current display set, so it cannot be made primary. " +
                $"Known: {string.Join(", ", placements.Select(p => p.DeviceName))}.");
        }

        int dx = -origin.X;
        int dy = -origin.Y;

        return placements
            .Select(p => p with
            {
                X = p.X + dx,
                Y = p.Y + dy,
                IsPrimary = string.Equals(p.DeviceName, primaryDevice, StringComparison.OrdinalIgnoreCase),
            })
            .ToList();
    }

    public static void ApplyLayout(
        IReadOnlyList<MonitorInfo> current, IReadOnlyList<MonitorPlacement> placements, Action<string> log)
    {
        if (placements.Count(p => p.IsPrimary) != 1 || placements.Any(p => p.IsPrimary != (p.X == 0 && p.Y == 0) || p.Width <= 0 || p.Height <= 0))
            throw new DisplayArrangeException("The topology must have one primary at (0,0).");

        (var paths, var modes) = DisplayConfig.QueryConfig(QDC_ONLY_ACTIVE_PATHS);
        log($"QueryDisplayConfig (active paths) returned 0, {paths.Length} paths, {modes.Length} modes.");
        if (paths.Length != current.Count || placements.Count != current.Count ||
            paths.Select(p => (p.sourceInfo.adapterId, p.sourceInfo.id)).Distinct().Count() != paths.Length)
            throw new DisplayArrangeException("Active paths changed or share a source. Use an extended desktop and retry.");

        var monitors = current.ToDictionary(m => (m.AdapterIdHigh, m.AdapterIdLow, m.TargetId));
        var desired = placements.ToDictionary(p => p.DeviceName, StringComparer.OrdinalIgnoreCase);
        var newModes = new List<DISPLAYCONFIG_MODE_INFO>(modes.Length);
        foreach (ref var path in paths.AsSpan())
        {
            var target = path.targetInfo;
            var source = path.sourceInfo;
            if (!monitors.TryGetValue((target.adapterId.HighPart, target.adapterId.LowPart, target.id), out var monitor) ||
                !MonitorIdentity.Same(DisplayConfig.GetMonitorDevicePath(path), monitor.MonitorDevicePath) ||
                !MonitorIdentity.Same(DisplayConfig.GetSourceGdiName(path), monitor.DeviceName) ||
                !desired.TryGetValue(monitor.DeviceName, out var placement) || placement.StableMonitorId != monitor.StableMonitorId ||
                monitor.SourceAdapterIdHigh != source.adapterId.HighPart || monitor.SourceAdapterIdLow != source.adapterId.LowPart ||
                monitor.SourceId != source.id ||
                source.modeInfoIdx >= modes.Length || modes[source.modeInfoIdx].infoType != DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
                throw new DisplayArrangeException("Active display paths changed while preparing the topology. Retry.");

            var sourceMode = modes[source.modeInfoIdx];
            var desktop = sourceMode.modeInfo.sourceMode;
            bool modeChanged = desktop.width != placement.Width || desktop.height != placement.Height ||
                               target.rotation != placement.Rotation;
            desktop.position = new POINTL { x = placement.X, y = placement.Y };
            desktop.width = (uint)placement.Width;
            desktop.height = (uint)placement.Height;
            sourceMode.modeInfo.sourceMode = desktop;
            path.sourceInfo.modeInfoIdx = (uint)newModes.Count;
            newModes.Add(sourceMode);
            path.targetInfo.rotation = placement.Rotation;
            path.targetInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            if (!modeChanged && target.modeInfoIdx < modes.Length &&
                modes[target.modeInfoIdx].infoType == DISPLAYCONFIG_MODE_INFO_TYPE_TARGET)
            {
                path.targetInfo.modeInfoIdx = (uint)newModes.Count;
                newModes.Add(modes[target.modeInfoIdx]);
            }
            log($"  {monitor.StableMonitorId} {placement}, rotation {placement.Rotation}");
        }

        // DisplayConfig uses path order as priority. Keep the other paths in their live order.
        paths = paths.OrderByDescending(p =>
        {
            var position = newModes[(int)p.sourceInfo.modeInfoIdx].modeInfo.sourceMode.position;
            return position.x == 0 && position.y == 0;
        }).ToArray();

        // Supply all active paths and source modes. For a mode change, Windows chooses the
        // matching target timing. Do not allow Windows to alter the supplied source geometry.
        DISPLAYCONFIG_MODE_INFO[] supplied = newModes.ToArray();
        Set(SDC_VALIDATE);
        Set(SDC_APPLY | SDC_SAVE_TO_DATABASE);

        void Set(uint flags)
        {
            int rc = SetDisplayConfig((uint)paths.Length, paths, (uint)supplied.Length, supplied,
                                      flags | SDC_USE_SUPPLIED_DISPLAY_CONFIG);
            string operation = flags == SDC_VALIDATE ? "validate" : "apply";
            log($"SetDisplayConfig ({operation}) returned {rc}.");
            if (rc != ERROR_SUCCESS)
                throw new DisplayArrangeException($"SetDisplayConfig ({operation}) failed with error {rc}.");
        }
    }

    public static MonitorInfo VerifyPrimary(string stableId, IReadOnlyList<MonitorInfo> monitors)
    {
        MonitorInfo actual = MonitorIdentityResolver.Resolve(stableId, monitors);
        if (!actual.IsPrimary || actual.PositionX != 0 || actual.PositionY != 0 || monitors.Count(m => m.IsPrimary) != 1)
            throw new DisplayArrangeException($"Windows did not confirm primary monitor {stableId} ({actual.DeviceName}) at (0,0).");
        return actual;
    }

    public static IReadOnlyList<MonitorPlacement> SnapshotLayout(IEnumerable<MonitorInfo> monitors)
        => monitors
            .Select(m => new MonitorPlacement(
                m.DeviceName, m.CurrentWidth, m.CurrentHeight, m.PositionX, m.PositionY, m.Rotation, m.IsPrimary, m.StableMonitorId))
            .ToList();
}
