using System.Text.Json;

namespace Ostraplan.Core;

/// <summary>
/// A part whose texture is a flipbook of frames rather than one picture: the game's <c>JsonItemAnimation</c>
/// (<c>objAnimation</c>), which an item def or a cooverlay skin can carry. <c>ItmHeater02</c> is the first stock
/// part to use one, an 8-frame glow laid out on a 4×4 sheet.
///
/// <para>Ostraplan draws a still, so only the first frame matters. Drawing the whole texture instead puts every
/// frame of the flipbook on the canvas, scaled up to the sheet's full size.</para>
///
/// <para>Ported from <c>Item.SetupAnimation</c>, which runs instead of the sheet and plain-sprite paths in both
/// <c>Item.SetData</c> and <c>Item.SetAlt</c>: the frame is cut out by <c>DataHandler.GetMaterialSheet</c>, and
/// <c>vScale</c> becomes one cell of the sheet rather than the whole texture.</para>
/// </summary>
public sealed record SpriteAnimation(int Columns, int Rows, int FrameCount)
{
    /// <summary>The def's <c>objAnimation</c>, or null when it declares none.</summary>
    public static SpriteAnimation? Parse(JsonElement def) =>
        def.TryGetProperty("objAnimation", out var a) && a.ValueKind == JsonValueKind.Object
            ? new SpriteAnimation(
                Math.Max(1, Json.Int(a, "nSheetColumns", 1)),
                Math.Max(1, Json.Int(a, "nSheetRows", 1)),
                Math.Max(1, Json.Int(a, "nFrameCount", 1)))
            : null;

    /// <summary>
    /// The part's sprite size in tiles, <c>Item.SetupAnimation</c>'s <c>vScale</c>: one cell of the sheet,
    /// <c>max(round(texturePx / cells / 16), 1)</c> per axis.
    /// </summary>
    public (int W, int H) Tiles(int textureW, int textureH) => (
        Math.Max(1, RoundToInt(textureW / (double)Columns / 16.0)),
        Math.Max(1, RoundToInt(textureH / (double)Rows / 16.0)));

    /// <summary>
    /// Where frame 0 sits in the texture, in image pixels with y running down, or null when the texture is too
    /// small to hold any of it.
    ///
    /// <para>Two game routines compose here. <c>Item.ConvertArrayIndexToTopLeftIndex</c> turns frame 0 into
    /// sheet cell <c>(Rows − 1) × Columns</c>, because Unity's texture origin is the bottom-left and the frames run
    /// from the top. <c>DataHandler.GetMaterialSheet</c> then sizes the cell from the <b>footprint</b>
    /// (<c>tiles × 16</c> px, not texture ÷ columns) and steps through the texture
    /// <c>round(textureW / cellW)</c> cells per row. For every stock animation the two cell sizes agree; where a
    /// mod's do not, this reproduces the game's crop rather than tidying it.</para>
    ///
    /// <para>The game may start on any even frame (<c>bRandomStartingFrame</c>). A still has to pick one, and
    /// frame 0 is the one the art is drawn to be read at.</para>
    /// </summary>
    public (int X, int Y, int W, int H)? FirstFrame(int textureW, int textureH, int footprintW, int footprintH)
    {
        if (textureW <= 0 || textureH <= 0) return null;
        var cellW = Math.Max(1, footprintW) * 16;
        var cellH = Math.Max(1, footprintH) * 16;
        var perRow = Math.Max(1, RoundToInt(textureW / (double)cellW));
        var index = (Rows - 1) * Columns;
        var rowFromBottom = index / perRow;
        var col = index % perRow;

        var x = col * cellW;
        var y = textureH - (rowFromBottom + 1) * cellH;
        // Clip to the texture. The game's material would wrap or clamp past the edge; a crop has to stop at it.
        var x0 = Math.Clamp(x, 0, textureW);
        var y0 = Math.Clamp(y, 0, textureH);
        var x1 = Math.Clamp(x + cellW, 0, textureW);
        var y1 = Math.Clamp(y + cellH, 0, textureH);
        return x1 > x0 && y1 > y0 ? (x0, y0, x1 - x0, y1 - y0) : null;
    }

    /// <summary><c>MathUtils.RoundToInt</c>: halves round away from zero, unlike <see cref="Math.Round(double)"/>.</summary>
    private static int RoundToInt(double v) => v >= 0 ? (int)(v + 0.5) : (int)(v - 0.5);
}
