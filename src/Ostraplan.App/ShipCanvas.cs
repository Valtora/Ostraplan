using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

public enum SymmetryMode { Off, Vertical, Horizontal, Both }

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
    private static readonly Pen SymPen = MakeSymPen();
    private static readonly Brush OobBrush = MakeOobBrush();
    private static readonly Pen OriginPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xD8, 0xA0, 0x3C)), 1.5));
    private static readonly Brush OriginBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0xD8, 0xA0, 0x3C)));
    private static readonly Typeface OriginTypeface = new("Segoe UI");
    private static readonly Pen GhostOkPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x5A, 0xD0, 0x6A)), 2));
    private static readonly Pen GhostBadPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0x5B, 0x5B)), 2));
    // per-cell hazard fill for both a ghost's failing cells and existing illegal placements (same red vocabulary)
    private static readonly Brush HazardFill = Frozen(new SolidColorBrush(Color.FromArgb(0x66, 0xD6, 0x45, 0x45)));
    // the sub-floor reservation a part projects under walkable floor (the tanks' 7x7 ring vs their 3x3 body)
    private static readonly Brush SubfloorFill = Frozen(new SolidColorBrush(Color.FromArgb(0x33, 0x6A, 0x8E, 0xB8)));
    private static readonly Brush LeakFill = Frozen(new SolidColorBrush(Color.FromArgb(0x77, 0x3F, 0xC8, 0xE0)));
    private static readonly Pen LeakPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xC8, 0x5F, 0xE0, 0xF0)), 1));
    private static readonly Pen SubfloorPen = MakeSubfloorPen();

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static Pen MakeSymPen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0x39, 0xC8, 0xC8)), 1)
        { DashStyle = DashStyles.Dash };
        pen.Freeze();
        return pen;
    }

    private static Pen MakeSubfloorPen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0x7A, 0x9E, 0xC8)), 1)
        { DashStyle = DashStyles.Dash };
        pen.Freeze();
        return pen;
    }

    /// <summary>Classic red hazard stripes (screen-fixed scale) for out-of-bounds areas.</summary>
    private static Brush MakeOobBrush()
    {
        var group = new DrawingGroup();
        using (var ctx = group.Open())
        {
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x12, 0xD6, 0x45, 0x45)), null, new Rect(0, 0, 12, 12));
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x3C, 0xE0, 0x50, 0x50)), 3);
            ctx.DrawLine(pen, new Point(-3, 9), new Point(9, -3));
            ctx.DrawLine(pen, new Point(3, 15), new Point(15, 3));
        }
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 12, 12),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 12, 12),
            ViewboxUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }

    public ShipDocument? Doc { get; private set; }
    public SpriteCache? Sprites { get; set; }
    public PartDef? ArmedPart { get; private set; }
    public int ArmedRot { get; private set; }
    public HashSet<Guid> SelectedIds { get; } = [];

    public double Zoom { get; private set; } = 48;   // screen px per tile
    private Vector _pan;                             // screen position of world origin
    private bool _panInitialized;

    public SymmetryMode SymMode { get; private set; }
    public (int X, int Y) SymCenter { get; private set; }
    public int ViewRot { get; private set; }   // plan-view rotation, 90-degree steps (Q/E)

    private enum Drag { None, Pan, Move, Band, Paint, BoxFill }
    private Drag _drag;
    private Point _dragStartScreen;
    private (int X, int Y) _dragStartCell;
    private (int X, int Y) _moveDelta;
    private (int X, int Y)? _hoverCell;
    private readonly List<IDocCommand> _stroke = [];   // live placements of the current paint/fill stroke
    private IReadOnlyList<(int X, int Y)> _illegalCells = [];   // tiles of existing illegal placements (from ProblemScan)
    private IReadOnlyList<(int X, int Y)> _leakCells = [];      // unsealed tiles of a leaking compartment (from the Ship Rating report)
    private string? _lastGhostReason;                          // dedupe GhostReasonChanged

    public event Action<IReadOnlyList<IDocCommand>>? StrokeCommitted;
    public event Action? SymmetryChanged;
    public event Action<IReadOnlyList<Placement>, int, int>? MoveRequested;
    public event Action? SelectionChanged;
    public event Action<(int X, int Y)?>? HoverChanged;
    public event Action? Disarmed;
    public event Action? ViewChanged;
    public event Action<(int X, int Y)>? ContextMenuRequested;   // right-clicked tile; window builds the layer picker
    public event Action<string?>? GhostReasonChanged;   // the armed ghost's illegality reason, null when legal/disarmed

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

    /// <summary>Tiles of existing illegal placements to hazard-tint; pushed by the window after each ProblemScan.</summary>
    public void SetIllegalCells(IReadOnlyList<(int X, int Y)> cells)
    {
        _illegalCells = cells;
        InvalidateVisual();
    }

    /// <summary>Highlight the unsealed tiles of a leaking compartment (from the Ship Rating law report). Empty clears it.</summary>
    public void SetLeakCells(IReadOnlyList<(int X, int Y)> cells)
    {
        _leakCells = cells;
        InvalidateVisual();
    }

    /// <summary>Set the hovered cell programmatically (drives the armed ghost; used by render tests).</summary>
    public void SetHover((int X, int Y)? cell)
    {
        _hoverCell = cell;
        HoverChanged?.Invoke(cell);
        InvalidateVisual();
    }

    /// <summary>
    /// World cells the part reserves as under-floor storage at this pose — sub-floor
    /// (IsSubTile) with no solid body (the large tanks' outer ring around their 3x3 core).
    /// </summary>
    private IEnumerable<(int X, int Y)> UnderFloorCells(PartDef part, int gx, int gy, int rot)
    {
        if (Doc is null) yield break;
        var effRot = part.Item.HasSpriteSheet ? 0 : GridMath.Norm(rot);
        var (rw, rh, adds) = GridMath.Rotate(part.Item.SocketAdds, part.Item.Width, part.Item.Height, effRot);
        for (var r = 0; r < rh; r++)
            for (var c = 0; c < rw; c++)
                if (r * rw + c < adds.Length && Doc.Catalog.IsUnderFloorLoot(adds[r * rw + c]))
                    yield return (gx + c, gy + r);
    }

    /// <summary>Tight bounds of the part's above-floor body at this pose (the whole footprint when it has no sub-floor ring).</summary>
    private (int X, int Y, int W, int H) AboveFloorBounds(PartDef part, int gx, int gy, int rot)
    {
        var (w, h) = GridMath.Size(part.Item.Width, part.Item.Height, rot);
        var under = UnderFloorCells(part, gx, gy, rot).ToHashSet();
        if (under.Count == 0) return (gx, gy, w, h);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (var r = 0; r < h; r++)
            for (var c = 0; c < w; c++)
                if (!under.Contains((gx + c, gy + r)))
                {
                    minX = Math.Min(minX, gx + c); minY = Math.Min(minY, gy + r);
                    maxX = Math.Max(maxX, gx + c); maxY = Math.Max(maxY, gy + r);
                }
        return maxX < minX ? (gx, gy, w, h) : (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private void RaiseGhostReason(string? reason)
    {
        if (reason == _lastGhostReason) return;
        _lastGhostReason = reason;
        GhostReasonChanged?.Invoke(reason);
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

    /// <summary>Replace the selection with a single placement (the layer picker's row click).</summary>
    public void SelectOnly(Placement p)
    {
        SelectedIds.Clear();
        SelectedIds.Add(p.Id);
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Replace the selection with a set of placements (the context-menu layer filter).</summary>
    public void SetSelection(IEnumerable<Placement> ps)
    {
        SelectedIds.Clear();
        foreach (var p in ps) SelectedIds.Add(p.Id);
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>
    /// Off -> Vertical -> Horizontal -> Both -> Off. When switching on from Off,
    /// the axes centre on the tile under the cursor (origin if the mouse is
    /// elsewhere); cycle to Off and back to re-centre.
    /// </summary>
    public void CycleSymmetry()
    {
        if (SymMode == SymmetryMode.Off) SymCenter = _hoverCell ?? (0, 0);
        SymMode = SymMode switch
        {
            SymmetryMode.Off => SymmetryMode.Vertical,
            SymmetryMode.Vertical => SymmetryMode.Horizontal,
            SymmetryMode.Horizontal => SymmetryMode.Both,
            _ => SymmetryMode.Off,
        };
        SymmetryChanged?.Invoke();
        InvalidateVisual();
    }

    public void RotateView(int delta)
    {
        ViewRot = GridMath.Norm(ViewRot + delta);
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Mouse point into the un-rotated (pan/zoom) space the grid math lives in.</summary>
    private Point ScreenToPanSpace(Point s)
    {
        if (ViewRot == 0) return s;
        var m = Matrix.Identity;
        m.RotateAt(-ViewRot, RenderSize.Width / 2, RenderSize.Height / 2);
        return m.Transform(s);
    }

    private Vector ScreenPanDelta(Vector v)
    {
        if (ViewRot == 0) return v;
        var m = Matrix.Identity;
        m.Rotate(-ViewRot);
        return m.Transform(v);
    }

    /// <summary>Area to cover in pan-space; rotated views need the viewport's diagonal.</summary>
    private Rect ViewportRect()
    {
        if (ViewRot == 0) return new Rect(RenderSize);
        var diag = Math.Sqrt(RenderSize.Width * RenderSize.Width + RenderSize.Height * RenderSize.Height);
        return new Rect((RenderSize.Width - diag) / 2, (RenderSize.Height - diag) / 2, diag, diag);
    }

    // ---- smooth WASD panning (per-frame while keys are held) ----

    private readonly HashSet<Key> _panKeys = [];
    private long _lastPanTick;
    private const double PanTilesPerSecond = 14;

    public void SetPanKey(Key key, bool down)
    {
        var changed = down ? _panKeys.Add(key) : _panKeys.Remove(key);
        if (!changed) return;
        if (down && _panKeys.Count == 1)
        {
            _lastPanTick = System.Diagnostics.Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += OnPanFrame;
        }
        else if (!down && _panKeys.Count == 0)
        {
            CompositionTarget.Rendering -= OnPanFrame;
        }
    }

    /// <summary>Call on window deactivation - a KeyUp we never see would leave the view drifting.</summary>
    public void ClearPanKeys()
    {
        if (_panKeys.Count == 0) return;
        _panKeys.Clear();
        CompositionTarget.Rendering -= OnPanFrame;
    }

    private void OnPanFrame(object? sender, EventArgs e)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var dt = (now - _lastPanTick) / (double)System.Diagnostics.Stopwatch.Frequency;
        _lastPanTick = now;
        if (dt <= 0 || dt > 0.25) return;

        var v = new Vector(
            (_panKeys.Contains(Key.A) ? 1 : 0) - (_panKeys.Contains(Key.D) ? 1 : 0),
            (_panKeys.Contains(Key.W) ? 1 : 0) - (_panKeys.Contains(Key.S) ? 1 : 0));
        if (v.LengthSquared == 0) return;
        if (v.LengthSquared > 1) v.Normalize();

        _pan += ScreenPanDelta(v * (PanTilesPerSecond * Zoom * dt));
        ViewChanged?.Invoke();
        InvalidateVisual();
    }

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

    private (int X, int Y) CellAt(Point screen)
    {
        var p = ScreenToPanSpace(screen);
        return ((int)Math.Floor((p.X - _pan.X) / Zoom), (int)Math.Floor((p.Y - _pan.Y) / Zoom));
    }

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
        var mouse = ScreenToPanSpace(e.GetPosition(this));
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
                var stack = Doc.HitTestStack(rmbCell.X, rmbCell.Y);
                if (stack.Count > 0)
                {
                    // if nothing in this stack is already selected, grab the topmost so a
                    // plain right-click + Delete still acts on the visible part — but keep an
                    // existing box selection (>1) intact so its layer filter / group actions
                    // apply to the whole thing, wherever inside it you click
                    if (SelectedIds.Count <= 1 && !stack.Any(p => SelectedIds.Contains(p.Id)))
                    {
                        SelectedIds.Clear();
                        SelectedIds.Add(stack[0].Id);
                        SelectionChanged?.Invoke();
                        InvalidateVisual();
                    }
                    ContextMenuRequested?.Invoke(rmbCell);
                }
            }
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left || Doc is null) return;
        var cell = CellAt(screen);

        // double-click a placed part to flood-select every 1×1 tile of the same def
        // 4-connected to it — a magic wand for bulk-deleting/replacing a run of identical
        // tiles. Only when nothing is armed. Seed def comes from a lone selected part when
        // it covers the tile (so the RMB layer-picker can reach a buried conduit first),
        // else the topmost part here. Ctrl+double-click adds the region to the selection.
        if (e.ClickCount == 2 && ArmedPart is null)
        {
            Placement? seed = null;
            if (SelectedIds.Count == 1)
            {
                var sel = Doc.Placements.FirstOrDefault(p => SelectedIds.Contains(p.Id));
                if (sel is not null && Doc.Covers(sel, cell.X, cell.Y)) seed = sel;
            }
            seed ??= Doc.HitTest(cell.X, cell.Y);
            if (seed is not null)
            {
                if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) SelectedIds.Clear();
                foreach (var p in FloodSelect.Collect(Doc, seed)) SelectedIds.Add(p.Id);
                SelectionChanged?.Invoke();
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }

        if (ArmedPart is not null)
        {
            _stroke.Clear();
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                _drag = Drag.BoxFill;   // rubber-band a rect, fill it on release
                _dragStartCell = cell;
            }
            else
            {
                _drag = Drag.Paint;     // live placement, keeps painting while dragged
                PaintAt(cell);
            }
            CaptureMouse();
            e.Handled = true;
            return;
        }

        // A plain left-click on a tile the CURRENT selection covers drags that selection —
        // it does NOT re-hit-test to the topmost part. Without this, a part reached via the
        // right-click layer menu (a thruster buried under walls/conduits) is lost the instant
        // you click to move it, because the wall on top wins the hit-test. Ctrl-click still
        // falls through to toggle individual parts.
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var selected = SelectedPlacements();
            if (selected.Any(p => Doc.Covers(p, cell.X, cell.Y)))
            {
                if (selected.Any(p => !Doc.IsLocked(p)))
                {
                    _drag = Drag.Move;
                    _dragStartCell = cell;
                    _moveDelta = (0, 0);
                    CaptureMouse();
                }
                e.Handled = true;
                InvalidateVisual();
                return;
            }
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
            if (SelectedPlacements().Any(p => !Doc.IsLocked(p)))   // the primary airlock never drags
            {
                _drag = Drag.Move;
                _dragStartCell = cell;
                _moveDelta = (0, 0);
                CaptureMouse();
            }
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
            if (_drag == Drag.Paint) PaintAt(cell);
            if (ArmedPart is not null || _drag != Drag.None) InvalidateVisual();
            else if (Doc is not null) InvalidateVisual();   // hover outline
        }

        switch (_drag)
        {
            case Drag.BoxFill:
                InvalidateVisual();
                break;
            case Drag.Pan:
                _pan += ScreenPanDelta(screen - _dragStartScreen);
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

        if (drag == Drag.Paint)
            CommitStroke();

        if (drag == Drag.BoxFill && Doc is not null && ArmedPart is not null)
        {
            var end = CellAt(e.GetPosition(this));
            var hollow = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);   // Ctrl at release = outline only
            var (w, h) = GridMath.Size(ArmedPart.Item.Width, ArmedPart.Item.Height, ArmedRot);
            var (x0, x1) = (Math.Min(_dragStartCell.X, end.X), Math.Max(_dragStartCell.X, end.X));
            var (y0, y1) = (Math.Min(_dragStartCell.Y, end.Y), Math.Max(_dragStartCell.Y, end.Y));
            for (var y = y0; y + h - 1 <= y1; y += h)
                for (var x = x0; x + w - 1 <= x1; x += w)
                {
                    // border = touches the rect edge, or is the last footprint step on its axis
                    if (hollow && x != x0 && y != y0 && x + 2 * w - 1 <= x1 && y + 2 * h - 1 <= y1)
                        continue;
                    TryPlacePose(x, y, ArmedRot);
                }
            CommitStroke();
        }

        if (drag == Drag.Move && Doc is not null && (_moveDelta.X != 0 || _moveDelta.Y != 0))
            MoveRequested?.Invoke(SelectedPlacements().Where(p => !Doc.IsLocked(p)).ToList(), _moveDelta.X, _moveDelta.Y);

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

    // ---- painting (live placements, committed as one undo step on release) ----

    private void PaintAt((int X, int Y) cell)
    {
        if (Doc is null || ArmedPart is null) return;
        var (w, h) = GridMath.Size(ArmedPart.Item.Width, ArmedPart.Item.Height, ArmedRot);
        TryPlacePose(cell.X - (w - 1) / 2, cell.Y - (h - 1) / 2, ArmedRot);
    }

    private void TryPlacePose(int x, int y, int rot)
    {
        if (Doc is null || ArmedPart is null) return;
        var (w, h) = GridMath.Size(ArmedPart.Item.Width, ArmedPart.Item.Height, rot);
        var seen = new HashSet<(int, int, int)>();
        foreach (var pose in WithSymmetry(x, y, rot, w, h))
        {
            if (!seen.Add(pose)) continue;
            if (SameDefCovers(pose.X, pose.Y, w, h, ArmedPart.DefName)) continue;   // no stacking while painting
            // the placement law: silently skip any pose the game's Item.CheckFit would refuse
            // (each symmetry mirror is judged independently — legal ones land, illegal ones don't)
            if (!CheckFit.Check(Doc, ArmedPart, pose.X, pose.Y, pose.Rot, includeEnvelope: true).Ok) continue;
            var cmd = new PlaceCommand(new Placement
            {
                DefName = ArmedPart.DefName,
                X = pose.X,
                Y = pose.Y,
                Rot = ArmedPart.Item.HasSpriteSheet ? 0 : pose.Rot,
            });
            cmd.Do(Doc);
            _stroke.Add(cmd);
        }
    }

    /// <summary>
    /// The pose plus its mirror copies. Mirrored rotations: a vertical axis flips
    /// East/West (rot' = 360-rot), a horizontal axis flips North/South
    /// (rot' = 180-rot); both = 180 turn. Effective footprints are unchanged.
    /// </summary>
    private IEnumerable<(int X, int Y, int Rot)> WithSymmetry(int x, int y, int rot, int w, int h)
    {
        yield return (x, y, rot);
        if (SymMode == SymmetryMode.Off) yield break;
        var mx = 2 * SymCenter.X - (x + w - 1);
        var my = 2 * SymCenter.Y - (y + h - 1);
        if (SymMode is SymmetryMode.Vertical or SymmetryMode.Both)
            yield return (mx, y, GridMath.Norm(360 - rot));
        if (SymMode is SymmetryMode.Horizontal or SymmetryMode.Both)
            yield return (x, my, GridMath.Norm(180 - rot));
        if (SymMode == SymmetryMode.Both)
            yield return (mx, my, GridMath.Norm(rot + 180));
    }

    private bool SameDefCovers(int x, int y, int w, int h, string defName) =>
        Doc!.Placements.Any(p =>
        {
            if (p.DefName != defName) return false;
            var (pw, ph) = Doc.FootprintOf(p);
            return p.X < x + w && p.X + pw > x && p.Y < y + h && p.Y + ph > y;
        });

    private void CommitStroke()
    {
        if (_stroke.Count == 0) return;
        StrokeCommitted?.Invoke(_stroke.ToList());
        _stroke.Clear();
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

        var rotated = ViewRot != 0;
        if (rotated) dc.PushTransform(new RotateTransform(ViewRot, RenderSize.Width / 2, RenderSize.Height / 2));
        var view = ViewportRect();

        DrawGrid(dc, view);

        foreach (var p in Doc.DrawOrder())
        {
            var offset = _drag == Drag.Move && SelectedIds.Contains(p.Id) && !Doc.IsLocked(p) ? _moveDelta : (0, 0);
            DrawPlacement(dc, p, offset);
        }

        DrawIllegalCells(dc);
        DrawLeakCells(dc);
        DrawOutOfBounds(dc, view);
        DrawOriginMarker(dc);
        if (SymMode != SymmetryMode.Off) DrawSymmetryAxes(dc, view);

        foreach (var p in Doc.Placements.Where(p => SelectedIds.Contains(p.Id)))
        {
            var (w, h) = Doc.FootprintOf(p);
            (int X, int Y) offset = _drag == Drag.Move && !Doc.IsLocked(p) ? _moveDelta : (0, 0);
            dc.DrawRectangle(null, SelectPen, CellRect(p.X + offset.X, p.Y + offset.Y, w, h));
        }

        if (ArmedPart is not null && _hoverCell is { } hover)
        {
            var (w, h) = GridMath.Size(ArmedPart.Item.Width, ArmedPart.Item.Height, ArmedRot);
            var (gx, gy) = (hover.X - (w - 1) / 2, hover.Y - (h - 1) / 2);
            var fit = CheckFit.Check(Doc!, ArmedPart, gx, gy, ArmedRot, includeEnvelope: true);
            RaiseGhostReason(fit.Ok ? null : fit.Reason);

            // sub-floor reservation: the ring a part projects under walkable floor (the tanks),
            // shaded apart from the above-floor body so the two footprints read distinctly
            var under = UnderFloorCells(ArmedPart, gx, gy, ArmedRot).ToList();
            foreach (var (cx, cy) in under)
                dc.DrawRectangle(SubfloorFill, null, CellRect(cx, cy, 1, 1));

            dc.PushOpacity(0.55);
            DrawSprite(dc, ArmedPart, gx, gy, ArmedRot, ghost: true);
            dc.Pop();

            foreach (var (cx, cy) in fit.FailedCells)   // failing cells override the sub-floor shade
                dc.DrawRectangle(HazardFill, null, CellRect(cx, cy, 1, 1));

            if (under.Count > 0)
            {
                // dashed outline round the whole reservation, the solid validity outline hugging the body
                dc.DrawRectangle(null, SubfloorPen, CellRect(gx, gy, w, h));
                var (bx, by, bw, bh) = AboveFloorBounds(ArmedPart, gx, gy, ArmedRot);
                dc.DrawRectangle(null, fit.Ok ? GhostOkPen : GhostBadPen, CellRect(bx, by, bw, bh));
            }
            else
            {
                dc.DrawRectangle(null, fit.Ok ? GhostOkPen : GhostBadPen, CellRect(gx, gy, w, h));
            }
        }
        else
        {
            RaiseGhostReason(null);
            if (_hoverCell is { } cell && _drag == Drag.None)
                dc.DrawRectangle(null, HoverPen, CellRect(cell.X, cell.Y, 1, 1));
        }

        if (_drag == Drag.Band && _hoverCell is { } bandEnd)
        {
            var (bx0, bx1) = (Math.Min(_dragStartCell.X, bandEnd.X), Math.Max(_dragStartCell.X, bandEnd.X));
            var (by0, by1) = (Math.Min(_dragStartCell.Y, bandEnd.Y), Math.Max(_dragStartCell.Y, bandEnd.Y));
            dc.DrawRectangle(BandBrush, BandPen, CellRect(bx0, by0, bx1 - bx0 + 1, by1 - by0 + 1));
        }

        if (_drag == Drag.BoxFill && _hoverCell is { } fillEnd)
        {
            var (fx0, fx1) = (Math.Min(_dragStartCell.X, fillEnd.X), Math.Max(_dragStartCell.X, fillEnd.X));
            var (fy0, fy1) = (Math.Min(_dragStartCell.Y, fillEnd.Y), Math.Max(_dragStartCell.Y, fillEnd.Y));
            var (fw, fh) = (fx1 - fx0 + 1, fy1 - fy0 + 1);
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && fw > 2 && fh > 2)
            {
                // hollow preview: four border strips
                dc.DrawRectangle(BandBrush, null, CellRect(fx0, fy0, fw, 1));
                dc.DrawRectangle(BandBrush, null, CellRect(fx0, fy1, fw, 1));
                dc.DrawRectangle(BandBrush, null, CellRect(fx0, fy0 + 1, 1, fh - 2));
                dc.DrawRectangle(BandBrush, null, CellRect(fx1, fy0 + 1, 1, fh - 2));
                dc.DrawRectangle(null, BandPen, CellRect(fx0, fy0, fw, fh));
            }
            else
            {
                dc.DrawRectangle(BandBrush, BandPen, CellRect(fx0, fy0, fw, fh));
            }
        }

        if (rotated) dc.Pop();
    }

    /// <summary>Hazard-tint the tiles of existing illegal placements (socket-law breaches from edits or opened files).</summary>
    private void DrawIllegalCells(DrawingContext dc)
    {
        foreach (var (x, y) in _illegalCells)
            dc.DrawRectangle(HazardFill, null, CellRect(x, y, 1, 1));
    }

    /// <summary>Tint the unsealed tiles of a compartment the Ship Rating report flagged as leaking to space.</summary>
    private void DrawLeakCells(DrawingContext dc)
    {
        foreach (var (x, y) in _leakCells)
            dc.DrawRectangle(LeakFill, LeakPen, CellRect(x, y, 1, 1));
    }

    /// <summary>
    /// Hazard-stripe every area beyond a docking port's mating face - the game's
    /// GetAirlockBounds envelope, made visible.
    /// </summary>
    private void DrawOutOfBounds(DrawingContext dc, Rect view)
    {
        var zones = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var p in Doc!.Placements)
        {
            var part = Doc.Part(p);
            if (part is null || !ProblemScan.IsDocksys(part, Doc.Catalog)) continue;
            if (!ProblemScan.TryGetFace(part, p, out var axisY, out var dir, out var face)) continue;

            var faceScreen = (axisY ? _pan.Y : _pan.X) + face * Zoom;
            var zone = axisY
                ? dir < 0
                    ? new Rect(view.X, view.Y, view.Width, Math.Max(0, faceScreen - view.Y))
                    : new Rect(view.X, faceScreen, view.Width, Math.Max(0, view.Bottom - faceScreen))
                : dir < 0
                    ? new Rect(view.X, view.Y, Math.Max(0, faceScreen - view.X), view.Height)
                    : new Rect(faceScreen, view.Y, Math.Max(0, view.Right - faceScreen), view.Height);
            if (zone.Width > 0 && zone.Height > 0) zones.Children.Add(new RectangleGeometry(zone));
        }
        if (zones.Children.Count > 0) dc.DrawGeometry(OobBrush, null, zones);
    }

    private void DrawSymmetryAxes(DrawingContext dc, Rect view)
    {
        var cx = Math.Round(_pan.X + (SymCenter.X + 0.5) * Zoom) + 0.5;
        var cy = Math.Round(_pan.Y + (SymCenter.Y + 0.5) * Zoom) + 0.5;
        if (SymMode is SymmetryMode.Vertical or SymmetryMode.Both)
            dc.DrawLine(SymPen, new Point(cx, view.Y), new Point(cx, view.Bottom));
        if (SymMode is SymmetryMode.Horizontal or SymmetryMode.Both)
            dc.DrawLine(SymPen, new Point(view.X, cy), new Point(view.Right, cy));
        dc.DrawRectangle(null, SymPen, CellRect(SymCenter.X, SymCenter.Y, 1, 1));
    }

    private void DrawGrid(DrawingContext dc, Rect view)
    {
        var pen = Zoom < 24 ? FaintGridPen : GridPen;   // fainter when zoomed out, never gone
        var x0 = (int)Math.Floor((view.X - _pan.X) / Zoom);
        var y0 = (int)Math.Floor((view.Y - _pan.Y) / Zoom);
        var x1 = (int)Math.Ceiling((view.Right - _pan.X) / Zoom);
        var y1 = (int)Math.Ceiling((view.Bottom - _pan.Y) / Zoom);

        for (var x = x0; x <= x1; x++)
        {
            var sx = Math.Round(_pan.X + x * Zoom) + 0.5;
            dc.DrawLine(x == 0 ? AxisPen : pen, new Point(sx, view.Y), new Point(sx, view.Bottom));
        }
        for (var y = y0; y <= y1; y++)
        {
            var sy = Math.Round(_pan.Y + y * Zoom) + 0.5;
            dc.DrawLine(y == 0 ? AxisPen : pen, new Point(view.X, sy), new Point(view.Right, sy));
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
            var at = new Point(rect.X + 3, rect.Bottom + 2);
            if (ViewRot != 0) dc.PushTransform(new RotateTransform(-ViewRot, at.X, at.Y));   // keep text upright
            dc.DrawText(label, at);
            if (ViewRot != 0) dc.Pop();
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

        // Draw the sprite at its OWN texture size (round(px/16) tiles — Item.SetData's
        // vScale), centered on the socket footprint. For most parts the sprite fills the
        // footprint; for the large tanks a 3x3 canister sprite sits centered in a 7x7
        // footprint whose outer ring is abstracted sub-floor storage, not the tank body.
        var (effW, effH) = GridMath.Size(part.Item.Width, part.Item.Height, rot);
        var (visW, visH) = Sprites!.SpriteTiles(part);
        var bmp = Sprites.Sprite(part);
        var center = new Point(_pan.X + (gx + effW / 2.0) * Zoom, _pan.Y + (gy + effH / 2.0) * Zoom);
        var sprite = new Rect(center.X - visW * Zoom / 2, center.Y - visH * Zoom / 2, visW * Zoom, visH * Zoom);

        var norm = GridMath.Norm(rot);
        if (norm != 0) dc.PushTransform(new RotateTransform(norm, center.X, center.Y));
        dc.DrawImage(bmp, sprite);
        if (norm != 0) dc.Pop();
    }
}
