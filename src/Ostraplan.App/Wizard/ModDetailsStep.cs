using System.Windows;
using System.Windows.Controls;
using Ostraplan.Core;

namespace Ostraplan.App.Wizard;

/// <summary>
/// What the mod is, as distinct from what the ship is: its name, author, version and notes, plus the option to
/// override an existing ship rather than add a new one.
///
/// <para>The mod name follows the ship name until the user edits it, then stops. That is why it is tracked rather
/// than simply defaulted: a design renamed after the mod name was customised must not quietly rename the mod.</para>
/// </summary>
public sealed class ModDetailsStep : WizardStep
{
    private readonly TextBox _modName, _author, _version, _notes;
    private readonly CheckBox _replace;
    private readonly ComboBox _picker;
    private readonly TextBlock _problem;

    private string _autoModName = "";
    private string _shipName = "";
    private bool _loaded;

    public override string Title => "Mod details";

    public ModDetailsStep()
    {
        var body = Body();

        _modName = Field(body, "Mod name", "");
        _modName.TextChanged += (_, _) => OnChanged();
        _author = Field(body, "Author", "");
        _version = Field(body, "Mod version", "1.0.0");
        _notes = Field(body, "Notes (optional)", "", multiline: true);

        Header(body, "REPLACE AN EXISTING SHIP");
        _replace = Add(body, new CheckBox
        {
            Content = "Replace an existing ship instead of adding a new one",
            Foreground = Ink, Margin = new Thickness(0, 2, 0, 4),
        });
        _picker = Add(body, new ComboBox
        {
            Margin = new Thickness(20, 0, 0, 2), IsEnabled = false,
            DisplayMemberPath = nameof(ShipFileEntry.Name), MaxDropDownHeight = 260,
        });
        _problem = Problem(body, indent: 20);

        _replace.Checked += (_, _) => { _picker.IsEnabled = true; SyncModName(); OnChanged(); };
        _replace.Unchecked += (_, _) => { _picker.IsEnabled = false; SyncModName(); OnChanged(); };
        _picker.SelectionChanged += (_, _) => { SyncModName(); OnChanged(); };

        Note(body,
            "Your design takes over the chosen ship's identity, so the game spawns yours in its place everywhere " +
            "(brokers, derelicts, missions). Structure only: the original's cargo and crew loadout are not carried " +
            "over. It only affects new spawns, not ships already in a save.", indent: 20);

        Content = body;
    }

    public override void Enter(WizardSession session)
    {
        var mod = session.Plan.Mod;
        _shipName = session.Plan.ShipName;

        if (!_loaded)
        {
            var ships = TemplateImport.ListShipFiles(session.Index);
            _picker.ItemsSource = ships;
            // the import-a-vanilla-ship, retrofit, replace-it flow: pre-select the ship this design is named after
            _picker.SelectedItem = mod.ReplaceShip
                ?? ships.FirstOrDefault(s => string.Equals(s.Name, session.Plan.ShipName, StringComparison.OrdinalIgnoreCase));
            _loaded = true;
        }

        _author.Text = mod.Author.Length > 0 ? mod.Author : session.Settings.ExportAuthor ?? session.Meta.Author;
        _version.Text = mod.Version;
        _notes.Text = mod.Notes;
        _replace.IsChecked = mod.ReplaceShip is not null;
        if (mod.ReplaceShip is not null) _picker.SelectedItem = mod.ReplaceShip;

        // an empty stored name means the user never customised it, so it should keep following the ship name
        if (mod.ModName.Length > 0) { _modName.Text = mod.ModName; _autoModName = ""; }
        else { _modName.Text = _autoModName = Proposed(); }
    }

    /// <summary>Keep the mod name showing a sensible default while the user has not customised it (the text still
    /// equals what was last auto-filled, or is blank). A user edit sticks.</summary>
    private void SyncModName()
    {
        if (_modName.Text.Trim().Length != 0 && _modName.Text != _autoModName) return;
        _modName.Text = _autoModName = Proposed();
    }

    private string Proposed() =>
        _replace.IsChecked == true && _picker.SelectedItem is ShipFileEntry e
            ? $"{e.Name} - Replaced via Ostraplan"
            : _shipName;

    public override string? Validate() =>
        _replace.IsChecked == true && _picker.SelectedItem is not ShipFileEntry
            ? ShowProblem(_problem, "Pick the ship to replace, or untick \"Replace an existing ship\".")
            : ShowProblem(_problem, null);

    public override void Leave(WizardSession session)
    {
        var mod = session.Plan.Mod;
        // Blank means "still following the ship name". ShipExport.ResolveModName re-derives exactly the same
        // default from a blank, so storing it that way keeps the two in step instead of freezing today's value.
        var typed = _modName.Text.Trim();
        mod.ModName = typed == _autoModName.Trim() ? "" : typed;
        mod.Author = _author.Text.Trim();
        mod.Version = _version.Text.Trim();
        mod.Notes = _notes.Text.Trim();
        mod.ReplaceShip = _replace.IsChecked == true && _picker.SelectedItem is ShipFileEntry e ? e : null;
    }
}
