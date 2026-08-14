using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// The shared "Condition / Wear" panel used by the export and update-save dialogs: what condition the ship should
/// be in when it lands in the game. The three answers are mutually exclusive, so they are three radio buttons
/// rather than a stack of checkboxes that can disagree with each other.
///
/// <list type="bullet">
/// <item><b>Keep</b> the condition it already has. Offered only where there IS one to keep — updating a ship in a
/// save (its own parts' wear) or granting a design that was imported from one (the source ship's, matched part by
/// part). Maps to <see cref="WearOptions.Pristine"/>, i.e. an unarmed pass, which is what leaves existing damage
/// alone; <see cref="KeepSourceCondition"/> additionally reports it to the grant path.</item>
/// <item><b>Full condition</b> — every installed part at 100%. On an update this is "Repair All" and actively
/// clears each part's <c>StatDamage</c> (<see cref="WearOptions.Repaired"/>); on a ship being minted fresh it is
/// simply a pristine build, and the same options produce it.</item>
/// <item><b>Worn</b> at a chosen target <b>average</b> condition (10%–100%), the game's own kiosk wear generalised.
/// Damage is applied per part by the engine (<see cref="WearModel"/>) — the slider is the average, parts spread
/// randomly around it, none below 10%.</item>
/// </list>
///
/// <para>The design half of "repair" is elsewhere and always available: a part that is broken <i>as a def</i> (a
/// damaged wall, a wrecked alarm) is fixed in the editor by <see cref="Ostraplan.Core.Repair"/>, because that is a
/// change to the layout rather than to a save's condition owners.</para>
/// </summary>
public sealed class WearControl : StackPanel
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;

    private readonly RadioButton? _keep;
    private readonly RadioButton _full;
    private readonly RadioButton _worn;
    private readonly Slider _condition;
    private readonly TextBlock _readout;
    private readonly TextBlock _spread;
    private readonly bool _keepIsSourceCondition;

    /// <summary>The vanilla "Used" average condition as a whole-percent slider value (≈88).</summary>
    private static readonly double VanillaPercent = Math.Round(WearModel.VanillaUsedCondition * 100.0);

    /// <summary>True when the user asked for each part's real condition to come across from the save the design was
    /// imported from. Only ever true on the grant path, which is the only one that can honour it; an update keeps
    /// its own ship's wear through <see cref="Wear"/> being unarmed, and needs no separate flag.</summary>
    public bool KeepSourceCondition => _keepIsSourceCondition && _keep?.IsChecked == true;

    /// <summary>The condition the user chose, in the engine's terms: unarmed to keep what is there, armed at 100%
    /// to repair everything, armed at the slider to wear it.</summary>
    public WearOptions Wear =>
        _worn.IsChecked == true ? new WearOptions(true, _condition.Value / 100.0)
        : _full.IsChecked == true ? WearOptions.Repaired
        : WearOptions.Pristine;

    /// <summary>Restore a previously chosen condition (the export wizard remembers it between runs). The seed is
    /// not restored: it is pinned per build, not per setting. An unarmed pass selects "keep" where that is offered
    /// and full condition where it is not, since a ship with no existing condition to keep is minted undamaged
    /// either way.</summary>
    public void SetWear(WearOptions wear)
    {
        if (wear.Enabled && !wear.IsRepair)
        {
            _condition.Value = Math.Clamp(Math.Round(wear.TargetCondition * 100.0), _condition.Minimum, _condition.Maximum);
            _worn.IsChecked = true;
        }
        else if (wear.IsRepair || _keep is null) _full.IsChecked = true;
        else _keep.IsChecked = true;
        Sync();
    }

    /// <summary>Set the carry-the-real-condition choice. No-op unless this panel's keep option actually <i>means</i>
    /// that — an update's keep option means "leave the ship's own wear alone" and is restored by
    /// <see cref="SetWear"/> along with everything else, so clearing it from here would silently move the user's
    /// answer off it.</summary>
    public void SetKeepSourceCondition(bool keep)
    {
        if (!_keepIsSourceCondition || _keep is null) return;
        if (keep) _keep.IsChecked = true;
        else if (_keep.IsChecked == true) _full.IsChecked = true;
        Sync();
    }

    /// <summary>Raised whenever the chosen condition changed, so a host can invalidate anything derived from it.</summary>
    public event Action? Changed;

    /// <param name="keepLabel">The "keep the condition it already has" option's label, or null to leave it out —
    /// which is right wherever there is no existing condition to keep (a mod export, a grant of a design that came
    /// from nowhere).</param>
    /// <param name="keepNote">The explanation under <paramref name="keepLabel"/>.</param>
    /// <param name="keepIsSourceCondition">True when "keep" means the SOURCE save's per-part condition (the grant
    /// path), which the host has to be told about separately; false when it means "leave the ship's own wear alone",
    /// which the engine reads straight off <see cref="Wear"/>.</param>
    /// <param name="fullLabel">The full-condition option's label. An update repairs an existing ship, so it says so;
    /// everywhere else the ship is simply built undamaged.</param>
    /// <param name="fullNote">The explanation under <paramref name="fullLabel"/>.</param>
    public WearControl(
        string? keepLabel = null, string? keepNote = null, bool keepIsSourceCondition = false,
        string fullLabel = "Pristine — every installed part at 100% condition", string? fullNote = null)
    {
        _keepIsSourceCondition = keepIsSourceCondition;
        // Radio groups are matched by name across the whole visual tree, so a per-instance name is what stops two
        // panels (the wizard rebuilds this control when the destination changes) sharing one selection.
        var group = "wear" + Guid.NewGuid().ToString("N");

        Header(this, "CONDITION / WEAR");

        if (keepLabel is { Length: > 0 })
        {
            _keep = Choice(group, keepLabel);
            Children.Add(_keep);
            if (keepNote is { Length: > 0 }) Children.Add(Note(keepNote));
        }

        _full = Choice(group, fullLabel);
        Children.Add(_full);
        if (fullNote is { Length: > 0 }) Children.Add(Note(fullNote));

        _worn = Choice(group, "Worn (spawn the ship used, like a broker kiosk ship)");
        Children.Add(_worn);

        _condition = new Slider
        {
            Minimum = 10, Maximum = 100, Value = VanillaPercent,
            TickFrequency = 1, IsSnapToTickEnabled = true,
            Margin = new Thickness(24, 6, 8, 0),
        };
        _condition.ValueChanged += (_, _) => Sync();
        Children.Add(_condition);

        _readout = new TextBlock { Foreground = Ink, FontSize = 12, Margin = new Thickness(24, 4, 0, 0) };
        Children.Add(_readout);

        _spread = new TextBlock
        {
            Text = "Each installed part is damaged randomly around this average, so condition varies part to part " +
                   "(no part ever drops below 10%). 88% matches the game's own kiosk (\"Used\") ships.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 4, 0, 0),
        };
        Children.Add(_spread);

        _worn.IsChecked = true;
        Sync();
    }

    private RadioButton Choice(string group, string label)
    {
        var radio = new RadioButton
        {
            Content = label, GroupName = group, Foreground = Ink,
            Margin = new Thickness(0, 4, 0, 2),
        };
        radio.Checked += (_, _) => Sync();
        return radio;
    }

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 2, 0, 4),
    };

    private void Sync()
    {
        // The slider belongs to the "worn" answer alone, so it greys with it rather than sitting live under a
        // choice that ignores it.
        var worn = _worn.IsChecked == true;
        _condition.IsEnabled = worn;
        _condition.Opacity = _readout.Opacity = _spread.Opacity = worn ? 1.0 : 0.4;

        var pct = (int)Math.Round(_condition.Value);
        var grade = Rating.ConditionGrade(_condition.Value / 100.0);
        var vanilla = pct == (int)VanillaPercent ? "  ·  Vanilla Used" : "";
        var tail = pct >= 100 ? "  ·  pristine" : $"  ·  rating ~{grade}";
        _readout.Text = $"Average condition: {pct}%{vanilla}{tail}";

        Changed?.Invoke();
    }

    private static void Header(Panel parent, string text) => parent.Children.Add(new TextBlock
    {
        Text = text, Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11, Margin = new Thickness(0, 16, 0, 5),
    });
}
