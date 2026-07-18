using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// Frozen, shareable bitmaps for the game's sprites. Sheet cells are cropped
/// with the Unity-to-WPF row flip already applied by Autotile.Cell. Everything
/// returned is Frozen, so it is safe to build on a worker thread.
/// </summary>
public sealed class SpriteCache
{
    private readonly Dictionary<string, BitmapSource?> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Path, int Col, int Row), BitmapSource> _cells = new();
    private readonly Lock _gate = new();

    public BitmapSource Missing { get; } = MakeMissing();

    public BitmapSource Sprite(PartDef part) =>
        (part.SpriteAbs is null ? null : Load(part.SpriteAbs)) ?? Missing;

    /// <summary>
    /// A non-sheet sprite's own size in tiles = round(texturePx / 16), min 1 —
    /// Item.SetData's vScale (Assembly-CSharp). This is the sprite's visual size,
    /// which for the large fuel tanks is smaller than the socket footprint
    /// (nCols x adds/nCols): the tank's 3x3 canister sprite sits centered in a 7x7
    /// footprint whose outer ring is abstracted sub-floor storage, not the tank.
    /// </summary>
    public (int W, int H) SpriteTiles(PartDef part)
    {
        var bmp = Sprite(part);
        return (Math.Max(1, (int)Math.Round(bmp.PixelWidth / 16.0)),
                Math.Max(1, (int)Math.Round(bmp.PixelHeight / 16.0)));
    }

    /// <summary>Cell size in px follows GetMaterialSheet: footprint tiles x 16.</summary>
    public (int Cols, int Rows) SheetDims(PartDef part)
    {
        var bmp = Sprite(part);
        var cw = Math.Max(1, part.Item.Width * 16);
        var ch = Math.Max(1, part.Item.Height * 16);
        return (Math.Max(1, bmp.PixelWidth / cw), Math.Max(1, bmp.PixelHeight / ch));
    }

    public BitmapSource SheetCell(PartDef part, int col, int rowFromTop)
    {
        if (part.SpriteAbs is null) return Missing;
        var key = (part.SpriteAbs, col, rowFromTop);
        lock (_gate)
        {
            if (_cells.TryGetValue(key, out var hit)) return hit;
        }

        var bmp = Sprite(part);
        var cw = Math.Max(1, part.Item.Width * 16);
        var ch = Math.Max(1, part.Item.Height * 16);
        var rect = new Int32Rect(col * cw, rowFromTop * ch, cw, ch);
        if (rect.X + rect.Width > bmp.PixelWidth || rect.Y + rect.Height > bmp.PixelHeight)
            return Missing;

        var cell = new CroppedBitmap(bmp, rect);
        cell.Freeze();
        lock (_gate) { _cells[key] = cell; }
        return cell;
    }

    /// <summary>Palette thumbnail: the isolated (no-neighbor) variant for sheet items.</summary>
    public BitmapSource Thumb(PartDef part)
    {
        if (!part.Item.HasSpriteSheet) return Sprite(part);
        var (cols, rows) = SheetDims(part);
        var (col, row) = Autotile.Cell(Autotile.Mask(false, false, false, false), cols, rows);
        return SheetCell(part, col, row);
    }

    // ---- normal maps (Light Viz) ----
    //
    // The game converts each strImgNorm PNG through ShaderSetup.NormalPNGtoDXTnm (x ← png.r, y ← 1−png.g) and the
    // LoSPass shader decodes n.xy = 2c−1 with z forced to 1. Net, in DOCUMENT convention (+y down): nx = 2·r−1,
    // ny = 2·g−1 — the raw channels, both flips cancelling. A part drawn rotated has its PIXELS rotated by the
    // canvas transform, so the embedded VECTORS are pre-rotated here by swizzling channels per 90° step
    // (rotating (nx, ny) by 90° CW in doc space: (nx, ny) → (−ny, nx) → r' = 255−g, g' = r).

    private readonly Dictionary<(string Path, int Rot), BitmapSource?> _norms = new();
    private readonly Dictionary<(string Path, int Rot, int Col, int Row), BitmapSource> _normCells = new();

    /// <summary>The part's normal-map sprite with its vectors pre-rotated for <paramref name="rot"/> (the canvas
    /// rotates the pixels), or null when the item has no normal map on disk (drawn as flat).</summary>
    public BitmapSource? NormalSprite(PartDef part, int rot) =>
        part.SpriteNormAbs is null ? null : LoadNorm(part.SpriteNormAbs, GridMath.Norm(rot));

    /// <summary>Sheet-cell crop of the normal map (sheet items never rotate), or null when absent. The cell
    /// rect comes from the ALBEDO footprint (the game samples _BumpMap with the albedo UVs).</summary>
    public BitmapSource? NormalSheetCell(PartDef part, int col, int rowFromTop)
    {
        if (part.SpriteNormAbs is null) return null;
        var key = (part.SpriteNormAbs, 0, col, rowFromTop);
        lock (_gate)
        {
            if (_normCells.TryGetValue(key, out var hit)) return hit;
        }
        var bmp = LoadNorm(part.SpriteNormAbs, 0);
        if (bmp is null) return null;
        var cw = Math.Max(1, part.Item.Width * 16);
        var ch = Math.Max(1, part.Item.Height * 16);
        var rect = new Int32Rect(col * cw, rowFromTop * ch, cw, ch);
        if (rect.X + rect.Width > bmp.PixelWidth || rect.Y + rect.Height > bmp.PixelHeight) return null;
        var cell = new CroppedBitmap(bmp, rect);
        cell.Freeze();
        lock (_gate) { _normCells[key] = cell; }
        return cell;
    }

    private BitmapSource? LoadNorm(string absPath, int rot)
    {
        lock (_gate)
        {
            if (_norms.TryGetValue((absPath, rot), out var hit)) return hit;
        }
        BitmapSource? result = null;
        if (Load(absPath) is { } src)
        {
            var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var px = new byte[w * h * 4];
            bgra.CopyPixels(px, w * 4, 0);
            for (var i = 0; i < px.Length; i += 4)
            {
                byte r = px[i + 2], g = px[i + 1];
                (px[i + 2], px[i + 1]) = rot switch
                {
                    90 => ((byte)(255 - g), r),
                    180 => ((byte)(255 - r), (byte)(255 - g)),
                    270 => (g, (byte)(255 - r)),
                    _ => (r, g),
                };
                px[i] = 255;
            }
            var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            wb.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
            wb.Freeze();
            result = wb;
        }
        lock (_gate) { _norms[(absPath, rot)] = result; }
        return result;
    }

    /// <summary>A glow decal's decoded pixels (plain BGRA32) for the light compositor, rotated CW by
    /// <paramref name="rot"/> like the game rotates the quad with its parent item (a wall lamp's glow bar lies
    /// along its wall). Null when the PNG is missing/unreadable.</summary>
    public (int W, int H, byte[] Bgra)? GlowPixels(string absPath, int rot = 0)
    {
        if (Load(absPath) is not { } src) return null;
        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);
        switch (GridMath.Norm(rot))
        {
            case 90:  return (h, w, RotatePixels(px, w, h, (x, y) => (h - 1 - y, x), h));
            case 180: return (w, h, RotatePixels(px, w, h, (x, y) => (w - 1 - x, h - 1 - y), w));
            case 270: return (h, w, RotatePixels(px, w, h, (x, y) => (y, w - 1 - x), h));
            default:  return (w, h, px);
        }
    }

    private static byte[] RotatePixels(byte[] src, int w, int h, Func<int, int, (int X, int Y)> map, int dstW)
    {
        var dst = new byte[src.Length];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (dx, dy) = map(x, y);
                Array.Copy(src, (y * w + x) * 4, dst, (dy * dstW + dx) * 4, 4);
            }
        return dst;
    }

    private BitmapSource? Load(string absPath)
    {
        lock (_gate)
        {
            if (_byPath.TryGetValue(absPath, out var hit)) return hit;
        }

        BitmapSource? bmp = null;
        try
        {
            if (File.Exists(absPath))
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.UriSource = new Uri(absPath, UriKind.Absolute);
                img.EndInit();
                img.Freeze();
                bmp = img;
            }
        }
        catch { /* unreadable png -> Missing placeholder */ }

        lock (_gate) { _byPath[absPath] = bmp; }
        return bmp;
    }

    private static BitmapSource MakeMissing()
    {
        // 16x16 magenta/black checker, the classic "sprite not found"
        var wb = new WriteableBitmap(16, 16, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new uint[16 * 16];
        for (var y = 0; y < 16; y++)
            for (var x = 0; x < 16; x++)
                pixels[y * 16 + x] = ((x / 8 + y / 8) % 2 == 0) ? 0xFFFF00FFu : 0xFF140014u;
        wb.WritePixels(new Int32Rect(0, 0, 16, 16), pixels, 16 * 4, 0);
        wb.Freeze();
        return wb;
    }
}
