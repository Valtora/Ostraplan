using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// The tile grid: renders the document's sprites (16 px art scaled with
/// nearest-neighbor), and turns mouse input into place/select/move/pan/zoom.
/// Mutations are NOT applied here - they are raised as events and the window
/// pushes them through the command stack.
/// </summary>
public sealed class ShipCanvas : FrameworkElement
{
    private static readonly double[] ZoomSteps = [16, 24, 32, 48, 64, 96, 128];

    private static readonly Brush Background = Frozen(new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1A)));
    private static readonly Pen GridPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF)), 1));
    private static readonly Pen AxisPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0x6A, 0x9F, 0xD8)), 1));
    private static readonly Pen HoverPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), 1));
    private static readonly Pen SelectPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x4E, 0xA6, 0xFF)), 2));
    private static readonly Brush BandBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0x4E, 0xA6, 0xFF)));
    private static readonly Pen BandPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0x4E, 0xA6, 0xFF)), 1));
    private static readonly Pen FaintGridPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)), 1));
    private static readonly Pen OriginPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xD8, 0xA0, 0x3C)), 1.5));
    private static readonly Brush OriginBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0xD8, 0xA0, 0x3C)));
    private static readonly Typeface OriginTypeface = new("Segoe UI");

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    public ShipDocument? Doc { get; private set; }
    public SpriteCache? Sprites { get; set; }
    public PartDef? ArmedPart { get; private set; }
    public int ArmedRot { get; private set; }
    public HashSet<Guid> SelectedIds { get; } = [];

    public double Zoom { get; private set; } = 48;   // screen px per tile
    private Vector _pan;                             // screen position of world origin
    private bool _panInitialized;

    private enum Drag { None, Pan, Move, Band }
    private Drag _drag;
    private Point _dragStartScreen;
    private (int X, int Y) _dragStartCell;
    private (int X, int Y) _moveDelta;
    private (int X, int Y)? _hoverCell;

    public event Action<PartDef, int, int, int>? PlaceRequested;
    public event Action<IReadOnlyList<Placement>, int, int>? MoveRequested;
    public event Action? SelectionChanged;
    public event Action<(int X, int Y)?>? HoverChanged;
    public event Action? Disarmed;
    public event Action? ViewChanged;
    public event Action<Placement>? ContextMenuRequested;

    public ShipCanvas()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        SnapsToDevicePixels = true;
        Focusable = true;
        ClipToBounds = true;
    }

    // ---- wiring ----

    public void SetDocument(ShipDocument doc)
    {
        if (Doc is not null) Doc.Changed -= InvalidateVisual;
        Doc = doc;
        doc.Changed += InvalidateVisual;
        SelectedIds.Clear();
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    public void SetArmed(PartDef? part)
    {
        ArmedPart = part;
        if (part is not null)
        {
            SelectedIds.Clear();
            SelectionChanged?.Invoke();
        }
        InvalidateVisual();
    }

    public void RotateArmed(int delta)
    {
        // sheet items (walls/floors) never rotate - Item.RotateCW is skipped for them
        if (ArmedPart is null || ArmedPart.Item.HasSpriteSheet) return;
        ArmedRot = GridMath.Norm(ArmedRot + delta);
        InvalidateVisual();
    }

    public List<Placement> SelectedPlacements() =>
        Doc is null ? [] : Doc.Placements.Where(p => SelectedIds.Contains(p.Id)).ToList();

    // ---- view ----

    public void FitContent()
    {
        if (Doc?.Bounds() is not { } b || RenderSize.Width < 1) return;
        var tilesW = b.MaxX - b.MinX + 3.0;
        var tilesH = b.MaxY - b.MinY + 3.0;
        var fit = Math.Min(RenderSize.Width / tilesW, RenderSize.Height / tilesH);
        Zoom = ZoomSteps.LastOrDefault(z => z <= fit, ZoomSteps[0]);
        var centerX = (b.MinX + b.MaxX + 1) / 2.0;
        var centerY = (b.MinY + b.MaxY + 1) / 2.0;
        _pan = new Vector(RenderSize.Width / 2 - centerX * Zoom, RenderSize.Height / 2 - centerY * Zoom);
        _panInitialized = true;
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (!_panInitialized && sizeInfo.NewSize.Width > 0)
        {
            _pan = new Vector(sizeInfo.NewSize.Width / 2, sizeInfo.NewSize.Height / 2);
            _panInitialized = true;
        }
    }

    private (int X, int Y) CellAt(Point screen) =>
        ((int)Math.Floor((screen.X - _pan.X) / Zoom), (int)Math.Floor((screen.Y - _pan.Y) / Zoom));

    private Rect CellRect(double x, double y, double w, double h) =>
        new(_pan.X + x * Zoom, _pan.Y + y * Zoom, w * Zoom, h * Zoom);

    // ---- input ----

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var idx = Array.IndexOf(ZoomSteps, Zoom);
        if (idx < 0) idx = 3;
        var next = Math.Clamp(idx + (e.Delta > 0 ? 1 : -1), 0, ZoomSteps.Length - 1);
        if (ZoomSteps[next] == Zoom) return;

        // keep the tile under the cursor stationary
        var mouse = e.GetPosition(this);
        var world = new Point((mouse.X - _pan.X) / Zoom, (mouse.Y - _pan.Y) / Zoom);
        Zoom = ZoomSteps[next];
        _pan = new Vector(mouse.X - world.X * Zoom, mouse.Y - world.Y * Zoom);
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        var screen = e.GetPosition(this);

        if (e.ChangedButton == MouseButton.Middle ||
            (e.ChangedButton == MouseButton.Left && Keyboard.IsKeyDown(Key.Space)))
        {
            _drag = Drag.Pan;
            _dragStartScreen = screen;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Right)
        {
            if (ArmedPart is not null)
            {
                SetArmed(null);
                Disarmed?.Invoke();
            }
            else if (Doc is not null)
            {
                var rmbCell = CellAt(screen);
                var rmbHit = Doc.HitTest(rmbCell.X, rmbCell.Y);
                if (rmbHit is not null)
                {
                    if (!SelectedIds.Contains(rmbHit.Id))
                    {
                        SelectedIds.Clear();
                        SelectedIds.Add(rmbHit.Id);
                        SelectionChanged?.Invoke();
                        InvalidateVisual();
                    }
                    ContextMenuRequested?.Invoke(rmbHit);
                }
            }
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left || Doc is null) return;
        var cell = CellAt(screen);

        if (ArmedPart is not null)
        {
            var (w, h) = GridMath.Size(ArmedPart.Item.Width, ArmedPart.Item.Height, ArmedRot);
            PlaceRequested?.Invoke(ArmedPart, cell.X - (w - 1) / 2, cell.Y - (h - 1) / 2, ArmedRot);
            e.Handled = true;
            return;
        }

        var hit = Doc.HitTest(cell.X, cell.Y);
        if (hit is not null)
        {
            var additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            if (!SelectedIds.Contains(hit.Id))
            {
                if (!additive) SelectedIds.Clear();
                SelectedIds.Add(hit.Id);
            }
            else if (additive)
            {
                SelectedIds.Remove(hit.Id);
            }
            SelectionChanged?.Invoke();
            _drag = Drag.Move;
            _dragStartCell = cell;
            _moveDelta = (0, 0);
            CaptureMouse();
        }
        else
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                SelectedIds.Clear();
                SelectionChanged?.Invoke();
            }
            _drag = Drag.Band;
            _dragStartScreen = screen;
            _dragStartCell = cell;
            CaptureMouse();
        }
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var screen = e.GetPosition(this);
        var cell = CellAt(screen);
        if (_hoverCell is null || _hoverCell.Value != cell)
        {
            _hoverCell = cell;
            HoverChanged?.Invoke(cell);
            if (ArmedPart is not null || _drag != Drag.None) InvalidateVisual();
            else if (Doc is not null) InvalidateVisual();   // hover outline
        }

        switch (_drag)
        {
            case Drag.Pan:
                _pan += screen - _dragStartScreen;
                _dragStartScreen = screen;
                ViewChanged?.Invoke();
                InvalidateVisual();
                break;
            case Drag.Move:
                _moveDelta = (cell.X - _dragStartCell.X, cell.Y - _dragStartCell.Y);
                InvalidateVisual();
                break;
            case Drag.Band:
                InvalidateVisual();
                break;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_drag == Drag.None) return;
        var drag = _drag;
        _drag = Drag.None;
        ReleaseMouseCapture();

        if (drag == Drag.Move && Doc is not null && (_moveDelta.X != 0 || _moveDelta.Y != 0))
            MoveRequested?.Invoke(SelectedPlacements(), _moveDelta.X, _moveDelta.Y);

        if (drag == Drag.Band && Doc is not null)
        {
            var end = CellAt(e.GetPosition(this));
            var (x0, x1) = (Math.Min(_dragStartCell.X, end.X), Math.Max(_dragStartCell.X, end.X));
            var (y0, y1) = (Math.Min(_dragStartCell.Y, end.Y), Math.Max(_dragStartCell.Y, end.Y));
            foreach (var p in Doc.Placements)
            {
                var (w, h) = Doc.FootprintOf(p);
                if (p.X <= x1 && p.X + w - 1 >= x0 && p.Y <= y1 && p.Y + h - 1 >= y0)
                    SelectedIds.Add(p.Id);
            }
            SelectionChanged?.Invoke();
        }

        _moveDelta = (0, 0);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hoverCell = null;
        HoverChanged?.Invoke(null);
        InvalidateVisual();
    }

    // ---- rendering ----

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Background, null, new Rect(RenderSize));
        if (Doc is null || Sprites is null) return;

        DrawGrid(dc);

        foreach (var p in Doc.DrawOrder())
        {
            var offset = _drag == Drag.Move && SelectedIds.Contains(p.Id) ? _moveDelta : (0, 0);
            DrawPlacement(dc, p, offset);
        }

        DrawOriginMarker(dc);

        foreach (var p in Doc.Placements.Where(p => SelectedIds.Contains(p.Id)))
        {
            var (w, h) = Doc.FootprintOf(p);
            (int X, int Y) offset = _drag == Drag.Move ? _moveDelta : (0, 0);
            dc.DrawRectangle(null, SelectPen, CellRect(p.X + offset.X, p.Y + offset.Y, w, h));
        }

        if (ArmedPart is not null && _hoverCell is { } hover)
        {
            var (w, h) = GridMath.Size(ArmedPart.Item.Width, ArmedPart.Item.Height, ArmedRot);
            var (gx, gy) = (hover.X - (w - 1) / 2, hover.Y - (h - 1) / 2);
            dc.PushOpacity(0.55);
            DrawSprite(dc, ArmedPart, gx, gy, ArmedRot, ghost: true);
            dc.Pop();
            dc.DrawRectangle(null, HoverPen, CellRect(gx, gy, w, h));
        }
        else if (_hoverCell is { } cell && _drag == Drag.None)
        {
            dc.DrawRectangle(null, HoverPen, CellRect(cell.X, cell.Y, 1, 1));
        }

        if (_drag == Drag.Band)
        {
            var cur = Mouse.GetPosition(this);
            dc.DrawRectangle(BandBrush, BandPen, new Rect(_dragStartScreen, cur));
        }
    }

    private void DrawGrid(DrawingContext dc)
    {
        var pen = Zoom < 24 ? FaintGridPen : GridPen;   // fainter when zoomed out, never gone
        var x0 = (int)Math.Floor(-_pan.X / Zoom);
        var y0 = (int)Math.Floor(-_pan.Y / Zoom);
        var x1 = (int)Math.Ceiling((RenderSize.Width - _pan.X) / Zoom);
        var y1 = (int)Math.Ceiling((RenderSize.Height - _pan.Y) / Zoom);

        for (var x = x0; x <= x1; x++)
        {
            var sx = Math.Round(_pan.X + x * Zoom) + 0.5;
            dc.DrawLine(x == 0 ? AxisPen : pen, new Point(sx, 0), new Point(sx, RenderSize.Height));
        }
        for (var y = y0; y <= y1; y++)
        {
            var sy = Math.Round(_pan.Y + y * Zoom) + 0.5;
            dc.DrawLine(y == 0 ? AxisPen : pen, new Point(0, sy), new Point(RenderSize.Width, sy));
        }
    }

    /// <summary>
    /// The ship's local origin. Not a game rule about airlocks - dock positions
    /// are free-form - but it is the coordinate anchor everything exports around,
    /// and where Ostraplan seeds the starting docking port.
    /// </summary>
    private void DrawOriginMarker(DrawingContext dc)
    {
        var rect = CellRect(0, 0, 1, 1);
        dc.DrawRectangle(null, OriginPen, rect);
        if (Zoom >= 32)
        {
            var label = new FormattedText("0,0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                OriginTypeface, Math.Clamp(Zoom / 4, 9, 14), OriginBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(label, new Point(rect.X + 3, rect.Bottom + 2));
        }
    }

    private void DrawPlacement(DrawingContext dc, Placement p, (int X, int Y) offset)
    {
        var part = Doc!.Part(p);
        if (part is null)
        {
            dc.DrawRectangle(Brushes.DarkSlateGray, null, CellRect(p.X + offset.X, p.Y + offset.Y, 1, 1));
            return;
        }
        DrawSprite(dc, part, p.X + offset.X, p.Y + offset.Y, p.Rot, ghost: false);
    }

    private void DrawSprite(DrawingContext dc, PartDef part, int gx, int gy, int rot, bool ghost)
    {
        if (part.Item.HasSpriteSheet && part.Item.CtSpriteSheet is { } ct)
        {
            // per-tile autotile crop; ghosts have no tile conds yet, so they show isolated
            var (cols, rows) = Sprites!.SheetDims(part);
            var (w, h) = (part.Item.Width, part.Item.Height);
            for (var r = 0; r < h; r++)
                for (var c = 0; c < w; c++)
                {
                    var mask = ghost ? 0 : Autotile.MaskAt(Doc!.Conds, ct, gx + c, gy + r);
                    var (col, row) = Autotile.Cell(mask, cols, rows);
                    dc.DrawImage(Sprites.SheetCell(part, col, row), CellRect(gx + c, gy + r, 1, 1));
                }
            return;
        }

        var (defW, defH) = (part.Item.Width, part.Item.Height);
        var (effW, effH) = GridMath.Size(defW, defH, rot);
        var target = CellRect(gx, gy, effW, effH);
        var bmp = Sprites!.Sprite(part);

        if (GridMath.Norm(rot) == 0)
        {
            dc.DrawImage(bmp, target);
            return;
        }

        var center = new Point(target.X + target.Width / 2, target.Y + target.Height / 2);
        dc.PushTransform(new RotateTransform(GridMath.Norm(rot), center.X, center.Y));
        dc.DrawImage(bmp, new Rect(center.X - defW * Zoom / 2, center.Y - defH * Zoom / 2, defW * Zoom, defH * Zoom));
        dc.Pop();
    }
}
