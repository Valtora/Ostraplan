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
/// placement law but a modded-override lets it place anyway (amber ghost) — false when it is hard-blocked (red).</summary>
public readonly record struct GhostStatus(string Reason, bool WillPlace);

/// <summary>
/// The tile grid: renders the document's sprites (16 px art scaled with
/// nearest-neighbor), and turns mouse input into place/select/move/pan/zoom.
/// Mutations are NOT applied here - they are raised as events and the window
/// pushes them through the command stack.
/// </summary>
public sealed class ShipCanvas : FrameworkElement
{
    // screen px per tile. 16 == 1x (the 16 px sprite drawn at native size); the sub-16 steps (0.5x/0.25x/0.125x)
    // zoom out far enough to frame a whole station, and stay clean nearest-neighbor downscales.
    private static readonly double[] ZoomSteps = [2, 4, 8, 16, 24, 32, 48, 64, 96, 128];

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
    private static readonly Brush WireDotBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC0, 0x8C, 0xF0)));
    private static readonly Pen WirePen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 0xC0, 0x8C, 0xF0)), 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Pen WirePreviewPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0xD8, 0xB0, 0xFF)), 1.5) { DashStyle = new DashStyle([3, 3], 0), StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
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
        }
    }
    private double _zoom = 48;
    private Vector _pan;                             // screen position of world origin
    private bool _panInitialized;

    public SymmetryMode SymMode { get; private set; }
    public (int X, int Y) SymCenter { get; private set; }
    public int ViewRot { get; private set; }   // plan-view rotation, 90-degree steps (Q/E)

    public bool ShowZones { get; private set; }        // zone overlay visibility (toolbar/key toggle)
    public Guid? ActiveZoneId { get; private set; }    // the zone being painted, or null when not in zone-paint mode
    public bool ShowPower { get; private set; }        // PowerViz conduit overlay visibility (toolbar/P toggle)
    public bool ShowRooms { get; private set; }        // RoomViz compartment overlay visibility (toolbar/C toggle)

    /// <summary>When true the canvas is in <b>wire mode</b>: click a signalable device to arm it as the signal
    /// source, then click another to connect them (click a connected one again to disconnect). See
    /// <see cref="DeviceLink"/> / <see cref="LinkToggleRequested"/>. Wires always render; this mode drives editing.</summary>
    public bool WireMode { get; private set; }
    private Placement? _wireSource;   // the armed signal source awaiting a target (wire mode)

    /// <summary>When true, a MODDED part may be placed where the (core-only) placement law says it doesn't fit — it's
    /// placed and flagged as a warning rather than hard-blocked. Core parts are always enforced. Set from
    /// <see cref="AppSettings.AllowModdedOverrides"/>.</summary>
    public bool AllowModdedOverrides { get; set; }

    private enum Drag { None, Pan, Move, Band, Paint, BoxFill, ZonePaint, ZoneBox }
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
    public event Action<string>? BrushPicked;   // Alt+LMB eyedropper: arm the def of the part under the cursor
    public event Action<int>? AirSelectionChanged;   // the highlighted air region's tile count changed (0 = cleared)
    public event Action? BandFilterRequested;   // a Shift+drag band select finished; window offers the layer filter chips
    public event Action<IReadOnlyList<(Placement P, int X, int Y, int Rot)>>? PosesRequested;   // a symmetric move: per-part target poses, committed as one SetPosesCommand
    public event Action<(int X, int Y)>? LooseContextMenuRequested;   // right-clicked a loose floor item; window builds its menu
    public event Action<GhostStatus?>? GhostReasonChanged;   // the armed ghost's illegality status, null when legal/disarmed
    public event Action? ShowZonesChanged;              // the zone overlay was toggled (update the toolbar caption)
    public event Action? ShowPowerChanged;              // the PowerViz overlay was toggled (update the toolbar caption + trigger a scan)
    public event Action? ShowRoomsChanged;              // the RoomViz overlay was toggled (update the toolbar caption + trigger a scan)
    public event Action? WireModeChanged;               // wire mode was toggled (update the hint / menu check)
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

    private void RaiseGhostReason(string? reason, bool willPlace = false)
    {
        GhostStatus? status = reason is null ? null : new GhostStatus(reason, willPlace);
        if (status.Equals(_lastGhostReason)) return;
        _lastGhostReason = status;
        GhostReasonChanged?.Invoke(status);
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
        Zoom = ZoomSteps.LastOrDefault(z => z <= Math.Min(fit, 64.0), ZoomSteps[0]);
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
        if (!_panInitialized && sizeInfo.NewSize.Width > 0)
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
        RaiseViewChanged();
        InvalidateVisual();
    }

    /// <summary>Step the zoom one level in (<paramref name="dir"/> &gt; 0) or out, anchored at the viewport centre —
    /// the keyboard zoom (+/-), mirroring the wheel step but around the middle of the view.</summary>
    public void ZoomStep(int dir)
    {
        var idx = Array.IndexOf(ZoomSteps, Zoom);
        if (idx < 0) idx = 3;
        var next = Math.Clamp(idx + Math.Sign(dir), 0, ZoomSteps.Length - 1);
        if (ZoomSteps[next] == Zoom) return;

        var centre = ScreenToPanSpace(new Point(RenderSize.Width / 2, RenderSize.Height / 2));
        var world = new Point((centre.X - _pan.X) / Zoom, (centre.Y - _pan.Y) / Zoom);
        Zoom = ZoomSteps[next];
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

        // Wire mode: left-click a signalable device to arm it as the signal source, then click another to
        // connect (or click a connected one to disconnect); the source stays armed so you can wire it to several
        // targets. Right-click (or a click on empty/non-device) clears the armed source. Intercepts before the
        // normal select/place/zone logic.
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
                _wireSource = null;
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }

        if (e.ChangedButton == MouseButton.Right)
        {
            // A loose floor item under the cursor wins the right-click, even while a brush is armed: disarm, select
            // it, and open its menu (Change Quantity / Delete) — otherwise a placed item is unreachable because the
            // brush stays armed after dropping.
            if (Doc is not null && Doc.LooseAt(CellAt(screen).X, CellAt(screen).Y) is { } looseRmb)
            {
                if (ArmedPart is not null) { SetArmed(null); Disarmed?.Invoke(); }
                SelectedIds.Clear();
                SelectionChanged?.Invoke();
                SelectedLoose = looseRmb;
                LooseSelectionChanged?.Invoke();
                InvalidateVisual();
                LooseContextMenuRequested?.Invoke(CellAt(screen));
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

        // Alt+LMB is the eyedropper: pick the (topmost) part under the cursor as the brush. Works whether or
        // not something is already armed, and takes priority over placing/selecting so an Alt-click never edits.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && Doc.HitTest(cell.X, cell.Y) is { } pick)
        {
            BrushPicked?.Invoke(pick.DefName);
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
            seed ??= Doc.HitTest(cell.X, cell.Y);
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

        // Unarmed left-click on a tile that holds a loose item selects it (it sits on top of the deck), so it can
        // be inspected and deleted. Ctrl-click falls through to the placement logic (reach the structure beneath).
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && Doc.LooseAt(cell.X, cell.Y) is { } looseHit)
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

        var hit = Doc.HitTest(cell.X, cell.Y);
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

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var screen = e.GetPosition(this);
        var cell = CellAt(screen);
        if (_hoverCell is null || _hoverCell.Value != cell)
        {
            _hoverCell = cell;
            HoverChanged?.Invoke(cell);
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
            var after = CargoEdit.Add(container.Cargo, null, grid, item, 1);
            if (after is null) { RaiseGhostReason("Container is full"); return; }
            var cmd = new SetCargoCommand(container, container.Cargo, after);
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
        var seen = new HashSet<(int, int, int)>();
        foreach (var pose in WithSymmetry(x, y, rot, w, h))
        {
            if (!seen.Add(pose)) continue;
            if (SameDefCovers(pose.X, pose.Y, w, h, ArmedPart.DefName)) continue;   // no stacking while painting
            // the placement law: skip any pose the game's Item.CheckFit would refuse (each symmetry mirror judged
            // independently — legal ones land, illegal ones don't). EXCEPTION: a MODDED part may be placed against
            // the core-only law when the override toggle is on — it lands and is flagged as a warning (ProblemScan).
            if (!CheckFit.Check(Doc, ArmedPart, pose.X, pose.Y, pose.Rot, includeEnvelope: true).Ok
                && !(AllowModdedOverrides && ArmedPart.IsModded)) continue;
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

    /// <summary>The pose plus its mirror copies for the current mode/axis — the pure <see cref="Symmetry.Poses"/>
    /// (unit-tested in Core) with this canvas's axis centre and active axes. Cursor pose first; coincident copies
    /// are the caller's to dedup.</summary>
    private IEnumerable<(int X, int Y, int Rot)> WithSymmetry(int x, int y, int rot, int w, int h) =>
        Symmetry.Poses(x, y, rot, w, h, SymCenter.X, SymCenter.Y,
            vertical: SymMode is SymmetryMode.Vertical or SymmetryMode.Both,
            horizontal: SymMode is SymmetryMode.Horizontal or SymmetryMode.Both);

    private bool SameDefCovers(int x, int y, int w, int h, string defName)
    {
        // any same-def part overlapping the w×h rect must cover one of its tiles — an index lookup
        // per tile, not an O(parts) scan (this runs per pose across a whole box-fill)
        for (var r = 0; r < h; r++)
            for (var c = 0; c < w; c++)
                foreach (var p in Doc!.PlacementsAt(x + c, y + r))
                    if (p.DefName == defName) return true;
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
                foreach (var p in Doc.DrawOrder())
                    DrawPlacement(ctx, p, (0, 0));
                foreach (var lo in Doc.LooseObjects)
                    if (Doc.Catalog.Lookup(lo.DefName) is { } part) DrawSprite(ctx, part, lo.X, lo.Y, lo.Rot, ghost: false);
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

        // The placement sprites: a Move drags selected parts (offset per frame) and a Paint adds
        // parts live, so both draw straight through; every other state (idle, band-select, box-fill
        // preview) leaves the ship untouched, so reuse the cached drawing and skip the whole
        // DrawOrder + autotile pass each frame — the win on a big ship during a band/fill drag.
        if (_drag is Drag.Move or Drag.Paint)
        {
            foreach (var p in Doc.DrawOrder())
            {
                var offset = _drag == Drag.Move && SelectedIds.Contains(p.Id) && !Doc.IsLocked(p) ? MoveDeltaFor(p) : (0, 0);
                DrawPlacement(dc, p, offset);
            }
        }
        else
        {
            // The cache is baked at pan zero, so shift it to the live pan with a transform — panning stays a
            // transform + one blit instead of rebuilding the whole ship each frame.
            dc.PushTransform(new TranslateTransform(_pan.X, _pan.Y));
            dc.DrawDrawing(StaticShip());
            dc.Pop();
        }

        DrawLooseObjects(dc);   // loose floor items sit on top of the deck/fixtures they rest on

        DrawIllegalCells(dc);
        DrawLeakCells(dc);
        DrawAirSelection(dc);
        if (ShowRooms) DrawRoomOverlay(dc);   // under the zones: rooms are the ground truth a zone is drawn onto
        if (ShowZones || ActiveZoneId is not null) DrawZones(dc);
        DrawOutOfBounds(dc, view);
        DrawOriginMarker(dc);
        if (ShowPower) DrawPowerOverlay(dc);
        DrawDeviceLinks(dc);
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
            var seen = new HashSet<(int, int, int)>();
            FitResult? cursor = null;
            foreach (var pose in WithSymmetry(gx, gy, ArmedRot, w, h))
            {
                if (!seen.Add(pose)) continue;
                var fit = DrawArmedGhost(dc, ArmedPart, pose.X, pose.Y, pose.Rot);
                cursor ??= fit;   // WithSymmetry yields the cursor pose first
            }
            if (cursor is { Ok: false } bad)
            {
                var why = bad.Reason ?? "doesn't fit here";
                var modded = ArmedPart.IsModded;
                if (modded && AllowModdedOverrides) RaiseGhostReason(why, willPlace: true);
                else if (modded) RaiseGhostReason(why + " — modded; turn on \"Mod overrides\" to place it anyway");
                else RaiseGhostReason(why);
            }
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
    private FitResult DrawArmedGhost(DrawingContext dc, PartDef part, int gx, int gy, int rot)
    {
        var (w, h) = GridMath.Size(part.Item.Width, part.Item.Height, rot);
        var fit = CheckFit.Check(Doc!, part, gx, gy, rot, includeEnvelope: true);

        // a modded part that fails the core-only law but WILL place via the override draws amber ("flagged, not
        // blocked") rather than red — so the ghost distinguishes "can't" from "against the rules but allowed".
        var overriding = !fit.Ok && AllowModdedOverrides && part.IsModded;
        var outlinePen = fit.Ok ? GhostOkPen : overriding ? GhostOverridePen : GhostBadPen;
        var cellFill = overriding ? OverrideFill : HazardFill;

        var under = UnderFloorCells(part, gx, gy, rot).ToList();
        foreach (var (cx, cy) in under)
            dc.DrawRectangle(SubfloorFill, null, CellRect(cx, cy, 1, 1));

        dc.PushOpacity(0.55);
        DrawSprite(dc, part, gx, gy, rot, ghost: true);
        dc.Pop();

        foreach (var (cx, cy) in fit.FailedCells)   // failing cells override the sub-floor shade
            dc.DrawRectangle(cellFill, null, CellRect(cx, cy, 1, 1));

        if (under.Count > 0)
        {
            // dashed outline round the whole reservation, the solid validity outline hugging the body
            dc.DrawRectangle(null, SubfloorPen, CellRect(gx, gy, w, h));
            var (bx, by, bw, bh) = AboveFloorBounds(part, gx, gy, rot);
            dc.DrawRectangle(null, outlinePen, CellRect(bx, by, bw, bh));
        }
        else
        {
            dc.DrawRectangle(null, outlinePen, CellRect(gx, gy, w, h));
        }
        DrawConnectorNubs(dc, part, gx, gy, rot);   // show where this part plugs into power, to orient it before placing
        return fit;
    }

    /// <summary>Draw the device signal connections: a violet line from each source device's centre to its target,
    /// a dot at the target end (source → target = signaller → driven). In wire mode it additionally rings every
    /// connectable device, brightly rings the armed source, and previews a wire to the device under the cursor.</summary>
    private void DrawDeviceLinks(DrawingContext dc)
    {
        if (Doc is null) return;

        foreach (var (_, source, target) in DeviceLinks.Resolved(Doc))
        {
            var a = DeviceCentre(source);
            var b = DeviceCentre(target);
            dc.DrawLine(WirePen, a, b);
            dc.DrawEllipse(WireDotBrush, null, b, 2.5, 2.5);
        }

        if (!WireMode) return;

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
                foreach (var p in Doc!.DrawOrder())
                    DrawPlacement(ctx, p, (0, 0));
            dg.Freeze();
            return _staticShip = dg;
        }
        finally { _pan = savedPan; }
    }

    /// <summary>Draw every loose floor item at its tile (resolved to its sprite), plus a select outline on the
    /// chosen one. Drawn each frame over the cached ship, like the zone overlay, since these are few.</summary>
    private void DrawLooseObjects(DrawingContext dc)
    {
        foreach (var lo in Doc!.LooseObjects)
        {
            if (Doc.Catalog.Lookup(lo.DefName) is not { } part) continue;
            DrawSprite(dc, part, lo.X, lo.Y, lo.Rot, ghost: false);
            if (SelectedLoose is { } sel && sel.Id == lo.Id)
            {
                var (w, h) = GridMath.Size(part.Item.Width, part.Item.Height, lo.Rot);
                dc.DrawRectangle(null, SelectPen, CellRect(lo.X, lo.Y, w, h));
            }
        }
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
        dc.DrawRectangle(null, ok ? GhostOkPen : GhostBadPen, CellRect(cell.X, cell.Y, w, h));
        RaiseGhostReason(ok ? null : "Drop an item onto a floor tile or an open container");
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
