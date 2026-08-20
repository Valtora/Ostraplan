using System.Windows;
using System.Windows.Controls;
using Ostraplan.Core;

namespace Ostraplan.App.Wizard;

/// <summary>
/// What the ship is: its name, its in-game identity, and the condition to bake in. All three destinations want
/// exactly these, which is why they share one step rather than each carrying a copy.
///
/// <para>Identity is editable on all three. Updating a ship in a save seeds it from the ship's current record on
/// import, so the boxes open on what the ship really is and an edit replaces it; the only field that reads
/// differently per destination is the in-game name, whose blank each of them answers differently (see
/// <see cref="NoteFor"/>). That is a note change, not a shape change.</para>
/// </summary>
public sealed class ShipStep : WizardStep
{
    private readonly TextBox _name, _publicName, _make, _model, _year, _designation, _description;
    private readonly TextBlock _nameProblem, _identityNote;
    private readonly Border _wearHost;

    private WearControl _wear;
    private ExportDestination _builtFor = ExportDestination.Mod;
    private bool _builtWithSourceCondition;
    private bool _builtForResidence;

    /// <summary>Kind-free on purpose. The rail is built before the session is attached, and "The design" is
    /// correct for a ship and a residence alike, so the step needs no per-kind title to stay accurate.</summary>
    public override string Title => "The design";

    public ShipStep()
    {
        var body = Body();

        _name = Field(body, "Design name", "");
        _nameProblem = Problem(body);
        _name.TextChanged += (_, _) => OnChanged();

        _identityHeader = Header(body, "IN-GAME IDENTITY");
        var identity = Add(body, new StackPanel());
        _publicName = Field(identity, "In-game name (optional)", "");
        _make = Field(identity, "Make", "");
        _model = Field(identity, "Model", "");
        _year = Field(identity, "Year", "");
        _designation = Field(identity, "Designation (class/role, e.g. \"Salvage Tug\")", "");
        _description = Field(identity, "Description (optional)", "", multiline: true);

        _identityNote = Note(body, NoteFor(ExportDestination.Mod, "ship", isResidence: false));   // retitled per kind on Enter

        _wearHost = Add(body, new Border { Margin = new Thickness(0, 4, 0, 0) });
        _wear = NewWearControl(ExportDestination.Mod, offerSourceCondition: false);
        // rebuilt per destination (and per kind) on Enter
        _wearHost.Child = _wear;

        Content = body;
    }

    /// <summary>The condition panel's copy depends on the destination, because only an update is writing onto a ship
    /// that already has a condition. There, keeping it and repairing it are both real answers, and both act on every
    /// installed part rather than only the ones that were edited. The other two destinations are minting a ship, so
    /// full condition is simply a pristine build and there is nothing to keep.
    ///
    /// <para><paramref name="offerSourceCondition"/> turns the keep option into the carry-the-real-condition choice.
    /// Only the grant destination offers that, and only for a design that came from a save: a mod export has no save
    /// to read a condition out of.</para></summary>
    private WearControl NewWearControl(ExportDestination destination, bool offerSourceCondition, string noun = "ship")
    {
        var update = destination == ExportDestination.UpdateShipInSave;
        var control = new WearControl(
            keepLabel: offerSourceCondition ? "Keep each part's condition from the source save"
                : update ? "Keep each part's existing condition"
                : null,
            keepNote: offerSourceCondition
                ? $"The {noun} arrives in the state it is really in, part by part, rather than at a fresh average. " +
                  $"This is what you want when you are moving a {noun} between saves. Parts you added since importing " +
                  "it were never on the original, so they arrive undamaged."
                : update
                    ? $"The {noun} keeps the wear it has now. Parts you added arrive undamaged, as newly built parts do."
                    : null,
            keepIsSourceCondition: offerSourceCondition,
            fullLabel: update
                ? "Repair everything — every installed part back to 100% condition"
                : "Pristine — every installed part at 100% condition",
            fullNote: update
                ? $"Clears the damage every installed part on the {noun} has accumulated, not just the parts you " +
                  "edited. Parts that are broken as a part in their own right (a damaged wall, a wrecked alarm) are " +
                  "repaired in the editor instead, with Design ▸ Repair All."
                : null);
        control.Changed += () =>
        {
            // SetWear raises this too, so the populating guard is what makes it mean "the user moved it"
            if (!IsPopulating) _wearTouched = true;
            OnChanged();
        };
        return control;
    }

    private bool _wearTouched;

    private readonly TextBlock _identityHeader;

    public override void Enter(WizardSession session)
    {
        var plan = session.Plan;
        _name.Text = plan.ShipName;
        _publicName.Text = plan.Identity.PublicName;
        _make.Text = plan.Identity.Make;
        _model.Text = plan.Identity.Model;
        _year.Text = plan.Identity.Year;
        _designation.Text = plan.Identity.Designation;
        _description.Text = plan.Identity.Description;

        _identityHeader.Text = session.ByKind("SHIP IDENTITY (IN-GAME)", "RESIDENCE IDENTITY (IN-GAME)");

        var offerSource = OffersSourceCondition(session);
        if (_builtFor != plan.Destination || _builtWithSourceCondition != offerSource
            || _builtForResidence != session.IsResidence)
        {
            var current = _wear.Wear;
            _wear = NewWearControl(plan.Destination, offerSource, session.Noun);
            _wearHost.Child = _wear;
            _wear.SetWear(current);
            (_builtFor, _builtWithSourceCondition, _builtForResidence) =
                (plan.Destination, offerSource, session.IsResidence);
        }
        _wear.SetWear(plan.Wear);
        _wear.SetKeepSourceCondition(offerSource && plan.NewShip.KeepSourceCondition);

        _identityNote.Text = NoteFor(plan.Destination, session.Noun, session.IsResidence);
    }

    /// <summary>
    /// Whether this run can carry each part's real condition across: a grant, of a design whose parts still name
    /// the save items they came from, with that save located. All three are required — the condition is read off
    /// the source ship's own condition owners, matched by <see cref="Placement.OriginStrID"/>.
    /// </summary>
    private static bool OffersSourceCondition(WizardSession session) =>
        session.Plan.Destination == ExportDestination.NewShipInSave
        && session.SaveContext is not null
        && session.Doc.Placements.Any(p => p.OriginStrID is not null);

    /// <summary>The identity note. Blank always means "I am not naming this one", but what the game then calls it
    /// depends on where the design is going, so each destination says which it is: an update keeps the name the ship
    /// already has, a mod export takes the game's own varied names (as every core template does), a granted ship
    /// takes the design name so you can find it in your save, and a granted apartment is named after its station
    /// the way the broker names one. The one thing blank never means is a nameless ship: the game rolls a fresh
    /// random name for any ship whose stored name is blank.</summary>
    private static string NoteFor(ExportDestination destination, string noun, bool isResidence)
    {
        const string tail = "Type a name to pin it: it shows at the transponder, comms, and broker listings. The " +
            "rest is flavor text. Edit these anytime from \"Ship Info\" — they are saved with the design.";
        return destination switch
        {
            ExportDestination.UpdateShipInSave =>
                $"These are the {noun}'s own in-game details, read out of your save. Change one and the write-back " +
                $"changes it on the {noun}. Leave the in-game name blank to keep the name it already has. " + tail,
            ExportDestination.Mod =>
                "Leave the in-game name blank and the game names the ship, a different name for each copy it " +
                "spawns, exactly as it names the ships it ships with. " + tail,
            _ when isResidence =>
                "Leave the in-game name blank and the apartment is named after its station, the way the real " +
                "estate broker names one. " + tail,
            _ => "Leave the in-game name blank to use the design name, so the ship is easy to pick out of your " +
                 "save. " + tail,
        };
    }

    public override string? Validate() =>
        _name.Text.Trim().Length == 0
            ? ShowProblem(_nameProblem, "Give the design a name.")
            : ShowProblem(_nameProblem, null);

    public override void Leave(WizardSession session)
    {
        var plan = session.Plan;
        plan.ShipName = _name.Text.Trim();
        plan.Wear = _wear.Wear;
        // Only written when this run could actually offer it, so switching destination mid-run doesn't clear an
        // answer the user gave on the grant path and would come back to.
        if (_builtWithSourceCondition) plan.NewShip.KeepSourceCondition = _wear.KeepSourceCondition;
        if (_wearTouched) plan.WearChosen = true;

        plan.Identity = new ExportMetadata(
            _publicName.Text.Trim(), _make.Text.Trim(), _model.Text.Trim(), _year.Text.Trim(),
            _designation.Text.Trim(), _description.Text.Trim());
    }
}
