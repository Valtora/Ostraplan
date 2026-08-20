using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

public enum SymmetryMode { Off, Vertical, Horizontal, Both }

/// <summary>The armed ghost's illegality status. <see cref="WillPlace"/> is true when the pose fails the core-only
/// placement law but a modded-override lets it place anyway (amber ghost) — false when it is hard-blocked (red).
/// <paramref name="Advisory"/> is a third state: the pose is fully legal but an unmet soft requirement (e.g. an
/// overhead light with no adjacent power conduit) is worth noting — a gentler "places, but …" than an override.</summary>
public readonly record struct GhostStatus(string Reason, bool WillPlace, bool Advisory = false);

/// <summary>
/// The tile grid: renders the document's sprites (16 px art scaled with
/// nearest-neighbor), and turns mouse input into place/select/move/pan/zoom.
/// Mutations are NOT applied here - they are raised as events and the window
/// pushes them through the command stack.
/// </summary>
public sealed class ShipCanvas : FrameworkElement
{
    // Screen px per tile. 16 == 1x (the 16 px sprite drawn at native size). Zooming moves on a smooth 0.1x
    // lattice (1.6 px/tile per wheel notch, Shift accelerates 5x to 0.5x/notch) between 0.125x (frames a whole
    // station) and 8x. Values snap to the lattice so the readout stays clean; NearestNeighbor keeps the pixel art
    // crisp at every step (non-integer multiples trade a little scaling evenness for the finer control).
    private const double BaseTilePx = 16.0;      // 1x
    private const double MinZoomPx = 2.0;        // 0.125x
    private const double MaxZoomPx = 128.0;      // 8x
    private const double ZoomNotch = 0.1;        // one wheel notch / keyboard step, in zoom-multiplier units
    private const double FastZoomFactor = 5.0;   // Shift-zoom accelerator (0.5x per notch)

    /// <summary>Snap a px-per-tile value to the 0.1x zoom lattice, clamped to the zoom range.</summary>
    private static double SnapZoom(double px) =>
        Math.Clamp(Math.Round(px / BaseTilePx / ZoomNotch) * ZoomNotch * BaseTilePx, MinZoomPx, MaxZoomPx);

    /// <summary>Largest lattice zoom that still fits <paramref name="px"/> (used by fit/focus framing).</summary>
    private static double SnapZoomDown(double px) =>
        Math.Clamp(Math.Floor(px / BaseTilePx / ZoomNotch) * ZoomNotch * BaseTilePx, MinZoomPx, MaxZoomPx);

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
    private static readonly Pen GhostOverridePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0xB0, 0x40)), 2));   // amber: modded, illegal by core rules, but placing anyway
    // dark halo under the ghost's facing needle, so the cue reads over a busy sprite whatever colour it lands on
    private static readonly Color NeedleHaloColor = Color.FromArgb(0xC0, 0x0A, 0x0E, 0x12);
    private static readonly Pen NeedleHaloPen = Frozen(new Pen(new SolidColorBrush(NeedleHaloColor), 4.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Brush NeedleHaloBrush = Frozen(new SolidColorBrush(NeedleHaloColor));
    // per-cell hazard fill for both a ghost's failing cells and existing illegal placements (same red vocabulary)
    private static readonly Brush HazardFill = Frozen(new SolidColorBrush(Color.FromArgb(0x66, 0xD6, 0x45, 0x45)));
    private static readonly Brush OverrideFill = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0xE0, 0xB0, 0x40)));   // amber tint for an overridden modded part's failing cells
    // the sub-floor reservation a part projects under walkable floor (the tanks' 7x7 ring vs their 3x3 body)
    private static readonly Brush SubfloorFill = Frozen(new SolidColorBrush(Color.FromArgb(0x33, 0x6A, 0x8E, 0xB8)));
    private static readonly Brush LeakFill = Frozen(new SolidColorBrush(Color.FromArgb(0x77, 0x3F, 0xC8, 0xE0)));
    private static readonly Pen LeakPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xC8, 0x5F, 0xE0, 0xF0)), 1));
    private static readonly Brush AirFill = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0x5A, 0xD0, 0x9A)));   // fill-region highlight (double-click enclosed air)
    private static readonly Pen AirPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0x6A, 0xE0, 0xB0)), 1));
    private static readonly Pen SubfloorPen = MakeSubfloorPen();
    // PowerViz: a soft cyan glow under the animated lit-conduit flow, a dim dashed red for orphaned runs, and
    // connector nubs (cyan = power input, green = power output) with a dark outline. The flowing lit pen is built
    // per-frame from the current phase, so it isn't frozen here.
    private static readonly Pen PowerGlowPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0x38, 0xC8, 0xF0)), 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Color PowerLitColor = Color.FromRgb(0x9A, 0xF0, 0xFF);
    private static readonly Pen PowerOffPen = MakePowerOffPen();
    // Connector badges: a lightning glyph + IN/OUT label on a dark pill, coloured blue for a power input (draws from
    // the conduit) and green for an output (feeds it) — a clearer cue than a plain square that vanished into the flow.
    private static readonly Brush ConnBgBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0x0A, 0x0E, 0x12)));
    private static readonly Brush ConnInBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x54, 0xAE, 0xFF)));    // input accent (blue, distinct from the cyan flow)
    private static readonly Brush ConnOutBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x5A, 0xD8, 0x74)));   // output accent (green)
    private static readonly Pen ConnInPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x7C, 0xC2, 0xFF)), 1.4));
    private static readonly Pen ConnOutPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x84, 0xE6, 0x99)), 1.4));
    private static readonly Brush ConnTextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0xF7, 0xFF)));
    private static readonly Typeface ConnTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Geometry BoltGeometry = MakeBolt();
    private static readonly Brush PowerWarnBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0xE0, 0xB0, 0x40)));   // unconnected-plug warning fill
    private static readonly Pen PowerWarnPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0xF0, 0xB8, 0x40)), 2));

    // Device signal connections (wire mode). Violet, to stand apart from the blue selection and the cyan power flow.
    // Drawn heavy: a wire crosses a busy, high-contrast deck at any angle, and a hairline was lost in it. The
    // preview matches the committed width so a wire doesn't change weight the moment it is committed — the dashes
    // and the lower alpha are what separate the two.
    private const double WireWidth = 4;
    private const double WireDotRadius = 4;
    private static readonly Brush WireDotBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC0, 0x8C, 0xF0)));
    private static readonly Pen WirePen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 0xC0, 0x8C, 0xF0)), WireWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Pen WirePreviewPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0xD8, 0xB0, 0xFF)), WireWidth) { DashStyle = new DashStyle([2, 2], 0), StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Pen WireNodePen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0xC0, 0x8C, 0xF0)), 1.2));
    private static readonly Pen WireSourcePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xD8, 0xB0, 0xFF)), 2.5));

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static Pen MakeSymPen()
    {
        // A bolder, brighter dashed cyan than before — the axes were easy to lose against the ship.
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0xE6, 0x4A, 0xE4, 0xE4)), 2.5)
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

    private static Pen MakePowerOffPen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0xC8, 0x55, 0x50)), 2)
        { DashStyle = new DashStyle([3, 3], 0), StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }

    /// <summary>A lightning-bolt glyph in a unit box (0..1, y-down), for the power connector badges.</summary>
    private static Geometry MakeBolt()
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(0.60, 0.02), true, true);
            c.PolyLineTo(
                [new Point(0.18, 0.56), new Point(0.45, 0.56), new Point(0.34, 0.98),
                 new Point(0.82, 0.42), new Point(0.53, 0.42)],
                true, false);
        }
        g.Freeze();
        return g;
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

    // Screen px per tile. Sprite sizes are baked into the cached ship drawing at this zoom, so a change must
    // rebuild it — but pan is applied as a transform (see StaticShip / OnRender), so panning never does.
    public double Zoom
    {
        get => _zoom;
        private set
        {
            if (_zoom == value) return;
            _zoom = value;
            _staticShip = null;
            _powerGeoDirty = true;   // segment geometries bake tile centres at this zoom
            _roomGeoDirty = true;    // room fills bake cell rects at this zoom
            _walkGeoDirty = true;    // walk-zone fills bake cell rects at this zoom
            // Light Viz is zoom-independent: its composite is a doc-space bitmap scaled at draw time
        }
    }
    private double _zoom = 48;
    private Vector _pan;                             // screen position of world origin
    private bool _panInitialized;
    private bool _fitPending;                        // FitContent was asked for before this canvas had a size — see FitContentWhenReady

    public SymmetryMode SymMode { get; private set; }
    public (int X, int Y) SymCenter { get; private set; }
    public int ViewRot { get; private set; }   // plan-view rotation, 90-degree steps (Q/E)

    public bool ShowZones { get; private set; }        // zone overlay visibility (toolbar/key toggle)
    public Guid? ActiveZoneId { get; private set; }    // the zone being painted, or null when not in zone-paint mode
    public bool ShowPower { get; private set; }        // PowerViz conduit overlay visibility (toolbar/P toggle)
    public bool ShowRooms { get; private set; }        // RoomViz compartment overlay visibility (toolbar/C toggle)
    public bool ShowLight { get; private set; }        // Light Viz overlay (toolbar/L toggle) — OFF by default: the plan opens on the plain sprite ship, not the in-game dark (an unlit airlock reads as black)
    public bool ShowWalk { get; private set; }         // WalkViz crew-access overlay (toolbar/K toggle)

    /// <summary>When true the canvas is in <b>wire mode</b>: click a signalable device to arm it as the signal
    /// source, then click another to connect them (click a connected one again to disconnect). See
    /// <see cref="DeviceLink"/> / <see cref="LinkToggleRequested"/>. Wires always render; this mode drives editing.</summary>
    public bool WireMode { get; private set; }
    private Placement? _wireSource;   // the armed signal source awaiting a target (wire mode)

    /// <summary>
    /// When true the canvas is in <b>Surfaces mode</b>: the deck is the subject. Everything that is not a wall or
    /// floor draws at <see cref="SurfaceGhostOpacity"/> and steps out of the way of clicks, and a 1×1 wall/floor
    /// brush re-skins the part already on a tile instead of being refused for landing on it (see
    /// <see cref="SurfacePaint"/>). Off, nothing about painting or picking changes.
    /// </summary>
    public bool SurfaceMode { get; private set; }

    /// <summary>How the pattern brushes alternate across a surface stroke (see <see cref="SurfacePattern"/>).
    /// Ignored outside Surfaces mode and while <see cref="PatternB"/> is unset.</summary>
    public SurfacePattern Pattern { get; private set; }

    /// <summary>What a surface stroke may do to a tile: re-skin only (the default), re-skin and fill, or fill only.
    /// See <see cref="SurfacePaintMode"/>.</summary>
    public SurfacePaintMode PaintMode { get; private set; } = SurfacePaintMode.Replace;

    /// <summary>Which layer Surfaces mode treats as the subject (see <see cref="SurfaceFocus"/>) — what stays
    /// bright, and what a click lands on.</summary>
    public SurfaceFocus LayerFocus { get; private set; }

    /// <summary>The secondary surface brush — the other half of a checkerboard or stripe. Null for a plain
    /// single-brush stroke. Only meaningful alongside a primary <see cref="ArmedPart"/> of the same class.</summary>
    public PartDef? PatternB { get; private set; }

    /// <summary>Opacity of the ghosted (non-surface) layers in Surfaces mode. A user preference: enough to keep
    /// the reactor and the beds as landmarks while painting round them, without them reading as the subject.</summary>
    public double SurfaceGhostOpacity
    {
        get => _surfaceGhostOpacity;
        set
        {
            var next = Math.Clamp(value, 0.0, 1.0);
            if (Math.Abs(_surfaceGhostOpacity - next) < 0.0001) return;
            _surfaceGhostOpacity = next;
            _staticShip = null;   // the opacity is baked into the cached drawing
            InvalidateVisual();
        }
    }
    private double _surfaceGhostOpacity = 0.15;

    /// <summary>When true, a MODDED part may be placed where the (core-only) placement law says it doesn't fit — it's
    /// placed and flagged as a warning rather than hard-blocked. Core parts are always enforced. Set from
    /// <see cref="AppSettings.AllowModdedOverrides"/>.</summary>
    public bool AllowModdedOverrides { get; set; }

    private enum Drag { None, Pan, Move, Band, Paint, BoxFill, ZonePaint, ZoneBox, Aim }
    private Drag _drag;
    private Point _dragStartScreen;
    private (int X, int Y) _dragStartCell;
    private (int X, int Y) _moveDelta;
    private (int X, int Y)? _hoverCell;
    private readonly List<IDocCommand> _stroke = [];   // live placements of the current paint/fill stroke
    private HashSet<(int X, int Y)>? _zoneWorking;      // the active zone's tiles being edited this stroke (preview), null when idle
    private HashSet<(int X, int Y)> _zoneBefore = [];   // the active zone's tiles at stroke start (for the undo snapshot)
    private bool _zoneErase;                            // this stroke removes tiles (Ctrl) rather than adds
    private IReadOnlyList<(int X, int Y)> _illegalCells = [];   // tiles of existing illegal placements (from ProblemScan)
    private IReadOnlyList<(int X, int Y)> _leakCells = [];      // unsealed tiles of a leaking compartment (from the Ship Rating report)
    private HashSet<(int X, int Y)> _airSelection = [];         // an enclosed "air" region highlighted for a fill (double-click empty space, then arm + Enter)
    private PowerOverlay _powerOverlay = PowerOverlay.Empty;    // the computed power network (doc coords), pushed by the window after each scan
    private RoomOverlay _roomOverlay = RoomOverlay.Empty;       // the certified compartments (doc coords), pushed by the window after each scan
    // RoomViz bakes exactly like PowerViz: the per-room tile fills become one frozen Geometry each in pan-zero
    // space (a station room is hundreds of cells — a DrawRectangle each, every frame, is what made panning crawl),
    // and the labels' FormattedText is built once per overlay rather than per frame. Both rebuild only when the
    // overlay data or the zoom changes; panning is then a transform over a handful of baked strokes.
    private List<(Geometry Geo, Brush Fill)>? _roomGeos;
    private List<RoomLabel>? _roomLabels;
    private bool _roomGeoDirty;
    private WalkOverlay _walkOverlay = WalkOverlay.Empty;   // the crew-access analysis (doc coords), pushed by the window after each scan
    // WalkViz bakes exactly like RoomViz: one frozen per-zone Geometry in pan-zero space, rebuilt only when the
    // overlay data or the zoom changes, so panning stays a transform over a handful of strokes.
    private List<(Geometry Geo, Brush Fill)>? _walkGeos;
    private bool _walkGeoDirty;
    private LightScene _lightScene = LightScene.Empty;   // the resolved lights/blocks/glows (doc coords), pushed by the window after each scan
    // Light Viz renders the game's deferred light pass in software at the native 16 px/tile: the ship's albedo and
    // normal maps are baked to doc-space bitmaps on the UI thread, then a worker runs the exact ported pipeline
    // (VisibilityMesh geometry + LoSPass shading + screen-blend + glow decals) and hands back one frozen bitmap
    // OnRender scales like a sprite. Pan/zoom never recompute it; only a new scene (edit/scan) does.
    private System.Windows.Media.Imaging.BitmapSource? _lightImage;   // the composited lit ship (premultiplied, 16 px/tile), frozen
    private Rect _lightImageRect;        // the doc-tile rect the composite covers
    private int _lightJob;               // monotonic token so a stale worker result can't clobber a newer one
    // Simulate: the damage heat map and the ghost strike path. Neither comes from the background analysis scan the
    // other overlays use — damage is a view over session state the user drives one strike at a time, and the ghost
    // path follows the cursor — so both are pushed straight in and neither is baked.
    private DamageOverlay _damageOverlay = DamageOverlay.Empty;
    private (Point Start, Point End)? _ghostPath;      // doc coords; drawn while a Simulate dialog is aiming
    private Point? _strikePivot;                       // doc coords; the fixed point every micrometeoroid converges on
    private bool _aiming;                              // a Simulate dialog owns the cursor: report angles, do not edit

    private double _powerPhase;                                 // animated dash offset for the lit conduit flow
    private bool _powerAnimating;                               // whether the per-frame flow tick is hooked
    private TimeSpan _lastPowerTime;                            // last CompositionTarget.Rendering time, to advance the phase by elapsed seconds
    private TimeSpan _lastPowerDraw;                            // last frame we actually repainted the flow (throttles the animation)
    // The power segments baked into frozen geometries in pan-zero space at the current zoom (one DrawGeometry each,
    // not hundreds of DrawLine calls) — rebuilt only when the overlay data or the zoom changes, then panned by a
    // transform like the ship. This is what keeps panning smooth with PowerViz on.
    private Geometry? _powerLitGeo;
    private Geometry? _powerOffGeo;
    private bool _powerGeoDirty;
    private GhostStatus? _lastGhostReason;                     // dedupe GhostReasonChanged
    private bool _armedLoose;                                  // the armed brush is an Items-tab loose item, not structure (single-click drop, no CheckFit)
    private bool _bandFilter;                                  // the current band select was Shift-initiated (offer the layer filter chips on release)

    /// <summary>The selected loose floor item (see <see cref="LooseObject"/>), or null. Distinct from the
    /// placement selection (<see cref="SelectedIds"/>): a loose item is a non-structural overlay, so it carries its
    /// own single-select highlight and Delete handling.</summary>
    public LooseObject? SelectedLoose { get; private set; }

    /// <summary>True while the armed palette brush is a loose item (Items tab) rather than buildable structure.</summary>
    public bool ArmedLoose => _armedLoose;

    // Cached vector drawing of every placement's sprite — the expensive part of a frame (DrawOrder
    // sort + per-tile autotile + DrawImage over the whole ship). Rebuilt only when the ship content
    // or the pan/zoom mapping changes; reused across the frames of a band-select or box-fill drag,
    // where the ship is static and only the overlay rectangle moves. Bypassed mid-move/paint.
    private DrawingGroup? _staticShip;

    public event Action<IReadOnlyList<IDocCommand>>? StrokeCommitted;
    public event Action? SymmetryChanged;
    public event Action<IReadOnlyList<Placement>, int, int>? MoveRequested;
    public event Action? SelectionChanged;
    public event Action? LooseSelectionChanged;   // the selected loose floor item changed (update the inspector)
    public event Action<(int X, int Y)?>? HoverChanged;
    public event Action? Disarmed;
    public event Action? ViewChanged;
    public event Action<(int X, int Y)>? ContextMenuRequested;   // right-clicked tile; window builds the layer picker
    public event Action<string, int>? BrushPicked;   // Alt+LMB eyedropper: arm the def AND the pose of the part under the cursor
    public event Action? ArmedChanged;   // the brush changed: a different part, a new rotation, or disarmed
    public event Action<int>? AirSelectionChanged;   // the highlighted air region's tile count changed (0 = cleared)
    public event Action<(int W, int H)?>? SelectionSizeChanged;   // live WxH (tiles) of a rubber-band box drag; null = no box drag in progress
    public event Action? BandFilterRequested;   // a Shift+drag band select finished; window offers the layer filter chips
    public event Action<IReadOnlyList<(Placement P, int X, int Y, int Rot)>>? PosesRequested;   // a symmetric move: per-part target poses, committed as one SetPosesCommand
    public event Action<(int X, int Y)>? LooseContextMenuRequested;   // right-clicked a loose floor item; window builds its menu
    public event Action<GhostStatus?>? GhostReasonChanged;   // the armed ghost's illegality status, null when legal/disarmed
    public event Action? ShowZonesChanged;              // the zone overlay was toggled (update the toolbar caption)
    public event Action? ShowPowerChanged;              // the PowerViz overlay was toggled (update the toolbar caption + trigger a scan)
    public event Action? ShowRoomsChanged;              // the RoomViz overlay was toggled (update the toolbar caption + trigger a scan)
    public event Action? ShowLightChanged;              // the Light Viz overlay was toggled (update the menu check + trigger a scan)
    public event Action? ShowWalkChanged;               // the WalkViz overlay was toggled (update the toolbar caption + trigger a scan)
    public event Action? WireModeChanged;               // wire mode was toggled (update the hint / menu check)
    public event Action? SurfaceModeChanged;            // Surfaces mode was toggled (update the toolbar highlight / hint / pattern bar)
    public event Action<Placement, Placement>? LinkToggleRequested;   // connect source→target, or disconnect if already linked
    public event Action? ActiveZoneChanged;             // the painted zone changed (sync the zones panel selection)
    /// <summary>A zone paint/erase/box/room-fill stroke finished: (zone id, tiles before, tiles after). The window
    /// turns this into one <c>SetZoneTilesCommand</c> undo step. Not raised when the stroke changed nothing.</summary>
    public event Action<Guid, IReadOnlyCollection<(int X, int Y)>, IReadOnlyCollection<(int X, int Y)>>? ZoneStrokeCommitted;

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
        if (Doc is not null) Doc.Changed -= OnContentChanged;
        Doc = doc;
        doc.Changed += OnContentChanged;
        SelectedIds.Clear();
        SelectionChanged?.Invoke();
        ActiveZoneId = null;   // a zone id from the previous document is stale
        _zoneWorking = null;
        _staticShip = null;
        ClearAirSelection();
        InvalidateVisual();
    }

    /// <summary>The document's content changed — drop the cached ship drawing and repaint. Any highlighted air
    /// region is now potentially stale (a wall may have moved), so drop it too.</summary>
    private void OnContentChanged()
    {
        _staticShip = null;
        ClearAirSelection();
        InvalidateVisual();
    }

    /// <summary>The tiles of the enclosed air region currently highlighted for a fill (see <see cref="FillAirSelection"/>).</summary>
    public IReadOnlyCollection<(int X, int Y)> AirSelection => _airSelection;

    /// <summary>Drop the highlighted air region (if any) and repaint.</summary>
    public void ClearAirSelection()
    {
        if (_airSelection.Count == 0) return;
        _airSelection = [];
        AirSelectionChanged?.Invoke(0);
        InvalidateVisual();
    }

    /// <summary>
    /// Fill every tile of the highlighted air region with the armed part — wherever it fits (the game's CheckFit)
    /// and a same-def part isn't already there — as one undo step, then clear the region. No-op without an armed
    /// structural part and a non-empty air selection. The region itself is chosen by double-clicking enclosed
    /// ("compartmentalized") empty space; open-to-space areas never become a selection, so a fill can't leak.
    /// </summary>
    public void FillAirSelection()
    {
        if (Doc is null || ArmedPart is null || _armedLoose || _airSelection.Count == 0) return;
        var (w, h) = GridMath.Size(ArmedPart.Item.Width, ArmedPart.Item.Height, ArmedRot);
        _stroke.Clear();
        // one coalesced Changed for the whole fill (a big compartment is many tiles), like the box-fill path
        using (Doc.SuspendChanged())
            foreach (var (x, y) in _airSelection)
                TryPlacePose(x - (w - 1) / 2, y - (h - 1) / 2, ArmedRot);
        CommitStroke();
        ClearAirSelection();
    }

    /// <summary>Pan or view-rotation changed: just notify listeners. The cached ship drawing is baked pan- and
    /// rotation-independently (both are applied as transforms in <see cref="OnRender"/>), so it survives — only a
    /// zoom change (via the <see cref="Zoom"/> setter) or a content change (<see cref="OnContentChanged"/>) drops it.
    /// This is what makes WASD/drag panning cheap on a big ship: a frame is a transform + one cached blit, not a full
    /// DrawOrder + autotile rebuild.</summary>
    private void RaiseViewChanged()
    {
        ViewChanged?.Invoke();
    }

    public void SetArmed(PartDef? part, bool loose = false)
    {
        ArmedPart = part;
        _armedLoose = loose && part is not null;
        if (part is not null)
        {
            SelectedIds.Clear();
            SelectionChanged?.Invoke();
            ClearLooseSelection();
            SetActiveZone(null);   // arming a part leaves zone-paint mode (the two modes are mutually exclusive)
        }
        ArmedChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>
    /// Set the brush's rotation outright, rather than stepping it with <see cref="RotateArmed"/>. This is how the
    /// eyedropper adopts the pose of the part it picked: picking a part that sits at 270° should hand you that part
    /// at 270°, not at whatever angle the last brush happened to be left at. Stored for sheet items too (they can't
    /// turn, but the value outlives them and applies to the next part that can), so every consumer keeps its own
    /// existing sheet rule rather than depending on this being pre-pinned.
    /// </summary>
    public void SetArmedRot(int rot)
    {
        var next = GridMath.Norm(rot);
        if (next == ArmedRot) return;
        ArmedRot = next;
        ArmedChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Clear the loose-item selection (if any) and repaint.</summary>
    public void ClearLooseSelection()
    {
        if (SelectedLoose is null) return;
        SelectedLoose = null;
        InvalidateVisual();
    }

    /// <summary>Toggle the zone overlay on/off. Zones auto-show while one is active for painting.</summary>
    public void ToggleZones()
    {
        ShowZones = !ShowZones;
        ShowZonesChanged?.Invoke();
        InvalidateVisual();
    }

    public void SetShowZones(bool on)
    {
        if (ShowZones == on) return;
        ShowZones = on;
        ShowZonesChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Dash-offset units per second the lit-conduit flow advances (cosmetic "energised" motion).</summary>
    private const double PowerFlowSpeed = 6.0;

    /// <summary>Toggle the PowerViz conduit overlay. The window listens on <see cref="ShowPowerChanged"/> to update
    /// the toolbar caption and schedule a scan — the network is only computed off-thread while the overlay is on.</summary>
    public void TogglePower() => SetShowPower(!ShowPower);

    public void SetShowPower(bool on)
    {
        if (ShowPower == on) return;
        ShowPower = on;
        if (!on) _powerOverlay = PowerOverlay.Empty;   // drop stale data so it can't flash on re-enable
        UpdatePowerAnimation();
        ShowPowerChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>True while a signal source is armed and awaiting its target (wire mode).</summary>
    public bool WireSourceArmed => _wireSource is not null;

    /// <summary>Drop the armed signal source without leaving wire mode (Esc, first press).</summary>
    public void ClearWireSource()
    {
        if (_wireSource is null) return;
        _wireSource = null;
        InvalidateVisual();
    }

    /// <summary>Toggle wire mode (device signal connections). Leaving the mode clears the armed source.</summary>
    public void ToggleWireMode() => SetWireMode(!WireMode);

    public void SetWireMode(bool on)
    {
        if (WireMode == on) return;
        WireMode = on;
        _wireSource = null;
        WireModeChanged?.Invoke();
        InvalidateVisual();
    }

    // ---- Surfaces mode (paint the deck) ----

    /// <summary>Toggle Surfaces mode (see <see cref="SurfaceMode"/>).</summary>
    public void ToggleSurfaceMode() => SetSurfaceMode(!SurfaceMode);

    public void SetSurfaceMode(bool on)
    {
        if (SurfaceMode == on) return;
        SurfaceMode = on;
        // Light Viz composites the whole ship into one bitmap, so there is no layer left to ghost — the two views
        // can't both be right about what the canvas is showing. Surfaces wins while it is on, and the toolbar says
        // so rather than leaving a lit button over an unlit ship.
        if (on) SetShowLight(false);
        _staticShip = null;   // the ghosting is baked into the cached drawing
        SurfaceModeChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Set the tiling pattern for surface strokes.</summary>
    public void SetPattern(SurfacePattern pattern)
    {
        if (Pattern == pattern) return;
        Pattern = pattern;
        InvalidateVisual();   // the ghost previews the pattern-resolved tile
    }

    /// <summary>Set (or clear) the secondary pattern brush.</summary>
    public void SetPatternB(PartDef? part)
    {
        if (ReferenceEquals(PatternB, part)) return;
        PatternB = part;
        InvalidateVisual();
    }

    /// <summary>Set what a surface stroke may do to a tile (re-skin, fill, or both).</summary>
    public void SetPaintMode(SurfacePaintMode mode)
    {
        if (PaintMode == mode) return;
        PaintMode = mode;
        InvalidateVisual();   // the ghost says whether this tile would take the stroke
    }

    /// <summary>Set which layer is the subject: what stays bright, and what a click lands on. Named for the layer
    /// rather than plain "focus", which on a <see cref="FrameworkElement"/> already means keyboard focus.</summary>
    public void SetLayerFocus(SurfaceFocus focus)
    {
        if (LayerFocus == focus) return;
        LayerFocus = focus;
        _staticShip = null;   // the ghosting is baked into the cached drawing
        InvalidateVisual();
    }

    /// <summary>The armed brush when it is painting surfaces — Surfaces mode, structural, and a 1×1 wall/floor
    /// skin. Null whenever the stroke should behave exactly as it always has.</summary>
    private PartDef? SurfaceBrush =>
        SurfaceMode && !_armedLoose && Doc is not null && SurfacePaint.IsSurfaceBrush(Doc.Catalog, ArmedPart)
            ? ArmedPart
            : null;

    /// <summary>The part a surface stroke lays on this tile: the pattern's choice between the armed brush and
    /// <see cref="PatternB"/>. Falls back to the armed brush if the pattern names a def the catalog can't resolve.</summary>
    private PartDef PatternPartAt(PartDef brush, int x, int y)
    {
        if (PatternB is not { } b || Pattern == SurfacePattern.Solid) return brush;
        // A pattern needs a matching pair. Arming a wall while B is still a floor would otherwise alternate
        // between the two layers, which is not a pattern but two different edits: paint plain until they match.
        if (Doc!.Catalog.RenderLayer(b) != Doc.Catalog.RenderLayer(brush)) return brush;
        var def = SurfacePaint.DefAt(Pattern, brush.DefName, b.DefName, x, y);
        return def == brush.DefName ? brush : Doc!.Catalog.Lookup(def) ?? brush;
    }

    /// <summary>True while this part should be ghosted: Surfaces mode is on and the current focus isn't on it.</summary>
    private bool IsGhosted(Placement p) =>
        SurfaceMode && !SurfacePaint.IsFocusLayer(Doc!.Catalog, Doc.Part(p), LayerFocus);

    /// <summary>Surfaces mode ghosts everything that isn't the focused deck layer. Loose clutter is never the
    /// deck, so it ghosts with the rest whatever the focus is.</summary>
    private bool IsGhosted(RenderItem item) =>
        item.Placement is { } p ? IsGhosted(p) : SurfaceMode;

    /// <summary>
    /// The part a click should land on. In Surfaces mode the ghosted layers are not just dim, they are out of the
    /// way: the topmost part <b>in focus</b> under the cursor wins, so a floor buried under a bed (or, on the
    /// Floors focus, under a wall) is one click away instead of a trip through the right-click layer picker.
    /// Otherwise the ordinary topmost-part hit test.
    /// </summary>
    private Placement? SurfaceAwareHit(int x, int y) =>
        SurfaceMode
            ? Doc!.HitTestStack(x, y).FirstOrDefault(p => SurfacePaint.IsFocusLayer(Doc.Catalog, Doc.Part(p), LayerFocus))
            : Doc!.HitTest(x, y);

    /// <summary>The freshly computed power network (document coords), pushed by the window after each scan.</summary>
    public void SetPowerOverlay(PowerOverlay overlay)
    {
        _powerOverlay = overlay ?? PowerOverlay.Empty;
        _powerGeoDirty = true;
        UpdatePowerAnimation();
        InvalidateVisual();
    }

    /// <summary>Toggle the RoomViz compartment overlay. Like PowerViz, the partition is only certified off-thread
    /// while the overlay is on — the window listens on <see cref="ShowRoomsChanged"/> to schedule that scan.</summary>
    public void ToggleRooms() => SetShowRooms(!ShowRooms);

    public void SetShowRooms(bool on)
    {
        if (ShowRooms == on) return;
        ShowRooms = on;
        if (!on) { _roomOverlay = RoomOverlay.Empty; _roomGeos = null; _roomLabels = null; }   // drop stale data so it can't flash on re-enable
        ShowRoomsChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>The freshly certified compartments (document coords), pushed by the window after each scan.</summary>
    public void SetRoomOverlay(RoomOverlay overlay)
    {
        _roomOverlay = overlay ?? RoomOverlay.Empty;
        _roomGeoDirty = true;
        InvalidateVisual();
    }

    /// <summary>True while a Simulate dialog is aiming: the canvas reports angles instead of editing, and draws the
    /// pivot and ghost path. Cleared when the dialog closes.</summary>
    public bool IsAiming => _aiming;

    /// <summary>Raised as the cursor moves while aiming, with the document tile under it. The Simulate dialog turns
    /// that into an angle through the solver's own inverse — the canvas knows about pixels and nothing else.</summary>
    public event Action<Point>? AimPointChanged;

    /// <summary>Hand the canvas to a Simulate dialog, or give it back. While aiming, the pivot is drawn and the
    /// left button swings the ghost path rather than placing or selecting anything.</summary>
    public void SetAiming(bool on, Point? pivotDoc = null)
    {
        _aiming = on;
        _strikePivot = on ? pivotDoc : null;
        if (!on) _ghostPath = null;
        Cursor = on ? System.Windows.Input.Cursors.Cross : null;
        InvalidateVisual();
    }

    /// <summary>The ghost strike path to draw, in document coords, or null for none.</summary>
    public void SetGhostPath((Point Start, Point End)? path)
    {
        _ghostPath = path;
        InvalidateVisual();
    }

    /// <summary>Push the damage heat map. Cheap and unbaked: it is a handful of parts, not a station's worth of
    /// room cells, and it changes only when the user fires.</summary>
    public void SetDamageOverlay(DamageOverlay overlay)
    {
        _damageOverlay = overlay ?? DamageOverlay.Empty;
        InvalidateVisual();
    }

    /// <summary>The document position under a screen point, in continuous tiles rather than snapped to a cell —
    /// aiming needs sub-tile precision or the ghost path jumps.</summary>
    public Point DocPointAt(Point screen)
    {
        var p = ScreenToPanSpace(screen);
        return new Point((p.X - _pan.X) / Zoom, (p.Y - _pan.Y) / Zoom);
    }

    /// <summary>Toggle the WalkViz crew-access overlay. Like PowerViz/RoomViz, the walk analysis is only computed
    /// off-thread while the overlay is on — the window listens on <see cref="ShowWalkChanged"/> to schedule that scan.</summary>
    public void ToggleWalk() => SetShowWalk(!ShowWalk);

    public void SetShowWalk(bool on)
    {
        if (ShowWalk == on) return;
        ShowWalk = on;
        if (!on) { _walkOverlay = WalkOverlay.Empty; _walkGeos = null; }   // drop stale data so it can't flash on re-enable
        ShowWalkChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>The freshly computed crew-access analysis (document coords), pushed by the window after each scan.</summary>
    public void SetWalkOverlay(WalkOverlay overlay)
    {
        _walkOverlay = overlay ?? WalkOverlay.Empty;
        _walkGeoDirty = true;
        InvalidateVisual();
    }

    /// <summary>Toggle the Light Viz interior-lighting overlay. Like PowerViz/RoomViz, lighting is only computed
    /// off-thread while the overlay is on — the window listens on <see cref="ShowLightChanged"/> to schedule that scan.</summary>
    public void ToggleLight() => SetShowLight(!ShowLight);

    public void SetShowLight(bool on)
    {
        if (ShowLight == on) return;
        ShowLight = on;
        if (!on) { _lightScene = LightScene.Empty; _lightImage = null; _lightJob++; }   // drop stale data (and orphan any in-flight composite) so it can't flash on re-enable
        // The other half of the exclusion in SetSurfaceMode: a lit composite bakes the whole ship into one image,
        // so Surfaces mode would keep its toolbar highlight while quietly ghosting nothing at all.
        else SetSurfaceMode(false);
        ShowLightChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>The freshly resolved light scene (document coords), pushed by the window after each scan while the
    /// overlay is on. Kicks the composite rebuild (albedo/normal bake on this thread, shading on a worker).</summary>
    public void SetLightScene(LightScene scene)
    {
        _lightScene = scene ?? LightScene.Empty;
        RebuildLightComposite();
        InvalidateVisual();
    }

    // Hook the per-frame flow tick only while the overlay is on AND there is something lit to animate.
    private void UpdatePowerAnimation()
    {
        var want = ShowPower && _powerOverlay.Powered.Count > 0;
        if (want == _powerAnimating) return;
        _powerAnimating = want;
        if (want) { _lastPowerTime = TimeSpan.Zero; CompositionTarget.Rendering += OnPowerFrame; }
        else CompositionTarget.Rendering -= OnPowerFrame;
    }

    private void OnPowerFrame(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs re) return;
        if (_lastPowerTime == TimeSpan.Zero) { _lastPowerTime = _lastPowerDraw = re.RenderingTime; return; }   // first frame: seed the clock
        var dt = (re.RenderingTime - _lastPowerTime).TotalSeconds;
        if (dt <= 0) return;
        _lastPowerTime = re.RenderingTime;
        _powerPhase = (_powerPhase + dt * PowerFlowSpeed) % 1000.0;   // bounded so the float never drifts

        // Repaint the flow at ~30 fps rather than every compositor frame — the flow reads the same but halves the
        // overlay redraws. A pan repaints on its own frame, so the flow still tracks the ship smoothly while panning.
        if ((re.RenderingTime - _lastPowerDraw).TotalSeconds < 1.0 / 30) return;
        _lastPowerDraw = re.RenderingTime;
        InvalidateVisual();
    }

    /// <summary>Enter (or leave, with null) zone-paint mode by making a zone active. Disarms any part brush and
    /// forces the overlay on so the painted zone is visible. Mirrors <see cref="SetArmed"/>.</summary>
    public void SetActiveZone(Guid? zoneId)
    {
        if (ActiveZoneId == zoneId) return;
        ActiveZoneId = zoneId;
        if (zoneId is not null)
        {
            ArmedPart = null;
            SelectedIds.Clear();
            SelectionChanged?.Invoke();
            ShowZones = true;
            ShowZonesChanged?.Invoke();
        }
        ActiveZoneChanged?.Invoke();
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

    private void RaiseGhostReason(string? reason, bool willPlace = false, bool advisory = false)
    {
        GhostStatus? status = reason is null ? null : new GhostStatus(reason, willPlace, advisory);
        if (status.Equals(_lastGhostReason)) return;
        _lastGhostReason = status;
        GhostReasonChanged?.Invoke(status);
    }

    public void RotateArmed(int delta)
    {
        // sheet items (walls/floors) never rotate - Item.RotateCW is skipped for them
        if (ArmedPart is null || ArmedPart.Item.HasSpriteSheet) return;
        ArmedRot = GridMath.Norm(ArmedRot + delta);
        ArmedChanged?.Invoke();
        InvalidateVisual();
    }

    public List<Placement> SelectedPlacements() =>
        Doc is null ? [] : Doc.Placements.Where(p => SelectedIds.Contains(p.Id)).ToList();

    // ---- symmetry-aware selection / move ----

    private bool SymVertical => SymMode is SymmetryMode.Vertical or SymmetryMode.Both;
    private bool SymHorizontal => SymMode is SymmetryMode.Horizontal or SymmetryMode.Both;

    /// <summary>The placements that mirror <paramref name="p"/> across the active symmetry axes — the parts a
    /// symmetry-mode build laid down opposite it, matched by exact mirrored top-left + def. Empty when symmetry is
    /// off or nothing sits at the mirror pose (an asymmetric spot). A part on an axis mirrors onto itself and is
    /// excluded (never yields <paramref name="p"/> itself).</summary>
    private IEnumerable<Placement> MirrorPartners(Placement p)
    {
        if (Doc is null || SymMode == SymmetryMode.Off) yield break;
        var (w, h) = Doc.FootprintOf(p);
        foreach (var (mx, my, _) in Symmetry.Poses(p.X, p.Y, p.Rot, w, h, SymCenter.X, SymCenter.Y, SymVertical, SymHorizontal).Skip(1))
        {
            var partner = Doc.Placements.FirstOrDefault(q => q.Id != p.Id && q.DefName == p.DefName && q.X == mx && q.Y == my);
            if (partner is not null) yield return partner;
        }
    }

    /// <summary>With symmetry on, pull each selected part's mirror partner(s) into the selection so a click or
    /// box-select grabs the whole symmetric group. One pass over the current selection reaches every partner (a
    /// quadrant's three mirrors are all direct mirrors of the original), and it is idempotent.</summary>
    private void ExtendSelectionAcrossSymmetry()
    {
        if (Doc is null || SymMode == SymmetryMode.Off || SelectedIds.Count == 0) return;
        var add = SelectedPlacements().SelectMany(MirrorPartners).Select(p => p.Id).ToList();
        foreach (var id in add) SelectedIds.Add(id);
    }

    /// <summary>Ctrl-clicking a part off the selection also drops its mirror partner(s), so the pair leaves together.</summary>
    private void RemoveMirrorPartners(Placement p)
    {
        foreach (var partner in MirrorPartners(p)) SelectedIds.Remove(partner.Id);
    }

    /// <summary>The move offset for <paramref name="p"/> this drag: the raw <see cref="_moveDelta"/>, but mirrored on
    /// the far side of each active axis so a symmetric selection stays symmetric (drag the left cluster right and the
    /// right cluster tracks left). The grabbed tile (<see cref="_dragStartCell"/>) is the reference side that follows
    /// the cursor; a part centred on an axis can't move along it without breaking its own symmetry, so that component
    /// is pinned to zero. With symmetry off this is just the raw delta.</summary>
    private (int X, int Y) MoveDeltaFor(Placement p)
    {
        // symmetric per-part deltas only for a genuine mirror set (cached at drag start); anything else
        // (a fresh paste straddling the axis) translates rigidly so it is not warped by the axis.
        if (Doc is null || !_symMove) return _moveDelta;
        var (w, h) = Doc.FootprintOf(p);
        return SymmetryOps.MoveDelta(p.X, p.Y, w, h, _moveDelta.X, _moveDelta.Y,
            SymCenter.X, SymCenter.Y, _dragStartCell.X, _dragStartCell.Y, SymVertical, SymHorizontal);
    }

    /// <summary>Whether this Move drag preserves symmetry — set once at drag start from <see cref="SelectionIsSymmetric"/>
    /// so the per-part mirror math is not recomputed each frame (and stays consistent between preview and commit).</summary>
    private bool _symMove;

    /// <summary>
    /// True when the current selection is a genuine mirror-symmetric set about <see cref="SymCenter"/> for the active
    /// axes: every selected part's mirror pose(s) are occupied by another selected part of the same def. Only then do
    /// the symmetry-preserving group edits (rotate/move) apply; an arbitrary selection (e.g. a fresh paste on one side)
    /// falls back to a plain group op so the axis it happens to straddle does not distort it.
    /// </summary>
    public bool SelectionIsSymmetric()
    {
        if (Doc is null || SymMode == SymmetryMode.Off) return false;
        var parts = SelectedPlacements();
        if (parts.Count == 0) return false;
        var items = parts
            .Select(p => { var (w, h) = Doc.FootprintOf(p); return new Symmetry.SetItem(p.DefName, p.X, p.Y, w, h); })
            .ToList();
        return Symmetry.IsSymmetricSet(items, SymCenter.X, SymCenter.Y, SymVertical, SymHorizontal);
    }

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

    /// <summary>Select a loose floor item alone, dropping any part selection — the loose half of
    /// <see cref="SelectOnly"/>, so the stacked picker and the cycle key can land on either kind.</summary>
    public void SelectOnlyLoose(LooseObject lo)
    {
        SelectedIds.Clear();
        SelectionChanged?.Invoke();
        SelectedLoose = lo;
        LooseSelectionChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Select whichever kind of drawable this is (see <see cref="RenderItem"/>).</summary>
    public void SelectItem(RenderItem item)
    {
        if (item.Placement is { } p) { ClearLooseSelection(); SelectOnly(p); }
        else if (item.Loose is { } lo) SelectOnlyLoose(lo);
    }

    /// <summary>
    /// What a Move Back / Move Forward acts on: the one selected drawable, and the tile whose pile it moves
    /// within. That tile is <paramref name="cell"/> when given (the right-clicked one), else the tile under the
    /// cursor, else the drawable's own top-left body tile — a part spanning several tiles is stacked differently
    /// against each of them, so the nudge has to know which one you meant. Null unless exactly one thing is
    /// selected, since re-stacking a box selection has no single answer.
    /// </summary>
    public (RenderItem Item, int X, int Y)? RestackTarget((int X, int Y)? cell = null)
    {
        if (Doc is null) return null;
        RenderItem item;
        if (SelectedLoose is { } lo) item = new RenderItem(null, lo);
        else if (SelectedIds.Count == 1 && Doc.Placements.FirstOrDefault(p => SelectedIds.Contains(p.Id)) is { } p)
            item = new RenderItem(p, null);
        else return null;

        foreach (var c in new[] { cell, _hoverCell })
            if (c is { } at && Doc.RenderStackAt(at.X, at.Y).Any(i => i.Id == item.Id)) return (item, at.X, at.Y);
        if (item.Placement is { } sel)
        {
            var (bx, by, _, _) = Doc.BodyBounds(sel);
            return (item, bx, by);
        }
        return (item, item.X, item.Y);
    }

    /// <summary>The loose item on a tile when it is the <b>topmost</b> thing drawn there, else null. A loose item
    /// nudged under a fixture is no longer what a click on that tile lands on.</summary>
    private LooseObject? TopLooseAt((int X, int Y) cell) =>
        Doc?.RenderStackAt(cell.X, cell.Y).FirstOrDefault().Loose;

    /// <summary>
    /// Step the selection one drawable down the pile under the cursor, wrapping at the bottom — the fast way
    /// through a stack that does not cost a trip to the right-click picker, and the reason a canister drawn under
    /// its regulator is still one keystroke away. Restarts from the top whenever the selection is not in the pile
    /// (a fresh tile), so it never depends on state that a mouse move could have invalidated.
    /// </summary>
    public void CycleSelectionUnderCursor()
    {
        if (Doc is null || _hoverCell is not { } cell) return;
        var stack = Doc.RenderStackAt(cell.X, cell.Y);
        if (stack.Count == 0) return;

        var current = -1;
        for (var i = 0; i < stack.Count && current < 0; i++)
            if (stack[i].IsLoose ? SelectedLoose is { } sel && sel.Id == stack[i].Id
                                 : SelectedIds.Count == 1 && SelectedIds.Contains(stack[i].Id))
                current = i;
        SelectItem(stack[current < 0 ? 0 : (current + 1) % stack.Count]);
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

    /// <summary>Set the symmetry mode directly (the View menu's radio options). Turning symmetry on centres the
    /// axes on the tile under the cursor, matching <see cref="CycleSymmetry"/>.</summary>
    public void SetSymmetry(SymmetryMode mode)
    {
        if (SymMode == mode) return;
        if (SymMode == SymmetryMode.Off && mode != SymmetryMode.Off) SymCenter = _hoverCell ?? (0, 0);
        SymMode = mode;
        SymmetryChanged?.Invoke();
        InvalidateVisual();
    }

    public void RotateView(int delta)
    {
        ViewRot = GridMath.Norm(ViewRot + delta);
        RaiseViewChanged();
        InvalidateVisual();
    }

    /// <summary>Set the plan-view orientation directly (restoring a saved design's last orientation, or resetting
    /// to 0 for a new document). Normalized to a 90° step.</summary>
    public void SetViewRot(int rot)
    {
        ViewRot = GridMath.Norm(rot);
        RaiseViewChanged();
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

    /// <summary>A pan-space point back to where it actually lands on screen — the inverse of
    /// <see cref="ScreenToPanSpace"/>, i.e. the view-rotation transform <see cref="OnRender"/> pushes.</summary>
    private Point PanSpaceToScreen(Point p)
    {
        if (ViewRot == 0) return p;
        var m = Matrix.Identity;
        m.RotateAt(ViewRot, RenderSize.Width / 2, RenderSize.Height / 2);
        return m.Transform(p);
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
        RaiseViewChanged();

        // The mouse isn't moving during a WASD pan, but the world tile under it is — so the armed
        // ghost (and the tile readout) would freeze on the old tile. Recompute the hovered cell from
        // the current cursor position each frame so the ghost tracks the cursor as the view slides.
        if (IsMouseOver)
        {
            var cell = CellAt(Mouse.GetPosition(this));
            if (_hoverCell is null || _hoverCell.Value != cell)
            {
                _hoverCell = cell;
                HoverChanged?.Invoke(cell);
            }
        }

        InvalidateVisual();
    }

    // ---- view ----

    /// <summary>
    /// Frame the content as soon as this canvas has a size, rather than only if it already has one.
    ///
    /// <para>A canvas built for a document tab has not been laid out at the moment the document is installed into
    /// it, so <see cref="FitContent"/> alone would no-op and the ship would open at the default zoom centred on the
    /// origin instead of framed. Deferring to the first render size makes opening into a new tab behave exactly like
    /// opening into the window that is already on screen.</para>
    /// </summary>
    public void FitContentWhenReady()
    {
        if (RenderSize.Width >= 1) FitContent();
        else _fitPending = true;
    }

    public void FitContent()
    {
        _fitPending = false;
        if (Doc?.Bounds() is not { } b || RenderSize.Width < 1) return;
        var tilesW = b.MaxX - b.MinX + 3.0;
        var tilesH = b.MaxY - b.MinY + 3.0;
        var fit = Math.Min(RenderSize.Width / tilesW, RenderSize.Height / tilesH);
        Zoom = SnapZoomDown(fit);
        var centerX = (b.MinX + b.MaxX + 1) / 2.0;
        var centerY = (b.MinY + b.MaxY + 1) / 2.0;
        _pan = new Vector(RenderSize.Width / 2 - centerX * Zoom, RenderSize.Height / 2 - centerY * Zoom);
        _panInitialized = true;
        RaiseViewChanged();
        InvalidateVisual();
    }

    /// <summary>Pan and zoom so <paramref name="tiles"/> are centred and comfortably framed (a few tiles of
    /// context, capped at a legible zoom so a single-tile issue isn't slammed to max) — the Problems list's
    /// "View" jump-to-issue.</summary>
    public void FocusTiles(IReadOnlyList<(int X, int Y)> tiles)
    {
        if (tiles.Count == 0 || RenderSize.Width < 1) return;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var (x, y) in tiles)
        {
            minX = Math.Min(minX, x); minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
        }
        var tilesW = maxX - minX + 6.0;   // keep a few tiles of context around the region
        var tilesH = maxY - minY + 6.0;
        var fit = Math.Min(RenderSize.Width / tilesW, RenderSize.Height / tilesH);
        Zoom = SnapZoomDown(Math.Min(fit, 64.0));
        var centerX = (minX + maxX + 1) / 2.0;
        var centerY = (minY + maxY + 1) / 2.0;
        _pan = new Vector(RenderSize.Width / 2 - centerX * Zoom, RenderSize.Height / 2 - centerY * Zoom);
        _panInitialized = true;
        RaiseViewChanged();
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (sizeInfo.NewSize.Width <= 0) return;

        // A deferred fit wins over the default centring: it was asked for against this document, and this is the
        // first moment there is a size to fit to (see FitContentWhenReady).
        if (_fitPending) { FitContent(); return; }

        if (!_panInitialized)
        {
            _pan = new Vector(sizeInfo.NewSize.Width / 2, sizeInfo.NewSize.Height / 2);
            _panInitialized = true;
            _staticShip = null;
        }
    }

    private (int X, int Y) CellAt(Point screen)
    {
        var p = ScreenToPanSpace(screen);
        return ((int)Math.Floor((p.X - _pan.X) / Zoom), (int)Math.Floor((p.Y - _pan.Y) / Zoom));
    }

    /// <summary>
    /// The tile a paste should land on: the one under the cursor, or the middle of the view when the cursor is not
    /// over this canvas at all. Null only before the canvas has been laid out.
    ///
    /// <para>Read live rather than from the last <see cref="HoverChanged"/>, because the cached hover is only as
    /// fresh as the last mouse <i>move</i>. A canvas the cursor is already resting over when it becomes the visible
    /// one — switching tabs with Ctrl+Tab, say — has had no move to record, so the cache says "nowhere" while the
    /// cursor is plainly on a tile. Asking the mouse is always right and costs nothing at the one moment it is
    /// asked.</para>
    /// </summary>
    public (int X, int Y)? PasteCell =>
        RenderSize.Width < 1
            ? null
            : CellAt(IsMouseOver ? Mouse.GetPosition(this) : new Point(RenderSize.Width / 2, RenderSize.Height / 2));

    private Rect CellRect(double x, double y, double w, double h) =>
        new(_pan.X + x * Zoom, _pan.Y + y * Zoom, w * Zoom, h * Zoom);

    // ---- input ----

    /// <summary>The next zoom for a step of <paramref name="notches"/> (fractional for precision trackpads):
    /// 0.1x per notch on the lattice, 0.5x with Shift held. Always moves at least one lattice step for a
    /// non-zero input, so a slow trackpad tick never gets rounded away.</summary>
    private double NextZoom(double notches)
    {
        var step = notches * ZoomNotch *
            (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? FastZoomFactor : 1.0);
        var target = SnapZoom((Zoom / BaseTilePx + step) * BaseTilePx);
        if (target == Zoom && notches != 0)
            target = SnapZoom(Zoom + Math.Sign(notches) * ZoomNotch * BaseTilePx);
        return target;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var next = NextZoom(e.Delta / 120.0);
        if (next == Zoom) return;

        // keep the tile under the cursor stationary
        var mouse = ScreenToPanSpace(e.GetPosition(this));
        var world = new Point((mouse.X - _pan.X) / Zoom, (mouse.Y - _pan.Y) / Zoom);
        Zoom = next;
        _pan = new Vector(mouse.X - world.X * Zoom, mouse.Y - world.Y * Zoom);
        RaiseViewChanged();
        InvalidateVisual();
    }

    /// <summary>Step the zoom one notch in (<paramref name="dir"/> &gt; 0) or out, anchored at the viewport centre —
    /// the keyboard zoom (+/-), mirroring the wheel step (Shift accelerates) but around the middle of the view.</summary>
    public void ZoomStep(int dir)
    {
        var next = NextZoom(Math.Sign(dir));
        if (next == Zoom) return;

        var centre = ScreenToPanSpace(new Point(RenderSize.Width / 2, RenderSize.Height / 2));
        var world = new Point((centre.X - _pan.X) / Zoom, (centre.Y - _pan.Y) / Zoom);
        Zoom = next;
        _pan = new Vector(centre.X - world.X * Zoom, centre.Y - world.Y * Zoom);
        RaiseViewChanged();
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

        // Aiming for a Simulate dialog: the canvas is a protractor, not an editor. Intercepts before selection,
        // placement, zones and wiring so no strike can nudge the design it is measuring. Pan above still works.
        if (_aiming && e.ChangedButton == MouseButton.Left)
        {
            AimPointChanged?.Invoke(DocPointAt(screen));
            _drag = Drag.Aim;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        // Wire mode: left-click a signalable device to arm it as the signal source, then click another to
        // connect (or click a connected one to disconnect); the source stays armed so you can wire it to several
        // targets. Right-click drops what's "in hand" first — a held palette brush, else the armed wire source —
        // so a cursor item isn't stranded while wiring (#7). Intercepts before the normal select/place/zone logic.
        if (WireMode && Doc is not null)
        {
            var wc = CellAt(screen);
            if (e.ChangedButton == MouseButton.Left)
            {
                var target = Doc.HitTestStack(wc.X, wc.Y).FirstOrDefault(p => DeviceLinks.IsConnectable(Doc, p));
                if (target is null) _wireSource = null;                       // empty / non-device → clear
                else if (_wireSource is null) _wireSource = target;           // arm the source
                else if (ReferenceEquals(_wireSource, target)) _wireSource = null;   // clicked source again → disarm
                else LinkToggleRequested?.Invoke(_wireSource, target);        // connect / disconnect (source stays armed)
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            if (e.ChangedButton == MouseButton.Right)
            {
                if (ArmedPart is not null) { SetArmed(null); Disarmed?.Invoke(); }   // discard the held brush first
                else _wireSource = null;                                             // otherwise drop the wire source
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }

        if (e.ChangedButton == MouseButton.Right)
        {
            var rmbCell = CellAt(screen);
            // A loose floor item the cursor is over wins the right-click when it is the TOPMOST thing there, even
            // while a brush is armed: disarm, select it, and open its menu (Change Quantity / Delete) — otherwise a
            // dropped item is unreachable because the brush stays armed after dropping. One that has been pushed
            // under a fixture falls through to the placement menu, whose stacked picker lists it.
            // Surfaces mode ghosts clutter and steps it out of the way of a click, the left button's rule, so the
            // deck under the item is what the menu is about there. The exception is a tile with no structure at
            // all: nothing would open, and a right-click that does nothing is worse than one on the clutter.
            if (Doc is not null && TopLooseAt(rmbCell) is { } looseRmb
                && (!SurfaceMode || Doc.HitTestStack(rmbCell.X, rmbCell.Y).Count == 0))
            {
                if (ArmedPart is not null) { SetArmed(null); Disarmed?.Invoke(); }
                SelectedIds.Clear();
                SelectionChanged?.Invoke();
                SelectedLoose = looseRmb;
                LooseSelectionChanged?.Invoke();
                InvalidateVisual();
                LooseContextMenuRequested?.Invoke(rmbCell);
                e.Handled = true;
                return;
            }
            if (ArmedPart is not null)
            {
                SetArmed(null);
                Disarmed?.Invoke();
            }
            else if (Doc is not null)
            {
                var stack = Doc.HitTestStack(rmbCell.X, rmbCell.Y);
                if (stack.Count > 0)
                {
                    // this menu is about structure, so drop any loose item still selected from an earlier click —
                    // the left-click path already keeps the two selections mutually exclusive, and the re-stack
                    // actions read whichever one is live to decide what they move
                    ClearLooseSelection();
                    // if nothing in this stack is already selected, grab the part a click would land on so a
                    // plain right-click + Delete still acts on the visible part — but keep an
                    // existing box selection (>1) intact so its layer filter / group actions
                    // apply to the whole thing, wherever inside it you click.
                    // The pick is the same one the left button makes (SurfaceAwareHit), so in Surfaces mode the
                    // right button reaches the deck under the clutter rather than the clutter. The whole menu
                    // (Rename, View contents, Delete) reads the selection, so picking the topmost part here made
                    // every one of them act on the fixture standing over the tile. A tile with nothing in focus on
                    // it falls back to the topmost, so the menu still opens on a ghosted stack.
                    if (SelectedIds.Count <= 1 && !stack.Any(p => SelectedIds.Contains(p.Id)))
                    {
                        SelectedIds.Clear();
                        SelectedIds.Add((SurfaceAwareHit(rmbCell.X, rmbCell.Y) ?? stack[0]).Id);
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

        // Alt+LMB is the eyedropper: pick the (topmost) part under the cursor as the brush, at its own rotation.
        // Works whether or not something is already armed, and takes priority over placing/selecting so an
        // Alt-click never edits.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && SurfaceAwareHit(cell.X, cell.Y) is { } pick)
        {
            BrushPicked?.Invoke(pick.DefName, pick.Rot);
            e.Handled = true;
            return;
        }

        // Zone-paint mode (a zone is active): left = add tiles, Ctrl+left = erase, Shift+left = box, double-click =
        // fill the enclosed room. This intercepts before the part select/flood logic below (a double-click here is a
        // room-fill, not a part flood-select). The stroke edits a working set previewed live; commit is one command.
        if (ActiveZoneId is { } azid && Doc.Zones.FirstOrDefault(z => z.Id == azid) is { } activeZone)
        {
            _zoneErase = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            _zoneBefore = [.. activeZone.Tiles];
            _zoneWorking = [.. activeZone.Tiles];
            if (e.ClickCount == 2)
            {
                var room = RoomTilesAt(cell).ToList();
                if (room.Count > 0) foreach (var t in room) ApplyZoneCell(t);
                else if (!_zoneErase) _zoneWorking.Add(cell);
                CommitZoneStroke(activeZone);
            }
            else
            {
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { _drag = Drag.ZoneBox; _dragStartCell = cell; RebuildZoneBox(cell); }
                else { _drag = Drag.ZonePaint; ApplyZoneCell(cell); }
                CaptureMouse();
                InvalidateVisual();
            }
            e.Handled = true;
            return;
        }

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
            seed ??= SurfaceAwareHit(cell.X, cell.Y);
            if (seed is not null)
            {
                if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) SelectedIds.Clear();
                foreach (var p in FloodSelect.Collect(Doc, seed)) SelectedIds.Add(p.Id);
                ExtendSelectionAcrossSymmetry();
                SelectionChanged?.Invoke();
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            // double-click on empty space selects the enclosed ("compartmentalized") air region, so you can arm a
            // part and press Enter to fill it (FillAirSelection). Open-to-space areas yield nothing, so a fill can't
            // leak into vacuum. The highlight persists until you fill it, edit the ship, or press Esc.
            var air = RoomTilesAt(cell).ToList();
            if (air.Count > 0)
            {
                SelectedIds.Clear();
                ClearLooseSelection();
                SelectionChanged?.Invoke();
                _airSelection = [.. air];
                AirSelectionChanged?.Invoke(_airSelection.Count);
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }

        if (ArmedPart is not null)
        {
            if (_armedLoose)
            {
                // A loose item is dropped with a single click (no drag-paint, no box-fill, no CheckFit): onto a
                // floor tile or into a container under the cursor. One command, committed immediately.
                _stroke.Clear();
                TryPlaceLoose(cell);
                CommitStroke();
                e.Handled = true;
                return;
            }
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

        // Shift+drag with no brush armed = rectangle select even when the drag starts on a part
        // (a plain drag there would move it, and a full-deck ship has no empty tile to start from).
        // On release the window offers layer filter chips to prune the catch (walls without floors, …).
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            ClearLooseSelection();
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                SelectedIds.Clear();
                SelectionChanged?.Invoke();
            }
            _drag = Drag.Band;
            _bandFilter = true;
            _dragStartScreen = screen;
            _dragStartCell = cell;
            CaptureMouse();
            e.Handled = true;
            InvalidateVisual();
            return;
        }

        // Unarmed left-click on a tile whose TOPMOST drawable is a loose item selects it, so it can be inspected
        // and deleted. Ctrl-click falls through to the placement logic (reach the structure beneath), as does a
        // loose item that has been pushed under a fixture — click order follows draw order.
        if (!SurfaceMode && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && TopLooseAt(cell) is { } looseHit)
        {
            SelectedIds.Clear();
            SelectionChanged?.Invoke();
            SelectedLoose = looseHit;
            LooseSelectionChanged?.Invoke();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        ClearLooseSelection();

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
                    _symMove = SelectionIsSymmetric();
                    CaptureMouse();
                }
                e.Handled = true;
                InvalidateVisual();
                return;
            }
        }

        var hit = SurfaceAwareHit(cell.X, cell.Y);
        if (hit is not null)
        {
            var additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            if (!SelectedIds.Contains(hit.Id))
            {
                if (!additive) SelectedIds.Clear();
                SelectedIds.Add(hit.Id);
                ExtendSelectionAcrossSymmetry();   // grab the mirror partner(s) too
            }
            else if (additive)
            {
                SelectedIds.Remove(hit.Id);
                RemoveMirrorPartners(hit);
            }
            SelectionChanged?.Invoke();
            if (SelectedPlacements().Any(p => !Doc.IsLocked(p)))   // the primary airlock never drags
            {
                _drag = Drag.Move;
                _dragStartCell = cell;
                _moveDelta = (0, 0);
                _symMove = SelectionIsSymmetric();
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
            _bandFilter = false;
            _dragStartScreen = screen;
            _dragStartCell = cell;
            CaptureMouse();
        }
        InvalidateVisual();
    }

    /// <summary>Report the live tile dimensions of a rubber-band box drag (band select, box fill, zone box) so the
    /// status bar can show "W × H" while you size it — handy for measuring room interiors as you build. Emits null
    /// for any non-box state, which clears the readout.</summary>
    private void RaiseSelectionSize()
    {
        if (_drag is Drag.Band or Drag.BoxFill or Drag.ZoneBox && _hoverCell is { } end)
            SelectionSizeChanged?.Invoke((Math.Abs(end.X - _dragStartCell.X) + 1, Math.Abs(end.Y - _dragStartCell.Y) + 1));
        else
            SelectionSizeChanged?.Invoke(null);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var screen = e.GetPosition(this);
        // Aiming reads a continuous position, not a cell: snapping the ghost path to tile centres makes it jump.
        if (_aiming && _drag == Drag.Aim)
        {
            AimPointChanged?.Invoke(DocPointAt(screen));
            e.Handled = true;
            return;
        }

        var cell = CellAt(screen);
        if (_hoverCell is null || _hoverCell.Value != cell)
        {
            _hoverCell = cell;
            HoverChanged?.Invoke(cell);
            RaiseSelectionSize();   // update the WxH readout as the box grows/shrinks
            if (_drag == Drag.Paint) PaintAt(cell);
            else if (_drag == Drag.ZonePaint) ApplyZoneCell(cell);
            else if (_drag == Drag.ZoneBox) RebuildZoneBox(cell);
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
                RaiseViewChanged();
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

        if ((drag == Drag.ZonePaint || drag == Drag.ZoneBox) && Doc is not null && ActiveZoneId is { } zid
            && Doc.Zones.FirstOrDefault(z => z.Id == zid) is { } zone)
        {
            if (drag == Drag.ZoneBox) RebuildZoneBox(CellAt(e.GetPosition(this)));
            CommitZoneStroke(zone);
        }

        if (drag == Drag.BoxFill && Doc is not null && ArmedPart is not null)
        {
            var end = CellAt(e.GetPosition(this));
            var hollow = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);   // Ctrl at release = outline only
            var (w, h) = GridMath.Size(ArmedPart.Item.Width, ArmedPart.Item.Height, ArmedRot);
            var (x0, x1) = (Math.Min(_dragStartCell.X, end.X), Math.Max(_dragStartCell.X, end.X));
            var (y0, y1) = (Math.Min(_dragStartCell.Y, end.Y), Math.Max(_dragStartCell.Y, end.Y));
            // one coalesced Changed for the whole fill, not one per tile — a 50x50 fill was firing
            // ~2500 problem scans (tens of seconds); now it's a single scan on release
            using (Doc.SuspendChanged())
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
        {
            var moving = SelectedPlacements().Where(p => !Doc.IsLocked(p)).ToList();
            if (!_symMove)
                MoveRequested?.Invoke(moving, _moveDelta.X, _moveDelta.Y);
            else
                // per-part mirrored deltas keep a symmetric selection symmetric — commit as explicit poses
                PosesRequested?.Invoke(moving.Select(p => { var (ox, oy) = MoveDeltaFor(p); return (p, p.X + ox, p.Y + oy, p.Rot); }).ToList());
        }

        if (drag == Drag.Band && Doc is not null)
        {
            var end = CellAt(e.GetPosition(this));
            var (x0, x1) = (Math.Min(_dragStartCell.X, end.X), Math.Max(_dragStartCell.X, end.X));
            var (y0, y1) = (Math.Min(_dragStartCell.Y, end.Y), Math.Max(_dragStartCell.Y, end.Y));
            foreach (var p in Doc.Placements)
            {
                if (IsGhosted(p)) continue;   // Surfaces mode: a box over the deck catches deck, not the clutter on it
                var (bx, by, bw, bh) = Doc.BodyBounds(p);   // band-select on the above-floor body
                if (bx <= x1 && bx + bw - 1 >= x0 && by <= y1 && by + bh - 1 >= y0)
                    SelectedIds.Add(p.Id);
            }
            ExtendSelectionAcrossSymmetry();   // a box-select grabs the mirrored cluster too
            SelectionChanged?.Invoke();
            if (_bandFilter && SelectedIds.Count > 0) BandFilterRequested?.Invoke();
            _bandFilter = false;
        }

        _moveDelta = (0, 0);
        RaiseSelectionSize();   // _drag is now None → clears the WxH readout
        InvalidateVisual();
    }

    // ---- painting (live placements, committed as one undo step on release) ----

    private void PaintAt((int X, int Y) cell)
    {
        if (Doc is null || ArmedPart is null) return;
        var (w, h) = GridMath.Size(ArmedPart.Item.Width, ArmedPart.Item.Height, ArmedRot);
        TryPlacePose(cell.X - (w - 1) / 2, cell.Y - (h - 1) / 2, ArmedRot);
    }

    /// <summary>
    /// Drop the armed loose item at a tile (see <see cref="LoosePlacement"/>): into a container under the cursor
    /// that accepts it, else resting on a floor tile. Builds the command and executes it into <c>_stroke</c> (the
    /// caller commits); a rejected drop (no floor, tile taken, container full) surfaces as a ghost-reason status.
    /// </summary>
    private void TryPlaceLoose((int X, int Y) cell)
    {
        if (Doc is null || ArmedPart is null) return;
        var item = ArmedPart;

        if (LoosePlacement.AcceptingContainerAt(Doc, Doc.Catalog, cell.X, cell.Y, item) is { } container)
        {
            var grid = Doc.Part(container)?.ContainerGrid ?? (6, 6);
            var after = CargoEdit.Add(container.Cargo, null, grid, item, 1, Doc.Catalog);
            if (after is null) { RaiseGhostReason("Container is full"); return; }
            var cmd = new SetCargoCommand(container, container.Cargo, after);
            cmd.Do(Doc);
            _stroke.Add(cmd);
            return;
        }

        // a crate or backpack already on the deck takes it too, exactly as an installed container does
        if (LoosePlacement.AcceptingLooseAt(Doc, Doc.Catalog, cell.X, cell.Y, item) is { } deckHost)
        {
            var grid = Doc.Catalog.Lookup(deckHost.DefName)?.ContainerGrid ?? (6, 6);
            var after = CargoEdit.Add(deckHost.Cargo, null, grid, item, 1, Doc.Catalog);
            if (after is null) { RaiseGhostReason("Container is full"); return; }
            var cmd = new SetLooseCargoCommand(deckHost, deckHost.Cargo, after);
            cmd.Do(Doc);
            _stroke.Add(cmd);
            return;
        }

        if (LoosePlacement.CanRestOnFloor(Doc, cell.X, cell.Y))
        {
            var cmd = new PlaceLooseCommand(new LooseObject { DefName = item.DefName, X = cell.X, Y = cell.Y, Rot = ArmedRot });
            cmd.Do(Doc);
            _stroke.Add(cmd);
            return;
        }

        RaiseGhostReason(Doc.LooseAt(cell.X, cell.Y) is not null
            ? "This tile already holds a loose item"
            : "Drop an item onto a floor tile or an open container");
    }

    private void TryPlacePose(int x, int y, int rot)
    {
        if (Doc is null || ArmedPart is null) return;
        var (w, h) = GridMath.Size(ArmedPart.Item.Width, ArmedPart.Item.Height, rot);
        var surface = SurfaceBrush;
        var seen = new HashSet<(int, int, int)>();
        foreach (var pose in WithSymmetry(x, y, rot, w, h))
        {
            if (!seen.Add(pose)) continue;
            // A surface stroke resolves its part per tile (the pattern) and re-skins what is already there rather
            // than being refused for landing on it. Both brushes of a pattern are 1×1 wall/floor skins, so the
            // footprint maths above still holds whichever one this tile takes.
            var part = surface is null ? ArmedPart : PatternPartAt(surface, pose.X, pose.Y);
            if (surface is not null)
            {
                if (SurfacePaint.SwapTargetAt(Doc, part, pose.X, pose.Y) is { } target)
                {
                    // The tile is spoken for by this class: re-skin it (unless this stroke only fills), and never
                    // stack a second one on it either way. The pose's rotation goes with it — the brush is aimed
                    // with R and the ghost previews that, so a re-skin has to land the way the preview showed it
                    // rather than inheriting whatever the part underneath was turned to. BuildSwap returns null
                    // when there is nothing to do (already this skin at this rotation, or the target is locked),
                    // which is also what absorbs a stroke re-entering a tile it just painted.
                    if (PaintMode != SurfacePaintMode.Fill
                        && ReplaceOps.BuildSwap(Doc, [target], part.DefName, pose.Rot) is { } swap)
                    {
                        swap.Cmd.Do(Doc);
                        _stroke.Add(swap.Cmd);
                    }
                    continue;
                }
                // Nothing of this class here. Re-skinning is all this stroke does, so leave the tile bare — this is
                // what stops a box or a checker drag spilling new deck past a room's irregular edges.
                if (PaintMode == SurfacePaintMode.Replace) continue;
            }
            if (SameDefAtPose(Doc.PlacementsAt(pose.X, pose.Y), pose.X, pose.Y, pose.Rot, part.DefName)) continue;   // skip an exact duplicate (paint-stroke re-entry), not a legal overlap
            // the placement law: skip any pose the game's Item.CheckFit would refuse (each symmetry mirror judged
            // independently — legal ones land, illegal ones don't). EXCEPTION: a MODDED part may be placed against
            // the core-only law when the override toggle is on — it lands and is flagged as a warning (ProblemScan).
            if (!CheckFit.Check(Doc, part, pose.X, pose.Y, pose.Rot, includeEnvelope: true).Ok
                && !(AllowModdedOverrides && part.IsModded)) continue;
            var cmd = new PlaceCommand(new Placement
            {
                DefName = part.DefName,
                X = pose.X,
                Y = pose.Y,
                Rot = part.Item.HasSpriteSheet ? 0 : pose.Rot,
            });
            cmd.Do(Doc);
            _stroke.Add(cmd);
        }
    }

    /// <summary>The pose plus its mirror copies for the current mode/axis — the pure <see cref="Symmetry.Poses"/>
    /// (unit-tested in Core) with this canvas's axis centre and active axes. Cursor pose first; coincident copies
    /// are the caller's to dedup.</summary>
    private IEnumerable<(int X, int Y, int Rot)> WithSymmetry(int x, int y, int rot, int w, int h) =>
        Symmetry.Poses(x, y, rot, w, h, SymCenter.X, SymCenter.Y,
            vertical: SymMode is SymmetryMode.Vertical or SymmetryMode.Both,
            horizontal: SymMode is SymmetryMode.Horizontal or SymmetryMode.Both);

    // Skip only an EXACT duplicate: same def already sitting at this exact pose (same top-left + rotation).
    // This still absorbs a paint stroke re-entering a cell it just painted, but it no longer overrides the
    // placement law — a legal same-def OVERLAP (cargopod trusses share a wall row by one tile; other
    // multi-tile parts interlock similarly) must stay placeable, and CheckFit is the sole judge of that.
    // A same-def duplicate shares the origin tile, so passing that tile's placements is one index lookup.
    internal static bool SameDefAtPose(IEnumerable<Placement> atOrigin, int x, int y, int rot, string defName)
    {
        foreach (var p in atOrigin)
            if (p.DefName == defName && p.X == x && p.Y == y && p.Rot == rot) return true;
        return false;
    }

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

    // ---- snapshot ----

    /// <summary>
    /// Render just the ship — every placement's sprite, no grid/overlays/UI — to a bitmap for a PNG
    /// export. Reuses the live sprite drawing (autotile, tank centring, rotation) by briefly pointing
    /// pan/zoom at the snapshot's pixel grid, so the image matches the canvas exactly. Null when the
    /// design is empty. Does not disturb the on-screen view.
    /// </summary>
    public System.Windows.Media.Imaging.BitmapSource? RenderSnapshot(int pxPerTile = 32, int marginTiles = 1)
    {
        if (Doc?.Bounds() is not { } b || Sprites is null) return null;

        var tilesW = b.MaxX - b.MinX + 1 + 2 * marginTiles;
        var tilesH = b.MaxY - b.MinY + 1 + 2 * marginTiles;
        var pxW = tilesW * pxPerTile;
        var pxH = tilesH * pxPerTile;

        var (savedPan, savedZoom, savedRot) = (_pan, Zoom, ViewRot);
        Zoom = pxPerTile;
        _pan = new Vector(-(b.MinX - marginTiles) * (double)pxPerTile, -(b.MinY - marginTiles) * (double)pxPerTile);
        ViewRot = 0;   // draw content unrotated; the editing orientation is applied as a wrapping transform
        try
        {
            // match the user's Q/E plan-view rotation (output dims swap at 90°/270°)
            var (outW, outH, m) = OrientOutput(savedRot, pxW, pxH);
            var dv = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.NearestNeighbor);
            using (var ctx = dv.RenderOpen())
            {
                ctx.DrawRectangle(Background, null, new Rect(0, 0, outW, outH));
                ctx.PushTransform(m);
                foreach (var i in Doc.RenderOrder()) DrawItem(ctx, i, (0, 0));
                ctx.Pop();
            }
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(outW, outH, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }
        finally
        {
            (_pan, Zoom, ViewRot) = (savedPan, savedZoom, savedRot);
        }
    }

    /// <summary>
    /// A high-resolution, well-lit "Ship Rating" snapshot: the ship's sprites on a light backdrop, every room
    /// tinted by its certification (green = certified, amber = sealed but uncertified, red = open to space), and
    /// labelled with a leader line out to the margin. Recomputes rooms in a known frame so tiles map to the same
    /// doc-tile grid the sprites draw in. Null when the design is empty. Does not disturb the on-screen view.
    /// </summary>
    public System.Windows.Media.Imaging.BitmapSource? RenderRatingSnapshot(
        IReadOnlyList<RoomSpecDef> specs, int pxPerTile = 64, int marginTiles = 5)
    {
        if (Doc?.Bounds() is not { } b || Sprites is null) return null;

        // rooms in a frame whose origin (minC,minR) we know, so a room's flat tile index -> doc tile is exact
        const int pad = 1;
        int minC = b.MinX - pad, minR = b.MinY - pad;
        int cols = b.MaxX - b.MinX + 1 + 2 * pad, rows = b.MaxY - b.MinY + 1 + 2 * pad;
        var grid = ShipGrid.FromDocumentFramed(Doc, Doc.Catalog, minC, minR, cols, rows);
        var partition = RoomBuilder.Build(grid);
        RoomCertifier.CertifyAll(partition, specs, Doc.Catalog);
        var friendly = specs.ToDictionary(s => s.Name, s => s.Friendly, StringComparer.Ordinal);

        var tilesW = b.MaxX - b.MinX + 1 + 2 * marginTiles;
        var tilesH = b.MaxY - b.MinY + 1 + 2 * marginTiles;
        var px = pxPerTile;
        if (Math.Max(tilesW, tilesH) * px > 4200) px = Math.Max(24, 4200 / Math.Max(tilesW, tilesH));   // cap the bitmap
        var pxW = tilesW * px;
        var pxH = tilesH * px;

        var (savedPan, savedZoom, savedRot) = (_pan, Zoom, ViewRot);
        Zoom = px;
        _pan = new Vector(-(b.MinX - marginTiles) * (double)px, -(b.MinY - marginTiles) * (double)px);
        ViewRot = 0;   // draw content unrotated; the editing orientation is applied as a wrapping transform
        try
        {
            // match the user's Q/E plan-view rotation: sprites + tints turn, output dims swap at 90°/270°,
            // labels stay upright and route to the nearest edge of the ROTATED output
            var (outW, outH, m) = OrientOutput(savedRot, pxW, pxH);
            double shipL = _pan.X + b.MinX * Zoom, shipR = _pan.X + (b.MaxX + 1) * Zoom;
            double shipT = _pan.Y + b.MinY * Zoom, shipB = _pan.Y + (b.MaxY + 1) * Zoom;
            var outShip = m.TransformBounds(new Rect(shipL, shipT, shipR - shipL, shipB - shipT));

            var labels = new List<SnapRoomLabel>();
            var dv = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.NearestNeighbor);
            using (var ctx = dv.RenderOpen())
            {
                ctx.DrawRectangle(SnapshotBg, null, new Rect(0, 0, outW, outH));
                ctx.PushTransform(m);
                foreach (var p in Doc.DrawOrder()) DrawPlacement(ctx, p, (0, 0));

                var roomIndex = 0;
                foreach (var room in partition.Rooms)
                {
                    if (room.Void || room.Tiles.Count == 0) continue;
                    var (fill, text) = RoomStyle(room, friendly, roomIndex++);

                    double sx = 0, sy = 0;
                    foreach (var idx in room.Tiles)
                    {
                        int dx = minC + idx % cols, dy = minR + idx / cols;
                        ctx.DrawRectangle(fill, null, CellRect(dx, dy, 1, 1));
                        sx += dx + 0.5; sy += dy + 0.5;
                    }
                    var centre = m.Transform(new Point(_pan.X + sx / room.Tiles.Count * Zoom, _pan.Y + sy / room.Tiles.Count * Zoom));
                    labels.Add(new SnapRoomLabel { Centre = centre, Ft = MakeLabel(text), Side = NearestSide(centre, outShip.Left, outShip.Right, outShip.Top, outShip.Bottom) });
                }
                ctx.Pop();

                // labels sit upright in the output margins, spread so they neither overlap nor cross their leaders
                double topY = marginTiles * 0.4 * px, botY = outH - marginTiles * 0.4 * px;
                double leftX = marginTiles * 0.4 * px, rightX = outW - marginTiles * 0.4 * px;
                LayoutSide(labels.Where(l => l.Side == 0), horizontal: true, fixedCoord: topY, min: px, max: outW - px);
                LayoutSide(labels.Where(l => l.Side == 1), horizontal: true, fixedCoord: botY, min: px, max: outW - px);
                LayoutSide(labels.Where(l => l.Side == 2), horizontal: false, fixedCoord: leftX, min: px, max: outH - px);
                LayoutSide(labels.Where(l => l.Side == 3), horizontal: false, fixedCoord: rightX, min: px, max: outH - px);
                foreach (var l in labels) DrawRoomLabel(ctx, l.Ft, l.Centre, new Point(l.Ax, l.Ay));
            }
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(outW, outH, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }
        finally
        {
            (_pan, Zoom, ViewRot) = (savedPan, savedZoom, savedRot);
        }
    }

    /// <summary>
    /// The same room-annotated "Ship Rating" snapshot as <see cref="RenderRatingSnapshot"/>, but serialized as an
    /// SVG string: the ship sprites are embedded once as a pixel-crisp base64 PNG layer, and everything drawn over
    /// them (the per-room tint, leader lines, and labels) is emitted as true vectors, so the annotations stay sharp
    /// at any zoom. Null when the design is empty. Does not disturb the on-screen view.
    /// </summary>
    public string? RenderRatingSnapshotSvg(IReadOnlyList<RoomSpecDef> specs, int pxPerTile = 64, int marginTiles = 5)
    {
        if (Doc?.Bounds() is not { } b || Sprites is null) return null;

        const int pad = 1;
        int minC = b.MinX - pad, minR = b.MinY - pad;
        int cols = b.MaxX - b.MinX + 1 + 2 * pad, rows = b.MaxY - b.MinY + 1 + 2 * pad;
        var grid = ShipGrid.FromDocumentFramed(Doc, Doc.Catalog, minC, minR, cols, rows);
        var partition = RoomBuilder.Build(grid);
        RoomCertifier.CertifyAll(partition, specs, Doc.Catalog);
        var friendly = specs.ToDictionary(s => s.Name, s => s.Friendly, StringComparer.Ordinal);

        var tilesW = b.MaxX - b.MinX + 1 + 2 * marginTiles;
        var tilesH = b.MaxY - b.MinY + 1 + 2 * marginTiles;
        var px = pxPerTile;
        if (Math.Max(tilesW, tilesH) * px > 4200) px = Math.Max(24, 4200 / Math.Max(tilesW, tilesH));   // cap the sprite raster
        var pxW = tilesW * px;
        var pxH = tilesH * px;

        var (savedPan, savedZoom, savedRot) = (_pan, Zoom, ViewRot);
        Zoom = px;
        _pan = new Vector(-(b.MinX - marginTiles) * (double)px, -(b.MinY - marginTiles) * (double)px);
        ViewRot = 0;   // draw content unrotated; the editing orientation is applied as a group transform
        try
        {
            // match the user's Q/E plan-view rotation: the sprite layer + room tints share a rotation group,
            // labels stay upright outside it and route to the nearest edge of the ROTATED output
            var (outW, outH, m) = OrientOutput(savedRot, pxW, pxH);
            var xform = SvgTransform(savedRot, pxW, pxH);

            // sprite layer -> transparent bitmap -> base64 PNG in content space (the group rotates it)
            var dv = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.NearestNeighbor);
            using (var ctx = dv.RenderOpen())
                foreach (var p in Doc.DrawOrder()) DrawPlacement(ctx, p, (0, 0));
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(pxW, pxH, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            enc.Save(ms);
            var spriteB64 = Convert.ToBase64String(ms.ToArray());

            // ship pixel bounds, mapped into the rotated output — labels route to its nearest edge (shortest leader)
            double shipL = _pan.X + b.MinX * Zoom, shipR = _pan.X + (b.MaxX + 1) * Zoom;
            double shipT = _pan.Y + b.MinY * Zoom, shipB = _pan.Y + (b.MaxY + 1) * Zoom;
            var outShip = m.TransformBounds(new Rect(shipL, shipT, shipR - shipL, shipB - shipT));

            var svg = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;
            svg.Append(ci, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{outW}\" height=\"{outH}\" viewBox=\"0 0 {outW} {outH}\">\n");
            var bg = ((SolidColorBrush)SnapshotBg).Color;
            svg.Append(ci, $"<rect width=\"{outW}\" height=\"{outH}\" fill=\"{Hex(bg)}\"/>\n");

            // the sprite layer + room tints share the rotation group; labels stay upright outside it
            svg.Append(xform.Length > 0 ? $"<g transform=\"{xform}\">\n" : "<g>\n");
            svg.Append(ci, $"<image x=\"0\" y=\"0\" width=\"{pxW}\" height=\"{pxH}\" " +
                           $"style=\"image-rendering:pixelated;image-rendering:crisp-edges\" " +
                           $"href=\"data:image/png;base64,{spriteB64}\"/>\n");

            // per-room tints (one <rect> per tile, grouped by room) + collect the labels
            var labels = new List<SnapRoomLabel>();
            var roomIndex = 0;
            foreach (var room in partition.Rooms)
            {
                if (room.Void || room.Tiles.Count == 0) continue;
                var (fillBrush, text) = RoomStyle(room, friendly, roomIndex++);
                var c = ((SolidColorBrush)fillBrush).Color;
                svg.Append(ci, $"<g fill=\"{Hex(c)}\" fill-opacity=\"{c.A / 255.0:0.###}\">");
                double sx = 0, sy = 0;
                foreach (var idx in room.Tiles)
                {
                    int dx = minC + idx % cols, dy = minR + idx / cols;
                    var r = CellRect(dx, dy, 1, 1);
                    svg.Append(ci, $"<rect x=\"{r.X:0.##}\" y=\"{r.Y:0.##}\" width=\"{r.Width:0.##}\" height=\"{r.Height:0.##}\"/>");
                    sx += dx + 0.5; sy += dy + 0.5;
                }
                svg.Append("</g>\n");
                var centre = m.Transform(new Point(_pan.X + sx / room.Tiles.Count * Zoom, _pan.Y + sy / room.Tiles.Count * Zoom));
                labels.Add(new SnapRoomLabel { Centre = centre, Ft = MakeLabel(text), Side = NearestSide(centre, outShip.Left, outShip.Right, outShip.Top, outShip.Bottom) });
            }
            svg.Append("</g>\n");   // close the rotation group — labels below are upright

            double topY = marginTiles * 0.4 * px, botY = outH - marginTiles * 0.4 * px;
            double leftX = marginTiles * 0.4 * px, rightX = outW - marginTiles * 0.4 * px;
            LayoutSide(labels.Where(l => l.Side == 0), horizontal: true, fixedCoord: topY, min: px, max: outW - px);
            LayoutSide(labels.Where(l => l.Side == 1), horizontal: true, fixedCoord: botY, min: px, max: outW - px);
            LayoutSide(labels.Where(l => l.Side == 2), horizontal: false, fixedCoord: leftX, min: px, max: outH - px);
            LayoutSide(labels.Where(l => l.Side == 3), horizontal: false, fixedCoord: rightX, min: px, max: outH - px);

            var leader = ((SolidColorBrush)LeaderPen.Brush).Color;
            var labelBg = ((SolidColorBrush)LabelBg).Color;
            foreach (var l in labels)
            {
                var ft = l.Ft;
                double lx = l.Ax, ly = l.Ay;
                svg.Append(ci, $"<line x1=\"{l.Centre.X:0.##}\" y1=\"{l.Centre.Y:0.##}\" x2=\"{lx:0.##}\" y2=\"{ly:0.##}\" " +
                               $"stroke=\"{Hex(leader)}\" stroke-opacity=\"{leader.A / 255.0:0.###}\" stroke-width=\"1.5\"/>\n");
                svg.Append(ci, $"<circle cx=\"{l.Centre.X:0.##}\" cy=\"{l.Centre.Y:0.##}\" r=\"3\" fill=\"#FFFFFF\"/>\n");
                double bxx = lx - ft.Width / 2 - 6, byy = ly - ft.Height / 2 - 3, bw = ft.Width + 12, bh = ft.Height + 6;
                svg.Append(ci, $"<rect x=\"{bxx:0.##}\" y=\"{byy:0.##}\" width=\"{bw:0.##}\" height=\"{bh:0.##}\" rx=\"3\" " +
                               $"fill=\"{Hex(labelBg)}\" fill-opacity=\"{labelBg.A / 255.0:0.###}\"/>\n");
                svg.Append(ci, $"<text x=\"{lx:0.##}\" y=\"{ly:0.##}\" font-family=\"Segoe UI, sans-serif\" font-size=\"17\" " +
                               $"fill=\"#FFFFFF\" text-anchor=\"middle\" dominant-baseline=\"central\">{Xml(ft.Text)}</text>\n");
            }

            svg.Append("</svg>\n");
            return svg.ToString();
        }
        finally
        {
            (_pan, Zoom, ViewRot) = (savedPan, savedZoom, savedRot);
        }
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    private static string Xml(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // ---- game preview art ----

    /// <summary>The frame the game's own ship previews are written at (<c>ScreenshotUtil.TakeScreenShot</c> crops
    /// its render to exactly this), and the aspect the kiosk and chargen panels expect their RawImage to be.</summary>
    private const int PreviewW = 800, PreviewH = 600;

    /// <summary>How much of the frame the subject fills. The whole ship is framed edge to edge with a little air;
    /// a room is framed at less than half so the surrounding decks stay visible, which is what makes the game's
    /// room thumbnails read as a place on a ship rather than a floating fragment.</summary>
    private const double ShipPreviewFill = 0.88, RoomPreviewFill = 0.45;

    /// <summary>How much closer than the whole-ship portrait a room thumbnail is drawn, at a minimum. Fitting the
    /// room alone is not enough on its own: on a small ship one room is most of the hull, so the fit lands at the
    /// ship's own zoom and every thumbnail comes out a near-copy of the portrait.</summary>
    private const double RoomZoomFloor = 1.6;

    /// <summary>Zoom ceiling for a preview, in output px per tile. Without it a two-room pod would be rendered at
    /// 100 px per 16 px sprite, which reads as a texture inspector rather than a ship portrait.</summary>
    private const int MaxPreviewPxPerTile = 48;

    /// <summary>Room thumbnails written per ship. The broker shows only a handful of slots and the game itself
    /// stops at 100; a low cap keeps a shareable mod folder from running to megabytes of near-black PNG.</summary>
    private const int MaxPreviewRooms = 12;

    /// <summary>Preview art is drawn on black, not on the editor's backdrop: these files sit beside the game's own
    /// in the same UI, and every one of those is a render of the ship against empty space.</summary>
    private static readonly Brush PreviewBg = Frozen(new SolidColorBrush(Colors.Black));

    /// <summary>
    /// The preview art the exported mod ships in <c>images/ships/</c>: one whole-ship portrait plus a thumbnail per
    /// certified room, all 800×600 on black, matching what the game's ship editor writes for a core ship. Null when
    /// the design is empty. Does not disturb the on-screen view.
    ///
    /// <para>Drawn unrotated whatever the editor's Q/E orientation is, unlike <see cref="RenderSnapshot"/>: this
    /// image stands in for the ship as the game will actually spawn it, and the plan-view rotation is a convenience
    /// of the editing surface that the exported data knows nothing about.</para>
    ///
    /// <para>Room naming follows <c>ScreenshotUtil.BuildTargetDict</c> exactly (spec <c>strName</c>, then
    /// <c>_1</c>/<c>_2</c> for repeats, skipping void, uncertified and trivially small rooms) because the broker
    /// recovers a thumbnail's room icon by stripping at the first underscore.</para>
    /// </summary>
    public ShipPreview? RenderGamePreview(IReadOnlyList<RoomSpecDef> specs)
    {
        if (Doc?.Bounds() is not { } b || Sprites is null) return null;

        var (savedPan, savedZoom, savedRot) = (_pan, Zoom, ViewRot);
        ViewRot = 0;
        try
        {
            var shipPx = FitPxPerTile(b.MaxX - b.MinX + 1, b.MaxY - b.MinY + 1, ShipPreviewFill);
            var ship = RenderPreviewFrame(b.MinX, b.MinY, b.MaxX, b.MaxY, shipPx);

            var rooms = new List<ShipPreviewRoom>();
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (name, rb) in PreviewRooms(b, specs))
            {
                if (rooms.Count >= MaxPreviewRooms) break;
                // the game's own dedupe: the plain name first, then _1, _2, … for each repeat
                var stem = name;
                for (var n = 1; !used.Add(stem); n++) stem = name + "_" + n;

                // fit the room, but never further out than a step in from the ship portrait: the point of a
                // thumbnail is to show one place aboard, which it stops doing the moment it frames the whole hull
                var px = Math.Clamp(
                    Math.Max(FitPxPerTile(rb.MaxX - rb.MinX + 1, rb.MaxY - rb.MinY + 1, RoomPreviewFill),
                             (int)Math.Ceiling(shipPx * RoomZoomFloor)),
                    1, MaxPreviewPxPerTile);
                rooms.Add(new ShipPreviewRoom(stem, RenderPreviewFrame(rb.MinX, rb.MinY, rb.MaxX, rb.MaxY, px)));
            }
            return new ShipPreview(ship, rooms);
        }
        finally
        {
            (_pan, Zoom, ViewRot) = (savedPan, savedZoom, savedRot);
        }
    }

    /// <summary>Output px per tile that fits a <paramref name="tilesW"/>×<paramref name="tilesH"/> subject into
    /// <paramref name="fill"/> of the preview frame. Whole pixels only, so a 16 px sprite lands on a pixel
    /// boundary and nearest-neighbour scaling stays even.</summary>
    private static int FitPxPerTile(int tilesW, int tilesH, double fill) =>
        Math.Clamp((int)Math.Floor(Math.Min(PreviewW * fill / Math.Max(1, tilesW), PreviewH * fill / Math.Max(1, tilesH))),
                   1, MaxPreviewPxPerTile);

    /// <summary>One 800×600 preview frame: the whole design drawn at <paramref name="pxPerTile"/>, centred on the
    /// given tile rect, on black. Everything outside the frame is clipped away by the bitmap, which is how a room
    /// thumbnail comes to show its neighbours cut off at the edges.</summary>
    private byte[] RenderPreviewFrame(int minX, int minY, int maxX, int maxY, int pxPerTile)
    {
        Zoom = pxPerTile;
        var centreX = (minX + maxX + 1) / 2.0;
        var centreY = (minY + maxY + 1) / 2.0;
        _pan = new Vector(PreviewW / 2.0 - centreX * pxPerTile, PreviewH / 2.0 - centreY * pxPerTile);

        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.NearestNeighbor);
        using (var ctx = dv.RenderOpen())
        {
            ctx.DrawRectangle(PreviewBg, null, new Rect(0, 0, PreviewW, PreviewH));
            foreach (var i in Doc!.RenderOrder()) DrawItem(ctx, i, (0, 0));
        }
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(PreviewW, PreviewH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);

        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// The rooms worth a thumbnail, each with its bounding rect in document tiles: certified, sealed, and more than
    /// the three tiles the game dismisses as a cupboard. Recomputed in a known frame, as
    /// <see cref="RenderRatingSnapshot"/> does, so a room's flat tile index maps back to an exact document tile.
    /// </summary>
    private IEnumerable<(string Name, (int MinX, int MinY, int MaxX, int MaxY) Bounds)> PreviewRooms(
        (int MinX, int MinY, int MaxX, int MaxY) b, IReadOnlyList<RoomSpecDef> specs)
    {
        const int pad = 1;
        int minC = b.MinX - pad, minR = b.MinY - pad;
        int cols = b.MaxX - b.MinX + 1 + 2 * pad, rows = b.MaxY - b.MinY + 1 + 2 * pad;
        var grid = ShipGrid.FromDocumentFramed(Doc!, Doc!.Catalog, minC, minR, cols, rows);
        var partition = RoomBuilder.Build(grid);
        RoomCertifier.CertifyAll(partition, specs, Doc.Catalog);

        foreach (var room in partition.Rooms)
        {
            // ScreenshotUtil.BuildTargetDict's own filter: void, blank-spec and <=3-tile rooms get no image
            if (room.Void || room.Tiles.Count <= 3 || room.RoomSpec is "Blank" or "") continue;

            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
            foreach (var idx in room.Tiles)
            {
                int dx = minC + idx % cols, dy = minR + idx / cols;
                x0 = Math.Min(x0, dx); y0 = Math.Min(y0, dy);
                x1 = Math.Max(x1, dx); y1 = Math.Max(y1, dy);
            }
            yield return (room.RoomSpec, (x0, y0, x1, y1));
        }
    }

    /// <summary>
    /// The output dimensions and content→output transform for a snapshot drawn in the editing orientation
    /// (<see cref="ViewRot"/>): the content (a <paramref name="pxW"/>×<paramref name="pxH"/> image) is rotated in
    /// 90° steps about its centre and re-origined at (0,0), matching the live Q/E plan-view rotation exactly.
    /// 90°/270° swap the output's width and height.
    /// </summary>
    private static (int OutW, int OutH, Transform M) OrientOutput(int rot, int pxW, int pxH)
    {
        rot = ((rot % 360) + 360) % 360;
        var g = new TransformGroup();
        g.Children.Add(new RotateTransform(rot, pxW / 2.0, pxH / 2.0));
        if (rot is 90 or 270)
        {
            // rotating a pxW×pxH box 90°/270° about its centre yields a pxH×pxW box centred at the same
            // point; shift it back so its top-left lands at (0,0)
            g.Children.Add(new TranslateTransform((pxH - pxW) / 2.0, (pxW - pxH) / 2.0));
            return (pxH, pxW, g);
        }
        return (pxW, pxH, g);   // 0° or 180° keep the dimensions
    }

    /// <summary>The SVG transform string equivalent to <see cref="OrientOutput"/>'s content→output transform, for
    /// the group holding the sprite layer and room tints. Empty at 0°.</summary>
    private static string SvgTransform(int rot, double pxW, double pxH)
    {
        rot = ((rot % 360) + 360) % 360;
        if (rot == 0) return "";
        var ci = CultureInfo.InvariantCulture;
        var rotate = string.Format(ci, "rotate({0} {1:0.##} {2:0.##})", rot, pxW / 2, pxH / 2);
        return rot is 90 or 270
            ? string.Format(ci, "translate({0:0.##} {1:0.##}) ", (pxH - pxW) / 2, (pxW - pxH) / 2) + rotate
            : rotate;
    }

    private static readonly Brush SnapshotBg = Frozen(new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x36)));
    private static readonly Brush RoomOpenFill = Frozen(new SolidColorBrush(Color.FromArgb(0x4A, 0xD6, 0x45, 0x45)));
    private static readonly Brush LabelBg = Frozen(new SolidColorBrush(Color.FromArgb(0xCC, 0x14, 0x16, 0x1A)));
    private static readonly Pen LeaderPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), 1.5));

    // a spread of distinct hues so adjacent rooms read apart at a glance; open-to-space is always the hazard red
    private static readonly Brush[] RoomPalette =
    [
        RoomFill(0x4A, 0x90, 0xE2), RoomFill(0x4C, 0xC2, 0x5B), RoomFill(0x9B, 0x59, 0xB6), RoomFill(0x1A, 0xBC, 0x9C),
        RoomFill(0xE6, 0x7E, 0x22), RoomFill(0xE8, 0x43, 0x93), RoomFill(0x00, 0xBC, 0xD4), RoomFill(0xAE, 0xEA, 0x00),
        RoomFill(0xF1, 0xC4, 0x0F), RoomFill(0x6C, 0x5C, 0xE7),
    ];

    private static Brush RoomFill(byte r, byte g, byte b) => Frozen(new SolidColorBrush(Color.FromArgb(0x46, r, g, b)));

    private static (Brush Fill, string Label) RoomStyle(RoomModel room, IReadOnlyDictionary<string, string> friendly, int index)
    {
        var label = room.Outside ? "Open to space"
            : room.RoomSpec != "Blank" ? friendly.GetValueOrDefault(room.RoomSpec, room.RoomSpec)
            : "Uncertified";
        var fill = room.Outside ? RoomOpenFill : RoomPalette[index % RoomPalette.Length];
        return (fill, label);
    }

    /// <summary>A room's label as it will be drawn: its ship-side (0=top,1=bottom,2=left,3=right), the room
    /// centre the leader points at, and — after <see cref="LayoutSide"/> — the label anchor in the margin.</summary>
    private sealed class SnapRoomLabel
    {
        public Point Centre;
        public System.Windows.Media.FormattedText Ft = null!;
        public int Side;
        public double Ax, Ay;
    }

    private static System.Windows.Media.FormattedText MakeLabel(string text) =>
        new(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 17, Brushes.White, 1.0) { TextAlignment = TextAlignment.Center };

    /// <summary>The ship edge nearest a room centre (0=top,1=bottom,2=left,3=right) — the shortest way out.</summary>
    private static int NearestSide(Point c, double left, double right, double top, double bottom)
    {
        double dT = c.Y - top, dB = bottom - c.Y, dL = c.X - left, dR = right - c.X;
        var m = Math.Min(Math.Min(dT, dB), Math.Min(dL, dR));
        return m == dT ? 0 : m == dB ? 1 : m == dL ? 2 : 3;
    }

    /// <summary>
    /// Lay out one edge's labels: each wants to sit straight out from its room (a short perpendicular leader), so
    /// we start at that ideal position and push neighbours apart only where they'd overlap. Processing in sorted
    /// order and only ever nudging along the edge keeps the order — so the leaders never cross each other.
    /// </summary>
    private static void LayoutSide(IEnumerable<SnapRoomLabel> side, bool horizontal, double fixedCoord, double min, double max)
    {
        var list = side.OrderBy(l => horizontal ? l.Centre.X : l.Centre.Y).ToList();
        if (list.Count == 0) return;
        const double gap = 10;
        var pos = new double[list.Count];
        var half = new double[list.Count];
        for (var i = 0; i < list.Count; i++)
        {
            pos[i] = horizontal ? list[i].Centre.X : list[i].Centre.Y;
            half[i] = (horizontal ? list[i].Ft.Width : list[i].Ft.Height) / 2 + 6;
        }
        // forward pass (push right/down), clamp the far end, backward pass (push left/up), clamp the near end
        for (var i = 1; i < list.Count; i++)
            pos[i] = Math.Max(pos[i], pos[i - 1] + half[i - 1] + gap + half[i]);
        pos[^1] = Math.Min(pos[^1], max - half[^1]);
        for (var i = list.Count - 2; i >= 0; i--)
            pos[i] = Math.Min(pos[i], pos[i + 1] - half[i + 1] - gap - half[i]);
        pos[0] = Math.Max(pos[0], min + half[0]);
        for (var i = 1; i < list.Count; i++)
            pos[i] = Math.Max(pos[i], pos[i - 1] + half[i - 1] + gap + half[i]);

        for (var i = 0; i < list.Count; i++)
        {
            if (horizontal) { list[i].Ax = pos[i]; list[i].Ay = fixedCoord; }
            else { list[i].Ax = fixedCoord; list[i].Ay = pos[i]; }
        }
    }

    private static void DrawRoomLabel(DrawingContext ctx, System.Windows.Media.FormattedText ft, Point room, Point label)
    {
        var box = new Rect(label.X - ft.Width / 2 - 6, label.Y - ft.Height / 2 - 3, ft.Width + 12, ft.Height + 6);
        ctx.DrawLine(LeaderPen, room, label);
        ctx.DrawEllipse(Brushes.White, null, room, 3, 3);
        ctx.DrawRoundedRectangle(LabelBg, null, box, 3, 3);
        ctx.DrawText(ft, new Point(label.X, label.Y - ft.Height / 2));
    }

    // ---- zone overlay ----

    /// <summary>Paint the zone overlay: a translucent per-tile fill in each zone's own colour (the grid lines show
    /// through so individual tiles read), plus a name label at the zone's centroid. The active (being-painted)
    /// zone is tinted more strongly. Drawn live in <see cref="OnRender"/> — never baked into the sprite cache — so
    /// paint/erase and undo appear immediately.</summary>
    private void DrawZones(DrawingContext dc)
    {
        if (Doc is null) return;
        foreach (var z in Doc.Zones)
        {
            var active = z.Id == ActiveZoneId;
            var tiles = active && _zoneWorking is not null ? (ICollection<(int X, int Y)>)_zoneWorking : z.Tiles;
            var fill = ZoneFillBrush(z.Color, active);
            foreach (var (x, y) in tiles)
                dc.DrawRectangle(fill, null, CellRect(x, y, 1, 1));
        }
        foreach (var z in Doc.Zones)
        {
            var tiles = z.Id == ActiveZoneId && _zoneWorking is not null ? (ICollection<(int X, int Y)>)_zoneWorking : z.Tiles;
            if (tiles.Count > 0) DrawZoneLabel(dc, z, tiles);
        }
    }

    private static Brush ZoneFillBrush(ZoneColor c, bool active)
    {
        static byte B(double v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);
        return Frozen(new SolidColorBrush(Color.FromArgb(active ? (byte)0x82 : (byte)0x4A, B(c.R), B(c.G), B(c.B))));
    }

    private void DrawZoneLabel(DrawingContext dc, ShipZone z, ICollection<(int X, int Y)> tiles)
    {
        double sx = 0, sy = 0;
        foreach (var (x, y) in tiles) { sx += x + 0.5; sy += y + 0.5; }
        var c = new Point(_pan.X + sx / tiles.Count * Zoom, _pan.Y + sy / tiles.Count * Zoom);
        var ft = MakeLabel(string.IsNullOrWhiteSpace(z.Name) ? "zone" : z.Name);
        var box = new Rect(c.X - ft.Width / 2 - 5, c.Y - ft.Height / 2 - 2, ft.Width + 10, ft.Height + 4);
        dc.DrawRoundedRectangle(LabelBg, null, box, 3, 3);
        dc.DrawText(ft, new Point(c.X, c.Y - ft.Height / 2));
    }

    // ---- zone painting (a working tile set, previewed live, committed as one SetZoneTilesCommand) ----

    private void ApplyZoneCell((int X, int Y) cell)
    {
        if (_zoneWorking is null) return;
        if (_zoneErase) _zoneWorking.Remove(cell); else _zoneWorking.Add(cell);
    }

    /// <summary>Rebuild the working set as the stroke-start tiles combined with the rectangle from the drag start
    /// to <paramref name="end"/> (added, or removed when erasing) — a box add/erase previewed as it drags.</summary>
    private void RebuildZoneBox((int X, int Y) end)
    {
        _zoneWorking = [.. _zoneBefore];
        var (x0, x1) = (Math.Min(_dragStartCell.X, end.X), Math.Max(_dragStartCell.X, end.X));
        var (y0, y1) = (Math.Min(_dragStartCell.Y, end.Y), Math.Max(_dragStartCell.Y, end.Y));
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
                if (_zoneErase) _zoneWorking.Remove((x, y)); else _zoneWorking.Add((x, y));
    }

    /// <summary>Finish a stroke: hand the before/after tile sets to the window (which pushes one command) unless
    /// nothing changed, then drop the working set so the overlay reflects the committed zone.</summary>
    private void CommitZoneStroke(ShipZone zone)
    {
        var after = _zoneWorking ?? new HashSet<(int X, int Y)>(zone.Tiles);
        if (!after.SetEquals(_zoneBefore)) ZoneStrokeCommitted?.Invoke(zone.Id, _zoneBefore, after);
        _zoneWorking = null;
        InvalidateVisual();
    }

    /// <summary>The document tiles of the enclosed (non-open-to-space) room under <paramref name="cell"/>, via the
    /// same room flood-fill the rating uses — so a double-click fills a whole compartment into the zone. Empty when
    /// the cell is open space or off the ship.</summary>
    private IEnumerable<(int X, int Y)> RoomTilesAt((int X, int Y) cell)
    {
        if (Doc is null || Doc.Bounds() is not { } b) return [];
        const int pad = 1;
        int minC = b.MinX - pad, minR = b.MinY - pad;
        int cols = b.MaxX - b.MinX + 1 + 2 * pad, rows = b.MaxY - b.MinY + 1 + 2 * pad;
        int cc = cell.X - minC, cr = cell.Y - minR;
        if (cc < 0 || cc >= cols || cr < 0 || cr >= rows) return [];
        var grid = ShipGrid.FromDocumentFramed(Doc, Doc.Catalog, minC, minR, cols, rows);
        var partition = RoomBuilder.Build(grid);
        var target = cc + cr * cols;
        foreach (var room in partition.Rooms)
            if (!room.Outside && room.Tiles.Contains(target))
                return room.Tiles.Select(idx => (minC + idx % cols, minR + idx / cols));
        return [];
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

        // The placement sprites. When Light Viz is on we always show the lit composite as the ship body — even
        // mid-drag — so the ship never "un-lights" while you manipulate it (the old drag path drew flat sprites,
        // which read as a flicker against the lit look). The composite is a snapshot from stroke start, so the only
        // parts that differ from it are the in-flux ones (the moving selection, the live paint stroke); those draw
        // live on top via DrawInFluxParts. A moving part therefore shows twice for the duration of the drag: lit at
        // its origin (baked in the composite) and live at the cursor — the expected drag-preview double.
        var lit = ShowLight && _lightImage is not null;
        if (_drag is Drag.Move or Drag.Paint && !lit)
        {
            // No composite to lean on (Light Viz off, or not yet baked): a Move drags selected parts (offset per
            // frame) and a Paint adds parts live, so both draw straight through, bypassing the cached drawing.
            DrawItems(dc, [.. Doc.RenderOrder()],
                i => _drag == Drag.Move && i.Placement is { } p && SelectedIds.Contains(p.Id) && !Doc.IsLocked(p)
                     ? MoveDeltaFor(p) : (0, 0));
        }
        else
        {
            // The cache/composite is baked at pan zero, so shift it to the live pan with a transform — panning stays
            // a transform + one blit instead of rebuilding the whole ship each frame. Every non-drag state (idle,
            // band-select, box-fill preview) reuses this too, so it skips the DrawOrder + autotile pass each frame.
            dc.PushTransform(new TranslateTransform(_pan.X, _pan.Y));
            if (lit)
            {
                // Light Viz: the game-exact composite (albedo x accumulated light + glow decals), one doc-space
                // bitmap at 16 px/tile scaled like a sprite. Unlit hull is a black silhouette, exactly in-game.
                var r = _lightImageRect;
                dc.DrawImage(_lightImage, new Rect(r.X * Zoom, r.Y * Zoom, r.Width * Zoom, r.Height * Zoom));
            }
            else dc.DrawDrawing(StaticShip());
            dc.Pop();
            if (_drag is Drag.Move or Drag.Paint) DrawInFluxParts(dc);   // the moving selection / live paint stroke, over the lit backdrop
        }

        DrawLooseSelection(dc);   // the outline on the selected loose item; the item itself draws with the ship
        DrawIllegalCells(dc);
        DrawLeakCells(dc);
        DrawAirSelection(dc);
        if (ShowRooms) DrawRoomOverlay(dc);   // under the zones: rooms are the ground truth a zone is drawn onto
        if (ShowWalk) DrawWalkOverlay(dc);    // over the rooms (a walk zone cuts across compartments), under the zones
        DrawDamageOverlay(dc);                // the heat map: only ever a handful of parts, and always on top
        DrawGhostPath(dc);                    // the strike being aimed, over everything
        if (ShowZones || ActiveZoneId is not null) DrawZones(dc);
        DrawOutOfBounds(dc, view);
        DrawOriginMarker(dc);
        if (ShowPower) DrawPowerOverlay(dc);
        if (WireMode) DrawDeviceLinks(dc);   // wiring is an overlay like the rest: gated here, not half-gated inside
        if (SymMode != SymmetryMode.Off) DrawSymmetryAxes(dc, view);

        foreach (var p in Doc.Placements.Where(p => SelectedIds.Contains(p.Id)))
        {
            var (bx, by, bw, bh) = Doc.BodyBounds(p);   // outline the above-floor body (3×3 for the tanks), not the 7×7 socket
            (int X, int Y) offset = _drag == Drag.Move && !Doc.IsLocked(p) ? MoveDeltaFor(p) : (0, 0);
            dc.DrawRectangle(null, SelectPen, CellRect(bx + offset.X, by + offset.Y, bw, bh));
            // connector nubs on a selected powered part, so its plugs/feed are visible for wiring
            if (Doc.Part(p) is { IsPowered: true } pd) DrawConnectorNubs(dc, pd, p.X + offset.X, p.Y + offset.Y, p.Rot);
        }

        if (ArmedPart is not null && _armedLoose && _hoverCell is { } looseHover)
        {
            DrawLooseGhost(dc, looseHover);
        }
        else if (ArmedPart is not null && _hoverCell is { } hover)
        {
            var (w, h) = GridMath.Size(ArmedPart.Item.Width, ArmedPart.Item.Height, ArmedRot);
            var (gx, gy) = (hover.X - (w - 1) / 2, hover.Y - (h - 1) / 2);

            // Preview the cursor pose AND every symmetry mirror, each judged independently: green where the
            // placement law allows it, red (with the offending tiles tinted) where it doesn't. A mirror that
            // won't land is now visible BEFORE the click instead of being a silent no-op — the root of the
            // "symmetry only works most of the time" reports. Coincident poses (a part on an axis mirrors onto
            // itself) draw once, exactly as TryPlacePose dedups them. The status-bar reason is the cursor pose's.
            var surface = SurfaceBrush;
            var seen = new HashSet<(int, int, int)>();
            FitResult? cursor = null;
            var cursorPart = ArmedPart;
            var cursorForced = false;   // the cursor pose was judged by the mode, not by the placement law
            foreach (var pose in WithSymmetry(gx, gy, ArmedRot, w, h))
            {
                if (!seen.Add(pose)) continue;
                // Surfaces mode previews what the stroke would actually lay: the pattern's choice for this tile,
                // and a green ghost wherever it re-skins rather than places (a same-class swap is always legal, so
                // the placement law has no say and would otherwise paint the tile red for being occupied).
                var part = surface is null ? ArmedPart : PatternPartAt(surface, pose.X, pose.Y);
                var verdict = SurfaceVerdict(surface, part, pose.X, pose.Y);
                var fit = DrawArmedGhost(dc, part, pose.X, pose.Y, pose.Rot, verdict);
                // WithSymmetry yields the cursor pose first
                if (cursor is null) { cursor = fit; cursorPart = part; cursorForced = verdict is not null; }
            }
            if (cursor is { Ok: false } bad)
            {
                var why = bad.Reason ?? "doesn't fit here";
                var modded = cursorPart.IsModded && !cursorForced;   // a mode refusal is not the law, and no override lifts it
                if (modded && AllowModdedOverrides) RaiseGhostReason(why, willPlace: true);
                else if (modded) RaiseGhostReason(why + " — modded; turn on \"Mod overrides\" to place it anyway");
                else RaiseGhostReason(why);
            }
            else if (cursor is { Advisory: { } adv }) RaiseGhostReason(adv, advisory: true);   // legal, but a soft req is unmet
            else RaiseGhostReason(null);
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

    /// <summary>Highlight the enclosed air region selected for a fill (double-click empty space; arm + Enter to fill).</summary>
    private void DrawAirSelection(DrawingContext dc)
    {
        foreach (var (x, y) in _airSelection)
            dc.DrawRectangle(AirFill, AirPen, CellRect(x, y, 1, 1));
    }

    /// <summary>
    /// Hazard-stripe the area beyond the mating face of the one port that bounds construction
    /// (Item.CheckFit's envelope, made visible). At most one zone: only the Primary airlock ever
    /// bounds, so a Secondary draws nothing (ProblemScan.BoundingPort).
    /// </summary>
    private void DrawOutOfBounds(DrawingContext dc, Rect view)
    {
        if (ProblemScan.BoundingPort(Doc!, Doc!.Catalog) is not { } p) return;
        if (!ProblemScan.TryGetFace(Doc.Part(p)!, p, out var axisY, out var dir, out var face)) return;

        var faceScreen = (axisY ? _pan.Y : _pan.X) + face * Zoom;
        var zone = axisY
            ? dir < 0
                ? new Rect(view.X, view.Y, view.Width, Math.Max(0, faceScreen - view.Y))
                : new Rect(view.X, faceScreen, view.Width, Math.Max(0, view.Bottom - faceScreen))
            : dir < 0
                ? new Rect(view.X, view.Y, Math.Max(0, faceScreen - view.X), view.Height)
                : new Rect(faceScreen, view.Y, Math.Max(0, view.Right - faceScreen), view.Height);
        if (zone.Width > 0 && zone.Height > 0) dc.DrawGeometry(OobBrush, null, new RectangleGeometry(zone));
    }

    /// <summary>Centre of a document tile in screen space (pre view-rotation transform, like <see cref="CellRect"/>).</summary>
    private Point TileCenter((int X, int Y) t) => new(_pan.X + (t.X + 0.5) * Zoom, _pan.Y + (t.Y + 0.5) * Zoom);

    // ---- RoomViz (the compartment overlay) ----

    private static readonly Brush RoomTextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0xF7, 0xFF)));
    private static readonly Brush RoomDimTextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xB4, 0xC0, 0xD0)));
    private static readonly Brush RoomWarnTextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF0, 0xC4, 0x60)));
    private static readonly Pen RoomEdgePen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), 1));

    /// <summary>Below this zoom a room label would be unreadable clutter, so only the tints draw.</summary>
    private const double RoomLabelMinZoom = 9.0;

    private const double RoomLabelPad = 5;
    private const double RoomLabelGap = 1;

    /// <summary>A room's baked label: its text lines and their block size (both zoom-independent), the room's
    /// centroid in pan-zero space, and how important it is to show (bigger rooms win a collision). Built once per
    /// overlay in <see cref="EnsureRoomVisuals"/> — <see cref="FormattedText"/> is far too costly to build per frame.</summary>
    private sealed class RoomLabel
    {
        public FormattedText[] Lines = [];
        public FormattedText[] TitleOnly = [];
        public double W, H;                 // full block
        public double TitleW, TitleH;       // fallback block, when the full one won't fit
        public Point Centre;                // pan-zero
        public int Weight;                  // tile count: the label a collision should keep
    }

    private static FormattedText RoomText(string s, double size, Brush brush, bool bold = false) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal,
                bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, 1.0)
        { TextAlignment = TextAlignment.Center };

    /// <summary>Bake the room fills into frozen pan-zero geometries at the current zoom and the labels into
    /// ready-made text, so a frame is a few <see cref="DrawingContext.DrawGeometry"/> calls plus some DrawText
    /// rather than a rectangle per tile and a text layout per room. Rebuilt only when the overlay or zoom changes
    /// (see <see cref="_roomGeoDirty"/>) — this is what keeps panning smooth with RoomViz on.</summary>
    private void EnsureRoomVisuals()
    {
        if (!_roomGeoDirty) return;
        _roomGeoDirty = false;

        var geos = new List<(Geometry, Brush)>(_roomOverlay.Rooms.Count);
        var labels = new List<RoomLabel>(_roomOverlay.Rooms.Count);
        foreach (var room in _roomOverlay.Rooms)
        {
            if (room.Tiles.Count == 0) continue;

            var geo = new StreamGeometry();
            using (var c = geo.Open())
                foreach (var (x, y) in room.Tiles)
                {
                    // one closed square per cell; the grid lines still show through the translucent fill
                    var r = new Rect(x * Zoom, y * Zoom, Zoom, Zoom);
                    c.BeginFigure(r.TopLeft, true, true);
                    c.PolyLineTo([r.TopRight, r.BottomRight, r.BottomLeft], true, false);
                }
            geo.Freeze();
            geos.Add((geo, room.Void ? RoomOpenFill : RoomPalette[room.Index % RoomPalette.Length]));

            var title = room.Certified ? room.SpecFriendly : room.Void ? "Unsealed" : "Uncertified";
            var titleFt = RoomText(title, 13, room.Certified ? RoomTextBrush : RoomWarnTextBrush, bold: true);
            var lines = new List<FormattedText>
            {
                titleFt,
                RoomText($"{room.TileCount} tiles · ${room.Value.ToString("#,##0", CultureInfo.InvariantCulture)}", 11, RoomDimTextBrush),
            };
            foreach (var miss in room.NearMisses)
                lines.Add(RoomText(miss, 11, RoomDimTextBrush));

            double sx = 0, sy = 0;
            foreach (var (x, y) in room.Tiles) { sx += x + 0.5; sy += y + 0.5; }

            labels.Add(new RoomLabel
            {
                Lines = [.. lines],
                TitleOnly = [titleFt],
                W = lines.Max(l => l.Width),
                H = lines.Sum(l => l.Height) + RoomLabelGap * (lines.Count - 1),
                TitleW = titleFt.Width,
                TitleH = titleFt.Height,
                Centre = new Point(sx / room.Tiles.Count * Zoom, sy / room.Tiles.Count * Zoom),
                Weight = room.TileCount,
            });
        }
        _roomGeos = geos;
        _roomLabels = labels;
    }

    /// <summary>
    /// RoomViz: the compartments the game would flood-fill, each tinted in its own hue so the partition reads at a
    /// glance, labelled with what it certifies as, its size and its worth. A room that certifies as nothing also
    /// lists why — what to add, and which member item forbids it — so the classic silent failure (a canister parked
    /// in an otherwise-valid quarters) is visible on the plan instead of only in the Ship Rating report.
    /// An unsealed room draws in the same hazard red the rating snapshot uses for open-to-space.
    /// Drawn live in <see cref="OnRender"/>, never baked into the sprite cache, so edits appear immediately.
    /// </summary>
    private void DrawRoomOverlay(DrawingContext dc)
    {
        if (_roomOverlay.IsEmpty) return;
        EnsureRoomVisuals();

        // fills: baked at pan zero, so draw them under the live-pan transform (one DrawGeometry per room)
        dc.PushTransform(new TranslateTransform(_pan.X, _pan.Y));
        foreach (var (geo, fill) in _roomGeos!)
            dc.DrawGeometry(fill, null, geo);
        dc.Pop();

        if (Zoom >= RoomLabelMinZoom) DrawRoomLabels(dc);
    }

    // Walk zones reuse the room hues (the same "these cells belong together" reading), but the exterior zone gets a
    // cold neutral so an EVA route never looks like another compartment.
    private static readonly Brush WalkExteriorFill = Frozen(new SolidColorBrush(Color.FromArgb(0x33, 0x8A, 0x9B, 0xB0)));
    private static readonly Pen UnreachablePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xE2, 0x4A, 0x4A)), 2.0));
    private static readonly Brush UnreachableFill = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0xE2, 0x4A, 0x4A)));
    private static readonly Pen EvaPortalPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xF0, 0xC4, 0x60)), 2.0)
    { DashStyle = new DashStyle([2, 2], 0) });

    /// <summary>Bake the walk-zone fills into frozen pan-zero geometries at the current zoom, exactly as
    /// <see cref="EnsureRoomVisuals"/> does for compartments — one DrawGeometry per zone per frame instead of a
    /// rectangle per tile.</summary>
    private void EnsureWalkVisuals()
    {
        if (!_walkGeoDirty) return;
        _walkGeoDirty = false;

        var geos = new List<(Geometry, Brush)>(_walkOverlay.Zones.Count);
        for (var i = 0; i < _walkOverlay.Zones.Count; i++)
        {
            var tiles = _walkOverlay.Zones[i];
            if (tiles.Count == 0) continue;

            var geo = new StreamGeometry();
            using (var c = geo.Open())
                foreach (var (x, y) in tiles)
                {
                    var r = new Rect(x * Zoom, y * Zoom, Zoom, Zoom);
                    c.BeginFigure(r.TopLeft, true, true);
                    c.PolyLineTo([r.TopRight, r.BottomRight, r.BottomLeft], true, false);
                }
            geo.Freeze();
            geos.Add((geo, _walkOverlay.ZoneIsExterior[i] ? WalkExteriorFill : RoomPalette[i % RoomPalette.Length]));
        }
        _walkGeos = geos;
    }

    /// <summary>
    /// WalkViz: every tile a crew member can stand on, tinted by which connected zone it belongs to, so "have I
    /// walled myself out of the engine room" is answerable at a glance. Two tiles sharing a hue are mutually
    /// reachable on foot; two hues means no route, and the usual culprit is a wall, a solid fixture, or a closed
    /// door that is unpowered, locked or damaged (those add <c>IsPortalStuck</c> and genuinely seal, unlike a
    /// powered one). Fittings no crew member can operate are ringed in hazard red at the point they would have to
    /// stand, and a doorway with vacuum on one side is dashed amber: crossable, but only in a suit.
    /// Drawn live in <see cref="OnRender"/>, never baked into the sprite cache, so edits appear immediately.
    /// </summary>
    private void DrawWalkOverlay(DrawingContext dc)
    {
        if (_walkOverlay.IsEmpty) return;
        EnsureWalkVisuals();

        dc.PushTransform(new TranslateTransform(_pan.X, _pan.Y));
        foreach (var (geo, fill) in _walkGeos!)
            dc.DrawGeometry(fill, null, geo);
        dc.Pop();

        // amber dashes = "suit up": a doorway with vacuum across it, or hull-mounted kit you EVA to
        foreach (var (x, y) in _walkOverlay.EvaOnlyPortals)
            dc.DrawRectangle(null, EvaPortalPen, CellRect(x, y, 1, 1));
        foreach (var (x, y) in _walkOverlay.EvaOnlyDevices)
            dc.DrawRectangle(null, EvaPortalPen, CellRect(x, y, 1, 1));

        // solid red = nobody can operate this, suited or not
        foreach (var (x, y) in _walkOverlay.UnreachableDevices)
            dc.DrawRectangle(UnreachableFill, UnreachablePen, CellRect(x, y, 1, 1));
    }

    /// <summary>
    /// The damage heat map: every part a run of strikes has touched, tinted green through amber to red by what it
    /// has left. Only damaged parts are drawn, so the eye goes to what took the hit rather than to a wash of green.
    ///
    /// <para>Not baked like RoomViz or WalkViz. Those fill hundreds of cells and had to be frozen into geometry to
    /// keep panning smooth; this is a handful of parts, and it changes on every strike, so baking would cost more
    /// than it saved.</para>
    /// </summary>
    private void DrawDamageOverlay(DrawingContext dc)
    {
        if (_damageOverlay.IsEmpty) return;
        foreach (var part in _damageOverlay.Parts)
        {
            var fill = DamageBrush(part.Condition);
            foreach (var (x, y) in part.Tiles) dc.DrawRectangle(fill, null, CellRect(x, y, 1, 1));
            // A destroyed part gets an outline too: at a glance a dark red tint and a very dark red tint are hard
            // to tell apart, and "gone" is the one answer nobody should have to squint at.
            if (part.Destroyed)
                foreach (var (x, y) in part.Tiles) dc.DrawRectangle(null, DestroyedPen, CellRect(x, y, 1, 1));
        }
    }

    /// <summary>Green at full health through amber to red at destroyed. Cached per 5% band: a ship of damaged parts
    /// would otherwise allocate a brush per part per frame.</summary>
    private static Brush DamageBrush(double condition)
    {
        var band = Math.Clamp((int)Math.Round(condition * 20), 0, 20);
        if (_damageBrushes[band] is { } cached) return cached;
        var t = band / 20.0;
        // Through amber rather than straight green-to-red, so the middle of the scale stays legible: the upper
        // half fades red out of a steady green, the lower half fades green out of a steady red.
        var (r, g) = t >= 0.5
            ? ((byte)(255 * (1 - t) * 2), (byte)200)
            : ((byte)220, (byte)(200 * t * 2));
        var brush = new SolidColorBrush(Color.FromArgb(150, r, g, 40));
        brush.Freeze();
        return _damageBrushes[band] = brush;
    }

    private static readonly Brush?[] _damageBrushes = new Brush?[21];

    private static readonly Pen DestroyedPen = FrozenPen(Color.FromArgb(230, 190, 30, 30), 2);

    /// <summary>The strike being aimed: the pivot every micrometeoroid converges on, and the ghost path through it.
    /// Drawn while a Simulate dialog owns the cursor and never otherwise.</summary>
    private void DrawGhostPath(DrawingContext dc)
    {
        if (!_aiming) return;

        if (_ghostPath is { } path)
        {
            var a = new Point(_pan.X + path.Start.X * Zoom, _pan.Y + path.Start.Y * Zoom);
            var b = new Point(_pan.X + path.End.X * Zoom, _pan.Y + path.End.Y * Zoom);
            dc.DrawLine(GhostPen, a, b);
        }

        if (_strikePivot is { } pivot)
        {
            var c = new Point(_pan.X + pivot.X * Zoom, _pan.Y + pivot.Y * Zoom);
            var rad = Math.Max(4, Zoom * 0.35);
            dc.DrawEllipse(null, PivotPen, c, rad, rad);
            dc.DrawLine(PivotPen, new Point(c.X - rad * 1.6, c.Y), new Point(c.X + rad * 1.6, c.Y));
            dc.DrawLine(PivotPen, new Point(c.X, c.Y - rad * 1.6), new Point(c.X, c.Y + rad * 1.6));
        }
    }

    private static readonly Pen GhostPen = FrozenDashedPen(Color.FromArgb(220, 255, 240, 120), 2, 6, 4);
    private static readonly Pen PivotPen = FrozenPen(Color.FromArgb(230, 255, 240, 120), 1.5);

    private static Pen FrozenPen(Color c, double thickness)
    {
        var p = new Pen(new SolidColorBrush(c), thickness);
        p.Freeze();
        return p;
    }

    private static Pen FrozenDashedPen(Color c, double thickness, double dash, double gap)
    {
        var p = new Pen(new SolidColorBrush(c), thickness)
        {
            DashStyle = new DashStyle([dash, gap], 0),
        };
        p.Freeze();
        return p;
    }

    /// <summary>
    /// Rebuild the Light Viz composite for the current scene: bake the ship's albedo and normal maps to doc-space
    /// bitmaps at the game's native 16 px/tile (UI thread — WPF renders them), then run the exact ported light
    /// pipeline on a worker (<see cref="LightComposite"/>: VisibilityMesh shadow geometry, the LoSPass falloff,
    /// screen-blend accumulation, glow decals) and store one frozen bitmap for <see cref="OnRender"/>. A stale
    /// worker result is dropped via <see cref="_lightJob"/>.
    /// </summary>
    private void RebuildLightComposite()
    {
        var job = ++_lightJob;
        // Keep the CURRENT composite on screen while the new one bakes off-thread — nulling it here made every edit
        // flash the unlit StaticShip() fallback for a frame or two ("lit -> black -> lit") until the worker returned.
        // The retained frame is at most one edit stale; the worker swaps it in place (guarded by _lightJob). Only the
        // paths below that genuinely have nothing to show clear it.
        if (!ShowLight || Doc is null || Sprites is null || Doc.Bounds() is not { } b) { _lightImage = null; return; }

        const int margin = 6;   // room for glow halos past the hull; lit pixels need albedo, which stays in bounds
        const int ppt = 16;
        int minX = b.MinX - margin, minY = b.MinY - margin;
        int tilesW = b.MaxX - b.MinX + 1 + 2 * margin, tilesH = b.MaxY - b.MinY + 1 + 2 * margin;
        int w = tilesW * ppt, h = tilesH * ppt;
        if ((long)w * h > 64_000_000) { _lightImage = null; return; }   // a station past ~500x500 tiles: skip rather than exhaust memory

        var albedo = BakeDocPixels(minX, minY, tilesW, tilesH, ppt, normalPass: false);
        var normal = BakeDocPixels(minX, minY, tilesW, tilesH, ppt, normalPass: true);
        var glows = new List<GlowImage>(_lightScene.Glows.Count);
        foreach (var g in _lightScene.Glows)
            if (Sprites.GlowPixels(g.SpriteAbs, g.Rot) is { } gp)
                glows.Add(new GlowImage(g.DocX, g.DocY, gp.W, gp.H, gp.Bgra));

        var scene = _lightScene;
        Task.Run(() =>
        {
            var acc = LightComposite.AccumulateLights(scene, w, h, ppt, minX, minY, normal);
            var outPx = LightComposite.Compose(albedo, acc, w, h, ppt, minX, minY, glows);
            var bmp = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, outPx, w * 4);
            bmp.Freeze();
            Dispatcher.InvokeAsync(() =>
            {
                if (job != _lightJob || !ShowLight) return;
                _lightImage = bmp;
                _lightImageRect = new Rect(minX, minY, tilesW, tilesH);
                InvalidateVisual();
            });
        });
    }

    /// <summary>Render the ship (placements + loose items) into a doc-space pixel buffer at
    /// <paramref name="ppt"/> px/tile — the albedo pass, or the normal-map pass (each sprite swapped for its
    /// vector-swizzled normal texture; uncovered pixels keep alpha 0 = flat). Premultiplied BGRA.</summary>
    private byte[] BakeDocPixels(int minX, int minY, int tilesW, int tilesH, int ppt, bool normalPass)
    {
        var (savedPan, savedZoom, savedRot) = (_pan, Zoom, ViewRot);
        Zoom = ppt;
        _pan = new Vector(-minX * (double)ppt, -minY * (double)ppt);
        ViewRot = 0;
        _normalPass = normalPass;
        try
        {
            var dv = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.NearestNeighbor);
            using (var ctx = dv.RenderOpen())
            {
                foreach (var i in Doc!.RenderOrder()) DrawItem(ctx, i, (0, 0));
            }
            int w = tilesW * ppt, h = tilesH * ppt;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var px = new byte[w * h * 4];
            rtb.CopyPixels(px, w * 4, 0);
            return px;
        }
        finally
        {
            _normalPass = false;
            (_pan, Zoom, ViewRot) = (savedPan, savedZoom, savedRot);
        }
    }

    /// <summary>
    /// Lay the room labels out and draw them. Two things the naive version got wrong on a real ship:
    /// <list type="bullet">
    /// <item>Labels must stay <b>upright</b>. The whole render pass sits under the view-rotation transform (Q/E),
    /// so text drawn plainly comes out sideways; each label counter-rotates about its own anchor, the same trick
    /// the connector badges use.</item>
    /// <item>Labels must not <b>overlap</b>. Centroids of neighbouring compartments can sit close together (and a
    /// near-miss line is wide), so labels are placed biggest-room-first and each one takes the first of: its full
    /// block, a nudge up/down, its title alone, or nothing. Dropping the smallest room's label beats stacking two
    /// unreadable ones.</item>
    /// </list>
    /// Off-screen labels are skipped, so a station costs only what is actually in view.
    /// </summary>
    private void DrawRoomLabels(DrawingContext dc)
    {
        var screen = new Rect(RenderSize);
        var taken = new List<Rect>();

        foreach (var label in _roomLabels!.OrderByDescending(l => l.Weight))
        {
            var anchor = new Point(_pan.X + label.Centre.X, _pan.Y + label.Centre.Y);   // pan space
            // Counter-rotating about the anchor makes the label an UPRIGHT box centred where the anchor lands on
            // screen, and a (0,dy) nudge about the anchor survives the round trip as a (0,dy) screen offset. So
            // collisions are resolved in screen space while the draw stays in the rotated pass.
            var at = PanSpaceToScreen(anchor);
            if (!screen.Contains(at)) continue;   // the room's centre isn't in view — nothing to label

            foreach (var (w, h, dy, lines) in Candidates(label))
            {
                var box = LabelBox(at, w, h, dy);
                if (taken.Any(t => t.IntersectsWith(box))) continue;
                taken.Add(box);
                DrawRoomLabelBlock(dc, anchor, LabelBox(anchor, w, h, dy), lines);
                break;
            }
        }
    }

    private static Rect LabelBox(Point at, double w, double h, double dy) =>
        new(at.X - w / 2 - RoomLabelPad, at.Y + dy - h / 2 - RoomLabelPad,
            w + RoomLabelPad * 2, h + RoomLabelPad * 2);

    /// <summary>Where to try putting a label, best first: the full block on the centroid, then nudged clear above
    /// or below it, then the title on its own. A small room losing its detail (or its label) beats two stacked
    /// unreadable ones — and the biggest-room-first order means what survives is what matters most.</summary>
    private static IEnumerable<(double W, double H, double Dy, FormattedText[] Lines)> Candidates(RoomLabel l)
    {
        yield return (l.W, l.H, 0, l.Lines);
        yield return (l.W, l.H, -(l.H / 2 + 10), l.Lines);
        yield return (l.W, l.H, l.H / 2 + 10, l.Lines);
        yield return (l.TitleW, l.TitleH, 0, l.TitleOnly);
    }

    private void DrawRoomLabelBlock(DrawingContext dc, Point anchor, Rect box, FormattedText[] lines)
    {
        // the render pass is rotated by ViewRot; counter-rotate about the anchor so the text reads upright
        var rotate = ViewRot != 0;
        if (rotate) dc.PushTransform(new RotateTransform(-ViewRot, anchor.X, anchor.Y));

        dc.DrawRoundedRectangle(LabelBg, RoomEdgePen, box, 3, 3);
        var y = box.Y + RoomLabelPad;
        foreach (var line in lines)
        {
            dc.DrawText(line, new Point(box.X + box.Width / 2, y));
            y += line.Height + RoomLabelGap;
        }

        if (rotate) dc.Pop();
    }

    /// <summary>
    /// PowerViz: the ship's conduit network. Orphaned (unpowered) runs draw as dim dashed red; live runs draw as a
    /// soft cyan glow under animated flowing dashes; wired devices with no live feed get an amber warning marker on
    /// each unpowered plug. A port of the game's power path draw (linePower / linePowerOff over aPwrTiles).
    /// </summary>
    private void DrawPowerOverlay(DrawingContext dc)
    {
        if (_powerOverlay.IsEmpty) return;
        EnsurePowerGeometry();

        // The segment geometries are baked at pan zero, so draw them under the live-pan transform (one DrawGeometry
        // per layer, not a DrawLine per segment) — the whole overlay is a handful of GPU strokes even on a station.
        dc.PushTransform(new TranslateTransform(_pan.X, _pan.Y));

        if (_powerOffGeo is not null)
            dc.DrawGeometry(null, PowerOffPen, _powerOffGeo);

        if (_powerLitGeo is not null)
        {
            var t = Math.Max(2.5, Zoom / 14.0);
            var litPen = new Pen(new SolidColorBrush(PowerLitColor), t)
            {
                DashStyle = new DashStyle([2, 2], -_powerPhase),
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            dc.DrawGeometry(null, PowerGlowPen, _powerLitGeo);   // static soft glow underlay
            dc.DrawGeometry(null, litPen, _powerLitGeo);         // animated flowing dashes on top
        }

        dc.Pop();

        // The unconnected-plug markers are few and don't animate — draw them directly in screen space.
        var r = Math.Max(3.0, Zoom * 0.16);
        foreach (var plug in _powerOverlay.UnconnectedPlugs)
            dc.DrawEllipse(PowerWarnBrush, PowerWarnPen, TileCenter(plug), r, r);
    }

    /// <summary>Bake the lit/unpowered segment sets into frozen pan-zero geometries at the current zoom, so the
    /// animated overlay is a couple of <see cref="DrawingContext.DrawGeometry"/> calls per frame. Rebuilt only when
    /// the overlay data or the zoom changes (see <see cref="_powerGeoDirty"/>).</summary>
    private void EnsurePowerGeometry()
    {
        if (!_powerGeoDirty) return;
        _powerGeoDirty = false;
        _powerLitGeo = BuildSegmentGeometry(_powerOverlay.Powered);
        _powerOffGeo = BuildSegmentGeometry(_powerOverlay.Unpowered);
    }

    private Geometry? BuildSegmentGeometry(IReadOnlyList<((int X, int Y) A, (int X, int Y) B)> segments)
    {
        if (segments.Count == 0) return null;
        Point Centre((int X, int Y) t) => new((t.X + 0.5) * Zoom, (t.Y + 0.5) * Zoom);   // pan-zero tile centre
        var geo = new StreamGeometry();
        using (var c = geo.Open())
            foreach (var (a, b) in segments)
            {
                c.BeginFigure(Centre(a), false, false);
                c.LineTo(Centre(b), true, false);
            }
        geo.Freeze();
        return geo;
    }

    /// <summary>
    /// Draw a part's power connector nubs at their map points, rotated with the part: input plugs (cyan, where the
    /// device draws from the conduit) and the output feed (green, where a source pushes into it). <paramref
    /// name="gx"/>/<paramref name="gy"/> is the rotated footprint's top-left doc cell. Mirrors the game's build-
    /// cursor connector sprites (CanvasManager's PowerInput/PowerOutput grid sprites) so a part can be oriented to
    /// meet a conduit before it is placed.
    /// </summary>
    private void DrawConnectorNubs(DrawingContext dc, PartDef part, int gx, int gy, int rot)
    {
        if (!part.IsPowered) return;
        Point At((double X, double Y) px)
        {
            var (tx, ty) = GridMath.MapPoint(px, part.Item.Width, part.Item.Height, rot);
            return new Point(_pan.X + (gx + tx) * Zoom, _pan.Y + (gy + ty) * Zoom);
        }
        foreach (var pt in part.PowerInputPoints) DrawConnectorBadge(dc, At(pt), isInput: true);
        if (part.PowerOutputPoint is { } outPt) DrawConnectorBadge(dc, At(outPt), isInput: false);
    }

    /// <summary>
    /// Draw a power-connector badge centred on <paramref name="center"/>: a lightning glyph plus an <b>IN</b>/<b>OUT</b>
    /// label on a dark pill, blue for an input plug, green for an output feed. Stays upright under view rotation and
    /// drops the label (bolt only) when the zoom is too small for legible text.
    /// </summary>
    private void DrawConnectorBadge(DrawingContext dc, Point center, bool isInput)
    {
        var accent = isInput ? ConnInBrush : ConnOutBrush;
        var border = isInput ? ConnInPen : ConnOutPen;
        var label = isInput ? "IN" : "OUT";

        var hgt = Math.Clamp(Zoom * 0.5, 13, 24);
        var pad = hgt * 0.2;
        var bolt = hgt * 0.66;
        var showText = hgt >= 15;

        FormattedText? txt = null;
        if (showText)
            txt = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                ConnTypeface, hgt * 0.6, ConnTextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var gap = showText ? hgt * 0.14 : 0;
        var w = pad + bolt + gap + (txt?.Width ?? 0) + pad;
        var rect = new Rect(center.X - w / 2, center.Y - hgt / 2, w, hgt);

        var rotate = ViewRot != 0;
        if (rotate) dc.PushTransform(new RotateTransform(-ViewRot, center.X, center.Y));   // keep the label upright

        dc.DrawRoundedRectangle(ConnBgBrush, border, rect, hgt * 0.3, hgt * 0.3);

        // the bolt, scaled uniformly from its unit box into a square slot
        var by = center.Y - bolt / 2;
        dc.PushTransform(new TranslateTransform(rect.X + pad, by));
        dc.PushTransform(new ScaleTransform(bolt, bolt));
        dc.DrawGeometry(accent, null, BoltGeometry);
        dc.Pop();
        dc.Pop();

        if (txt is not null)
            dc.DrawText(txt, new Point(rect.X + pad + bolt + gap, center.Y - txt.Height / 2));

        if (rotate) dc.Pop();
    }

    /// <summary>
    /// Draw one armed-part ghost at (<paramref name="gx"/>,<paramref name="gy"/>,<paramref name="rot"/>): the
    /// sub-floor reservation shade (the tanks), the translucent sprite, hazard-tinted failing cells, and a
    /// green/red validity outline hugging the above-floor body. Returns the fit so the caller can surface the
    /// cursor pose's reason. Shared by the plain ghost and every symmetry mirror so a mirror previews identically
    /// to how it will place.
    /// </summary>
    /// <summary>
    /// What a Surfaces stroke would really do to this tile, when that is not a question the placement law can
    /// answer. Re-skinning the part already there is legal by construction (same layer, same footprint), yet
    /// <see cref="CheckFit"/> would refuse it for the tile being occupied by the very part being replaced. The
    /// other two are refusals the law knows nothing about: a tile the current <see cref="PaintMode"/> declines to
    /// touch. Null hands the pose back to the law, which is every non-surface stroke.
    /// </summary>
    private FitResult? SurfaceVerdict(PartDef? surface, PartDef part, int x, int y)
    {
        if (surface is null) return null;
        if (SurfacePaint.SwapTargetAt(Doc!, part, x, y) is not null)
            return PaintMode == SurfacePaintMode.Fill
                ? new FitResult(false, [], "this tile already has one — switch to Replace to change it")
                : FitResult.Legal;
        return PaintMode == SurfacePaintMode.Replace
            ? new FitResult(false, [], "nothing to re-skin on this tile — switch to Fill to lay a new one")
            : null;
    }

    /// <summary><paramref name="verdict"/> overrides the placement law for a pose the law cannot judge — see
    /// <see cref="SurfaceVerdict"/>. Null (the default) asks <see cref="CheckFit"/>, as every other ghost does.</summary>
    private FitResult DrawArmedGhost(DrawingContext dc, PartDef part, int gx, int gy, int rot, FitResult? verdict = null)
    {
        var (w, h) = GridMath.Size(part.Item.Width, part.Item.Height, rot);
        var fit = verdict ?? CheckFit.Check(Doc!, part, gx, gy, rot, includeEnvelope: true);

        // a modded part that fails the core-only law but WILL place via the override draws amber ("flagged, not
        // blocked") rather than red — so the ghost distinguishes "can't" from "against the rules but allowed".
        // A legal-but-advisory pose (a soft req unmet, e.g. an overhead light with no adjacent conduit) draws the
        // same amber: it places, and the amber outline + tinted advisory cell say "noted" without saying "can't".
        // A forced verdict is never an overridable law failure: the stroke will skip this tile whatever the
        // mod-override toggle says, so it must not draw the amber "against the rules, but placing" ghost.
        var overriding = verdict is null && !fit.Ok && AllowModdedOverrides && part.IsModded;
        var advisory = fit.Ok && fit.Advisory is not null;
        var outlinePen = fit.Ok ? (advisory ? GhostOverridePen : GhostOkPen) : overriding ? GhostOverridePen : GhostBadPen;
        var cellFill = overriding ? OverrideFill : HazardFill;

        var under = UnderFloorCells(part, gx, gy, rot).ToList();
        foreach (var (cx, cy) in under)
            dc.DrawRectangle(SubfloorFill, null, CellRect(cx, cy, 1, 1));

        dc.PushOpacity(0.55);
        DrawSprite(dc, part, gx, gy, rot, ghost: true);
        dc.Pop();

        foreach (var (cx, cy) in fit.FailedCells)   // failing cells override the sub-floor shade
            dc.DrawRectangle(cellFill, null, CellRect(cx, cy, 1, 1));

        if (advisory)   // tint the tile the soft req points at (e.g. where a power conduit is wanted)
            foreach (var (cx, cy) in fit.AdvisoryCells ?? [])
                dc.DrawRectangle(OverrideFill, null, CellRect(cx, cy, 1, 1));

        Rect body;
        if (under.Count > 0)
        {
            // dashed outline round the whole reservation, the solid validity outline hugging the body
            dc.DrawRectangle(null, SubfloorPen, CellRect(gx, gy, w, h));
            var (bx, by, bw, bh) = AboveFloorBounds(part, gx, gy, rot);
            body = CellRect(bx, by, bw, bh);
        }
        else body = CellRect(gx, gy, w, h);

        dc.DrawRectangle(null, outlinePen, body);
        DrawFacingNeedle(dc, part, body, rot, outlinePen);
        DrawConnectorNubs(dc, part, gx, gy, rot);   // show where this part plugs into power, to orient it before placing
        return fit;
    }

    /// <summary>
    /// The armed part's rotation, on the ghost, as a compass needle: a stub from the footprint's centre out to its
    /// leading edge with a dot at the pivot, drawn in the outline's own colour so the cue never competes with the
    /// green/amber/red validity language, over a dark halo so it stays readable on top of a busy sprite. Drawn at
    /// every angle including 0°, so the needle reads as "this is which way it faces" rather than as a warning, and
    /// the resting orientation is as visible as a turned one. Walls and floors autotile rather than turn, so they
    /// never get one, which is the same rule <see cref="DrawRot"/> draws the sprite by and keeps the cue honest
    /// about what will be placed. The needle stays inside the footprint so it can never be read as belonging to the
    /// neighbouring tile, is capped near a tile long so a 7×7 tank gets a needle rather than a spear, and is
    /// skipped entirely when the zoom leaves it too short to read.
    /// </summary>
    private void DrawFacingNeedle(DrawingContext dc, PartDef part, Rect body, int rot, Pen pen)
    {
        if (part.Item.HasSpriteSheet) return;

        // 0° points up the screen and the angle runs clockwise, matching the RotateTransform the sprite is drawn under
        var rad = DrawRot(part, rot) * Math.PI / 180;
        var (dx, dy) = (Math.Sin(rad), -Math.Cos(rad));
        var centre = new Point(body.X + body.Width / 2, body.Y + body.Height / 2);

        // distance from the centre to wherever this direction leaves the footprint, then inset off the outline
        var toEdge = Math.Min(
            Math.Abs(dx) < 1e-6 ? double.MaxValue : body.Width / 2 / Math.Abs(dx),
            Math.Abs(dy) < 1e-6 ? double.MaxValue : body.Height / 2 / Math.Abs(dy));
        var len = Math.Min(toEdge - Zoom * 0.14, Zoom * 0.85);
        if (len < 2) return;

        var tip = new Point(centre.X + dx * len, centre.Y + dy * len);
        var dot = Math.Max(1.5, Zoom * 0.07);
        dc.DrawLine(NeedleHaloPen, centre, tip);
        dc.DrawEllipse(NeedleHaloBrush, null, centre, dot + 1.25, dot + 1.25);
        dc.DrawLine(pen, centre, tip);
        dc.DrawEllipse(pen.Brush, null, centre, dot, dot);
    }

    /// <summary>Draw the device signal connections: a violet line from each source device's centre to its target,
    /// a dot at the target end (source → target = signaller → driven). It also rings every connectable device,
    /// brightly rings the armed source, and previews a wire to the device under the cursor.
    /// <para>Wire-mode only, gated by the caller. The committed wires used to draw whatever the mode was, so a
    /// wired-up ship stayed criss-crossed with violet lines over every other view.</para></summary>
    private void DrawDeviceLinks(DrawingContext dc)
    {
        if (Doc is null) return;

        foreach (var (_, source, target) in DeviceLinks.Resolved(Doc))
        {
            var a = DeviceCentre(source);
            var b = DeviceCentre(target);
            dc.DrawLine(WirePen, a, b);
            dc.DrawEllipse(WireDotBrush, null, b, WireDotRadius, WireDotRadius);
        }

        foreach (var p in Doc.Placements)
            if (DeviceLinks.IsConnectable(Doc, p))
            {
                var (bx, by, bw, bh) = Doc.BodyBounds(p);
                dc.DrawRectangle(null, WireNodePen, CellRect(bx, by, bw, bh));
            }

        if (_wireSource is not null && Doc.ById(_wireSource.Id) is not null)
        {
            var (sx, sy, sw, sh) = Doc.BodyBounds(_wireSource);
            dc.DrawRectangle(null, WireSourcePen, CellRect(sx, sy, sw, sh));

            if (_hoverCell is { } hc
                && Doc.HitTestStack(hc.X, hc.Y).FirstOrDefault(p => DeviceLinks.IsConnectable(Doc, p)) is { } hoverTarget
                && !ReferenceEquals(hoverTarget, _wireSource))
                dc.DrawLine(WirePreviewPen, DeviceCentre(_wireSource), DeviceCentre(hoverTarget));
        }
    }

    /// <summary>Screen-space centre of a placement's above-floor body — the anchor a wire connects to.</summary>
    private Point DeviceCentre(Placement p)
    {
        var (bx, by, bw, bh) = Doc!.BodyBounds(p);
        var r = CellRect(bx, by, bw, bh);
        return new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
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

    /// <summary>
    /// The cached vector drawing of every placement at rest, baked at <b>pan zero</b> (so it is
    /// pan-independent — <see cref="OnRender"/> applies the current pan as a <see cref="TranslateTransform"/>) but at
    /// the current <see cref="Zoom"/>. Built on first use and reused until the ship content (<see
    /// cref="OnContentChanged"/>) or the zoom (the <see cref="Zoom"/> setter) changes. Frozen so WPF can render it on
    /// the compositor thread without re-walking the scene each frame. Baking pan in used to rebuild the whole ship
    /// every pan frame — the source of the WASD/drag pan lag on big ships.
    /// </summary>
    private Drawing StaticShip()
    {
        if (_staticShip is not null) return _staticShip;
        var savedPan = _pan;
        _pan = default;   // bake at the origin; the live pan rides on a transform, not the geometry
        try
        {
            var dg = new DrawingGroup();
            using (var ctx = dg.Open())
                DrawItems(ctx, [.. Doc!.RenderOrder()], _ => (0, 0));
            dg.Freeze();
            return _staticShip = dg;
        }
        finally { _pan = savedPan; }
    }

    /// <summary>Draw only the parts in flux during a Move/Paint drag, live, over the retained lit composite: the
    /// moving selection (each at its per-frame offset) for a Move, or the parts placed so far this stroke for a
    /// Paint (which the composite, baked at stroke start, doesn't yet contain). Everything static comes from the
    /// composite, so this keeps the ship lit while an edit is in progress. Autotiling of the moving parts is
    /// computed from the still-unmutated document exactly as the flat drag path did, then translated.</summary>
    private void DrawInFluxParts(DrawingContext dc)
    {
        if (Doc is null) return;
        if (_drag == Drag.Move)
        {
            foreach (var p in Doc.DrawOrder())
                if (SelectedIds.Contains(p.Id) && !Doc.IsLocked(p))
                    DrawPlacement(dc, p, MoveDeltaFor(p));
        }
        else if (_drag == Drag.Paint)
        {
            foreach (var cmd in _stroke)
            {
                if (cmd is PlaceCommand pc) DrawPlacement(dc, pc.Placement, (0, 0));
                else if (cmd is PlaceLooseCommand lc && Doc.Catalog.Lookup(lc.Obj.DefName) is { } part)
                    DrawSprite(dc, part, lc.Obj.X, lc.Obj.Y, lc.Obj.Rot, ghost: false);
                // a SetCargoCommand (loose dropped into a container) has no on-grid sprite — nothing to draw
            }
        }
    }

    /// <summary>Outline the selected loose item. The sprites themselves draw with the rest of the ship (loose
    /// items share the one render order), so only the selection marker is left as an overlay — it must stay on
    /// top, and it must survive the Light Viz path, where the item is baked into the composite.</summary>
    private void DrawLooseSelection(DrawingContext dc)
    {
        if (SelectedLoose is not { } sel || Doc!.Catalog.Lookup(sel.DefName) is not { } part) return;
        var (w, h) = GridMath.Size(part.Item.Width, part.Item.Height, sel.Rot);
        dc.DrawRectangle(null, SelectPen, CellRect(sel.X, sel.Y, w, h));
    }

    /// <summary>Preview the armed loose item at the hover tile: the semi-transparent sprite plus a green/red
    /// outline for whether it may land there (a floor tile or an accepting container). Mirrors
    /// <see cref="TryPlaceLoose"/>'s decision so the click matches the preview.</summary>
    private void DrawLooseGhost(DrawingContext dc, (int X, int Y) cell)
    {
        if (ArmedPart is not { } part || Doc is null) return;
        var (w, h) = GridMath.Size(part.Item.Width, part.Item.Height, ArmedRot);
        var ok = LoosePlacement.AcceptingContainerAt(Doc, Doc.Catalog, cell.X, cell.Y, part) is not null
                 || LoosePlacement.CanRestOnFloor(Doc, cell.X, cell.Y);

        dc.PushOpacity(0.55);
        DrawSprite(dc, part, cell.X, cell.Y, ArmedRot, ghost: true);
        dc.Pop();
        var pen = ok ? GhostOkPen : GhostBadPen;
        var body = CellRect(cell.X, cell.Y, w, h);
        dc.DrawRectangle(null, pen, body);
        DrawFacingNeedle(dc, part, body, ArmedRot, pen);
        RaiseGhostReason(ok ? null : "Drop an item onto a floor tile or an open container");
    }

    /// <summary>
    /// Draw a run of drawables (placements and loose items alike, in one render order), ghosting the non-deck
    /// layers when Surfaces mode is on. Two passes rather than one opacity push per part, so the ghosted layers
    /// share a single transparency group. Draw order is layer-major (floors, then walls, then everything above),
    /// so surfaces are already its prefix and splitting it in two changes nothing but the opacity.
    /// </summary>
    private void DrawItems(DrawingContext dc, IReadOnlyList<RenderItem> ordered, Func<RenderItem, (int X, int Y)> offsetOf)
    {
        if (!SurfaceMode)
        {
            foreach (var i in ordered) DrawItem(dc, i, offsetOf(i));
            return;
        }
        foreach (var i in ordered)
            if (!IsGhosted(i)) DrawItem(dc, i, offsetOf(i));
        dc.PushOpacity(SurfaceGhostOpacity);
        foreach (var i in ordered)
            if (IsGhosted(i)) DrawItem(dc, i, offsetOf(i));
        dc.Pop();
    }

    /// <summary>Draw one drawable — a placement's sprite, or a loose floor item's.</summary>
    private void DrawItem(DrawingContext dc, RenderItem item, (int X, int Y) offset)
    {
        if (item.Placement is { } p) { DrawPlacement(dc, p, offset); return; }
        if (Doc!.Catalog.Lookup(item.DefName) is { } part)
            DrawSprite(dc, part, item.X + offset.X, item.Y + offset.Y, item.Rot, ghost: false);
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

    // While set, DrawSprite draws each part's vector-swizzled NORMAL map instead of its albedo sprite (the Light
    // Viz normal-pass bake). Parts with no normal map draw nothing — alpha 0 reads as a flat surface downstream.
    private bool _normalPass;

    /// <summary>
    /// The rotation a part is actually drawn at. Sheet items (walls, floors) autotile to their neighbours instead
    /// of turning, and every other site agrees on that by keying off <see cref="ItemDef.HasSpriteSheet"/> alone:
    /// <see cref="CheckFit"/> tests the socket ring at 0, <see cref="TryPlacePose"/> stores 0, and
    /// <see cref="ShipDocument"/> derives sub-floor cells from 0. The draw has to use the same rule or it
    /// promises a rotation the part will never place at. It cannot read the autotile branch above as the test,
    /// because that branch also requires a <c>ctSpriteSheet</c>: a def declaring <c>bHasSpriteSheet</c> without
    /// one (no core part does, a mod may) falls past it and would otherwise ghost turned and place straight.
    /// </summary>
    internal static int DrawRot(PartDef part, int rot) =>
        part.Item.HasSpriteSheet ? 0 : GridMath.Norm(rot);

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
                    var cell = _normalPass ? Sprites.NormalSheetCell(part, col, row) : Sprites.SheetCell(part, col, row);
                    if (cell is not null) dc.DrawImage(cell, CellRect(gx + c, gy + r, 1, 1));
                }
            return;
        }

        // Draw the sprite at its OWN texture size (round(px/16) tiles — Item.SetData's
        // vScale), centered on the socket footprint. For most parts the sprite fills the
        // footprint; for the large tanks a 3x3 canister sprite sits centered in a 7x7
        // footprint whose outer ring is abstracted sub-floor storage, not the tank body.
        var norm = DrawRot(part, rot);
        var (effW, effH) = GridMath.Size(part.Item.Width, part.Item.Height, norm);
        var (visW, visH) = Sprites!.SpriteTiles(part);
        var bmp = _normalPass ? Sprites.NormalSprite(part, norm) : Sprites.Sprite(part);
        if (bmp is null) return;
        var center = new Point(_pan.X + (gx + effW / 2.0) * Zoom, _pan.Y + (gy + effH / 2.0) * Zoom);
        var sprite = new Rect(center.X - visW * Zoom / 2, center.Y - visH * Zoom / 2, visW * Zoom, visH * Zoom);

        if (norm != 0) dc.PushTransform(new RotateTransform(norm, center.X, center.Y));
        dc.DrawImage(bmp, sprite);
        if (norm != 0) dc.Pop();
    }
}
