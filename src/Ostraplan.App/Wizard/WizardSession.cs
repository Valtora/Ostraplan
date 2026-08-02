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

    /// <summary>The save this design was imported from, or null. Its absence is what disables the update
    /// destination, and its presence is what makes a save picker unnecessary there.</summary>
    public SaveSourceRef? SourceSave { get; init; }

    /// <summary>The already-located context for the source save, when the design was imported this session. Null
    /// for a reopened <c>.oplan</c>, which has to relocate it.</summary>
    public SaveShipContext? SaveContext { get; set; }

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
}
