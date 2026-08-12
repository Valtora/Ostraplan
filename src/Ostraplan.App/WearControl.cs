using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// The shared "Condition / Wear" panel used by the export and update-save dialogs. A checkbox arms wear and a
/// slider picks the target <b>average</b> part condition (10%–100%); the readout shows the value, the vanilla
/// marker and the expected rating grade. Exposes the chosen <see cref="WearOptions"/>. Damage is applied per part
/// by the engine (<see cref="WearModel"/>) — the slider is the average, parts spread randomly around it, none
/// below 10%.
///
/// <para>A design imported from a save can instead <see cref="KeepSourceCondition">keep the condition it really
/// has</see>, which is what a transfer between saves wants: the ship as it is, not a re-rolled copy of it. That
/// choice sits above the wear controls and greys them, because the two are alternatives rather than settings that
/// combine.</para>
/// </summary>
public sealed class WearControl : StackPanel
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;

    private readonly CheckBox? _keepSource;
    private readonly TextBlock? _keepSourceNote;
    private readonly CheckBox _apply;
    private readonly Slider _condition;
    private readonly TextBlock _readout;
    private readonly TextBlock _spread;

    /// <summary>The vanilla "Used" average condition as a whole-percent slider value (≈88).</summary>
    private static readonly double VanillaPercent = Math.Round(WearModel.VanillaUsedCondition * 100.0);

    /// <summary>True when the user asked for each part's real condition to come across from the save the design was
    /// imported from. Always false when the host didn't offer the choice, and the wear settings then stand alone.</summary>
    public bool KeepSourceCondition => _keepSource?.IsChecked == true;

    /// <summary>The wear the user chose. <see cref="WearOptions.Enabled"/> is false when the checkbox is off
    /// (a pristine ship) or the slider sits at 100%. Meaningless while <see cref="KeepSourceCondition"/> is set:
    /// the engine is carrying real damage and rolls none.</summary>
    public WearOptions Wear
    {
        get
        {
            var target = _condition.Value / 100.0;
            var enabled = _apply.IsChecked == true && target < 0.9999;
            return new WearOptions(enabled, target);
        }
    }

    /// <summary>Restore a previously chosen wear (the export wizard remembers it between runs). The seed is not
    /// restored: it is pinned per build, not per setting.</summary>
    public void SetWear(WearOptions wear)
    {
        _apply.IsChecked = wear.Enabled;
        _condition.Value = Math.Clamp(Math.Round(wear.TargetCondition * 100.0), _condition.Minimum, _condition.Maximum);
        Sync();
    }

    /// <summary>Set the carry-the-real-condition choice. No-op when the host didn't offer it.</summary>
    public void SetKeepSourceCondition(bool keep)
    {
        if (_keepSource is null) return;
        _keepSource.IsChecked = keep;
        Sync();
    }

    /// <summary>Raised whenever the chosen wear changed, so a host can invalidate anything derived from it.</summary>
    public event Action? Changed;

    /// <param name="defaultOn">Whether wear starts armed (export: true; save-edit: caller's choice).</param>
    /// <param name="overrideNote">When set, an extra warning line — used by save-edit to flag that wear replaces
    /// each part's existing damage across the whole ship.</param>
    /// <param name="sourceConditionNote">When set, the panel offers "keep each part's condition from the source
    /// save" above the wear controls, with this as its explanation. Only a destination that can actually honour it
    /// (a grant of a design imported from a save) passes one.</param>
    public WearControl(bool defaultOn, string? overrideNote = null, string? sourceConditionNote = null)
    {
        Header(this, "CONDITION / WEAR");

        if (sourceConditionNote is { Length: > 0 })
        {
            _keepSource = new CheckBox
            {
                Content = "Keep each part's condition from the source save",
                Foreground = Ink, Margin = new Thickness(0, 2, 0, 2),
            };
            _keepSource.Checked += (_, _) => Sync();
            _keepSource.Unchecked += (_, _) => Sync();
            Children.Add(_keepSource);

            _keepSourceNote = new TextBlock
            {
                Text = sourceConditionNote,
                Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 2, 0, 6),
            };
            Children.Add(_keepSourceNote);
        }

        _apply = new CheckBox
        {
            Content = "Apply wear (spawn the ship worn, like a used kiosk ship)",
            Foreground = Ink, IsChecked = defaultOn, Margin = new Thickness(0, 2, 0, 2),
        };
        _apply.Checked += (_, _) => Sync();
        _apply.Unchecked += (_, _) => Sync();
        Children.Add(_apply);

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
        if (overrideNote is { Length: > 0 })
            Children.Add(new TextBlock
            {
                Text = overrideNote,
                Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 4, 0, 0),
            });

        Sync();
    }

    private void Sync()
    {
        // Carrying the real condition and rolling a new one are alternatives, so arming the first stands the whole
        // wear block down rather than leaving two live controls that disagree about what the ship's condition is.
        var carrying = KeepSourceCondition;
        _apply.IsEnabled = _condition.IsEnabled = !carrying;
        if (_keepSourceNote is not null) _keepSourceNote.Opacity = carrying ? 1.0 : 0.7;

        var on = _apply.IsChecked == true && !carrying;
        _apply.Opacity = _spread.Opacity = carrying ? 0.4 : 1.0;
        _condition.Opacity = _readout.Opacity = on ? 1.0 : 0.4;

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
