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
