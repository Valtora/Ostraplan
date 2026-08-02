using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Ostraplan.Core;

namespace Ostraplan.App.Wizard;

/// <summary>
/// Where the edited ship is written, and what the edit costs.
///
/// <para>The cost model is <see cref="EditCost"/>: new parts at full base value, moved parts at half, times a
/// multiplier. The readout follows the slider live and Next refuses when the deduction can't be afforded, so the
/// user lowers the multiplier, reduces their changes, or unticks rather than finding out at the write.</para>
/// </summary>
public sealed class UpdateTargetStep : WizardStep
{
    private readonly TextBlock _saveName, _counts, _multLabel, _costLine, _balanceLine, _problem, _backupHint;
    private readonly RadioButton _copy, _inPlace;
    private readonly CheckBox _backup, _deduct;
    private readonly Slider _mult;
    private readonly TextBlock _noBalance;

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

        _multLabel = Add(body, new TextBlock { Foreground = Ink, FontSize = 12, Margin = new Thickness(24, 6, 0, 0) });
        _mult = Add(body, new Slider
        {
            Minimum = 0, Maximum = EditCost.MaxMultiplier, Value = EditCost.DefaultMultiplier,
            TickFrequency = 0.5, IsSnapToTickEnabled = true,
            TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight,
            Margin = new Thickness(24, 2, 8, 0),
        });
        _mult.ValueChanged += (_, _) => { Recost(); OnChanged(); };

        _costLine = Add(body, new TextBlock
        {
            Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 6, 0, 0),
        });
        _balanceLine = Add(body, new TextBlock { Foreground = Ink, FontSize = 12, Margin = new Thickness(24, 2, 0, 0) });
        _problem = Problem(body, indent: 24);

        Content = body;
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
            ? $"{diff.KeptCount} kept · {diff.MovedCount} moved · {diff.NewCount} added · {diff.DeletedCount} deleted"
            : "";

        _inPlace.IsChecked = plan.InPlace;
        _copy.IsChecked = !plan.InPlace;
        _backup.IsChecked = plan.Backup;

        var hasBalance = driver?.Balance is not null;
        _deduct.IsEnabled = hasBalance;
        _deduct.IsChecked = plan.Deduct && hasBalance;
        _noBalance.Visibility = hasBalance ? Visibility.Collapsed : Visibility.Visible;
        _mult.Value = Math.Clamp(plan.Multiplier, _mult.Minimum, _mult.Maximum);

        SyncBackup();
        Recost();
    }

    private void Recost()
    {
        var driver = _session?.Driver as UpdateDriver;
        var on = _deduct.IsChecked == true && _deduct.IsEnabled;
        _multLabel.Opacity = _mult.Opacity = _costLine.Opacity = _balanceLine.Opacity = on ? 1.0 : 0.4;
        _multLabel.Text = $"Cost multiplier: {_mult.Value:0.0}×";

        if (!on || driver?.BaseCost is not { } baseCost)
        {
            _costLine.Text = "Edits are free (cost not deducted).";
            _balanceLine.Text = driver?.Balance is { } b0 ? $"Balance: {Money(b0)} (unchanged)" : "";
            ShowProblem(_problem, null);
            return;
        }

        var terms = new List<string>
        {
            $"{baseCost.NewParts} added: {Money(baseCost.NewValue)}",
            $"{baseCost.MovedParts} moved: ½ × {Money(baseCost.MovedValue)}",
        };
        if (baseCost.NewCargo > 0)   // authored cargo items, priced at full value like new parts
            terms.Add($"{baseCost.NewCargo} item{(baseCost.NewCargo == 1 ? "" : "s")}: {Money(baseCost.CargoValue)}");

        var cost = _mult.Value * baseCost.Total;
        _costLine.Text = $"( {string.Join("  +  ", terms)} )  ×  {_mult.Value:0.0}×  =  {Money(cost)}";
        var balance = driver.Balance ?? 0;
        _balanceLine.Text = $"Balance: {Money(balance)}  →  {Money(balance - cost)}";
        ShowProblem(_problem, balance - cost >= 0 ? null : Unaffordable);
    }

    private const string Unaffordable =
        "Not enough credits. Lower the multiplier, reduce your changes, or untick \"Deduct the edit cost\".";

    public override string? Validate()
    {
        if (_deduct.IsChecked != true || _deduct.IsEnabled == false) return ShowProblem(_problem, null);
        if (_session?.Driver is not UpdateDriver { BaseCost: { } baseCost, Balance: { } balance })
            return ShowProblem(_problem, null);
        return balance - _mult.Value * baseCost.Total >= 0
            ? ShowProblem(_problem, null)
            : ShowProblem(_problem, Unaffordable);
    }

    public override void Leave(WizardSession session)
    {
        var plan = session.Plan.Update;
        plan.InPlace = _inPlace.IsChecked == true;
        plan.Backup = _backup.IsChecked == true;
        plan.Deduct = _deduct.IsChecked == true && _deduct.IsEnabled;
        plan.Multiplier = _mult.Value;
    }

    private static string Money(double v) => "$" + v.ToString("#,##0.##", CultureInfo.InvariantCulture);
}
