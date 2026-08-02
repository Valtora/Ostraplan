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
    private readonly List<(string Pool, CheckBox Box)> _derelict = [];
    private readonly TextBox _brokerWeight, _startStation, _startMortgage;
    private readonly CheckBox _startingShip;
    private readonly RadioButton _startWeighted, _startExclusive;
    private readonly TextBlock _problem, _bandHint;

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
        body.Children.Add(new TextBlock
        {
            Text = "Derelict fields (found while salvaging):", Foreground = Ink, Margin = new Thickness(0, 14, 0, 3),
        });
        var derelictWrap = Add(body, new WrapPanel { Margin = new Thickness(6, 0, 0, 2) });
        foreach (var (pool, label) in KioskExport.DerelictPools)
        {
            var cb = new CheckBox { Content = label, Foreground = Ink, Margin = new Thickness(0, 0, 14, 4), MinWidth = 90 };
            cb.Checked += (_, _) => OnChanged();
            cb.Unchecked += (_, _) => OnChanged();
            _derelict.Add((pool, cb));
            derelictWrap.Children.Add(cb);
        }
        _bandHint = Note(body, "", indent: 6);
        Note(body,
            "The game wrecks a derelict itself when it first loads, so an export aimed only at these leaves the " +
            "condition slider off. Venus is its own flavour of hull rather than a size.", indent: 6);
        Add(body, new TextBlock
        {
            Text = "Only a NEW GAME. Derelicts are scattered when the world is generated, so a save you already " +
                   "have will never grow one.",
            Foreground = ThemeManager.Warn, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(6, 6, 0, 0),
        });

        Note(body,
            "If another ship mod adds to the same pools, run Ostrasort's conflict patch afterward so both mods' " +
            "ships survive.");
        _problem = Problem(body);

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

        var parts = session.Doc.Placements.Count;
        var suggested = KioskExport.SuggestDerelictBand(parts);

        if (!_loaded)
        {
            // the game's own weight is only the starting point: a weight the user set last time has to survive
            mod.BrokerWeight ??= KioskExport.DefaultBrokerWeight(session.Index, "RandomShipBrokerOKLG");
            mod.DerelictWeight ??= KioskExport.DefaultBrokerWeight(session.Index, suggested);
            mod.StartWeight = KioskExport.DefaultBrokerWeight(session.Index, StartingShipExport.ShipEventsPool);
            if (mod.StartMortgage <= 0) mod.StartMortgage = Math.Round(session.BuyEstimate);
            _loaded = true;
        }

        // The bands overlap badly (Small reaches 800 parts, Medium starts at 319), so the suggestion is a nearest
        // fit and the real ranges are shown beside it rather than a claim about which size this hull "is".
        var band = KioskExport.DerelictBands.First(b => b.Pool == suggested);
        var label = KioskExport.DerelictPools.First(p => p.Pool == suggested).Label;
        _bandHint.Text =
            $"This design has {parts} parts. Closest band: {label} ({band.Min} to {band.Max} parts in game). " +
            string.Join("  ", KioskExport.DerelictBands.Select(b =>
                $"{KioskExport.DerelictPools.First(p => p.Pool == b.Pool).Label} {b.Min}-{b.Max}"));

        foreach (var (pool, box) in _broker) box.IsChecked = mod.BrokerPools.Contains(pool);
        foreach (var (pool, box) in _special) box.IsChecked = mod.SpecialOfferPools.Contains(pool);
        foreach (var (pool, box) in _derelict) box.IsChecked = mod.DerelictPools.Contains(pool);
        _brokerWeight.Text = (mod.BrokerWeight ?? 0.05).ToString("0.####", CultureInfo.InvariantCulture);
        _startingShip.IsChecked = mod.StartingShip;
        _startExclusive.IsChecked = mod.StartingShipExclusive;
        _startWeighted.IsChecked = !mod.StartingShipExclusive;
        _startStation.Text = mod.StartStation;
        _startMortgage.Text = mod.StartMortgage.ToString("0", CultureInfo.InvariantCulture);
        SyncStart();
    }

    /// <summary>
    /// A ship nothing will ever spawn is refused here rather than written and wondered about later. Every route
    /// counts: a kiosk, a Special Offer, a Shipbreaker start, or a derelict field.
    /// </summary>
    public override string? Validate() =>
        Ticked().Any()
            ? ShowProblem(_problem, null)
            : ShowProblem(_problem,
                "Pick at least one way to get this ship in game. Without one, the mod writes a ship file that " +
                "nothing in the game will ever spawn.");

    private IEnumerable<CheckBox> Ticked() =>
        _broker.Concat(_special).Concat(_derelict).Select(x => x.Box).Where(b => b.IsChecked == true)
            .Concat(_startingShip.IsChecked == true ? [_startingShip] : Array.Empty<CheckBox>());

    public override void Leave(WizardSession session)
    {
        var mod = session.Plan.Mod;
        mod.BrokerPools = [.. _broker.Where(b => b.Box.IsChecked == true).Select(b => b.Pool)];
        mod.SpecialOfferPools = [.. _special.Where(s => s.Box.IsChecked == true).Select(s => s.Pool)];
        mod.DerelictPools = [.. _derelict.Where(d => d.Box.IsChecked == true).Select(d => d.Pool)];
        mod.BrokerWeight = ParseDouble(_brokerWeight.Text, 0.05);
        mod.StartingShip = _startingShip.IsChecked == true;
        mod.StartingShipExclusive = _startExclusive.IsChecked == true;
        mod.StartStation = _startStation.Text.Trim() is { Length: > 0 } s ? s : "OKLG";
        mod.StartMortgage = ParseDouble(_startMortgage.Text, 0);

        // A wreck is damaged by the game when it first loads, so baking wear on top would double-damage every
        // part. Only the untouched default is overridden: a user who set the slider themselves keeps their answer.
        if (!session.Plan.WearChosen && mod.DerelictPools.Count > 0 && mod.BrokerPools.Count == 0
            && mod.SpecialOfferPools.Count == 0 && !mod.StartingShip)
            session.Plan.Wear = WearOptions.Pristine;
    }

    private static double ParseDouble(string text, double fallback) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0
            ? v : fallback;
}
