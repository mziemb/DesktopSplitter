namespace DesktopSplitter.Models;

/// <summary>Keep the virtual group clear of existing screens without moving physical screens.</summary>
public static class VirtualPlacementPolicy
{
    public static bool Overlaps(PixelRect a, PixelRect b)
        => a.X < b.X + b.W && a.X + a.W > b.X && a.Y < b.Y + b.H && a.Y + a.H > b.Y;
    public static (int X, int Y) Offset(IReadOnlyList<PixelRect> proposed, IReadOnlyList<PixelRect> existing)
    {
        if (proposed.Count == 0 || existing.Count == 0 || !proposed.Any(v => existing.Any(p => Overlaps(v, p)))) return (0, 0);
        int right = existing.Max(p => checked(p.X + p.W));
        int top = proposed.Min(p => p.Y), bottom = proposed.Max(p => checked(p.Y + p.H));
        var anchors = existing.Where(p => p.X + p.W == right).OrderBy(p => p.Y).ToList();
        bool adjacent = anchors.Any(p => top < p.Y + p.H && bottom > p.Y);
        // A collision-free group still needs an edge connection to the desktop.
        int dy = adjacent ? 0 : checked(anchors[0].Y - top);
        return (checked(right - proposed.Min(v => v.X)), dy);
    }
}
