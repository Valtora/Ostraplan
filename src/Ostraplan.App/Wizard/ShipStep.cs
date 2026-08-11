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
/// differently there is the in-game name, where blank means "keep the name it has" rather than "fall back to the
/// ship name". That is a note change, not a shape change.</para>
/// </summary>
public sealed class ShipStep : WizardStep
{
    private readonly TextBox _name, _publicName, _make, _model, _year, _designation, _description;
    private readonly TextBlock _nameProblem, _identityNote;
    private readonly Border _wearHost;

    private WearControl _wear;
    private ExportDestination _builtFor = ExportDestination.Mod;

    public override string Title => "The ship";

    public ShipStep()
    {
        var body = Body();

        _name = Field(body, "Ship name", "");
        _nameProblem = Problem(body);
        _name.TextChanged += (_, _) => OnChanged();

        Header(body, "SHIP IDENTITY (IN-GAME)");
        var identity = Add(body, new StackPanel());
        _publicName = Field(identity, "In-game name (optional)", "");
        _make = Field(identity, "Make", "");
        _model = Field(identity, "Model", "");
        _year = Field(identity, "Year", "");
        _designation = Field(identity, "Designation (class/role, e.g. \"Salvage Tug\")", "");
        _description = Field(identity, "Description (optional)", "", multiline: true);

        _identityNote = Note(body, NoteFor(ExportDestination.Mod));

        _wearHost = Add(body, new Border { Margin = new Thickness(0, 4, 0, 0) });
        _wear = NewWearControl(ExportDestination.Mod);
        _wearHost.Child = _wear;

        Content = body;
    }

    /// <summary>The wear panel's copy depends on the destination: an update re-rolls the condition of every
    /// installed part on the ship, replacing whatever damage it already had, which the other two cannot do because
    /// they are writing a ship that does not exist yet.</summary>
    private WearControl NewWearControl(ExportDestination destination)
    {
        var control = new WearControl(defaultOn: true,
            overrideNote: destination == ExportDestination.UpdateShipInSave
                ? "When armed, this replaces the current condition of every installed part on the ship, not just " +
                  "the ones you edited. Untick to keep each part's existing wear."
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

        if (_builtFor != plan.Destination)
        {
            var current = _wear.Wear;
            _wear = NewWearControl(plan.Destination);
            _wearHost.Child = _wear;
            _wear.SetWear(current);
            _builtFor = plan.Destination;
        }
        _wear.SetWear(plan.Wear);

        _identityNote.Text = NoteFor(plan.Destination);
    }

    /// <summary>The identity note. An update writes onto a ship that already has an identity, so a blank in-game
    /// name there keeps the one it has rather than falling back to the ship name (the game re-rolls a random name
    /// for a ship whose stored one is blank, so there is nothing else blank could usefully mean).</summary>
    private static string NoteFor(ExportDestination destination) =>
        destination == ExportDestination.UpdateShipInSave
            ? "These are the ship's own in-game details, read out of your save. Change one and the write-back " +
              "changes it on the ship. Leave the in-game name blank to keep the name it already has. The rest is " +
              "flavor text. Edit these anytime from \"Ship Info\" — they are saved with the design."
            : "Leave the in-game name blank to use the ship name (or, when replacing a ship, the game's usual varied " +
              "names). Type a name to pin it: it shows at the transponder, comms, and broker listings. The rest is " +
              "flavor text. Edit these anytime from \"Ship Info\" — they are saved with the design.";

    public override string? Validate() =>
        _name.Text.Trim().Length == 0
            ? ShowProblem(_nameProblem, "Give the ship a name.")
            : ShowProblem(_nameProblem, null);

    public override void Leave(WizardSession session)
    {
        var plan = session.Plan;
        plan.ShipName = _name.Text.Trim();
        plan.Wear = _wear.Wear;
        if (_wearTouched) plan.WearChosen = true;

        plan.Identity = new ExportMetadata(
            _publicName.Text.Trim(), _make.Text.Trim(), _model.Text.Trim(), _year.Text.Trim(),
            _designation.Text.Trim(), _description.Text.Trim());
    }
}
