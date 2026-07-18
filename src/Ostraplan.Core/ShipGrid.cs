namespace Ostraplan.Core;

/// <summary>An item resolved onto the grid: its geometry/conditions, world centre,
/// rotation, footprint top-left tile, and anchor (centre) tile index.</summary>
public sealed record PlacedPart(
    ResolvedPart Part, string? StrID, double CX, double CY, int Rot,
    int TopLeftCol, int TopLeftRow, int AnchorIndex);

/// <summary>
/// A headless ship as the analysis engine sees it: a fixed nCols×nRows tile grid
/// with accumulated per-tile conditions, plus the placed parts. Built from a
/// <see cref="ShipTemplate"/> (parity/import) — and, later, from a live document.
///
/// <para>Coordinate model, ported exactly (CondOwner.TLTileCoords, Ship.UpdateTiles,
/// Ship.GetTileIndexAtWorldCoords, verified 0.15.1.6): an item's stored (fX,fY) is
/// its footprint <b>centre</b>. Top-left tile world coords =
/// (fX − (W/2 − 0.5), fY + (H/2 − 0.5)) using the <b>rotated</b> footprint W×H; tile
/// (col,row) with col = round(worldX − vShipPos.x), row = −round(worldY − vShipPos.y);
/// index = col + row·nCols. Socket adds fill the footprint row-major from the
/// top-left, exactly as <see cref="TileConds"/> lays them.</para>
/// </summary>
public sealed class ShipGrid
{
    public int NCols { get; }
    public int NRows { get; }
    public double VShipPosX { get; }
    public double VShipPosY { get; }
    public TileConds Conds { get; }
    public IReadOnlyList<PlacedPart> Parts { get; }

    private ShipGrid(int nCols, int nRows, double vx, double vy, TileConds conds, IReadOnlyList<PlacedPart> parts)
    {
        NCols = nCols;
        NRows = nRows;
        VShipPosX = vx;
        VShipPosY = vy;
        Conds = conds;
        Parts = parts;
    }

    /// <summary>World coords (game +y up) → tile index, or -1 off-grid — Ship.GetTileIndexAtWorldCoords.</summary>
    public int TileAtWorld(double worldX, double worldY)
    {
        var col = Rnd(worldX - VShipPosX);
        var row = -Rnd(worldY - VShipPosY);
        return InBounds(col, row) ? Index(col, row) : -1;
    }

    public int TileCount => NCols * NRows;
    public int Index(int col, int row) => col + row * NCols;
    public int Col(int index) => index % NCols;
    public int Row(int index) => index / NCols;
    public bool InBounds(int col, int row) => col >= 0 && col < NCols && row >= 0 && row < NRows;

    public IReadOnlyDictionary<string, double>? CondsAt(int index) =>
        Conds.At(Col(index), Row(index));

    public bool Has(int index, string cond) =>
        CondsAt(index) is { } c && c.ContainsKey(cond);

    /// <summary>
    /// Game rotation (fRotation, transform Z euler = CCW degrees) → Ostraplan Rot
    /// in {0,90,180,270}. <see cref="GridMath.Rotate"/> is CW, so the sign inverts —
    /// verified against the Babak's rotated docksys/tow/cargo parts: CCW makes every
    /// solid-wall tile land on the stored room complement exactly (0 miss / 0 extra),
    /// CW misplaces the asymmetric 90/270 patterns. (Only 90↔270 differ; 0/180 are
    /// direction-agnostic.) P3 export must apply the inverse mapping.
    /// </summary>
    public static int ToRot(double fRotation) => GridMath.Norm(-(int)Math.Round(fRotation / 90.0) * 90);

    internal static int Rnd(double d) => (int)Math.Round(d, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The game→grid coordinate mapping (<c>CondOwner.TLTileCoords</c> + <c>GetTileIndexAtWorldCoords</c>):
    /// a stored item's centre <c>(fX,fY)</c> and CCW <c>fRotation</c>, with a footprint <paramref name="w"/>×<paramref name="h"/>,
    /// become the top-left tile <c>(col,row)</c> and Ostraplan <c>rot</c> in a grid whose tile (0,0) sits at
    /// <c>(vShipPosX, vShipPosY)</c>. Shared by <see cref="FromTemplate"/> (parity), <c>TemplateImport</c>, and the
    /// export round-trip so all three use one mapping. (Export applies the exact inverse — see <see cref="ShipExport"/>.)
    /// </summary>
    public static (int Col, int Row, int Rot) TemplateTile(
        double fX, double fY, double fRotation, int w, int h, double vShipPosX, double vShipPosY)
    {
        var rot = ToRot(fRotation);
        var (wr, hr) = GridMath.Size(w, h, rot);
        return (Rnd(fX - (wr / 2.0 - 0.5) - vShipPosX), -Rnd(fY + (hr / 2.0 - 0.5) - vShipPosY), rot);
    }

    /// <summary>
    /// Build a grid from a template. Unresolvable item defs (missing geometry) are
    /// skipped and named in <paramref name="warnings"/>, mirroring the game dropping
    /// an item whose def failed to load.
    /// </summary>
    public static ShipGrid FromTemplate(ShipTemplate tmpl, PartResolver resolver, Catalog catalog, List<string>? warnings = null)
    {
        var conds = new TileConds(catalog);
        var parts = new List<PlacedPart>(tmpl.Items.Count);

        foreach (var item in tmpl.Items)
        {
            var resolved = resolver.Resolve(item.DefName);
            if (resolved is null)
            {
                warnings?.Add($"{tmpl.Name}: item def '{item.DefName}' has no resolvable geometry — skipped.");
                continue;
            }

            var (topLeftCol, topLeftRow, rot) = TemplateTile(
                item.FX, item.FY, item.FRotation, resolved.Item.Width, resolved.Item.Height, tmpl.VShipPosX, tmpl.VShipPosY);

            var anchorCol = Rnd(item.FX - tmpl.VShipPosX);
            var anchorRow = -Rnd(item.FY - tmpl.VShipPosY);
            var anchorIndex = anchorCol >= 0 && anchorCol < tmpl.NCols && anchorRow >= 0 && anchorRow < tmpl.NRows
                ? anchorCol + anchorRow * tmpl.NCols
                : -1;

            conds.Apply(new Placement { DefName = item.DefName, X = topLeftCol, Y = topLeftRow, Rot = rot },
                resolved.Item, +1);

            parts.Add(new PlacedPart(resolved, item.StrID, item.FX, item.FY, rot, topLeftCol, topLeftRow, anchorIndex));
        }

        return new ShipGrid(tmpl.NCols, tmpl.NRows, tmpl.VShipPosX, tmpl.VShipPosY, conds, parts);
    }

    /// <summary>
    /// Build an analysis grid from a live document. Placement X/Y are already top-left
    /// tile coordinates, so this just offsets them to a 0-based grid with a one-tile
    /// exterior margin (so the surrounding void room can form) and reuses each part's
    /// resolved <see cref="PartDef"/>. The anchor is the footprint centre tile.
    /// </summary>
    public static ShipGrid FromDocument(ShipDocument doc, Catalog catalog)
    {
        if (doc.Bounds() is not { } b)
            return new ShipGrid(1, 1, 0, 0, new TileConds(catalog), []);

        const int pad = 1;
        return FromDocumentFramed(doc, catalog, b.MinX - pad, b.MinY - pad,
            (b.MaxX - b.MinX + 1) + 2 * pad, (b.MaxY - b.MinY + 1) + 2 * pad);
    }

    /// <summary>
    /// Build an analysis grid from a live document in an <b>explicit</b> frame: document tile
    /// (<paramref name="originCol"/>, <paramref name="originRow"/>) maps to grid (0,0), and the grid is
    /// <paramref name="nCols"/>×<paramref name="nRows"/>. Save-edit inject uses this to compute rooms/rating in
    /// the injected ship's own grid — the original size (so grid-relative indices stay put) or grown to fit
    /// new parts — rather than the bounds-plus-pad frame <see cref="FromDocument"/> picks. VShipPos carries
    /// the document-coord origin, as in FromDocument; room building ignores it.
    /// </summary>
    public static ShipGrid FromDocumentFramed(ShipDocument doc, Catalog catalog, int originCol, int originRow, int nCols, int nRows)
    {
        var conds = new TileConds(catalog);
        var parts = new List<PlacedPart>();
        foreach (var p in doc.Placements)
        {
            if (doc.Part(p) is not { } part) continue;
            var gx = p.X - originCol;
            var gy = p.Y - originRow;
            conds.Apply(new Placement { DefName = p.DefName, X = gx, Y = gy, Rot = p.Rot }, part.Item, +1);

            var (w, h) = GridMath.Size(part.Item.Width, part.Item.Height, p.Rot);
            var ac = gx + w / 2;
            var ar = gy + h / 2;
            var anchorIndex = ac >= 0 && ac < nCols && ar >= 0 && ar < nRows ? ac + ar * nCols : -1;

            var resolved = new ResolvedPart(part.DefName, part.Friendly, part.Item,
                part.StartingConds, part.StartingCondValues, part.MapPoints);
            parts.Add(new PlacedPart(resolved, p.Id.ToString(), 0, 0, p.Rot, gx, gy, anchorIndex));
        }

        // VShipPos doubles as the grid origin in document coords (position of tile 0,0)
        return new ShipGrid(nCols, nRows, originCol, originRow, conds, parts);
    }

    /// <summary>Map a grid tile index back to document tile coords (grid origin = VShipPos).</summary>
    public (int X, int Y) GridToDoc(int index) => (Col(index) + (int)VShipPosX, Row(index) + (int)VShipPosY);

    /// <summary>
    /// The grid tile a part's map point lands on, or −1 off-grid — the game's
    /// <c>CondOwner.GetPos(point)</c> → <c>GetTileIndexAtWorldCoords</c> chain: the point is
    /// pixels around the item centre (+y up), rotated with the part (<see cref="GridMath.MapPoint"/>),
    /// and the world coordinate rounds away-from-zero onto a tile (<c>MathUtils.RoundToInt</c>,
    /// same as <see cref="Rnd"/>).
    /// </summary>
    public int MapPointTile(PlacedPart part, (double X, double Y) px)
    {
        var (tx, ty) = GridMath.MapPoint(px, part.Part.Item.Width, part.Part.Item.Height, part.Rot);
        var col = Rnd(part.TopLeftCol + tx - 0.5);
        var row = Rnd(part.TopLeftRow + ty - 0.5);
        return InBounds(col, row) ? Index(col, row) : -1;
    }

    /// <summary>
    /// The <b>continuous</b> grid position a part's map point lands on (tile units, cell (c,r)'s centre at
    /// (c+0.5, r+0.5)) — the sub-tile form of <see cref="MapPointTile"/>, which just rounds this to a tile.
    /// Light Viz places each light here rather than at a tile centre so its occlusion has a facing (a wall light
    /// offset into its room shadows the far side). Not bounds-checked; a point may sit off-grid.
    /// </summary>
    public (double Col, double Row) MapPointPos(PlacedPart part, (double X, double Y) px)
    {
        var (tx, ty) = GridMath.MapPoint(px, part.Part.Item.Width, part.Part.Item.Height, part.Rot);
        return (part.TopLeftCol + tx, part.TopLeftRow + ty);
    }
}
