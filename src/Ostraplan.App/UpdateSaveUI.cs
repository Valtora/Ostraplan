using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// The "Update ship in save" options: write target (a copy, or the original in place) and an opt-in cost
/// deduction with a live-costed multiplier slider. The cost model is <see cref="EditCost"/> — new parts at full
/// base value, moved parts at half, times the multiplier — so the readout updates as the slider moves and the
/// Write button disables when the deduction can't be afforded (the user lowers the tax, reduces changes, or
/// unchecks). The heavy inject runs after this dialog returns, with the chosen <see cref="Charge"/>.
/// </summary>
public sealed class UpdateSaveDialog : Window
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;

    private readonly RadioButton _copy, _inPlace;
    private readonly CheckBox _backup;
    private readonly CheckBox _deduct;
    private readonly Slider _mult;
    private readonly TextBlock _multLabel, _costLine, _balanceLine, _cannotAfford;
    private readonly Button _ok;

    private readonly EditCostBreakdown _baseCost;   // computed at multiplier 1.0 (Total == the per-1× base cost)
    private readonly double? _balance;

    /// <summary>True = edit the original save in place; false = write a copy.</summary>
    public bool InPlace => _inPlace.IsChecked == true;

    /// <summary>Whether to back the original save up before an in-place write (only meaningful when
    /// <see cref="InPlace"/>). Ticked by default; the user can opt out to avoid a pile of backup saves.</summary>
    public bool Backup => _backup.IsChecked == true;

    /// <summary>The chosen cost multiplier (only meaningful when <see cref="Deduct"/>).</summary>
    public double Multiplier => _mult.Value;

    /// <summary>True when the user opted to deduct the cost.</summary>
    public bool Deduct => _deduct.IsChecked == true && _deduct.IsEnabled;

    /// <summary>The credits the edit costs at the current settings (0 when not deducting).</summary>
    public double Cost => Deduct ? Multiplier * _baseCost.Total : 0;

    /// <summary>The resulting balance after the deduction, or null when there's no balance / no deduction.</summary>
    public double? ResultingBalance => Deduct && _balance is { } b ? b - Cost : null;

    public UpdateSaveDialog(string saveName, int kept, int moved, int added, int deleted,
        EditCostBreakdown baseCost, double? currentBalance)
    {
        _baseCost = baseCost;
        _balance = currentBalance;

        Title = "Update ship in save";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = ThemeManager.WindowBg;

        var body = new StackPanel { Margin = new Thickness(18) };

        body.Children.Add(new TextBlock
        {
            Text = $"“{saveName}”", Foreground = Ink, FontSize = 15, FontWeight = FontWeights.SemiBold,
        });
        body.Children.Add(new TextBlock
        {
            Text = $"{kept} kept · {moved} moved · {added} added · {deleted} deleted",
            Foreground = Dim, FontSize = 12, Margin = new Thickness(0, 2, 0, 0),
        });

        // ---- write target ----
        Header(body, "WRITE TO");
        _copy = new RadioButton { Content = "A copy (keeps the original save untouched)", Foreground = Ink, IsChecked = true, Margin = new Thickness(0, 2, 0, 2) };
        _inPlace = new RadioButton { Content = "The original save, in place", Foreground = Ink, Margin = new Thickness(0, 2, 0, 2) };
        body.Children.Add(_copy);
        body.Children.Add(_inPlace);
        body.Children.Add(new TextBlock
        {
            Text = "Editing in place modifies the original save. Return to the Main Menu in game before writing, or the " +
                   "game may overwrite your edit on its next autosave.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 0, 0, 4),
        });
        // opt-in backup for the in-place write: ticked by default (safe), untickable to avoid accumulating a backup
        // save on every edit. Only relevant to the in-place path — a copy leaves the original untouched already.
        _backup = new CheckBox
        {
            Content = "Back up the original save first", Foreground = Ink, IsChecked = true,
            Margin = new Thickness(20, 0, 0, 2),
        };
        body.Children.Add(_backup);
        var backupHint = new TextBlock
        {
            Text = "A separate, loadable copy in your Saves folder (beside this save). Untick to skip it and avoid " +
                   "piling up backups as you iterate — but then a bad edit can't be rolled back.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(38, 0, 0, 0),
        };
        body.Children.Add(backupHint);
        // the backup choice only applies to an in-place write; grey it out (and force it on conceptually) for a copy
        void SyncBackupEnabled()
        {
            var on = _inPlace.IsChecked == true;
            _backup.IsEnabled = on;
            _backup.Opacity = backupHint.Opacity = on ? 1.0 : 0.4;
        }
        _copy.Checked += (_, _) => SyncBackupEnabled();
        _inPlace.Checked += (_, _) => SyncBackupEnabled();
        SyncBackupEnabled();

        // ---- cost ----
        Header(body, "EDIT COST");
        _deduct = new CheckBox
        {
            Content = "Deduct the edit cost from your credits", Foreground = Ink, IsChecked = false,
            IsEnabled = currentBalance is not null, Margin = new Thickness(0, 2, 0, 2),
        };
        _deduct.Checked += (_, _) => Recost();
        _deduct.Unchecked += (_, _) => Recost();
        body.Children.Add(_deduct);
        if (currentBalance is null)
            body.Children.Add(new TextBlock
            {
                Text = "No player balance found in this save, so the cost can't be deducted.",
                Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 0, 0, 0),
            });

        _multLabel = new TextBlock { Foreground = Ink, FontSize = 12, Margin = new Thickness(24, 6, 0, 0) };
        body.Children.Add(_multLabel);
        _mult = new Slider
        {
            Minimum = 0, Maximum = EditCost.MaxMultiplier, Value = EditCost.DefaultMultiplier,
            TickFrequency = 0.5, IsSnapToTickEnabled = true, TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight,
            Margin = new Thickness(24, 2, 0, 0),
        };
        _mult.ValueChanged += (_, _) => Recost();
        body.Children.Add(_mult);

        _costLine = new TextBlock { Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 6, 0, 0) };
        _balanceLine = new TextBlock { Foreground = Ink, FontSize = 12, Margin = new Thickness(24, 2, 0, 0) };
        _cannotAfford = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xD6, 0x45, 0x45)), FontSize = 12, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 4, 0, 0), Visibility = Visibility.Collapsed,
        };
        body.Children.Add(_costLine);
        body.Children.Add(_balanceLine);
        body.Children.Add(_cannotAfford);

        // ---- buttons ----
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        _ok = new Button { Content = "Write…", Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        _ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(_ok);
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);

        Content = body;
        Recost();
    }

    private static string Money(double v) => "$" + v.ToString("#,##0.##", CultureInfo.InvariantCulture);

    private void Recost()
    {
        var on = Deduct;
        _multLabel.Opacity = _mult.Opacity = _costLine.Opacity = _balanceLine.Opacity = on ? 1.0 : 0.4;
        _multLabel.Text = $"Cost multiplier: {Multiplier:0.0}×";

        if (!on)
        {
            _costLine.Text = "Edits are free (cost not deducted).";
            _balanceLine.Text = _balance is { } b0 ? $"Balance: {Money(b0)} (unchanged)" : "";
            _cannotAfford.Visibility = Visibility.Collapsed;
            _ok.IsEnabled = true;
            return;
        }

        var terms = new List<string>
        {
            $"{_baseCost.NewParts} added: {Money(_baseCost.NewValue)}",
            $"{_baseCost.MovedParts} moved: ½ × {Money(_baseCost.MovedValue)}",
        };
        if (_baseCost.NewCargo > 0)   // authored cargo items, priced at full value like new parts
            terms.Add($"{_baseCost.NewCargo} item{(_baseCost.NewCargo == 1 ? "" : "s")}: {Money(_baseCost.CargoValue)}");
        _costLine.Text = $"( {string.Join("  +  ", terms)} )  ×  {Multiplier:0.0}×  =  {Money(Cost)}";
        var bal = _balance ?? 0;
        var resulting = bal - Cost;
        _balanceLine.Text = $"Balance: {Money(bal)}  →  {Money(resulting)}";

        var afford = resulting >= 0;
        _cannotAfford.Visibility = afford ? Visibility.Collapsed : Visibility.Visible;
        _cannotAfford.Text = afford ? "" :
            "Not enough credits. Lower the multiplier, reduce your changes, or uncheck “Deduct the edit cost”.";
        _ok.IsEnabled = afford;
    }

    private static void Header(Panel parent, string text) => parent.Children.Add(new TextBlock
    {
        Text = text, Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11, Margin = new Thickness(0, 14, 0, 4),
    });
}
