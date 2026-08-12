namespace Ostraplan.Core;

/// <summary>
/// How a surface stroke picks between its two brushes, tile by tile. <see cref="Solid"/> uses the primary
/// brush everywhere; the rest alternate with the secondary one. Parity is taken from <b>world</b> tile
/// coordinates rather than the stroke's own origin, so two strokes over neighbouring tiles continue one
/// pattern instead of each restarting it — and a checkerboard survives being painted in several passes.
/// </summary>
public enum SurfacePattern
{
    /// <summary>The primary brush on every tile.</summary>
    Solid,

    /// <summary>Alternating tiles, diagonally offset: the classic checkerboard.</summary>
    Checker,

    /// <summary>Alternating rows (bands running left to right).</summary>
    StripesH,

    /// <summary>Alternating columns (bands running top to bottom).</summary>
    StripesV,
}

/// <summary>
/// What a surface stroke is allowed to do to a tile. The default is <see cref="Replace"/> because the mode is a
/// <b>skinning</b> tool: a box or a checker drag over a room would otherwise spill new deck past the room's
/// irregular edges, and laying new deck is what the mode being off is for.
/// </summary>
public enum SurfacePaintMode
{
    /// <summary>Only re-skin what is already on the tile. A bare tile is left bare.</summary>
    Replace,

    /// <summary>Re-skin what is there, and place on a bare tile. One brush does everything, spills included.</summary>
    ReplaceAndFill,

    /// <summary>Only place on bare tiles, never touch an existing part — the ordinary brush, plus patterns.</summary>
    Fill,
}

/// <summary>
/// Which layer Surfaces mode treats as the subject: what stays bright, and what a click lands on. The wall layer
/// draws over the floor and wins every hit test, so <see cref="Floors"/> is the only way to see or reach the floor
/// <b>under</b> a wall — and the shipped ships floor most of their wall tiles, so on an imported ship that is a
/// large part of the deck. It matters even with the wall standing: a floor's autotile mask reads its four
/// neighbours' conditions, so whether the floor continues under the wall changes how the visible floor beside it
/// draws its edge.
/// </summary>
public enum SurfaceFocus
{
    /// <summary>Walls and floors both bright — the whole deck.</summary>
    Both,

    /// <summary>Floors bright, walls ghosted with everything else: the under-wall view.</summary>
    Floors,

    /// <summary>Walls bright, floors ghosted: a wall run read against a dimmed deck.</summary>
    Walls,
}

/// <summary>
/// Surface painting: treating the walls and floors already on the ship as a canvas, and painting a
/// different skin onto an <b>area</b> of them rather than replacing them by type.
///
/// <para>The ship-wide re-skin (<see cref="ThemeOps"/>) and "Replace with…" (<see cref="ReplaceOps"/>) both
/// answer "change this kind of part into that kind". Neither answers "make these tiles checkerboard" or
/// "armour this run of wall", which is a spatial question and wants a brush. That is what this is: the
/// canvas resolves each tile of a stroke through <see cref="DefAt"/>, and where the tile already holds a
/// part of the brush's own class (<see cref="SwapTargetAt"/>) the stroke <b>re-skins it in place</b> via
/// <see cref="ReplaceOps.BuildSwap"/> instead of being refused by the placement law for landing on an
/// occupied tile. Bare tiles still take an ordinary placement, so one brush both lays new deck and
/// re-skins the deck that is already there.</para>
///
/// <para>Only 1×1 wall and floor skins paint (<see cref="IsSurfaceBrush"/>). That is deliberately narrower
/// than <see cref="IsSurfaceLayer"/>, which asks the different question of what the canvas should keep
/// bright while it ghosts everything else: a 5×1 door is wall-layer structure worth seeing, but it is not
/// the same class as a 1×1 wall, so a wall stroke runs straight past it and leaves it intact.</para>
/// </summary>
public static class SurfacePaint
{
    /// <summary>True for a part that draws as the deck itself — a floor or a wall (doors included). This is the
    /// <b>visibility</b> question the Surfaces view asks, not the paintability one: see <see cref="IsSurfaceBrush"/>.</summary>
    public static bool IsSurfaceLayer(Catalog catalog, PartDef? part) =>
        part is not null && catalog.RenderLayer(part) is Catalog.LayerFloor or Catalog.LayerWall;

    /// <summary>
    /// True for a part the current <paramref name="focus"/> treats as the subject: what stays bright, and what a
    /// click lands on. Everything else ghosts and steps aside, including the wall layer under
    /// <see cref="SurfaceFocus.Floors"/> — which is the whole point of that focus, since a wall otherwise hides and
    /// out-hit-tests the floor beneath it.
    /// </summary>
    public static bool IsFocusLayer(Catalog catalog, PartDef? part, SurfaceFocus focus)
    {
        if (part is null) return false;
        var layer = catalog.RenderLayer(part);
        return focus switch
        {
            SurfaceFocus.Floors => layer == Catalog.LayerFloor,
            SurfaceFocus.Walls => layer == Catalog.LayerWall,
            _ => layer is Catalog.LayerFloor or Catalog.LayerWall,
        };
    }

    /// <summary>
    /// True for a part that can be used as a surface brush: a 1×1 wall or floor skin. The 1×1 bound is what
    /// keeps a stroke honest — a swap is only legal between parts of the same (layer, footprint) class
    /// (<see cref="ReplaceOps"/>), and every wall/floor cooverlay the game ships comes in that one footprint.
    /// Containers are excluded here for the same reason they are in <see cref="ReplaceOps.CommonClass"/>:
    /// an inventory grid does not survive a def-change.
    /// </summary>
    public static bool IsSurfaceBrush(Catalog catalog, PartDef? part) =>
        part is { IsContainer: false, Item.Width: 1, Item.Height: 1 } && IsSurfaceLayer(catalog, part);

    /// <summary>
    /// The placed part a surface stroke would re-skin on this tile: the one sharing <paramref name="brush"/>'s
    /// (render layer, footprint) class. Null when the tile is bare of that class, which is the caller's cue to
    /// place normally instead.
    ///
    /// <para>A locked part (the primary airlock) is returned like any other. It must be: the caller has to know
    /// the tile is spoken for, so it neither re-skins it nor drops a second part on top of it.
    /// <see cref="ReplaceOps.BuildSwap"/> is what declines to touch it.</para>
    /// </summary>
    public static Placement? SwapTargetAt(ShipDocument doc, PartDef brush, int x, int y)
    {
        var cls = (doc.Catalog.RenderLayer(brush), brush.Item.Width, brush.Item.Height);
        foreach (var p in doc.PlacementsAt(x, y))
        {
            var part = doc.Part(p);
            if (part is null || part.IsContainer) continue;
            if ((doc.Catalog.RenderLayer(part), part.Item.Width, part.Item.Height) == cls) return p;
        }
        return null;
    }

    /// <summary>
    /// Which brush this tile takes. Falls back to <paramref name="a"/> whenever there is nothing to alternate
    /// with (Solid, or no secondary brush set), so a pattern left half-configured paints a plain surface
    /// rather than nothing at all.
    /// </summary>
    public static string DefAt(SurfacePattern pattern, string a, string? b, int x, int y)
    {
        if (pattern == SurfacePattern.Solid || string.IsNullOrEmpty(b)) return a;
        // & 1 rather than % 2: it yields 1 for negative coordinates too, so the pattern stays continuous
        // across the origin instead of mirroring about it.
        var second = pattern switch
        {
            SurfacePattern.Checker => ((x + y) & 1) == 1,
            SurfacePattern.StripesH => (y & 1) == 1,
            SurfacePattern.StripesV => (x & 1) == 1,
            _ => false,
        };
        return second ? b! : a;
    }
}
