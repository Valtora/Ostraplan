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
/// </summary>
public sealed class WearControl : StackPanel
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;

    private readonly CheckBox _apply;
    private readonly Slider _condition;
    private readonly TextBlock _readout;

    /// <summary>The vanilla "Used" average condition as a whole-percent slider value (≈88).</summary>
    private static readonly double VanillaPercent = Math.Round(WearModel.VanillaUsedCondition * 100.0);

    /// <summary>The wear the user chose. <see cref="WearOptions.Enabled"/> is false when the checkbox is off
    /// (a pristine ship) or the slider sits at 100%.</summary>
    public WearOptions Wear
    {
        get
        {
            var target = _condition.Value / 100.0;
            var enabled = _apply.IsChecked == true && target < 0.9999;
            return new WearOptions(enabled, target);
        }
    }

    /// <param name="defaultOn">Whether wear starts armed (export: true; save-edit: caller's choice).</param>
    /// <param name="overrideNote">When set, an extra warning line — used by save-edit to flag that wear replaces
    /// each part's existing damage across the whole ship.</param>
    public WearControl(bool defaultOn, string? overrideNote = null)
    {
        Header(this, "CONDITION / WEAR");

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

        Children.Add(new TextBlock
        {
            Text = "Each installed part is damaged randomly around this average, so condition varies part to part " +
                   "(no part ever drops below 10%). 88% matches the game's own kiosk (\"Used\") ships.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 4, 0, 0),
        });
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
        var on = _apply.IsChecked == true;
        _condition.Opacity = _readout.Opacity = on ? 1.0 : 0.4;

        var pct = (int)Math.Round(_condition.Value);
        var grade = Rating.ConditionGrade(_condition.Value / 100.0);
        var vanilla = pct == (int)VanillaPercent ? "  ·  Vanilla Used" : "";
        var tail = pct >= 100 ? "  ·  pristine" : $"  ·  rating ~{grade}";
        _readout.Text = $"Average condition: {pct}%{vanilla}{tail}";
    }

    private static void Header(Panel parent, string text) => parent.Children.Add(new TextBlock
    {
        Text = text, Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11, Margin = new Thickness(0, 16, 0, 5),
    });
}
