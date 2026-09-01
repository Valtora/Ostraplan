using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Ostraplan.Core;

namespace Ostraplan.App.Wizard;

/// <summary>
/// How a ship becomes obtainable in game: broker kiosk stock, the Special Offer slot shown to a player who owns
/// nothing, the Shipbreaker starting ship, and the derelict fields. All of it is loot data written alongside the
/// ship file.
///
/// <para><b>A panel rather than a step</b>, because two places ask a ship this question: the export wizard's
/// <see cref="ObtainableStep"/>, once, and the bundle editor, once per ship in the pack. Both drive the same
/// <see cref="DeliveryPlan"/>, so a route added here appears in both without being wired twice.</para>
///
/// <para>Mod-only either way. A save destination puts the ship straight into the player's hands, so there is
/// nothing to make it obtainable through.</para>
/// </summary>
public sealed class ObtainablePanel : UserControl
{
    private readonly List<(string Pool, CheckBox Box)> _broker = [];
    private readonly List<(string Pool, CheckBox Box)> _special = [];
    private readonly List<(string Pool, CheckBox Box)> _derelict = [];
    private readonly TextBox _brokerWeight, _startStation, _startMortgage;
    private readonly CheckBox _startingShip, _noRoute;
    private readonly RadioButton _startWeighted, _startExclusive;
    private readonly TextBlock _problem, _bandHint;
    private readonly Expander _advanced;
    private readonly WrapPanel _brokerWrap, _specialWrap;

    private bool _loaded;

    /// <summary>Raised when a route changed, so a host can drop anything it derived from the old answer.</summary>
    public event Action? Changed;

    private bool _populating;

    private void OnChanged()
    {
        if (!_populating) Changed?.Invoke();
    }

    public ObtainablePanel()
    {
        var body = PaneUi.Body();

        body.Children.Add(new TextBlock
        {
            Text = "Ship broker kiosks (regular stock):", Foreground = PaneUi.Ink, Margin = new Thickness(0, 2, 0, 3),
        });
        // Filled on Enter, not here: which kiosks exist is a question about the loaded game data, and the step is
        // constructed before there is a session to ask.
        _brokerWrap = PaneUi.Add(body, new WrapPanel { Margin = new Thickness(6, 0, 0, 2) });

        var weightRow = PaneUi.Add(body, new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 2, 0, 2) });
        weightRow.Children.Add(new TextBlock
        {
            Text = "Weight (how often it appears):", Foreground = PaneUi.Dim, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
        });
        _brokerWeight = PaneUi.SmallBox("0.05", 70);
        weightRow.Children.Add(_brokerWeight);

        body.Children.Add(new TextBlock
        {
            Text = "Special Offer (shown only when you own no ship/property):", Foreground = PaneUi.Ink,
            Margin = new Thickness(0, 12, 0, 3),
        });
        _specialWrap = PaneUi.Add(body, new WrapPanel { Margin = new Thickness(6, 0, 0, 2) });
        PaneUi.Note(body,
            "Heads up: the game always lists a Special Offer ship at \"$0\". The real price only shows when you click " +
            "Buy (a game quirk, not a pricing error). Add it to a broker kiosk above for a visible list price.",
            indent: 6);

        _startingShip = PaneUi.Add(body, new CheckBox
        {
            Content = "Offer as a starting ship (Shipbreaker career)", Foreground = PaneUi.Ink,
            Margin = new Thickness(0, 12, 0, 2),
        });
        _startWeighted = PaneUi.Add(body, new RadioButton
        {
            Content = "Weighted chance (alongside the vanilla salvage pods)", GroupName = "startMode",
            IsChecked = true, IsEnabled = false, Foreground = PaneUi.Ink, Margin = new Thickness(20, 4, 0, 1),
        });
        _startExclusive = PaneUi.Add(body, new RadioButton
        {
            Content = "Only your ship offered (guaranteed start)", GroupName = "startMode",
            IsEnabled = false, Foreground = PaneUi.Ink, Margin = new Thickness(20, 0, 0, 2),
        });
        _startingShip.Checked += (_, _) => { SyncStart(); SyncAdvanced(); OnChanged(); };
        _startingShip.Unchecked += (_, _) => { SyncStart(); SyncAdvanced(); OnChanged(); };

        var startRow = PaneUi.Add(body, new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 2, 0, 2) });
        startRow.Children.Add(new TextBlock
        {
            Text = "Start at ATC:", Foreground = PaneUi.Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        _startStation = PaneUi.SmallBox("OKLG", 70);
        startRow.Children.Add(_startStation);
        startRow.Children.Add(new TextBlock
        {
            Text = "Mortgage ($):", Foreground = PaneUi.Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 6, 0),
        });
        _startMortgage = PaneUi.SmallBox("0", 100);
        startRow.Children.Add(_startMortgage);

        PaneUi.Note(body,
            "The game has no true ship picker. \"Weighted chance\" adds your ship as one option among the vanilla " +
            "salvage pods. \"Only your ship offered\" replaces that start-event pool with just your ship, so a fresh " +
            "Shipbreaker always starts with it (this drops the vanilla pods, and any other mod's start ships, from " +
            "the roll).", indent: 20);
        body.Children.Add(new TextBlock
        {
            Text = "Derelict fields (found while salvaging):", Foreground = PaneUi.Ink, Margin = new Thickness(0, 14, 0, 3),
        });
        var derelictWrap = PaneUi.Add(body, new WrapPanel { Margin = new Thickness(6, 0, 0, 2) });
        foreach (var (pool, label) in KioskExport.DerelictPools)
        {
            var cb = new CheckBox { Content = label, Foreground = PaneUi.Ink, Margin = new Thickness(0, 0, 14, 4), MinWidth = 90 };
            cb.Checked += (_, _) => { SyncAdvanced(); OnChanged(); };
            cb.Unchecked += (_, _) => { SyncAdvanced(); OnChanged(); };
            _derelict.Add((pool, cb));
            derelictWrap.Children.Add(cb);
        }
        _bandHint = PaneUi.Note(body, "", indent: 6);
        PaneUi.Note(body,
            "The game wrecks a derelict itself when it first loads, so an export aimed only at these leaves the " +
            "condition slider off. Venus is its own flavour of hull rather than a size.", indent: 6);
        PaneUi.Add(body, new TextBlock
        {
            Text = "Only a NEW GAME. Derelicts are scattered when the world is generated, so a save you already " +
                   "have will never grow one.",
            Foreground = ThemeManager.Warn, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(6, 6, 0, 0),
        });

        PaneUi.Note(body,
            "If another ship mod adds to the same pools, run Ostrasort's conflict patch afterward so both mods' " +
            "ships survive.");
        _problem = PaneUi.Problem(body);

        // ---- the escape hatch ----
        // A bare ship file is a real output for someone assembling a modpack or wiring loot.json by hand, but it
        // is also what you get by forgetting to tick anything, and those two have to look different. Putting it
        // behind a disclosure makes it a decision rather than an oversight.
        _noRoute = new CheckBox
        {
            Content = new TextBlock
            {
                Text = "No route: I'll wire it up myself", TextWrapping = TextWrapping.Wrap, MaxWidth = 400,
            },
            Foreground = PaneUi.Ink, Margin = new Thickness(0, 2, 0, 2),
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        _noRoute.Checked += (_, _) => { PaneUi.ShowProblem(_problem, null); OnChanged(); };
        _noRoute.Unchecked += (_, _) => OnChanged();

        var advancedBody = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        advancedBody.Children.Add(_noRoute);
        PaneUi.Note(advancedBody,
            "Writes the ship file and nothing else, so the game will never spawn it on its own. Pick this when you " +
            "are assembling a modpack, editing loot.json yourself, or referencing the ship from another mod. " +
            "Ticking any route above takes precedence over it.", indent: 24);

        _advanced = PaneUi.Add(body, new Expander
        {
            Header = "Advanced", Foreground = PaneUi.Dim, Margin = new Thickness(0, 16, 0, 0), Content = advancedBody,
        });

        Content = body;
    }

    private void SyncStart()
    {
        var on = _startingShip.IsChecked == true;
        _startWeighted.IsEnabled = _startExclusive.IsEnabled = on;
    }

    /// <summary>Any real way for the game to place this ship. The escape hatch is not one of them: it is the
    /// statement that there is none.</summary>
    private bool AnyRoute() =>
        _startingShip.IsChecked == true
        || _broker.Concat(_special).Concat(_derelict).Any(x => x.Box.IsChecked == true);

    /// <summary>
    /// Keep the escape hatch honest against the routes above it. "No route" while a route is ticked is a
    /// contradiction, so a ticked route disables it and clears it, rather than leaving two answers standing.
    /// </summary>
    private void SyncAdvanced()
    {
        var busy = AnyRoute();
        if (busy) _noRoute.IsChecked = false;
        _noRoute.IsEnabled = !busy;
        _advanced.Opacity = busy ? 0.55 : 1.0;
    }

    /// <summary>Build one checkbox per discovered pool into <paramref name="wrap"/>, recording it against its
    /// loot name so the plan round-trips by name rather than by position.</summary>
    private void FillPools(
        WrapPanel wrap, List<(string Pool, CheckBox Box)> into,
        IReadOnlyList<(string Pool, string Label)> pools, double minWidth)
    {
        foreach (var (pool, label) in pools)
        {
            var cb = new CheckBox { Content = label, Foreground = PaneUi.Ink, Margin = new Thickness(0, 0, 14, 4), MinWidth = minWidth };
            cb.Checked += (_, _) => { SyncAdvanced(); OnChanged(); };
            cb.Unchecked += (_, _) => { SyncAdvanced(); OnChanged(); };
            into.Add((pool, cb));
            wrap.Children.Add(cb);
        }
    }

    /// <summary>
    /// Fill the panel in from one ship's routes. <paramref name="parts"/> is that design's part count, which the
    /// derelict band hint is measured against, and <paramref name="buyEstimate"/> pre-fills a starting ship's
    /// mortgage.
    /// </summary>
    public void Load(DataIndex index, DeliveryPlan mod, int parts, double buyEstimate)
    {
        _populating = true;
        try { LoadCore(index, mod, parts, buyEstimate); }
        finally { _populating = false; }
    }

    private void LoadCore(DataIndex index, DeliveryPlan mod, int parts, double buyEstimate)
    {
        var suggested = KioskExport.SuggestDerelictBand(parts);

        if (!_loaded)
        {
            // Which kiosks exist is data, not a constant: game 1.0 took the station broker count from five to
            // thirteen, and a mod can add more. Building the boxes from the loaded loot table means the dialog
            // offers every kiosk actually present rather than the set that existed when this was written.
            FillPools(_brokerWrap, _broker, KioskExport.BrokerPoolsIn(index), minWidth: 150);
            FillPools(_specialWrap, _special, KioskExport.SpecialOfferPoolsIn(index), minWidth: 110);

            // the game's own weight is only the starting point: a weight the user set last time has to survive
            mod.BrokerWeight ??= KioskExport.DefaultBrokerWeight(index, "RandomShipBrokerOKLG");
            mod.DerelictWeight ??= KioskExport.DefaultBrokerWeight(index, suggested);
            mod.StartWeight = KioskExport.DefaultBrokerWeight(index, StartingShipExport.ShipEventsPool);
            if (mod.StartMortgage <= 0) mod.StartMortgage = Math.Round(buyEstimate);
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
        _noRoute.IsChecked = mod.NoDeliveryRoute;

        // Decided on entry and left alone after: the disclosure opens when the step has nothing in it, which is
        // exactly when the hatch is the thing the user needs to see, and stays shut when the step is already busy
        // with routes. Toggling it live as boxes are ticked would move a control out from under the cursor.
        _advanced.IsExpanded = !AnyRoute();
        SyncAdvanced();
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
    /// counts: a kiosk, a Special Offer, a Shipbreaker start, or a derelict field. The one way past it is to say
    /// so deliberately, under Advanced.
    /// </summary>
    public string? Validate() =>
        AnyRoute() || _noRoute.IsChecked == true
            ? PaneUi.ShowProblem(_problem, null)
            : PaneUi.ShowProblem(_problem,
                "Pick at least one way to get this ship in game. Without one, the mod writes a ship file that " +
                "nothing in the game will ever spawn. If that is what you want, say so under Advanced.");

    /// <summary>Write the panel back onto the ship's routes.</summary>
    public void Save(DeliveryPlan mod)
    {
        mod.BrokerPools = [.. _broker.Where(b => b.Box.IsChecked == true).Select(b => b.Pool)];
        mod.SpecialOfferPools = [.. _special.Where(s => s.Box.IsChecked == true).Select(s => s.Pool)];
        mod.DerelictPools = [.. _derelict.Where(d => d.Box.IsChecked == true).Select(d => d.Pool)];
        mod.BrokerWeight = ParseDouble(_brokerWeight.Text, 0.05);
        mod.StartingShip = _startingShip.IsChecked == true;
        mod.StartingShipExclusive = _startExclusive.IsChecked == true;
        mod.StartStation = _startStation.Text.Trim() is { Length: > 0 } s ? s : "OKLG";
        mod.StartMortgage = ParseDouble(_startMortgage.Text, 0);
        mod.NoDeliveryRoute = _noRoute.IsChecked == true && !AnyRoute();
    }

    private static double ParseDouble(string text, double fallback) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0
            ? v : fallback;
}
