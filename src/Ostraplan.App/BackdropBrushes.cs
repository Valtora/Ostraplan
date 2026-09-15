using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>The backdrop as the canvas needs it: the brush to fill with, and whether it is light enough that the
/// plan's overlays have to be drawn in dark ink instead of the white they have always used.</summary>
/// <param name="Brush">Fills the whole control, before any view transform. Anchored to the screen.</param>
/// <param name="IsLight">Whether the overlays need dark ink.</param>
/// <param name="TileCell">For a backdrop laid on the tile grid, one tile's pattern over a 0..2 square, which the
/// canvas tiles under its own view transform (see <see cref="BackdropBrushes.TileBrush"/>). Null for a backdrop
/// anchored to the screen, which is all of <paramref name="Brush"/>.</param>
/// <param name="TileFar">What <paramref name="TileCell"/> fades to when a tile is too few pixels to show it.</param>
public sealed record BackdropVisual(Brush Brush, bool IsLight, Drawing? TileCell = null, Brush? TileFar = null);

/// <summary>
/// Builds the plan's backdrop brush from a <see cref="BackdropSettings"/>.
///
/// <para>A locale backdrop is composited <b>once</b> into a single image rather than drawn as a stack of layers
/// every frame. The canvas repaints on every pan and zoom, and the heaviest locale (Mars sub-surface) is twenty
/// layers, so twenty draws a frame is a cost the plan should not carry in order to look at scenery. Every stock
/// layer is square, so one square composite holds all of them.</para>
///
/// <para>The consequence is that the layers cannot move relative to each other, so there is no depth scrolling.
/// That is deliberate beyond the performance: the brush is anchored to the viewport, so the backdrop holds still
/// while the ship moves over it and nothing drifts in the corner of the eye during a pan. The game scrolls its
/// layers because the player is flying through the scene. Here the scene is a backdrop to work in front of.</para>
/// </summary>
public sealed class BackdropBrushes(SpriteCache sprites)
{
    /// <summary>The composite's edge, in pixels: the size of the largest layer the stock data ships, so the
    /// biggest art is carried at its own resolution and only the smaller layers are scaled up.</summary>
    private const int TilePx = 1920;

    private readonly Dictionary<(string Locale, int Seed, int Dim), BackdropVisual> _locales = [];

    /// <summary>The brush and ink for <paramref name="settings"/>. Falls back to a flat colour whenever a locale
    /// cannot be drawn, which covers a mod removed since the setting was made and an install whose art is
    /// missing: a backdrop that cannot be found should cost the scenery, never the plan.</summary>
    public BackdropVisual For(BackdropSettings settings, Catalog? catalog)
    {
        settings = settings.Clamped();
        return settings.Kind switch
        {
            BackdropKind.Checker => Checker(settings),
            BackdropKind.TileChecker => TileChecker(settings),
            BackdropKind.Locale when catalog is not null && Locale(settings, catalog) is { } visual => visual,
            _ => Solid(settings.Solid),
        };
    }

    /// <summary>
    /// The brush that lays <paramref name="cell"/> across the plan one tile at a time, starting from the tile
    /// corner at <paramref name="origin"/> (the plan's origin in the coordinates being drawn in, which on the canvas
    /// is its pan) at <paramref name="tilePx"/> pixels a tile. Built per frame, because the pan and zoom are part of
    /// the brush; the drawing inside it is frozen and shared, so a frame costs one small object.
    /// </summary>
    public static Brush TileBrush(Drawing cell, Vector origin, double tilePx) => new DrawingBrush(cell)
    {
        TileMode = TileMode.Tile,
        Viewport = new Rect(origin.X, origin.Y, tilePx, tilePx),
        ViewportUnits = BrushMappingMode.Absolute,
        Stretch = Stretch.Fill,
    };

    private static BackdropVisual Solid(string hex)
    {
        var (r, g, b) = Backdrop.ParseColour(hex) ?? Backdrop.ParseColour(BackdropSettings.DefaultSolid)!.Value;
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return new BackdropVisual(brush, Backdrop.IsLight(r, g, b));
    }

    /// <summary>
    /// A two-colour checkerboard at <see cref="BackdropSettings.CheckerSquare"/> pixels a square, drawn as a
    /// tiled <see cref="DrawingBrush"/> over a 2x2 cell. That is the same shape the out-of-bounds hatching
    /// already uses on this canvas.
    /// </summary>
    private static BackdropVisual Checker(BackdropSettings settings)
    {
        double square = settings.CheckerSquare;
        var brush = new DrawingBrush(CheckerCell(settings))
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 2 * square, 2 * square),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill,
        };
        brush.Freeze();
        return new BackdropVisual(brush, Backdrop.IsLightChecker(settings.Solid, settings.CheckerAlt));
    }

    /// <summary>
    /// The checkerboard on the tile grid (#71): the same 2x2 cell as <see cref="Checker"/>, laid once per tile under
    /// the canvas's view transform rather than across the screen. What fills the control underneath is the first
    /// colour, so a turned view's corners, which the tiled pass does not reach, match the ground rather than show a
    /// seam of something else.
    /// </summary>
    private static BackdropVisual TileChecker(BackdropSettings settings)
    {
        var (fr, fg, fb) = Backdrop.Blend(settings.Solid, settings.CheckerAlt);
        var far = new SolidColorBrush(Color.FromRgb(fr, fg, fb));
        far.Freeze();
        var ground = Solid(settings.Solid).Brush;
        return new BackdropVisual(ground, Backdrop.IsLightChecker(settings.Solid, settings.CheckerAlt),
            CheckerCell(settings), far);
    }

    /// <summary>One cell of either checkerboard over a 0..2 square: the first colour, with the second in the top
    /// right and bottom left.</summary>
    private static Drawing CheckerCell(BackdropSettings settings)
    {
        var (ar, ag, ab) = Backdrop.ParseColour(settings.Solid)!.Value;
        var (br, bg, bb) = Backdrop.ParseColour(settings.CheckerAlt)!.Value;
        var a = new SolidColorBrush(Color.FromRgb(ar, ag, ab));
        var b = new SolidColorBrush(Color.FromRgb(br, bg, bb));

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(a, null, new RectangleGeometry(new Rect(0, 0, 2, 2))));
        group.Children.Add(new GeometryDrawing(b, null, new RectangleGeometry(new Rect(1, 0, 1, 1))));
        group.Children.Add(new GeometryDrawing(b, null, new RectangleGeometry(new Rect(0, 1, 1, 1))));
        group.Freeze();
        return group;
    }

    private BackdropVisual? Locale(BackdropSettings settings, Catalog catalog)
    {
        if (ParallaxCatalog.Find(catalog, settings.Locale) is not { } locale) return null;

        var seed = locale.RepresentativeSeed();
        var key = (locale.Name, seed, (int)Math.Round(settings.LocaleDimming * 100));
        if (_locales.TryGetValue(key, out var hit)) return hit;

        var paths = ParallaxCatalog.ResolveImages(catalog, locale, seed);
        var images = paths.Select(sprites.Image).OfType<BitmapSource>().ToArray();
        if (images.Length == 0) return null;

        var composite = Composite(images, settings.LocaleDimming);
        // Filled once across the canvas rather than tiled. A locale's front layers are single objects, not a
        // pattern: Venus is one planet and Saturn has one set of rings, and a tiled backdrop puts four of them on
        // a wide window. UniformToFill crops the square composite to the canvas instead, which is the one place a
        // starfield's exact framing does not matter.
        var brush = new ImageBrush(composite) { Stretch = Stretch.UniformToFill };
        brush.Freeze();

        var visual = new BackdropVisual(brush, MeanLuminance(composite) > Backdrop.LightThreshold);
        _locales[key] = visual;
        return visual;
    }

    /// <summary>
    /// The layers drawn back to front onto one square, then darkened.
    ///
    /// <para>Each layer covers the whole square rather than repeating at its own native size. The layers are not
    /// all the same size (1920, 960 and 640 all appear), and repeating a 640 one nine times to fill the square
    /// puts a visible grid of seams across every cloud and city-lights backdrop, because those images were drawn
    /// to be seen once and not as a tile. Upscaling costs a little sharpness on a backdrop nobody is reading, and
    /// that is the better half of the trade.</para>
    /// </summary>
    private static BitmapSource Composite(IReadOnlyList<BitmapSource> layers, double dimming)
    {
        var square = new Rect(0, 0, TilePx, TilePx);
        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
        {
            // The ground. Layers are transparent PNGs and the front one may be a lone wreck, so something opaque
            // has to be underneath or the plan shows through its own backdrop.
            ctx.DrawRectangle(Brushes.Black, null, square);

            foreach (var layer in layers) ctx.DrawImage(layer, square);

            if (dimming > 0)
            {
                var veil = new SolidColorBrush(Color.FromArgb((byte)Math.Round(dimming * 255), 0, 0, 0));
                ctx.DrawRectangle(veil, null, square);
            }
        }

        var rtb = new RenderTargetBitmap(TilePx, TilePx, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>Mean relative luminance of a composited backdrop, sampled on a coarse grid rather than per pixel:
    /// this decides one boolean, and reading 3.7 million pixels to decide it is not a trade worth making.</summary>
    private static double MeanLuminance(BitmapSource image)
    {
        const int samples = 64;
        var scaled = new TransformedBitmap(image, new ScaleTransform(
            samples / (double)image.PixelWidth, samples / (double)image.PixelHeight));
        var stride = samples * 4;
        var pixels = new byte[stride * samples];
        var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        converted.CopyPixels(pixels, stride, 0);

        var total = 0.0;
        for (var i = 0; i < pixels.Length; i += 4)
            total += Backdrop.Luminance(pixels[i + 2], pixels[i + 1], pixels[i]);
        return total / (samples * samples);
    }

    /// <summary>Drop every cached composite. Called when the catalogue is reloaded, since a mod may have changed
    /// what a locale's layers are or what art they point at.</summary>
    public void Clear() => _locales.Clear();
}
