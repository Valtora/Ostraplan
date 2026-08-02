using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Ostraplan.Core;

namespace Ostraplan.App.Wizard;

/// <summary>
/// How the exported ship becomes obtainable in game: broker kiosk stock, the Special Offer slot shown to a player
/// who owns nothing, and the Shipbreaker starting ship. All of it is loot data written alongside the ship file.
///
/// <para>Mod-only. A save destination puts the ship straight into the player's hands, so there is nothing to make
/// it obtainable through.</para>
/// </summary>
public sealed class ObtainableStep : WizardStep
{
    private readonly List<(string Pool, CheckBox Box)> _broker = [];
    private readonly List<(string Pool, CheckBox Box)> _special = [];
    private readonly TextBox _brokerWeight, _startStation, _startMortgage;
    private readonly CheckBox _startingShip;
    private readonly RadioButton _startWeighted, _startExclusive;

    private bool _loaded;

    public override string Title => "Obtainable in game";

    public ObtainableStep()
    {
        var body = Body();

        body.Children.Add(new TextBlock
        {
            Text = "Ship broker kiosks (regular stock):", Foreground = Ink, Margin = new Thickness(0, 2, 0, 3),
        });
        var brokerWrap = Add(body, new WrapPanel { Margin = new Thickness(6, 0, 0, 2) });
        foreach (var (pool, label) in KioskExport.BrokerPools)
        {
            var cb = new CheckBox { Content = label, Foreground = Ink, Margin = new Thickness(0, 0, 14, 4), MinWidth = 130 };
            cb.Checked += (_, _) => OnChanged();
            cb.Unchecked += (_, _) => OnChanged();
            _broker.Add((pool, cb));
            brokerWrap.Children.Add(cb);
        }

        var weightRow = Add(body, new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 2, 0, 2) });
        weightRow.Children.Add(new TextBlock
        {
            Text = "Weight (how often it appears):", Foreground = Dim, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
        });
        _brokerWeight = SmallBox("0.05", 70);
        weightRow.Children.Add(_brokerWeight);

        body.Children.Add(new TextBlock
        {
            Text = "Special Offer (shown only when you own no ship/property):", Foreground = Ink,
            Margin = new Thickness(0, 12, 0, 3),
        });
        var specialWrap = Add(body, new WrapPanel { Margin = new Thickness(6, 0, 0, 2) });
        foreach (var (pool, label) in KioskExport.SpecialOfferPools)
        {
            var cb = new CheckBox { Content = label, Foreground = Ink, Margin = new Thickness(0, 0, 14, 4), MinWidth = 110 };
            cb.Checked += (_, _) => OnChanged();
            cb.Unchecked += (_, _) => OnChanged();
            _special.Add((pool, cb));
            specialWrap.Children.Add(cb);
        }
        Note(body,
            "Heads up: the game always lists a Special Offer ship at \"$0\". The real price only shows when you click " +
            "Buy (a game quirk, not a pricing error). Add it to a broker kiosk above for a visible list price.",
            indent: 6);

        _startingShip = Add(body, new CheckBox
        {
            Content = "Offer as a starting ship (Shipbreaker career)", Foreground = Ink,
            Margin = new Thickness(0, 12, 0, 2),
        });
        _startWeighted = Add(body, new RadioButton
        {
            Content = "Weighted chance (alongside the vanilla salvage pods)", GroupName = "startMode",
            IsChecked = true, IsEnabled = false, Foreground = Ink, Margin = new Thickness(20, 4, 0, 1),
        });
        _startExclusive = Add(body, new RadioButton
        {
            Content = "Only your ship offered (guaranteed start)", GroupName = "startMode",
            IsEnabled = false, Foreground = Ink, Margin = new Thickness(20, 0, 0, 2),
        });
        _startingShip.Checked += (_, _) => { SyncStart(); OnChanged(); };
        _startingShip.Unchecked += (_, _) => { SyncStart(); OnChanged(); };

        var startRow = Add(body, new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 2, 0, 2) });
        startRow.Children.Add(new TextBlock
        {
            Text = "Start at ATC:", Foreground = Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        _startStation = SmallBox("OKLG", 70);
        startRow.Children.Add(_startStation);
        startRow.Children.Add(new TextBlock
        {
            Text = "Mortgage ($):", Foreground = Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 6, 0),
        });
        _startMortgage = SmallBox("0", 100);
        startRow.Children.Add(_startMortgage);

        Note(body,
            "The game has no true ship picker. \"Weighted chance\" adds your ship as one option among the vanilla " +
            "salvage pods. \"Only your ship offered\" replaces that start-event pool with just your ship, so a fresh " +
            "Shipbreaker always starts with it (this drops the vanilla pods, and any other mod's start ships, from " +
            "the roll).", indent: 20);
        Note(body,
            "If another ship mod adds to the same kiosks, run Ostrasort's conflict patch afterward so both mods' " +
            "ships survive.");

        Content = body;
    }

    private void SyncStart()
    {
        var on = _startingShip.IsChecked == true;
        _startWeighted.IsEnabled = _startExclusive.IsEnabled = on;
    }

    public override void Enter(WizardSession session)
    {
        var mod = session.Plan.Mod;

        if (!_loaded)
        {
            // the game's own weight is only the starting point: a weight the user set last time has to survive
            mod.BrokerWeight ??= KioskExport.DefaultBrokerWeight(session.Index, "RandomShipBrokerOKLG");
            mod.StartWeight = KioskExport.DefaultBrokerWeight(session.Index, StartingShipExport.ShipEventsPool);
            if (mod.StartMortgage <= 0) mod.StartMortgage = Math.Round(session.BuyEstimate);
            _loaded = true;
        }

        foreach (var (pool, box) in _broker) box.IsChecked = mod.BrokerPools.Contains(pool);
        foreach (var (pool, box) in _special) box.IsChecked = mod.SpecialOfferPools.Contains(pool);
        _brokerWeight.Text = (mod.BrokerWeight ?? 0.05).ToString("0.####", CultureInfo.InvariantCulture);
        _startingShip.IsChecked = mod.StartingShip;
        _startExclusive.IsChecked = mod.StartingShipExclusive;
        _startWeighted.IsChecked = !mod.StartingShipExclusive;
        _startStation.Text = mod.StartStation;
        _startMortgage.Text = mod.StartMortgage.ToString("0", CultureInfo.InvariantCulture);
        SyncStart();
    }

    public override void Leave(WizardSession session)
    {
        var mod = session.Plan.Mod;
        mod.BrokerPools = [.. _broker.Where(b => b.Box.IsChecked == true).Select(b => b.Pool)];
        mod.SpecialOfferPools = [.. _special.Where(s => s.Box.IsChecked == true).Select(s => s.Pool)];
        mod.BrokerWeight = ParseDouble(_brokerWeight.Text, 0.05);
        mod.StartingShip = _startingShip.IsChecked == true;
        mod.StartingShipExclusive = _startExclusive.IsChecked == true;
        mod.StartStation = _startStation.Text.Trim() is { Length: > 0 } s ? s : "OKLG";
        mod.StartMortgage = ParseDouble(_startMortgage.Text, 0);
    }

    private static double ParseDouble(string text, double fallback) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0
            ? v : fallback;
}
