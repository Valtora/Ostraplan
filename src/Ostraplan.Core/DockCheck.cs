namespace Ostraplan.Core;

/// <summary>
/// Everything <c>GridUtils.CreateDockingPortGrid</c> reads off a def: its socket adds (which are where a
/// bulky item's floor vectors come from), the socket grid's column count, and its conditions. Deliberately
/// narrower than <see cref="PartDef"/>/<see cref="ResolvedPart"/> so the grid builder can be driven from
/// either, and from a synthetic test catalog.
/// </summary>
public sealed record DockDef(string Name, string[] SocketAdds, int NCols, IReadOnlySet<string> Conds)
{
    public bool Has(string cond) => Conds.Contains(cond);

    /// <summary>Any of <paramref name="conds"/> — <c>DataCO.HasCond(HashSet&lt;string&gt;)</c>, which is
    /// <c>_conds.Overlaps(conds)</c>.</summary>
    public bool HasAny(IReadOnlySet<string> conds) => Conds.Overlaps(conds);

    /// <summary>Unrotated footprint, derived exactly as <see cref="ItemDef"/> does.</summary>
    public int Width => NCols < 1 ? 1 : NCols;

    /// <inheritdoc cref="Width"/>
    public int Height => SocketAdds.Length == 0 ? 1 : Math.Max(1, SocketAdds.Length / Width);

    /// <summary>
    /// The item's floor vectors at <paramref name="rotation"/>, as offsets from its own centre —
    /// <c>DataCO.GetFloorVectors</c>. Every non-<c>"Blank"</c> socket add contributes one, so this is a
    /// subset of the footprint rather than the whole rectangle, and it is what a <b>bulky</b> item spreads
    /// across in the docking grid. Rotation is CCW, the game's own sense.
    /// </summary>
    public List<(double X, double Y)> FloorVectors(int rotation)
    {
        var list = new List<(double X, double Y)>();
        if (SocketAdds.Length == 0 || NCols < 1) return list;
        var rows = SocketAdds.Length / NCols;
        var cx = (NCols - 1) / 2.0;
        var cy = (rows - 1) / 2.0;
        for (var i = 0; i < SocketAdds.Length; i++)
        {
            if (SocketAdds[i] is null or "Blank") continue;
            var x = i % NCols - cx;
            var y = cy - i / NCols;
            list.Add((rotation % 360) switch
            {
                90 => (-y, x),
                180 => (-x, -y),
                270 => (y, -x),
                _ => (x, y),
            });
        }
        return list;
    }
}

/// <summary>Resolves a placed def name to what the docking grid needs, or null when the def is unknown —
/// the game's <c>DataHandler.GetDataCO(strName)</c>, whose null return makes the loop skip the item.</summary>
public delegate DockDef? DockDefLookup(string defName);

/// <summary>Builds a <see cref="DockDefLookup"/> over real or synthetic data.</summary>
public static class DockDefs
{
    /// <summary>
    /// The catalogue first, then a <see cref="PartResolver"/> over the same install. Both are needed: the
    /// catalogue holds the buildable palette a document is made of, while a ship template names far more
    /// than that (raw hull, ship-special systems, tiles), and only the resolver reaches those. A synthetic
    /// <c>Fixtures</c> catalog carries no <see cref="Catalog.Index"/>, so it resolves through the first
    /// half alone.
    /// </summary>
    public static DockDefLookup For(Catalog catalog)
    {
        var resolver = catalog.Index is { } index ? new PartResolver(index) : null;
        var cache = new Dictionary<string, DockDef?>(StringComparer.Ordinal);
        return name =>
        {
            if (cache.TryGetValue(name, out var hit)) return hit;
            DockDef? def = null;
            if (catalog.Lookup(name) is { } part)
                def = new DockDef(name, part.Item.SocketAdds, part.Item.NCols,
                    new HashSet<string>(part.StartingConds, StringComparer.Ordinal));
            else if (resolver?.Resolve(name) is { } resolved)
                def = new DockDef(name, resolved.Item.SocketAdds, resolved.Item.NCols, resolved.CondSet);
            cache[name] = def;
            return def;
        };
    }
}

/// <summary>
/// One cell of a docking grid — the game's <c>DataCOWrapper</c>, cut down to what
/// <c>GridUtils.AllowedToOverlap</c> and <c>GatherDockingPortData</c> actually read.
///
/// <para><see cref="Origin"/> is the wrapper's <c>OriginX/OriginY</c>: the cell a port mates from. The game
/// sets it only for a <b>bulky</b> item and leaves it at (0,0) otherwise, which is safe here because every
/// docking port carries <c>IsPortal</c> and so every port is bulky.</para>
/// </summary>
public sealed record DockCell(string DefName, bool IsDockSys, string? ItemId, double Rotation, (int X, int Y) Origin)
{
    /// <summary>The game's empty-grid-cell sentinel. <b>Not</b> an absent item: a Blank cell is written into
    /// every empty neighbour of every item, and it collides with anything that is not itself Blank or a
    /// docking port. That halo is what holds two hulls a tile apart.</summary>
    public const string BlankName = "Blank";

    public bool IsBlank => DefName == BlankName;

    public static readonly DockCell Blank = new(BlankName, false, null, 0, (0, 0));
}

/// <summary>
/// A sparse integer grid with the game's own <c>Grid&lt;T&gt;</c> semantics, which matter to the answer:
/// writing past the right or bottom edge <b>grows</b> Width/Height, while writing past the left or top edge
/// stores the cell but leaves the bounds alone, so it can never be read back by the bounds-checked overlay.
/// That is not a detail to tidy up. 218 of the 220 stock templates sit flush against their grid's left and
/// top edges, so a halo column at x = −1 exists and is unreachable, and the game therefore lets another hull
/// come one tile closer on that side than it does on the other three.
///
/// <para>The game's (width, height) constructor also pre-fills every in-range cell with null. That is
/// unobservable here: the only readers skip nulls, and the two methods that would notice
/// (<c>MergeBontoA</c>, <c>NormalizeToPositive</c>) exist to fold <i>already-docked</i> ships into the grid,
/// which neither a design nor a template ever has.</para>
/// </summary>
public sealed class DockGrid(int width, int height)
{
    private readonly Dictionary<(int X, int Y), DockCell> _cells = [];

    public int Width { get; private set; } = width;
    public int Height { get; private set; } = height;

    public IReadOnlyDictionary<(int X, int Y), DockCell> Cells => _cells;

    public DockCell? this[int x, int y]
    {
        get => _cells.GetValueOrDefault((x, y));
        set
        {
            if (value is null) return;
            if (x >= Width) Width = x + 1;
            if (y >= Height) Height = y + 1;
            _cells[(x, y)] = value;
        }
    }
}

/// <summary>One item as the grid builder sees it, in the <b>game's</b> frame: centre (fX,fY) with +y up, and
/// a CCW rotation. <paramref name="Contained"/> is <c>strParentID != null</c>, which the builder skips.</summary>
public sealed record DockItem(string DefName, double FX, double FY, double FRotation, string? ItemId, bool Contained = false);

/// <summary>One of the ship's parts in its own document frame, for drawing it. Kept alongside the grid because
/// the grid is a collision model and cannot be drawn: it holds one cell per item whatever the item's size, and
/// stamps Blank halo cells that are not parts at all.</summary>
/// <param name="W">Unrotated footprint width; <paramref name="Rot"/> has not been applied to it.</param>
public sealed record DockPart(string DefName, int X, int Y, int Rot, int W, int H);

/// <summary>An open docking port: which item it is, where it mates from, and which class it belongs to.</summary>
/// <param name="Anchor">The port's grid cell, the game's <c>DockingPortDTO.GridPos</c>.</param>
/// <param name="DocTile">The port's tile in its ship's document frame.</param>
public sealed record DockPort(
    string ItemId, string DefName, string Friendly, double Rotation, (int X, int Y) Anchor,
    bool TypeB, (int X, int Y) DocTile)
{
    /// <summary>"Secondary" when the port carries <c>IsTypeB</c>, "Primary" otherwise — the two classes the
    /// game's own <c>strNameShort</c> uses. It decides which port bounds construction, and nothing whatever
    /// about docking legality, which is purely geometric.</summary>
    public string Class => TypeB ? "Secondary" : "Primary";
}

/// <summary>
/// A ship reduced to what a docking test needs: the grid its hull occupies and the ports on it.
///
/// <para>Built in the <b>game's</b> grid frame, because that frame is part of the answer (see
/// <see cref="DockGrid"/>). A template keeps its own <c>vShipPos</c>/<c>nCols</c>/<c>nRows</c>; a design gets
/// the bbox-plus-one-tile frame <see cref="ShipExport"/> writes, which is the frame the game will use for
/// that design once it exists.</para>
/// </summary>
public sealed class DockShip
{
    /// <summary>The conditions that make an item spread across its floor vectors rather than occupying a
    /// single cell — <c>GridUtils._bulkyItems</c>. Everything else is one cell whatever its footprint, so a
    /// 1x5 panel is one cell plus its halo.</summary>
    public static readonly IReadOnlySet<string> BulkyConds = new HashSet<string>(StringComparer.Ordinal)
        { "IsPortal", "IsNavStation", "IsHeavyLiftRotor", "IsRCSCluster", "IsShipWeapon" };

    /// <summary>The 8 neighbours every item stamps a Blank halo into — <c>SilhouetteUtility.AllDirectionVectors</c>.</summary>
    private static readonly (int X, int Y)[] AllDirections =
        [(0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1), (1, 0), (1, 1)];

    public required string Name { get; init; }
    public required DockGrid Grid { get; init; }
    public required IReadOnlyList<DockPort> Ports { get; init; }

    /// <summary>
    /// How this ship's docking grid relates to the tile frame its parts are expressed in: the document tile at
    /// grid column 0, the one at grid row <c>NRows</c>, and the row count.
    ///
    /// <para>A design's frame is its document's. A template's is the template's own tile coordinates, which
    /// works out to (0, 0, nRows) because <see cref="ShipGrid.TemplateTile"/> and
    /// <c>GridUtils.CalculateGridOffset</c> both hang off <c>vShipPos</c> — and no shipped template has a
    /// fractional one, which is what makes the two agree exactly rather than to within a tile.</para>
    /// </summary>
    public required (int OriginCol, int OriginRow, int NRows) DocFrame { get; init; }

    /// <summary>The ship's parts in its <see cref="DocFrame"/>, for drawing. Empty when nothing was drawable.</summary>
    public required IReadOnlyList<DockPart> Parts { get; init; }

    /// <summary>
    /// A docking-grid cell back to the tile it came from. The two frames differ by a translation and a y flip,
    /// so one affine map covers every cell rather than only the anchors.
    /// </summary>
    public (int X, int Y) DocTileOf(int gridX, int gridY) =>
        (gridX + DocFrame.OriginCol, DocFrame.OriginRow + DocFrame.NRows - gridY);

    /// <summary>The inverse of <see cref="DocTileOf"/>.</summary>
    public (int X, int Y) GridCellOf(int docX, int docY) =>
        (docX - DocFrame.OriginCol, DocFrame.OriginRow + DocFrame.NRows - docY);

    /// <summary>
    /// Build the docking grid — <c>GridUtils.CreateDockingPortGrid</c>, shallow branch (a design and a
    /// template are both stored data rather than a live ship).
    ///
    /// <para><b>There is no <c>IsInstalled</c> filter.</b> Deck cargo occupies cells and lays its own halo,
    /// so a crate near an airlock refuses a mate. That is the game's rule, and it is the one that makes this
    /// check worth having. A <c>IsMooringPort</c> item would take a branch of its own, and does not need one
    /// here: the game creates mooring ports at dock time, no stock template carries one, and a design cannot
    /// place one.</para>
    /// </summary>
    public static DockGrid BuildGrid(
        IEnumerable<DockItem> items, double vShipPosX, double vShipPosY, int nCols, int nRows, DockDefLookup lookup)
    {
        // CalculateGridOffset: the grid is y-UP with row 0 at the hull's bottom, and it is one cell wider
        // and taller than the ship's declared size.
        var offsetX = ShipGrid.Rnd(0 - vShipPosX);
        var offsetY = ShipGrid.Rnd(nRows - vShipPosY);
        var grid = new DockGrid(nCols + 1, nRows + 1);

        foreach (var item in items)
        {
            if (item.Contained) continue;                       // strParentID != null
            if (lookup(item.DefName) is not { } def) continue;  // DataHandler.GetDataCO returned null

            var x = item.FX + offsetX;
            var y = item.FY + offsetY;
            var xWhole = Math.Abs(x - Math.Floor(x)) < 0.0001;
            var yWhole = Math.Abs(y - Math.Floor(y)) < 0.0001;

            if (def.HasAny(BulkyConds))
            {
                var vectors = FloorVectorGrid(x, y, item.FRotation, def);
                if (vectors.Count == 0) continue;               // IsPlaceholder, or no geometry at all
                var first = vectors[0];
                var cell = new DockCell(def.Name, def.Has("IsDockSys"), item.ItemId, item.FRotation,
                    (Trunc(first.X), Trunc(first.Y)));
                foreach (var v in vectors) grid[Trunc(v.X), Trunc(v.Y)] = cell;
            }
            else
            {
                if (def.Has("IsSystem")) continue;              // skips the halo too, as in the game
                var cell = new DockCell(def.Name, def.Has("IsDockSys"), item.ItemId, item.FRotation, (0, 0));
                if (!xWhole || !yWhole)
                {
                    foreach (var v in GenericFloorVector(x, xWhole, y, yWhole))
                        grid[Trunc(v.X), Trunc(v.Y)] = cell;
                }
                else
                {
                    // An item landing on a cell another already holds is dropped, and its halo with it.
                    if (grid[Trunc(x), Trunc(y)] is not null) continue;
                    grid[Trunc(x), Trunc(y)] = cell;
                }
            }

            // The Blank halo, into every neighbour still empty.
            foreach (var (dx, dy) in AllDirections)
            {
                var nx = Trunc(x + dx);
                var ny = Trunc(y + dy);
                if (grid[nx, ny] is null) grid[nx, ny] = DockCell.Blank;
            }
        }

        return grid;
    }

    /// <summary>
    /// A bulky item's occupied cells, nearest-to-its-own-position first —
    /// <c>SilhouetteUtility.GetFloorVectorGrid</c>. The first entry becomes the port's mating anchor, and for
    /// an airlock it is the door cell. The insert-at-front-on-a-new-minimum is the game's own, and it is why
    /// the first entry is the <i>last</i> strict minimum found rather than simply the nearest.
    /// </summary>
    private static List<(double X, double Y)> FloorVectorGrid(double x, double y, double rotation, DockDef def)
    {
        var list = new List<(double X, double Y)>();
        if (def.Has("IsPlaceholder")) return list;

        var vectors = def.FloorVectors((int)rotation);
        if (vectors.Count == 0)
        {
            list.Add((x, y));
            return list;
        }

        var best = double.MaxValue;
        foreach (var v in vectors)
        {
            var p = (X: v.X + x, Y: v.Y + y);
            var d2 = v.X * v.X + v.Y * v.Y;   // (p − (x,y)).sqrMagnitude
            if (d2 < best) { best = d2; list.Insert(0, p); }
            else list.Add(p);
        }
        return list;
    }

    /// <summary>
    /// Where a non-bulky item sitting on a half coordinate spreads to —
    /// <c>SilhouetteUtility.GetGenericFloorVector</c>. The 2x2 case names one corner twice and omits another;
    /// that is the game's array reproduced, because the omitted corner is a cell that really does stay free
    /// for an incoming hull.
    /// </summary>
    private static (double X, double Y)[] GenericFloorVector(double x, bool xWhole, double y, bool yWhole)
    {
        (double X, double Y)[] offsets =
            !xWhole && yWhole ? [(-0.5, 0), (0.5, 0)]
            : !(!yWhole && xWhole) ? [(-0.5, -0.5), (0.5, -0.5), (0.5, -0.5), (0.5, 0.5)]
            : [(0, -0.5), (0, 0.5)];
        return [.. offsets.Select(o => (o.X + x, o.Y + y))];
    }

    /// <summary>C#'s cast truncates toward zero exactly as the game's does, and the difference from rounding
    /// is load-bearing on the negative side of the origin.</summary>
    private static int Trunc(double d) => (int)d;

    /// <summary>
    /// A parsed ship template, in its own grid frame. This is the faithful path: a template's
    /// <c>vShipPos</c> normally sits flush against its hull's left and top edges, which is what puts a halo
    /// column outside the bounds check (see <see cref="DockGrid"/>). Re-framing it would change the answer.
    /// </summary>
    public static DockShip FromTemplate(ShipTemplate tmpl, Catalog catalog, DockDefLookup lookup)
    {
        var items = tmpl.Items
            .Select(i => new DockItem(i.DefName, i.FX, i.FY, i.FRotation, i.StrID, i.Contained))
            .ToList();
        var grid = BuildGrid(items, tmpl.VShipPosX, tmpl.VShipPosY, tmpl.NCols, tmpl.NRows, lookup);

        // The template's own tile coordinates are this ship's document frame — the same mapping
        // ShipGrid.FromTemplate uses, so a ship read for drawing lands where an imported one would.
        var parts = new List<DockPart>(items.Count);
        var docTiles = new Dictionary<string, (int X, int Y)>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.Contained || lookup(item.DefName) is not { } def) continue;
            var (col, row, rot) = ShipGrid.TemplateTile(
                item.FX, item.FY, item.FRotation, def.Width, def.Height, tmpl.VShipPosX, tmpl.VShipPosY);
            parts.Add(new DockPart(item.DefName, col, row, rot, def.Width, def.Height));
            if (item.ItemId is { } id) docTiles[id] = (col, row);
        }

        return new DockShip
        {
            Name = tmpl.PublicName is { Length: > 0 } name && name != ShipExport.VariedNames ? name : tmpl.Name,
            Grid = grid,
            Ports = PortsOf(items, grid, catalog, lookup, docTiles),
            DocFrame = (0, 0, tmpl.NRows),
            Parts = parts,
        };
    }

    /// <summary>
    /// A live design, in the frame <see cref="ShipExport"/> writes for it: <c>vShipPos</c> at (0,0) and the
    /// grid one tile larger than the bounding box on every side. Structure and deck items alike, converted to
    /// the game's centre-and-CCW form by the same inverse the exporter uses.
    /// </summary>
    public static DockShip FromDocument(ShipDocument doc, Catalog catalog, DockDefLookup lookup, string name)
    {
        if (doc.Bounds() is not { } b)
            return new DockShip
            {
                Name = name, Grid = new DockGrid(1, 1), Ports = [], DocFrame = (0, 0, 1), Parts = [],
            };

        var (originCol, originRow) = (b.MinX - 1, b.MinY - 1);
        var nCols = b.MaxX - b.MinX + 3;
        var nRows = b.MaxY - b.MinY + 3;

        var items = new List<DockItem>();
        var parts = new List<DockPart>();
        var docTiles = new Dictionary<string, (int X, int Y)>(StringComparer.Ordinal);

        void Add(string defName, int x, int y, int rot, string id)
        {
            if (catalog.Lookup(defName) is not { } part) return;
            var (w, h) = GridMath.Size(part.Item.Width, part.Item.Height, rot);
            items.Add(new DockItem(defName,
                x - originCol + (w / 2.0 - 0.5),
                -(y - originRow + (h / 2.0 - 0.5)),
                GridMath.Norm(-rot), id));
            parts.Add(new DockPart(defName, x, y, rot, part.Item.Width, part.Item.Height));
            docTiles[id] = (x, y);
        }

        // ORDER IS PART OF THE ANSWER, so it has to be the order the game will actually read. An item whose
        // cell already holds anything, a neighbour's Blank halo included, is dropped from the grid entirely
        // (see BuildGrid), which makes the whole thing order-dependent. What the game reads is the emitted
        // aItems array, which ShipExport writes in document order, structure first and the deck items after
        // all of them.
        foreach (var p in doc.Placements) Add(p.DefName, p.X, p.Y, p.Rot, p.Id.ToString());
        foreach (var lo in doc.LooseObjects) Add(lo.DefName, lo.X, lo.Y, lo.Rot, lo.Id.ToString());

        var grid = BuildGrid(items, 0, 0, nCols, nRows, lookup);
        return new DockShip
        {
            Name = name,
            Grid = grid,
            Ports = PortsOf(items, grid, catalog, lookup, docTiles),
            DocFrame = (originCol, originRow, nRows),
            Parts = parts,
        };
    }

    /// <summary>
    /// The ship's open docking ports. Membership is <c>TIsDockSysInstalled</c> (<c>IsDockSys</c> +
    /// <c>IsInstalled</c>, the trigger <c>Ship.AddCO</c> files <c>aDockingPorts</c> from), which is narrower
    /// than the bare <c>IsDockSys</c> flag the overlay reads on a cell: a mooring port carries the cond
    /// without being installed and never registers as a port.
    ///
    /// <para>Nothing is dropped as occupied. <c>GetOpenDockingPorts</c> removes the ports of already-docked
    /// ships, and neither a design nor a template has any.</para>
    /// </summary>
    private static IReadOnlyList<DockPort> PortsOf(
        IEnumerable<DockItem> items, DockGrid grid, Catalog catalog, DockDefLookup lookup,
        IReadOnlyDictionary<string, (int X, int Y)> docTiles)
    {
        // GatherDockingPortData reads the anchor off whichever grid cell it meets first. Every cell of one
        // bulky item shares a single wrapper, so the anchor is the same whichever that is.
        var anchors = new Dictionary<string, (int X, int Y)>(StringComparer.Ordinal);
        foreach (var (_, cell) in grid.Cells)
            if (cell.IsDockSys && cell.ItemId is { } id) anchors.TryAdd(id, cell.Origin);

        var ports = new List<DockPort>();
        foreach (var item in items)
        {
            if (item.Contained || item.ItemId is not { } id) continue;
            if (lookup(item.DefName) is not { } def) continue;
            if (!IsInstalledDocksys(def, catalog)) continue;
            if (!anchors.TryGetValue(id, out var anchor)) continue;   // every cell of it lost to another item
            ports.Add(new DockPort(id, item.DefName,
                catalog.Lookup(item.DefName)?.Friendly ?? item.DefName,
                item.FRotation, anchor, def.Has(ProblemScan.TypeBCond),
                docTiles.GetValueOrDefault(id)));
        }
        return ports;
    }

    /// <summary>The <c>TIsDockSysInstalled</c> trigger, read from conditions rather than def names so a port a
    /// player pried open in game is still a port. <see cref="ProblemScan.IsDocksys"/> against a
    /// <see cref="DockDef"/> rather than a <see cref="PartDef"/>.</summary>
    private static bool IsInstalledDocksys(DockDef def, Catalog catalog) =>
        catalog.Triggers.TryGetValue(ProblemScan.DocksysTrigger, out var ct)
        && ct.Reqs.Length > 0
        && ct.Reqs.All(def.Has);
}
