using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// One open design and everything that belongs to it alone: the document, its undo stack, its identity, its canvas,
/// and the report windows measuring it. One of these per document tab.
///
/// <para><b>Why it is a bag of mutable properties rather than a model.</b> Every field here was a field on
/// <see cref="MainWindow"/> before tabs, read and written from several hundred places across that file. Lifting them
/// into this type verbatim, and leaving <c>MainWindow</c> with properties that forward to the active session, is
/// what let tabs arrive without rewriting the editor: the call sites did not change, only what they resolve to. A
/// tidier model would have been a bigger and far riskier diff for no behaviour anybody can see.</para>
///
/// <para><b>What is deliberately not here.</b> The catalogue, sprites, game environment and settings are one per
/// app and shared by every tab, so opening a second design costs no second load. The clipboard is shared too, on
/// purpose: copying in one tab and pasting into another is the whole reason for having more than one open.</para>
/// </summary>
/// <summary>
/// Everything the off-thread scan's answer depends on: which design, the state of that design as far as the
/// analysis can see it (<see cref="ShipDocument.AnalysisKey"/>), which overlays are asking for work, and the
/// settings the walk and light passes read. Two scans with equal keys must produce equal results, which is what
/// lets the second one be skipped.
///
/// <para>The document goes in by reference so a tab switch never matches, whatever the two designs contain.</para>
/// </summary>
internal readonly record struct ScanKey(
    ShipDocument? Doc, long Analysis, bool Power, bool Light, bool Walk, bool Rooms,
    WalkOptions WalkOptions, string? SunLocation, double SunAngle);

internal sealed class DocumentSession
{
    /// <summary>This session's canvas. One per session rather than one shared and swapped, so a tab keeps its own
    /// zoom, pan, orientation, selection, overlays and cached ship drawing without any of it having to be captured
    /// and restored on every switch — the class of bug where one ship's overlay turns up on another's.</summary>
    public required ShipCanvas Board { get; init; }

    /// <summary>This session's undo history. Reset rather than replaced when a document is installed, so the
    /// subscriptions taken out when the session was built stay live.</summary>
    public CommandStack Stack { get; } = new();

    public ShipDocument? Doc { get; set; }
    public OplanMeta Meta { get; set; } = new();

    /// <summary>This session's subscription to <see cref="ShipDocument.Changed"/>, kept so it can be taken off the
    /// document it was put on. One per session rather than one method group, because the handler has to say which
    /// session changed: a report window left open on a background tab can still write to that tab's document, and
    /// the chrome it must not refresh is the active tab's.</summary>
    public Action? DocChanged { get; set; }

    /// <summary>Take this session's handler off its document. Called when the document is replaced and when the tab
    /// is closed.</summary>
    public void DetachDoc()
    {
        if (Doc is not null && DocChanged is not null) Doc.Changed -= DocChanged;
    }

    /// <summary>True while this holds a new design nothing has been done to. It is what lets Open and Import take
    /// over the tab the app started on instead of leaving an empty one beside every design.</summary>
    public bool IsBlank { get; set; }

    /// <summary>Set when this design was imported from a save FOR EDITING — enables writing it back.</summary>
    public SaveShipContext? SaveContext { get; set; }

    /// <summary>Non-command persisted edits (ship identity, view orientation) — their unsaved state.</summary>
    public bool StateDirty { get; set; }

    /// <summary>Parts an opened .oplan referenced whose defs aren't in the current game + mods data. While this is
    /// non-empty the design is INCOMPLETE and held read-only. See <c>MainWindow.GuardIncompleteSave</c>.</summary>
    public IReadOnlyList<OplanPart> UnresolvedParts { get; set; } = [];

    /// <summary>The most recent scan result, re-rendered when an alert is dismissed or restored. Per session so
    /// coming back to a tab shows its problems again without waiting on a re-scan.</summary>
    public List<Problem> LastProblems { get; set; } = [];

    /// <summary>What the last scan that actually finished was a function of. A scan whose key matches this one
    /// would recompute the answer already on screen, so it does not run. Default until the first scan lands, which
    /// is never equal to a real key (its Doc is null), so the first one always runs.</summary>
    public ScanKey LastScanKey { get; set; }

    /// <summary>The analysis reports held open beside the editor. One of each at most, and they belong to this
    /// document: they close with its tab rather than being left describing a ship that is no longer on screen.</summary>
    public RatingReportWindow? RatingReport { get; set; }
    public DiagnosticsWindow? DiagnosticsReport { get; set; }
    public FlightWindow? FlightReport { get; set; }

    /// <summary>The item manifest held open beside the editor, on the same terms as the reports above. It is an
    /// edit route as well as a list — its rows rename and delete — so it belongs to one design as firmly as they
    /// do, and closing the tab takes it with them.</summary>
    public ManifestWindow? Manifest { get; set; }

    /// <summary>Which untitled auto-save bucket this session rotates in while it has no file of its own. Assigned
    /// by <c>MainWindow.FreeUntitledSlot</c> and meaningless once the design has been saved somewhere. See
    /// <see cref="AutoSaveStore.KeyFor"/>.</summary>
    public int UntitledSlot { get; init; }

    /// <summary>What this design is called: the file's name once it has one, else the design name. Matches what
    /// the title bar and the status strip show for the active document.</summary>
    public string DisplayName =>
        Doc?.FilePath is { } f ? System.IO.Path.GetFileNameWithoutExtension(f) : Meta.Name;

    /// <summary>
    /// What the tab wears, which is <see cref="DisplayName"/> with an apartment's station prefix dropped. The
    /// game names a bought residence <c>&lt;station&gt; | &lt;designation&gt;</c>
    /// (<see cref="ResidenceGrant"/>, GAME-INTERNALS §19), so one imported from a save and not yet saved to a
    /// file arrives called something like "K-Leg: Port Azikiwe | Asteroid Residence". The station half is the
    /// longer half and it is the same on every apartment at that station, so the tab keeps the designation and
    /// the title bar keeps the whole name.
    ///
    /// <para>Only for a residence, and split on the first pipe the way <see cref="SaveZip.StationOf"/> splits the
    /// RegID: a ship may have a pipe in its name and mean it.</para>
    /// </summary>
    public string TabName
    {
        get
        {
            var name = DisplayName;
            if (Doc?.IsResidence != true) return name;
            var pipe = name.IndexOf('|');
            if (pipe < 0) return name;
            var designation = name[(pipe + 1)..].Trim();
            return designation.Length > 0 ? designation : name;   // "<station> |" with nothing after it is still a name
        }
    }

    /// <summary>True when closing this tab would lose work.</summary>
    public bool Dirty => Doc is not null && (Stack.Dirty || StateDirty);
}
