using System.Windows;
using System.Windows.Controls;
using Ostraplan.Core;

namespace Ostraplan.App.Wizard;

/// <summary>
/// What the ship is: its name, its in-game identity, and the condition to bake in. All three destinations want
/// exactly these, which is why they share one step rather than each carrying a copy.
///
/// <para>Identity is <b>read-only</b> when updating a ship in a save, because <see cref="SaveEdit"/> preserves the
/// original record's identity verbatim. It is shown greyed with a note rather than hidden, so the step does not
/// change shape as the destination changes and so the reason is visible rather than inferred.</para>
/// </summary>
public sealed class ShipStep : WizardStep
{
    private readonly TextBox _name, _publicName, _make, _model, _year, _designation, _description;
    private readonly TextBlock _nameProblem, _identityNote;
    private readonly Border _wearHost;
    private readonly StackPanel _identity;

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
        _identity = Add(body, new StackPanel());
        _publicName = Field(_identity, "In-game name (optional)", "");
        _make = Field(_identity, "Make", "");
        _model = Field(_identity, "Model", "");
        _year = Field(_identity, "Year", "");
        _designation = Field(_identity, "Designation (class/role, e.g. \"Salvage Tug\")", "");
        _description = Field(_identity, "Description (optional)", "", multiline: true);

        _identityNote = Note(body,
            "Leave the in-game name blank to use the ship name (or, when replacing a ship, the game's usual varied " +
            "names). Type a name to pin it: it shows at the transponder, comms, and broker listings. The rest is " +
            "flavor text. Edit these anytime from \"Ship Info\" — they are saved with the design.");

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

        var readOnly = plan.Destination == ExportDestination.UpdateShipInSave;
        foreach (var box in new[] { _publicName, _make, _model, _year, _designation, _description })
        {
            box.IsReadOnly = readOnly;
            box.IsEnabled = !readOnly;
        }
        _identity.Opacity = readOnly ? 0.5 : 1.0;
        _identityNote.Text = readOnly
            ? "The ship keeps the identity it already has in the save. Ostraplan rewrites its structure, not who it " +
              "is, so these are shown for reference only. Export as a mod to give a design a new identity."
            : "Leave the in-game name blank to use the ship name (or, when replacing a ship, the game's usual varied " +
              "names). Type a name to pin it: it shows at the transponder, comms, and broker listings. The rest is " +
              "flavor text. Edit these anytime from \"Ship Info\" — they are saved with the design.";
    }

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

        // Identity is read-only for the update destination, so writing it back there would be writing back what we
        // just displayed. Harmless, but it would also mark the design dirty for nothing.
        if (plan.Destination == ExportDestination.UpdateShipInSave) return;
        plan.Identity = new ExportMetadata(
            _publicName.Text.Trim(), _make.Text.Trim(), _model.Text.Trim(), _year.Text.Trim(),
            _designation.Text.Trim(), _description.Text.Trim());
    }
}
