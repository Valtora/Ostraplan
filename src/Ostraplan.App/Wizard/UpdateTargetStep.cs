using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Ostraplan.Core;

namespace Ostraplan.App.Wizard;

/// <summary>
/// Where the edited ship is written, and what the edit costs.
///
/// <para>The cost model is <see cref="EditCost"/>: added and moved parts are priced by a multiplier each, over the
/// part's base value. Two sliders rather than one, so a modular refit that shifts a lot of parts without conjuring
/// any can be priced accordingly. Next refuses when the deduction can't be afforded, so the user lowers a
/// multiplier, reduces their changes, or unticks rather than finding out at the write.</para>
///
/// <para>The bill is shown as a <b>ledger and a balance meter</b> (<see cref="BuildLedger"/>,
/// <see cref="BuildMeter"/>), both following the sliders live. A number the user is about to be charged has to be
/// legible at a glance, and an inline equation is not: the ledger gives one aligned row per kind of change so the
/// figures can be compared down a column, and the meter answers the question the sliders are really being moved
/// to settle, which is whether this fits inside the credits on hand.</para>
/// </summary>
public sealed class UpdateTargetStep : WizardStep
{
    private readonly TextBlock _saveName, _counts, _freeNote, _balanceLine, _problem, _backupHint;
    private readonly TextBlock _newMultLabel, _movedMultLabel, _multHint;
    private readonly RadioButton _copy, _inPlace;
    private readonly CheckBox _backup, _deduct;
    private readonly Slider _newMult, _movedMult;
    private readonly TextBlock _noBalance;

    private readonly Border _ledger, _meter;
    private LedgerRow _added = null!, _moved = null!, _reformed = null!, _cargo = null!, _total = null!;
    private TextBlock _meterLeft = null!, _meterRight = null!, _meterCaption = null!;
    private Border _meterFill = null!;
    private ColumnDefinition _meterFillCol = null!, _meterRestCol = null!;

    private WizardSession? _session;

    public override string Title => "Write target & cost";

    public UpdateTargetStep()
    {
        var body = Body();

        _saveName = Add(body, new TextBlock { Foreground = Ink, FontSize = 15, FontWeight = FontWeights.SemiBold });
        _counts = Add(body, new TextBlock { Foreground = Dim, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) });

        Header(body, "WRITE TO");
        _copy = Add(body, new RadioButton
        {
            Content = "A copy (keeps the original save untouched)", Foreground = Ink, IsChecked = true,
            Margin = new Thickness(0, 2, 0, 2),
        });
        _inPlace = Add(body, new RadioButton
        {
            Content = "The original save, in place", Foreground = Ink, Margin = new Thickness(0, 2, 0, 2),
        });
        Note(body,
            "Editing in place modifies the original save. Return to the Main Menu in game before writing, or the " +
            "game may overwrite your edit on its next autosave.", indent: 20);

        _backup = Add(body, new CheckBox
        {
            Content = "Back up the original save first", Foreground = Ink, IsChecked = true,
            Margin = new Thickness(20, 4, 0, 2),
        });
        _backupHint = Note(body,
            "A separate, loadable copy in your Saves folder (beside this save). Untick to skip it and avoid piling " +
            "up backups as you iterate, but then a bad edit can't be rolled back.", indent: 38);

        _copy.Checked += (_, _) => { SyncBackup(); OnChanged(); };
        _inPlace.Checked += (_, _) => { SyncBackup(); OnChanged(); };

        Header(body, "EDIT COST");
        _deduct = Add(body, new CheckBox
        {
            Content = "Deduct the edit cost from your credits", Foreground = Ink, Margin = new Thickness(0, 2, 0, 2),
        });
        _deduct.Checked += (_, _) => { Recost(); OnChanged(); };
        _deduct.Unchecked += (_, _) => { Recost(); OnChanged(); };
        _noBalance = Note(body,
            "No player balance found in this save, so the cost can't be deducted.", indent: 24);
        _noBalance.Visibility = Visibility.Collapsed;

        (_newMultLabel, _newMult) = MultiplierSlider(body, EditCost.DefaultNewMultiplier);
        (_movedMultLabel, _movedMult) = MultiplierSlider(body, EditCost.DefaultMovedMultiplier);
        _multHint = Note(body,
            "Added parts are conjured outside the game's build economy; moved parts you already own. Priced " +
            "separately so a refit that rearranges a lot without adding much needn't cost like a rebuild. Set " +
            "either to 0× to make that side free.", indent: 24);

        _ledger = BuildLedger(body);
        _meter = BuildMeter(body);

        _freeNote = Add(body, new TextBlock
        {
            Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 8, 0, 0),
        });
        _balanceLine = Add(body, new TextBlock { Foreground = Ink, FontSize = 12, Margin = new Thickness(24, 2, 0, 0) });
        _problem = Problem(body, indent: 24);

        Content = body;
    }

    // ---- the cost ledger ----

    /// <summary>
    /// The bill as a tally rather than an equation: one right-aligned row per kind of change, a rule, and the
    /// total. An inline "( a + b ) × m = total" is compact but unreadable at a glance, and this step's whole job
    /// is letting the user judge a number before they commit to it.
    ///
    /// <para>Every row is built once and re-filled as the sliders move, so a drag re-flows nothing and the total
    /// never jumps around under the cursor. Rows for a kind of change the edit doesn't contain collapse to
    /// nothing (an Auto row with hidden children has no height), so a plain move-only edit shows two lines.</para>
    /// </summary>
    private Border BuildLedger(Panel body)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 3; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < 6; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _added = new LedgerRow(grid, 0);
        _moved = new LedgerRow(grid, 1);
        _reformed = new LedgerRow(grid, 2);
        _cargo = new LedgerRow(grid, 3);

        var rule = new Border { Height = 1, Background = ThemeManager.PanelBorder, Margin = new Thickness(0, 6, 0, 6) };
        Grid.SetRow(rule, 4);
        Grid.SetColumnSpan(rule, 4);
        grid.Children.Add(rule);

        _total = new LedgerRow(grid, 5, emphasis: true);

        return Add(body, new Border
        {
            Background = ThemeManager.PanelBg, BorderBrush = ThemeManager.PanelBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(24, 10, 8, 0), Child = grid,
        });
    }

    /// <summary>
    /// What the edit takes out of the player's credits, as a bar. The affordability gate is the one thing on this
    /// step that can stop Next, so it is worth being able to see coming: the fill grows toward the right as the
    /// multipliers rise, and goes red the moment the cost passes the balance, which is exactly when Next refuses.
    /// </summary>
    private Border BuildMeter(Panel body)
    {
        var stack = new StackPanel();

        var labels = new Grid();
        labels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        labels.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _meterLeft = new TextBlock { Foreground = Dim, FontSize = 11 };
        _meterRight = new TextBlock { Foreground = Ink, FontSize = 11, FontWeight = FontWeights.SemiBold };
        Grid.SetColumn(_meterRight, 1);
        labels.Children.Add(_meterLeft);
        labels.Children.Add(_meterRight);
        stack.Children.Add(labels);

        // Two star columns whose weights are the spent/left split: the fill sizes itself in the layout pass, so
        // there is no pixel arithmetic to get wrong and it re-flows correctly with the pane.
        var track = new Grid { Height = 8, Margin = new Thickness(0, 4, 0, 0) };
        _meterFillCol = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
        _meterRestCol = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        track.ColumnDefinitions.Add(_meterFillCol);
        track.ColumnDefinitions.Add(_meterRestCol);
        _meterFill = new Border { CornerRadius = new CornerRadius(4), Background = ThemeManager.Accent };
        track.Children.Add(_meterFill);

        stack.Children.Add(new Border
        {
            Background = ThemeManager.FieldBg, BorderBrush = ThemeManager.PanelBorder,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = track,
        });

        _meterCaption = new TextBlock { Foreground = Dim, FontSize = 11, Margin = new Thickness(0, 3, 0, 0) };
        stack.Children.Add(_meterCaption);

        return Add(body, new Border { Margin = new Thickness(24, 10, 8, 0), Child = stack });
    }

    /// <summary>One line of the ledger: what changed, its summed base value, the multiplier it prices at, and the
    /// figure that lands on the bill. Held as four cells rather than a formatted string so the columns line up
    /// down the table, which is the entire point of showing it this way.</summary>
    private sealed class LedgerRow
    {
        private readonly TextBlock _what, _value, _mult, _cost;

        public LedgerRow(Grid grid, int row, bool emphasis = false)
        {
            _what = Cell(grid, row, 0, TextAlignment.Left, emphasis);
            _value = Cell(grid, row, 1, TextAlignment.Right, false);
            _mult = Cell(grid, row, 2, TextAlignment.Right, false);
            _cost = Cell(grid, row, 3, TextAlignment.Right, emphasis);
        }

        private static TextBlock Cell(Grid grid, int row, int col, TextAlignment align, bool emphasis)
        {
            var cell = new TextBlock
            {
                Foreground = emphasis || col == 3 ? Ink : Dim,
                FontSize = emphasis ? 13 : 12,
                FontWeight = emphasis ? FontWeights.SemiBold : FontWeights.Normal,
                TextAlignment = align,
                Margin = new Thickness(col == 0 ? 0 : 18, 1, 0, 1),
            };
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, col);
            grid.Children.Add(cell);
            return cell;
        }

        /// <summary>Fill the row and show it. <paramref name="value"/> is the base-value sum, priced at
        /// <paramref name="multiplier"/>.</summary>
        public void Show(string what, double value, double multiplier)
        {
            _what.Text = what;
            _value.Text = Money(value);
            _mult.Text = $"×{multiplier:0.0}";
            _cost.Text = Money(value * multiplier);
            Visible(true);
        }

        /// <summary>The total line: no base value or multiplier of its own.</summary>
        public void ShowTotal(double cost)
        {
            _what.Text = "Total";
            _value.Text = _mult.Text = "";
            _cost.Text = Money(cost);
            Visible(true);
        }

        public void Hide() => Visible(false);

        private void Visible(bool on)
        {
            var v = on ? Visibility.Visible : Visibility.Collapsed;
            _what.Visibility = _value.Visibility = _mult.Visibility = _cost.Visibility = v;
        }
    }

    /// <summary>A labelled 0×-to-<see cref="EditCost.MaxMultiplier"/> slider. The label's text is written by
    /// <see cref="Recost"/>, which is what keeps both readouts and the cost line in step.</summary>
    private (TextBlock Label, Slider Slider) MultiplierSlider(Panel body, double initial)
    {
        var label = Add(body, new TextBlock { Foreground = Ink, FontSize = 12, Margin = new Thickness(24, 6, 0, 0) });
        var slider = Add(body, new Slider
        {
            Minimum = 0, Maximum = EditCost.MaxMultiplier, Value = initial,
            TickFrequency = 0.5, IsSnapToTickEnabled = true,
            TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight,
            Margin = new Thickness(24, 2, 8, 0),
        });
        slider.ValueChanged += (_, _) => { Recost(); OnChanged(); };
        return (label, slider);
    }

    private void SyncBackup()
    {
        // the backup choice only applies to an in-place write; a copy leaves the original untouched already
        var on = _inPlace.IsChecked == true;
        _backup.IsEnabled = on;
        _backup.Opacity = _backupHint.Opacity = on ? 1.0 : 0.4;
    }

    public override void Enter(WizardSession session)
    {
        _session = session;
        var driver = session.Driver as UpdateDriver;
        var plan = session.Plan.Update;

        _saveName.Text = driver?.Context is { } ctx ? $"“{ctx.Source.SaveName}”" : "";
        _counts.Text = driver?.Diff is { } diff
            ? string.Join(" · ", Segments(diff))
            : "";

        _inPlace.IsChecked = plan.InPlace;
        _copy.IsChecked = !plan.InPlace;
        _backup.IsChecked = plan.Backup;

        var hasBalance = driver?.Balance is not null;
        _deduct.IsEnabled = hasBalance;
        _deduct.IsChecked = plan.Deduct && hasBalance;
        _noBalance.Visibility = hasBalance ? Visibility.Collapsed : Visibility.Visible;
        _newMult.Value = Math.Clamp(plan.NewMultiplier, _newMult.Minimum, _newMult.Maximum);
        _movedMult.Value = Math.Clamp(plan.MovedMultiplier, _movedMult.Minimum, _movedMult.Maximum);

        SyncBackup();
        Recost();
    }

    /// <summary>The change counts for the header. Re-stated parts get their own segment, and only when there are
    /// any: on most edits nothing was uninstalled and the line reads exactly as it always did.</summary>
    private static IEnumerable<string> Segments(ShipDiff diff)
    {
        yield return $"{diff.KeptCount} kept";
        yield return $"{diff.MovedCount} moved";
        if (diff.ReformedCount > 0) yield return $"{diff.ReformedCount} un/installed";
        yield return $"{diff.NewCount} added";
        yield return $"{diff.DeletedCount} deleted";
    }

    private void Recost()
    {
        var driver = _session?.Driver as UpdateDriver;
        var on = _deduct.IsChecked == true && _deduct.IsEnabled;
        _newMultLabel.Opacity = _newMult.Opacity = _movedMultLabel.Opacity = _movedMult.Opacity =
            _multHint.Opacity = on ? 1.0 : 0.4;
        _newMultLabel.Text = $"Added parts: {_newMult.Value:0.0}× base value";
        _movedMultLabel.Text = $"Moved or un/installed parts: {_movedMult.Value:0.0}× base value";

        if (!on || driver?.BaseCost is not { } baseCost)
        {
            // nothing is being charged, so a tally of zeros would be noise: say so in a line and stand down
            _ledger.Visibility = _meter.Visibility = Visibility.Collapsed;
            _freeNote.Visibility = _balanceLine.Visibility = Visibility.Visible;
            _freeNote.Text = "Edits are free (cost not deducted).";
            _balanceLine.Text = driver?.Balance is { } b0 ? $"Balance: {Money(b0)} (unchanged)" : "";
            ShowProblem(_problem, null);
            return;
        }

        _ledger.Visibility = _meter.Visibility = Visibility.Visible;
        _freeNote.Visibility = _balanceLine.Visibility = Visibility.Collapsed;

        double newM = _newMult.Value, movedM = _movedMult.Value;
        Line(_added, baseCost.NewParts, "added", baseCost.NewValue, newM);
        Line(_moved, baseCost.MovedParts, "moved", baseCost.MovedValue, movedM);
        // uninstalled / installed / doors toggled — owned already, so priced off the moved multiplier
        Line(_reformed, baseCost.ReformedParts, "un/installed", baseCost.ReformedValue, movedM);
        // authored cargo, conjured like a new part and priced with them
        Line(_cargo, baseCost.NewCargo, baseCost.NewCargo == 1 ? "cargo item" : "cargo items",
            baseCost.CargoValue, newM);

        var cost = EditCost.Total(baseCost, newM, movedM);
        _total.ShowTotal(cost);

        var balance = driver.Balance ?? 0;
        ShowMeter(balance, cost);
        ShowProblem(_problem, balance - cost >= 0 ? null : Unaffordable);
    }

    /// <summary>Fill one ledger row, or hide it when the edit contains none of that kind of change.</summary>
    private static void Line(LedgerRow row, int count, string what, double value, double multiplier)
    {
        if (count <= 0) row.Hide();
        else row.Show($"{count} {what}", value, multiplier);
    }

    /// <summary>Point the balance meter at the current figures. The fill is the share of the player's credits this
    /// edit consumes, clamped at full and reddened once it no longer fits.</summary>
    private void ShowMeter(double balance, double cost)
    {
        var affordable = balance - cost >= 0;
        var share = balance > 0 ? Math.Clamp(cost / balance, 0, 1) : cost > 0 ? 1 : 0;

        _meterFillCol.Width = new GridLength(share, GridUnitType.Star);
        _meterRestCol.Width = new GridLength(1 - share, GridUnitType.Star);
        _meterFill.Background = affordable ? ThemeManager.Accent : ThemeManager.Bad;

        _meterLeft.Text = $"Balance {Money(balance)}";
        _meterRight.Text = affordable ? $"Left {Money(balance - cost)}" : $"Short {Money(cost - balance)}";
        _meterRight.Foreground = affordable ? Ink : ThemeManager.Bad;
        _meterCaption.Text = affordable
            ? $"This edit takes {Money(cost)}, {share * 100:0.#}% of your credits."
            : $"This edit needs {Money(cost)}, more than you have.";
    }

    private const string Unaffordable =
        "Not enough credits. Lower a multiplier, reduce your changes, or untick \"Deduct the edit cost\".";

    public override string? Validate()
    {
        if (_deduct.IsChecked != true || _deduct.IsEnabled == false) return ShowProblem(_problem, null);
        if (_session?.Driver is not UpdateDriver { BaseCost: { } baseCost, Balance: { } balance })
            return ShowProblem(_problem, null);
        return balance - EditCost.Total(baseCost, _newMult.Value, _movedMult.Value) >= 0
            ? ShowProblem(_problem, null)
            : ShowProblem(_problem, Unaffordable);
    }

    public override void Leave(WizardSession session)
    {
        var plan = session.Plan.Update;
        plan.InPlace = _inPlace.IsChecked == true;
        plan.Backup = _backup.IsChecked == true;
        plan.Deduct = _deduct.IsChecked == true && _deduct.IsEnabled;
        plan.NewMultiplier = _newMult.Value;
        plan.MovedMultiplier = _movedMult.Value;
    }

    private static string Money(double v) => "$" + v.ToString("#,##0.##", CultureInfo.InvariantCulture);
}
