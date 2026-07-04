namespace Ostraplan.Core;

/// <summary>
/// The game's wall/floor sprite-sheet selection, ported exactly:
///  - mask bits from the 4 cardinal neighbors whose tile conds trigger the
///    item's ctSpriteSheet: N=8, W=4, E=2, S=1        (Item.SetSpriteSheetIndex)
///  - mask -> sheet cell via the fixed 16-entry table  (Item.SpriteSheetIndices)
///  - the cell index counts rows from the texture BOTTOM (Unity UV origin -
///    DataHandler.GetMaterialSheet), so WPF flips the row.
/// </summary>
public static class Autotile
{
    private static readonly Dictionary<int, int> MaskToCell = new()
    {
        { 3, 12 }, { 7, 13 }, { 5, 14 }, { 8, 15 },
        { 11, 8 }, { 15, 9 }, { 13, 10 }, { 2, 11 },
        { 10, 4 }, { 14, 5 }, { 12, 6 }, { 4, 7 },
        { 6, 0 }, { 0, 1 }, { 9, 2 }, { 1, 3 },
    };

    public static int Mask(bool north, bool west, bool east, bool south) =>
        (north ? 8 : 0) | (west ? 4 : 0) | (east ? 2 : 0) | (south ? 1 : 0);

    /// <summary>Sheet cell as (column, row-from-top) for a cols-by-rows sheet.</summary>
    public static (int Col, int RowFromTop) Cell(int mask, int cols, int rows)
    {
        var v = MaskToCell[mask & 15];
        if (v >= cols * rows) v %= cols * rows;   // non-4x4 sheets: stay in bounds
        return (v % cols, rows - 1 - v / cols);
    }

    /// <summary>Mask for the tile at (x, y) given its ship's tile conditions.</summary>
    public static int MaskAt(TileConds conds, string ctName, int x, int y) => Mask(
        conds.TriggeredByName(ctName, x, y - 1),
        conds.TriggeredByName(ctName, x - 1, y),
        conds.TriggeredByName(ctName, x + 1, y),
        conds.TriggeredByName(ctName, x, y + 1));
}
