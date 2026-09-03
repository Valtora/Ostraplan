using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// The nav modules' art as bitmaps: a <see cref="NavModScene"/> drawn with WPF at whatever size the arrange board
/// shows the module, memoised per module and size. One of these is built off-thread once the game data has loaded
/// (<see cref="Build"/>), which is where the file is read, the sprites become frozen bitmaps and the fonts are
/// installed; the drawing itself happens on the UI thread the first time a module is shown, since a
/// <see cref="RenderTargetBitmap"/> belongs to the thread that renders it.
///
/// <para>It is a picture of the prefab, not a screen. A label is laid out the way TextMeshPro would at rest (its
/// auto-size range, its alignment, wrapped to its rect) in the game's own face, a sliced sprite is drawn in nine
/// pieces, and a tint multiplies the sprite the way the engine does. Nothing here knows about the ship.</para>
/// </summary>
public sealed class NavModArtCache
{
    private readonly NavModArtPack _pack;
    private readonly Dictionary<int, BitmapSource> _sprites = new();
    private readonly Dictionary<(int Id, uint Tint), BitmapSource> _tinted = new();
    private readonly Dictionary<(int Id, uint Tint, int Piece), CroppedBitmap> _pieces = new();
    private readonly Dictionary<uint, SolidColorBrush> _brushes = new();
    private readonly Dictionary<string, Typeface> _fonts;
    private readonly Dictionary<(string Key, int W, int H), BitmapSource> _renders = new();
    private readonly Lock _gate = new();

    /// <summary>The face a label falls back to when its own could not be installed. Segoe is close to Noto Sans,
    /// which is what most of the labels use.</summary>
    private static readonly FontFamily Fallback = new("Segoe UI");

    private NavModArtCache(NavModArtPack pack, Dictionary<string, Typeface> fonts)
    {
        _pack = pack;
        _fonts = fonts;
    }

    public int ModuleCount => _pack.Scenes.Count;
    public string? UnityVersion => _pack.UnityVersion;
    public IReadOnlyList<string> Notes => _pack.Notes;

    /// <summary>The board's own colour, for drawing behind the modules.</summary>
    public static Color BoardColour => ToColor(NavModArt.BoardRgba);

    /// <summary>Read the art out of the install and get it ready to draw. Safe off the UI thread: every bitmap it
    /// makes is frozen. Null, with the reason, when the art is not available. The catalogue supplies the size the
    /// console gives each module, which is the size its prefab is laid out at.</summary>
    public static NavModArtCache? Build(GameEnv env, Catalog catalog, out string? problem)
    {
        var pack = NavModArt.Build(env, NavConsole.ScreenSizes(catalog));
        if (!pack.Ok)
        {
            problem = pack.Problem;
            return null;
        }
        problem = null;
        return new NavModArtCache(pack, InstallFonts(pack.Fonts));
    }

    public bool Has(string key) => _pack.Scenes.ContainsKey(key);

    /// <summary>
    /// The module at a pixel size. <paramref name="scale"/> is how many of those pixels one of the game's canvas
    /// units is (the board's pixel height over <see cref="NavModScene.ReferenceBoardHeight"/>), which sizes the
    /// labels and the sliced borders. UI thread only.
    /// </summary>
    public BitmapSource? Render(string key, int pixelW, int pixelH, double scale)
    {
        if (pixelW < 1 || pixelH < 1 || _pack.Scene(key) is not { } scene) return null;
        var id = (key, pixelW, pixelH);
        if (_renders.TryGetValue(id, out var hit)) return hit;

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
            foreach (var op in scene.Ops)
                Draw(dc, op, pixelW, pixelH, scale);

        var bmp = new RenderTargetBitmap(pixelW, pixelH, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        _renders[id] = bmp;
        return bmp;
    }

    private void Draw(DrawingContext dc, NavModOp op, int w, int h, double scale)
    {
        var r = new Rect(op.Rect.X * w, op.Rect.Y * h, Math.Max(0, op.Rect.W * w), Math.Max(0, op.Rect.H * h));
        if (r.Width < 0.5 || r.Height < 0.5) return;

        // A turned or flipped piece: its box is the bounds of the turn, so the content is drawn the other way
        // up (or round) in a rect of the pre-turn size about the same centre, under the transform that maps it
        // onto the box. The orient is orthonormal, so the mapped content covers the box exactly.
        var o = op.Orient;
        var pushed = !o.IsIdentity;
        if (pushed)
        {
            var content = o.Swaps ? new Size(r.Height, r.Width) : r.Size;
            var centre = new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
            var m = new Matrix(o.A, o.B, o.C, o.D, 0, 0);
            m.Translate(centre.X - (o.A * centre.X + o.C * centre.Y), centre.Y - (o.B * centre.X + o.D * centre.Y));
            dc.PushTransform(new MatrixTransform(m));
            r = new Rect(centre.X - content.Width / 2, centre.Y - content.Height / 2, content.Width, content.Height);
        }

        switch (op)
        {
            case NavModFill fill:
                dc.DrawRectangle(Brush(fill.Rgba), null, r);
                break;
            case NavModSpriteOp sprite when sprite.SpriteId >= 0 && sprite.SpriteId < _pack.Sprites.Count:
                DrawSprite(dc, sprite, r, scale);
                break;
            case NavModTextOp text:
                DrawText(dc, text, r, scale);
                break;
        }
        if (pushed) dc.Pop();
    }

    // ---- sprites ----

    private void DrawSprite(DrawingContext dc, NavModSpriteOp op, Rect r, double scale)
    {
        var sprite = _pack.Sprites[op.SpriteId];
        var bmp = Tinted(op.SpriteId, op.Tint);
        if (!op.Sliced || sprite.Border.IsEmpty)
        {
            if (op.PreserveAspect && sprite.Width > 0 && sprite.Height > 0)
            {
                // letterboxed in the rect about its centre, which is Image.preserveAspect
                var k = Math.Min(r.Width / sprite.Width, r.Height / sprite.Height);
                double w = sprite.Width * k, h = sprite.Height * k;
                r = new Rect(r.Left + (r.Width - w) / 2, r.Top + (r.Height - h) / 2, w, h);
            }
            dc.DrawImage(bmp, r);
            return;
        }

        // The border is in sprite pixels; on screen it covers UnitsPerPixel canvas units per pixel, and a canvas
        // unit is `scale` of our pixels, so it scales with the labels. When the rect is smaller than its own
        // borders the corners shrink to share it, which is what Unity does.
        var b = sprite.Border;
        var px = op.UnitsPerPixel * scale;
        double left = b.Left * px, right = b.Right * px, top = b.Top * px, bottom = b.Bottom * px;
        if (left + right > r.Width) { var k = r.Width / (left + right); left *= k; right *= k; }
        if (top + bottom > r.Height) { var k = r.Height / (top + bottom); top *= k; bottom *= k; }

        double[] sx = [0, b.Left, sprite.Width - b.Right, sprite.Width];
        double[] sy = [0, b.Top, sprite.Height - b.Bottom, sprite.Height];
        double[] dx = [r.Left, r.Left + left, r.Right - right, r.Right];
        double[] dy = [r.Top, r.Top + top, r.Bottom - bottom, r.Bottom];
        for (var col = 0; col < 3; col++)
            for (var row = 0; row < 3; row++)
            {
                int sw = (int)(sx[col + 1] - sx[col]), sh = (int)(sy[row + 1] - sy[row]);
                double dw = dx[col + 1] - dx[col], dh = dy[row + 1] - dy[row];
                // a border wider than its sprite, or a rect the borders exactly fill, leaves a piece with nothing
                // to draw (the clamp above can land a hair below zero in floating point)
                if (sw <= 0 || sh <= 0 || dw <= 0.01 || dh <= 0.01) continue;
                var src = new Int32Rect((int)sx[col], (int)sy[row], sw, sh);
                dc.DrawImage(Piece(op.SpriteId, op.Tint, row * 3 + col, bmp, src), new Rect(dx[col], dy[row], dw, dh));
            }
    }

    private CroppedBitmap Piece(int id, uint tint, int piece, BitmapSource whole, Int32Rect src)
    {
        var key = (id, tint, piece);
        if (_pieces.TryGetValue(key, out var hit)) return hit;
        var crop = new CroppedBitmap(whole, src);
        crop.Freeze();
        _pieces[key] = crop;
        return crop;
    }

    private BitmapSource Tinted(int id, uint tint)
    {
        if (tint == 0xFFFFFFFF) return Untinted(id);
        lock (_gate)
        {
            if (_tinted.TryGetValue((id, tint), out var hit)) return hit;
            var sprite = _pack.Sprites[id];
            var px = (byte[])sprite.Bgra.Clone();
            int r = (int)(tint >> 24) & 0xFF, g = (int)(tint >> 16) & 0xFF, b = (int)(tint >> 8) & 0xFF, a = (int)tint & 0xFF;
            for (var i = 0; i < px.Length; i += 4)
            {
                px[i] = (byte)(px[i] * b / 255);
                px[i + 1] = (byte)(px[i + 1] * g / 255);
                px[i + 2] = (byte)(px[i + 2] * r / 255);
                px[i + 3] = (byte)(px[i + 3] * a / 255);
            }
            var bmp = FromBgra(sprite.Width, sprite.Height, px);
            _tinted[(id, tint)] = bmp;
            return bmp;
        }
    }

    private BitmapSource Untinted(int id)
    {
        lock (_gate)
        {
            if (_sprites.TryGetValue(id, out var hit)) return hit;
            var sprite = _pack.Sprites[id];
            var bmp = FromBgra(sprite.Width, sprite.Height, sprite.Bgra);
            _sprites[id] = bmp;
            return bmp;
        }
    }

    private static BitmapSource FromBgra(int w, int h, byte[] bgra)
    {
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
        bmp.Freeze();
        return bmp;
    }

    // ---- text ----

    private void DrawText(DrawingContext dc, NavModTextOp op, Rect r, double scale)
    {
        var own = op.FontKey.Length > 0 ? _fonts.GetValueOrDefault(op.FontKey) : null;
        var typeface = own is null
            ? new Typeface(Fallback, FontStyles.Normal, op.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal)
            : op.Bold && own.Weight != FontWeights.Bold
                ? new Typeface(own.FontFamily, own.Style, FontWeights.Bold, own.Stretch)
                : own;
        var brush = Brush(op.Rgba);
        var align = op.Horizontal switch
        {
            NavTextAlign.Start => TextAlignment.Left,
            NavTextAlign.End => TextAlignment.Right,
            _ => TextAlignment.Center,
        };

        // Wrapped to the rect at word boundaries only. WPF breaks inside a word that is wider than MaxTextWidth,
        // which TextMeshPro never does (a word too wide overflows, and auto-size shrinks it), so the width a label
        // is laid out in is never less than its widest word: RESET stays RESET. Measured word by word, because
        // FormattedText.MinWidth is not that figure for text with a line break in it.
        var words = op.Text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        FormattedText Make(double points)
        {
            var em = Math.Max(1, points * scale);
            FormattedText Build(string s, double width) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, em, brush, 1.0)
            {
                MaxTextWidth = Math.Max(1, width),
                TextAlignment = align,
                Trimming = TextTrimming.None,
            };
            var widest = 0.0;
            foreach (var word in words) widest = Math.Max(widest, Build(word, 100000).WidthIncludingTrailingWhitespace);
            return Build(op.Text, Math.Max(r.Width, widest + 1));
        }

        // The stored size is the one TextMeshPro fitted in the editor and wrote back into the prefab, so it is
        // the size the game shows; auto-size here only shrinks it, towards the label's own minimum, when this
        // layout leaves it less room than the editor did. Growing it to fill the rect, which is what the
        // auto-size range invites, put CLEAR outside its button and titles at twice their size.
        var text = Make(op.Size);
        if (op.AutoSize)
            for (var pt = op.Size - 0.5; pt >= op.SizeMin - 1e-9 && (text.Height > r.Height + 0.5 || text.MinWidth > r.Width + 0.5); pt -= 0.5)
                text = Make(pt);

        var y = op.Vertical switch
        {
            NavTextAlign.Start => r.Top,
            NavTextAlign.End => r.Bottom - text.Height,
            _ => r.Top + (r.Height - text.Height) / 2,
        };
        dc.DrawText(text, new Point(r.Left, y));
    }

    /// <summary>
    /// Put the game's faces where WPF can load them. A font can only be loaded from a file or a directory, so each
    /// one is written once to the user data folder and read back as a private typeface. <b>One folder per font:</b>
    /// WPF enumerates a font <i>location</i>, and given a file it enumerates the file's whole directory, so five
    /// faces in one folder all came back as the first of them and every label was set in Jura. A face that will
    /// not install is left out and its labels fall back to <see cref="Fallback"/>.
    /// </summary>
    private static Dictionary<string, Typeface> InstallFonts(IReadOnlyDictionary<string, byte[]> fonts)
    {
        var result = new Dictionary<string, Typeface>(StringComparer.Ordinal);
        if (fonts.Count == 0) return result;
        string root;
        try
        {
            root = Path.Combine(AppSettings.Dir, "fonts");
            Directory.CreateDirectory(root);
        }
        catch { return result; }

        foreach (var (name, data) in fonts)
        {
            try
            {
                var safe = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
                var dir = Path.Combine(root, safe);
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, safe + ".ttf");
                if (!File.Exists(path) || new FileInfo(path).Length != data.Length) File.WriteAllBytes(path, data);
                // the file's own face, weight and stretch: robotocondensed is family "Roboto", stretch Condensed,
                // and asking for the family alone would give the regular width
                var typeface = Fonts.GetTypefaces(new Uri(dir + Path.DirectorySeparatorChar)).FirstOrDefault();
                if (typeface is not null) result[name] = typeface;
            }
            catch
            {
                // this face falls back; the others still install
            }
        }
        return result;
    }

    // ---- colours ----

    private SolidColorBrush Brush(uint rgba)
    {
        lock (_gate)
        {
            if (_brushes.TryGetValue(rgba, out var hit)) return hit;
            var brush = new SolidColorBrush(ToColor(rgba));
            brush.Freeze();
            _brushes[rgba] = brush;
            return brush;
        }
    }

    private static Color ToColor(uint rgba) => Color.FromArgb(
        (byte)(rgba & 0xFF), (byte)(rgba >> 24), (byte)((rgba >> 16) & 0xFF), (byte)((rgba >> 8) & 0xFF));
}
