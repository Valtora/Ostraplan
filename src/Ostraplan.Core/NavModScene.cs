namespace Ostraplan.Core;

/// <summary>
/// What a nav module looks like on the console, reduced to something a renderer can draw without Unity: an
/// ordered list of fills, sprites and labels, each placed in the module's own unit square. The module's
/// <c>Container</c> is <c>(0,0)-(1,1)</c> with y running <b>down</b> (a bitmap's frame, not the game's anchors),
/// so one scene renders at whatever pixel size the arrange board gives the module.
///
/// <para>This is the design-time state of the module's prefab and nothing more: the chrome, the control art in
/// its default position, and every label the artist typed. Anything the game fills in at runtime (a fuel bar, the
/// transponder callsign, the map) is not here, which is why a rendered module reads as the module's purpose rather
/// than as a live screen. See <see cref="NavModArt"/> for where it comes from.</para>
/// </summary>
public sealed record NavModScene(string Key, IReadOnlyList<NavModOp> Ops)
{
    /// <summary>
    /// The console screen's height in the game's <b>canvas units</b>, which is what a label's point size, an
    /// anchored offset and a sliced border are all measured in. The ship GUI canvases scale with the screen
    /// against a 1280×720 reference matched on height (<c>CanvasScaler</c> on <c>Canvas GUI</c> and its siblings),
    /// so a unit is 1.5 screen pixels at 1080p, and the board, which measures about 1700×810 screen pixels there,
    /// is about 1133×540 units. A renderer scales everything by <c>boardHeightPx / ReferenceBoardHeight</c>.
    /// </summary>
    public const double ReferenceBoardHeight = 540;

    /// <summary>The board's width at the same reference, for turning an anchored offset in canvas units into a
    /// fraction of the module.</summary>
    public const double ReferenceBoardWidth = 1133;

    /// <summary>The canvases' <c>referencePixelsPerUnit</c>. A sprite's nine-slice border is in its own pixels,
    /// and covers <c>border × ReferencePixelsPerUnit / sprite.pixelsPerUnit</c> canvas units on screen: a 34px
    /// border on a 100 PPU sprite is 11 units wide, not 34.</summary>
    public const double ReferencePixelsPerUnit = 32;
}

/// <summary>One drawing instruction in a <see cref="NavModScene"/>. Colours are <c>0xRRGGBBAA</c>.
/// <see cref="Rect"/> is where the piece lands, as the bounding box of whatever turn or flip its transforms
/// gave it; <see cref="Orient"/> says which turn or flip that was, so the content is drawn the right way round
/// inside the box.</summary>
public abstract record NavModOp(UnitRect Rect)
{
    public Orient Orient { get; init; } = Orient.Identity;
}

/// <summary>
/// A quarter-turn and/or flip, as the linear part of a transform in the bitmap's y-down frame with the scale
/// taken out: <c>x' = A·x + C·y</c>, <c>y' = B·x + D·y</c>, each entry -1, 0 or 1. A prefab rotates its
/// rotor-efficiency meter by 90° and mirrors a slider handle, and Unity applies those to the whole subtree, so
/// the walk accumulates them and hands a renderer this rather than an angle it would have to decompose again.
/// <see cref="Swaps"/> is true for a 90° or 270° turn, when the content's width lies along the box's height.
/// </summary>
public readonly record struct Orient(int A, int B, int C, int D)
{
    public static readonly Orient Identity = new(1, 0, 0, 1);
    public bool IsIdentity => A == 1 && B == 0 && C == 0 && D == 1;
    public bool Swaps => A == 0;
}

/// <summary>A solid rectangle: an <c>Image</c> with no sprite, which Unity draws as a tinted quad.</summary>
public sealed record NavModFill(UnitRect Rect, uint Rgba) : NavModOp(Rect);

/// <summary>A sprite stretched over the rect, or nine-sliced across it when <paramref name="Sliced"/> (the
/// sprite's border comes with the sprite itself, <see cref="NavModSprite.Border"/>, and
/// <paramref name="UnitsPerPixel"/> is how many canvas units one of its pixels covers, which sizes the border).
/// <paramref name="PreserveAspect"/> keeps the sprite's own shape, letterboxed in the rect.</summary>
public sealed record NavModSpriteOp(UnitRect Rect, int SpriteId, uint Tint, bool Sliced, bool PreserveAspect,
    double UnitsPerPixel) : NavModOp(Rect);

/// <summary>A label. <paramref name="Size"/>, <paramref name="SizeMin"/> and <paramref name="SizeMax"/> are
/// TextMeshPro point sizes in canvas units (<see cref="NavModScene.ReferenceBoardHeight"/>). With
/// <paramref name="AutoSize"/> on, <paramref name="Size"/> is the size TextMeshPro fitted in the editor and wrote
/// back into the prefab, which is what the game shows: a renderer draws at that size and shrinks towards
/// <paramref name="SizeMin"/> only if its own layout leaves the label no room, never grows it.</summary>
public sealed record NavModTextOp(
    UnitRect Rect, string Text, string FontKey, double Size, double SizeMin, double SizeMax, bool AutoSize,
    bool Bold, uint Rgba, NavTextAlign Horizontal, NavTextAlign Vertical) : NavModOp(Rect);

public enum NavTextAlign { Start, Middle, End }

/// <summary>A rect in a module's unit square: y down, origin top-left. Values outside 0..1 are allowed, since a
/// child of the prefab can overhang its container.</summary>
public readonly record struct UnitRect(double X, double Y, double W, double H);

/// <summary>A sprite's pixels, cropped out of its texture, as top-down BGRA32 rows. <paramref name="PixelsPerUnit"/>
/// is the sprite's own setting (100 for the UI art), which with the canvas's reference decides how much screen
/// a nine-slice border covers.</summary>
public sealed record NavModSprite(int Id, string Name, int Width, int Height, byte[] Bgra, SpriteBorder Border,
    double PixelsPerUnit);

/// <summary>A nine-slice border in sprite pixels, in Unity's order (left, bottom, right, top).</summary>
public readonly record struct SpriteBorder(int Left, int Bottom, int Right, int Top)
{
    public bool IsEmpty => Left == 0 && Bottom == 0 && Right == 0 && Top == 0;
}

/// <summary>
/// The geometry of Unity's <c>RectTransform</c>, ported so a prefab's hierarchy can be laid out off the engine.
/// Everything is in a y-<b>up</b> frame, the game's own, and converted to a bitmap's y-down frame only at the end
/// (<see cref="ToUnit"/>).
/// </summary>
public static class UguiLayout
{
    /// <summary>A rect in canvas pixels, y up: <c>Y</c> is the bottom edge.</summary>
    public readonly record struct PxRect(double X, double Y, double W, double H);

    /// <summary>
    /// Resolve a child against its parent the way <c>RectTransform</c> does. The anchors pick a sub-rect of the
    /// parent; <paramref name="sizeDelta"/> is added to that sub-rect's size; and <paramref name="anchoredPos"/> is
    /// where the child's pivot sits relative to the same pivot point on the anchor sub-rect. With the anchors
    /// stretched and both deltas zero, which is how the console lays its modules out, the child simply fills the
    /// anchor sub-rect.
    /// </summary>
    public static PxRect Resolve(PxRect parent,
        (double X, double Y) anchorMin, (double X, double Y) anchorMax,
        (double X, double Y) sizeDelta, (double X, double Y) anchoredPos, (double X, double Y) pivot)
    {
        var anchorW = (anchorMax.X - anchorMin.X) * parent.W;
        var anchorH = (anchorMax.Y - anchorMin.Y) * parent.H;
        var w = anchorW + sizeDelta.X;
        var h = anchorH + sizeDelta.Y;
        var x = parent.X + anchorMin.X * parent.W + anchorW * pivot.X + anchoredPos.X - w * pivot.X;
        var y = parent.Y + anchorMin.Y * parent.H + anchorH * pivot.Y + anchoredPos.Y - h * pivot.Y;
        return new PxRect(x, y, w, h);
    }

    /// <summary>A rect in the container's frame as a fraction of the container, flipped to y down.</summary>
    public static UnitRect ToUnit(PxRect r, PxRect container) => new(
        (r.X - container.X) / container.W,
        1 - (r.Y + r.H - container.Y) / container.H,
        r.W / container.W,
        r.H / container.H);

    /// <summary>
    /// A 2D affine map in the y-up frame, <c>x' = A·x + C·y + Tx</c>, <c>y' = B·x + D·y + Ty</c>: what a
    /// <c>RectTransform</c>'s local rotation and scale do to everything under it. <see cref="Local"/> builds one
    /// for a node from its own rect and pivot, <see cref="Then"/> composes it under its parent's, and
    /// <see cref="Bounds"/> is where a rect lands once mapped.
    /// </summary>
    public readonly record struct Affine(double A, double B, double C, double D, double Tx, double Ty)
    {
        public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);

        /// <summary>Rotation by <paramref name="degrees"/> (counter-clockwise, y up) and a scale, both about the
        /// node's pivot point, which is where Unity applies them.</summary>
        public static Affine Local(PxRect rect, (double X, double Y) pivot, double degrees, (double X, double Y) scale)
        {
            if (Math.Abs(degrees) < 1e-6 && Math.Abs(scale.X - 1) < 1e-6 && Math.Abs(scale.Y - 1) < 1e-6) return Identity;
            double px = rect.X + rect.W * pivot.X, py = rect.Y + rect.H * pivot.Y;
            double cos = Math.Cos(degrees * Math.PI / 180), sin = Math.Sin(degrees * Math.PI / 180);
            // T(P) · R · S · T(-P)
            double a = cos * scale.X, c = -sin * scale.Y, b = sin * scale.X, d = cos * scale.Y;
            return new Affine(a, b, c, d, px - (a * px + c * py), py - (b * px + d * py));
        }

        /// <summary>This map applied first, then <paramref name="outer"/>.</summary>
        public Affine Then(Affine outer) => new(
            outer.A * A + outer.C * B, outer.B * A + outer.D * B,
            outer.A * C + outer.C * D, outer.B * C + outer.D * D,
            outer.A * Tx + outer.C * Ty + outer.Tx, outer.B * Tx + outer.D * Ty + outer.Ty);

        public (double X, double Y) Apply(double x, double y) => (A * x + C * y + Tx, B * x + D * y + Ty);

        /// <summary>The axis-aligned box a rect maps into. Exact for quarter turns, which is every turn the
        /// prefabs use.</summary>
        public PxRect Bounds(PxRect r)
        {
            var (x0, y0) = Apply(r.X, r.Y);
            var (x1, y1) = Apply(r.X + r.W, r.Y);
            var (x2, y2) = Apply(r.X, r.Y + r.H);
            var (x3, y3) = Apply(r.X + r.W, r.Y + r.H);
            double minX = Math.Min(Math.Min(x0, x1), Math.Min(x2, x3)), maxX = Math.Max(Math.Max(x0, x1), Math.Max(x2, x3));
            double minY = Math.Min(Math.Min(y0, y1), Math.Min(y2, y3)), maxY = Math.Max(Math.Max(y0, y1), Math.Max(y2, y3));
            return new PxRect(minX, minY, maxX - minX, maxY - minY);
        }

        /// <summary>The turn and flip this map amounts to, in the y-down frame a bitmap is drawn in (a
        /// counter-clockwise turn here is clockwise there, which is what negating the off-diagonal does), with
        /// the scale taken out. A map that is not a quarter turn gets the nearest one.</summary>
        public Orient ToOrient()
        {
            static int Sign(double v) => Math.Abs(v) < 1e-6 ? 0 : v > 0 ? 1 : -1;
            var a = Sign(A); var d = Sign(D);
            var b = Sign(B); var c = Sign(C);
            // an axis has to map somewhere: a map that rounds both entries of a column to zero is degenerate
            if (a == 0 && b == 0) a = 1;
            if (c == 0 && d == 0) d = 1;
            return new Orient(a, -b, -c, d);
        }
    }
}
