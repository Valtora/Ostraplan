using System.Windows;
using Ostraplan.Core;

namespace Ostraplan.App.Wizard;

/// <summary>
/// Everything a step or a driver needs, in one parameter: the plan being filled in, the design being exported, and
/// the read-only world around it.
///
/// <para><see cref="Owner"/> is a live window, so this object <b>is</b> UI-owned. That is deliberate and safe:
/// every background call in the wizard goes through a static helper whose parameters are all plain data, so the
/// session never enters an <see cref="Ui.OffThread"/> closure, and the capture guard catches it immediately if one
/// ever does.</para>
/// </summary>
public sealed class WizardSession
{
    public required ExportPlan Plan { get; init; }
    public required ShipDocument Doc { get; init; }
    public required Catalog Catalog { get; init; }
    public required IReadOnlyList<RoomSpecDef> Specs { get; init; }
    public required DataIndex Index { get; init; }
    public required GameEnv Env { get; init; }
    public required AppSettings Settings { get; init; }

    /// <summary>The design's saved identity. Edits made in the wizard flow back onto it, so the Ship Info dialog
    /// and the export never drift apart.</summary>
    public required OplanMeta Meta { get; init; }

    /// <summary>Every save game found, for the new-ship destination's picker. Empty disables that destination.</summary>
    public required IReadOnlyList<SaveEntry> Saves { get; init; }

    /// <summary>The save this design was imported from, or null. Its presence is what makes a save picker
    /// unnecessary on the update destination.</summary>
    public SaveSourceRef? SourceSave { get; init; }

    /// <summary>
    /// A ship the caller already chose for the update destination, for a design that carries no
    /// <see cref="SourceSave"/> of its own.
    ///
    /// <para>The Update Ship in Save menu action asks <b>before</b> building the wizard, so that cancelling the
    /// picker abandons the whole thing. Without this the wizard would already exist by the time the question was
    /// asked, and a cancel would leave it open on a step whose only content is the reason it cannot continue.</para>
    /// </summary>
    public SaveSourceRef? UpdateTarget { get; init; }

    /// <summary>The already-located context for the source save, when the design was imported this session. Null
    /// for a reopened <c>.oplan</c>, which has to relocate it.</summary>
    public SaveShipContext? SaveContext { get; set; }

    /// <summary>The part palette, for the missing-parts step's stand-in picker. The main window's list, not
    /// anything the wizard builds.</summary>
    public IReadOnlyList<PartVM> Palette { get; init; } = [];

    /// <summary>A rough purchase value for the design, used to pre-fill the starting-ship mortgage.</summary>
    public double BuyEstimate { get; init; }

    /// <summary>Whether Ostrasort is already known to this install, which is what makes "register after exporting"
    /// default to ticked.</summary>
    public bool OstrasortKnown { get; init; }

    /// <summary>The wizard window, for nested dialogs (the folder picker, the in-place confirmation). Set by the
    /// wizard on construction, because the session is built before the window it belongs to. Never touched off the
    /// UI thread.</summary>
    public Window Owner { get; set; } = null!;

    /// <summary>The driver for the currently selected destination. Swapped when the destination changes.</summary>
    public ExportDriver Driver { get; set; } = null!;

    /// <summary>
    /// Renders the mod's preview art (see <see cref="ShipPreview"/>). Supplied by the main window because drawing
    /// needs the live canvas and its sprite atlas, which the wizard has no handle on.
    ///
    /// <para><b>Call it on the UI thread only.</b> It returns plain PNG bytes precisely so the result, and not the
    /// delegate, is what crosses into <see cref="Ui.OffThread"/>. Null when the host supplied no renderer, which is
    /// every test that builds a session by hand.</para>
    /// </summary>
    public Func<ShipPreview?>? RenderPreview { get; init; }

    // ---- what to call the thing being exported ----
    //
    // The wizard is one flow, not two. A residence goes through the same steps as a ship because the mechanics
    // are the same: build the record, write it into a copy of a save. Only the words differ, and only in the
    // places where "ship" would be actively wrong (a residence is not parked, does not take the ferry, and is
    // registered at a station). Deciding the noun once, here, is what keeps a second modal from appearing.

    /// <summary>True when the design being exported is a station residence rather than a vessel.</summary>
    public bool IsResidence => Doc.IsResidence;

    /// <summary>"apartment" or "ship", for mid-sentence use.</summary>
    public string Noun => IsResidence ? "apartment" : "ship";

    /// <summary>"Apartment" or "Ship", for the start of a sentence or a Review label.</summary>
    public string NounCap => IsResidence ? "Apartment" : "Ship";

    /// <summary>Pick between two phrasings by kind. Reads better at a call site than a ternary on
    /// <see cref="IsResidence"/> repeated a dozen times, and makes the residence variant impossible to forget.</summary>
    public string ByKind(string ship, string residence) => IsResidence ? residence : ship;
}
