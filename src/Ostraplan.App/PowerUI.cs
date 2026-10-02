using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// Power Budget: what the design's switched-on devices draw, what its batteries hold, and how long they last.
///
/// <para>The game shows a time remaining only on one battery at a time, averaged over the last ten seconds of a ship
/// already flying. This answers it for the whole design before it is built, one card per conduit network, from the
/// same authored rates (<see cref="PowerBudget"/>). Nothing here is a simulation: endurance is stored charge divided
/// by steady draw, and the window says what that leaves out.</para>
///
/// <para>Modeless (see <see cref="ReportWindow"/>): the figures are measured against the ship, so an edit marks them
/// stale and Re-run measures again.</para>
/// </summary>
public sealed class PowerWindow : ReportWindow
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;
    private static Brush Accent => ThemeManager.Accent;
    private static Brush Good => ThemeManager.Good;
    private static Brush Warn => ThemeManager.Warn;

    private PowerBudgetReport _report = PowerBudgetReport.Empty;
    private string _designName = "";

    public PowerWindow()
    {
        Title = "Power Budget";
        Width = Math.Min(600, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(860, SystemParameters.WorkArea.Height - 40);
    }

    /// <summary>Show a run's budget, replacing whatever this window was showing.</summary>
    public void SetReport(PowerBudgetReport report, string designName)
    {
        _report = report;
        _designName = designName;
        SetBody(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = BuildBody(report) });
    }

    /// <summary>The report's content on its own, unparented, for the <c>--powersmoke</c> preview to measure at its
    /// full height.</summary>
    internal FrameworkElement Preview(PowerBudgetReport report, string designName)
    {
        _report = report;
        _designName = designName;
        var body = BuildBody(report);
        body.Background = ThemeManager.WindowBg;
        return body;
    }

    private StackPanel BuildBody(PowerBudgetReport report)
    {
        var body = new StackPanel { Margin = new Thickness(18) };
        body.Children.Add(new TextBlock { Text = "POWER BUDGET", Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11 });

        var (verdict, brush, sub) = Verdict(report);
        body.Children.Add(new TextBlock
        {
            Text = verdict, Foreground = brush, FontSize = 26, FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2),
        });
        body.Children.Add(Note(sub, new Thickness(0, 0, 0, 6)));

        foreach (var net in report.Networks)
            body.Children.Add(NetworkCard(net));
        foreach (var core in report.Reactors)
            body.Children.Add(ReactorCard(core));
        if (report.Unconnected.Count > 0)
            body.Children.Add(UnconnectedCard(report.Unconnected));

        body.Children.Add(Header("WHAT THIS LEAVES OUT"));
        foreach (var line in Caveats(report))
            body.Children.Add(Note(line, new Thickness(0, 0, 0, 4)));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var copy = new Button { Content = "Copy report", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => CopyToClipboard();
        var close = new Button { Content = "Close", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        close.Click += (_, _) => Close();
        buttons.Children.Add(copy);
        buttons.Children.Add(close);
        body.Children.Add(buttons);
        return body;
    }

    // ---- the headline

    /// <summary>The one figure a player asked for: how long until something goes dark. With several networks that
    /// is the first one to run out, since the others cannot help it.</summary>
    private static (string Text, Brush Brush, string Sub) Verdict(PowerBudgetReport r)
    {
        var draining = r.Networks.Where(n => n.LoadKw > 0 && !n.Sustained).ToList();
        var fed = r.Networks.Where(n => n.LoadKw > 0 && n.Sustained).ToList();

        if (draining.Count == 0 && fed.Count == 0)
            return ("Nothing draws power", Dim, r.Unconnected.Count > 0
                ? "Some devices would, but none of them is connected to a battery or generator."
                : "No switched-on device is connected to a battery or generator.");

        if (draining.Count == 0)
            return (fed.Any(n => n.Unlimited) ? "The reactor carries the load" : "Generators carry the load", Good,
                "Nothing drains the batteries while they run.");

        var worst = draining.MinBy(n => n.HoursOnBatteries ?? 0)!;
        var hours = worst.HoursOnBatteries ?? 0;
        var sub = hours <= 0
            ? $"Network {worst.Number} has devices drawing power but no charged battery."
            : draining.Count > 1 || fed.Count > 0
                ? $"Network {worst.Number} runs out first. Steady draw against stored charge, in game time."
                : "Steady draw against stored charge, in game time.";
        return (hours <= 0 ? "No battery power" : $"{Duration(hours)} on batteries", hours < 1 ? Warn : Accent, sub);
    }

    // ---- networks

    private static UIElement NetworkCard(PowerNetworkBudget net)
    {
        var card = new StackPanel();
        card.Children.Add(Header($"NETWORK {net.Number}"));

        // A network nothing draws from is usually a spare battery on a run of its own. One line says that.
        if (net.Loads.Count == 0)
        {
            card.Children.Add(Note(
                $"{Stores(net)}. Nothing draws from it"
                + (net.RechargedByReactor ? ", and the reactor recharges it." : "."), new Thickness(0, 0, 0, 0)));
            return card;
        }

        var slots = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var _ in Enumerable.Range(0, 3))
            slots.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddSlot(slots, 0, "Load", Kw(net.LoadKw));
        AddSlot(slots, 1, "Stored", Kwh(net.StoredKwh));
        AddSlot(slots, 2, net.Sustained ? "If the supply stops" : "On batteries alone",
            net.HoursOnBatteries is { } h ? h > 0 ? Duration(h) : "none" : "--");
        card.Children.Add(slots);

        if (net.Unlimited)
            card.Children.Add(Note("The running reactor feeds this network, so the batteries do not drain while it runs.", new Thickness(0, 0, 0, 4), Good));
        else if (net.Generators.Count > 0)
            card.Children.Add(Note(
                $"{string.Join(", ", net.Generators.Select(g => g.Name))} supplies {Kw(net.GeneratorKw)}"
                + (net.Sustained ? ", which covers the load." : $", short of the load by {Kw(net.LoadKw - net.GeneratorKw)}."),
                new Thickness(0, 0, 0, 4), net.Sustained ? Good : Warn));
        if (net.RechargedByReactor)
            card.Children.Add(Note("The running reactor recharges these batteries.", new Thickness(0, 0, 0, 4), Good));
        if (net.Batteries.Count > 0)
            card.Children.Add(Note(Stores(net) + ".", new Thickness(0, 0, 0, 6)));

        card.Children.Add(LoadTable(net.Loads));
        return card;
    }

    /// <summary>The batteries on a network, grouped: "4 × Battery, 324 kWh".</summary>
    private static string Stores(PowerNetworkBudget net)
    {
        if (net.Batteries.Count == 0) return "No batteries";
        var groups = net.Batteries.GroupBy(b => b.Name)
            .Select(g => g.Count() == 1 ? g.Key : $"{g.Count()} × {g.Key}");
        var line = $"{string.Join(", ", groups)}: {Kwh(net.StoredKwh)}";
        if (net.CapacityKwh - net.StoredKwh > 0.05) line += $" of {Kwh(net.CapacityKwh)}";
        return line;
    }

    /// <summary>A network's devices, grouped by name and draw so forty lights read as one row, biggest first.
    /// <paramref name="notes"/> is off for devices that reach no network, where how they would come on is moot.</summary>
    private static UIElement LoadTable(IReadOnlyList<PowerLoad> loads, bool notes = true)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var row = 0;
        foreach (var g in Group(loads))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock
            {
                Text = g.Count > 1 ? $"{g.Name} × {g.Count}" : g.Name, Foreground = Ink, FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 12, 0),
            };
            Grid.SetRow(name, row);
            grid.Children.Add(name);

            var kw = new TextBlock
            {
                Text = Kw(g.Kw), Foreground = Ink, FontSize = 12, FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 2, 0, 0),
            };
            Grid.SetRow(kw, row);
            Grid.SetColumn(kw, 1);
            grid.Children.Add(kw);
            row++;

            if (!notes || LoadNote(g.Sample) is not { } text) continue;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var note = new TextBlock
            {
                Text = text, Foreground = Warn, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12, 0, 0, 3),
            };
            Grid.SetRow(note, row);
            Grid.SetColumnSpan(note, 2);
            grid.Children.Add(note);
            row++;
        }
        return grid;
    }

    private static string? LoadNote(PowerLoad l) =>
        l.IsReactor ? "The reactor sits in Battery Mode on load and draws this until it is lit."
        : l.OffInPlan ? l.HasKnob
            ? "Switched off in the plan, but the game turns it back on. Set its bus knob to Off to keep it off."
            : "Switched off in the plan, but the game turns it back on. Wire it to a breaker box to keep it off."
        : null;

    private sealed record LoadGroup(string Name, int Count, double Kw, PowerLoad Sample);

    private static IEnumerable<LoadGroup> Group(IEnumerable<PowerLoad> loads) =>
        loads.GroupBy(l => (l.Name, Math.Round(l.Kw, 9), l.OffInPlan, l.IsReactor))
            .Select(g => new LoadGroup(g.Key.Name, g.Count(), g.Sum(l => l.Kw), g.First()))
            .OrderByDescending(g => g.Kw)
            .ThenBy(g => g.Name, StringComparer.CurrentCulture);

    // ---- the reactor

    private static UIElement ReactorCard(ReactorBudget core)
    {
        var card = new StackPanel();
        card.Children.Add(Header("REACTOR"));
        card.Children.Add(new TextBlock
        {
            Text = core.Name, Foreground = Ink, FontSize = 12, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        var (state, stateBrush) = core.State switch
        {
            ReactorState.Running => ("Spawns running.", Good),
            ReactorState.BatteryMode => ("Spawns in Battery Mode, drawing from the batteries until someone lights it.", Warn),
            ReactorState.WillNotStayLit => ($"Set to spawn running, but it shuts down on load: {core.Reason}.", Warn),
            _ => (core.Reason is { } why ? $"Spawns cold: {why}." : "Spawns cold.", Ink),
        };
        card.Children.Add(Note(state, new Thickness(0, 0, 0, 6), stateBrush));

        // Starting it.
        if (!core.CanStart)
            card.Children.Add(Note(core.InputNetwork is null
                ? "It can't be started: its power input isn't connected to a network with batteries."
                : $"It can't be started: Network {core.InputNetwork} has no charged battery.", new Thickness(0, 0, 0, 4), Warn));
        else
            card.Children.Add(Note(
                $"Starting it takes {Kw(core.StartingKw)} in Battery Mode. Network {core.InputNetwork} holds that for "
                + $"{Duration(core.StartingHours!.Value)}, alongside everything else drawing there.",
                new Thickness(0, 0, 0, 4), core.StartingHours < 0.25 ? Warn : Ink));
        if (core.Capacitors > 0)
            card.Children.Add(Note(
                $"Charging the {Plural(core.Capacitors, "capacitor")} adds {Kwh(core.CapacitorKwh)} over about 20 seconds, "
                + $"peaking at {Kw(core.CapacitorPeakKw)}.", new Thickness(0, 0, 0, 4)));
        if (core.FieldCoils > 0)
            card.Children.Add(Note(
                $"Switching the field coils on before ignition draws {Kw(PowerBudget.FieldCoilKw)} per coil"
                + (core.StartingHoursWithCoils is { } coils
                    ? $" and empties Network {core.InputNetwork} in {Duration(coils)}."
                    : ".")
                + " Leave them off until it is lit.", new Thickness(0, 0, 0, 4), Warn));

        // Running it.
        card.Children.Add(Note(core.OutputNetwork is { } output
            ? $"Running, it powers Network {output} with no practical limit."
            : "Its power output isn't connected to anything, so running it powers nothing.",
            new Thickness(0, 4, 0, 4), core.OutputNetwork is null ? Warn : Ink));
        if (!core.CanRecharge)
            card.Children.Add(Note("No MHD generator is fitted, so it never recharges the batteries.", new Thickness(0, 0, 0, 4), Warn));
        else if (core.RechargeNetworks.Count == 0)
            card.Children.Add(Note("Its MHD can recharge batteries, but none is connected to its power points.", new Thickness(0, 0, 0, 4), Warn));
        else
        {
            card.Children.Add(Note(
                $"With the MHD switched on, it recharges the batteries on {NetworkList(core.RechargeNetworks)}: "
                + $"90% in {Duration(PowerBudget.RechargeSeconds90 / PowerBudget.SecondsPerHour)}, "
                + $"99% in {Duration(PowerBudget.RechargeSeconds99 / PowerBudget.SecondsPerHour)}, from empty.",
                new Thickness(0, 0, 0, 4)));
            if (core.State == ReactorState.Running && !core.MhdSwitchOn)
                card.Children.Add(Note("Its MHD switch is off on the panel, so it spawns not charging.", new Thickness(0, 0, 0, 4), Warn));
        }
        return card;
    }

    private static UIElement UnconnectedCard(IReadOnlyList<PowerLoad> loads)
    {
        var card = new StackPanel();
        card.Children.Add(Header("NOT CONNECTED"));
        card.Children.Add(Note(
            "These would draw power, but nothing reaches their power input. They stay off.",
            new Thickness(0, 0, 0, 4)));
        card.Children.Add(LoadTable(loads, notes: false));
        return card;
    }

    private static IEnumerable<string> Caveats(PowerBudgetReport r)
    {
        yield return "Only the steady draw. Weapon shots, and lift rotors while manoeuvring, draw more on top.";
        yield return "One division, not a run: a battery that empties early does not switch anything off here.";
        if (r.ChargingContainers > 0)
            yield return "Batteries inside charging lockers are not counted.";
        if (r.Reactors.Any(c => c.CanRecharge))
            yield return "Recharge times are at normal speed. At high time compression the batteries charge more slowly.";
        yield return "Battery capacity follows painted condition. The export's wear setting lowers it further.";
    }

    // ---- clipboard

    private void CopyToClipboard()
    {
        var r = _report;
        var sb = new StringBuilder();
        sb.AppendLine($"Power budget: {_designName}");
        sb.AppendLine(Verdict(r).Text);

        foreach (var net in r.Networks)
        {
            sb.AppendLine();
            sb.AppendLine($"Network {net.Number}");
            sb.AppendLine($"  Load       {Kw(net.LoadKw)}");
            sb.AppendLine($"  Stored     {Kwh(net.StoredKwh)} ({Stores(net)})");
            if (net.HoursOnBatteries is { } h)
                sb.AppendLine($"  {(net.Sustained ? "If supply stops" : "On batteries"),-10} {(h > 0 ? Duration(h) : "none")}");
            if (net.Unlimited) sb.AppendLine("  Fed by the running reactor");
            else if (net.Generators.Count > 0) sb.AppendLine($"  Generators {Kw(net.GeneratorKw)}");
            if (net.RechargedByReactor) sb.AppendLine("  Recharged by the reactor");
            foreach (var g in Group(net.Loads))
                sb.AppendLine($"    {Kw(g.Kw),12}  {(g.Count > 1 ? $"{g.Name} × {g.Count}" : g.Name)}{(LoadNote(g.Sample) is { } n ? $"  ! {n}" : "")}");
        }

        foreach (var core in r.Reactors)
        {
            sb.AppendLine();
            sb.AppendLine($"Reactor: {core.Name}");
            sb.AppendLine($"  State      {core.State}{(core.Reason is { } why ? $" ({why})" : "")}");
            sb.AppendLine(core.CanStart
                ? $"  Starting   {Kw(core.StartingKw)}, {Duration(core.StartingHours!.Value)} on Network {core.InputNetwork}"
                : "  Starting   not possible: no charged battery at its power input");
            if (core.Capacitors > 0) sb.AppendLine($"  Capacitors {Kwh(core.CapacitorKwh)}, peak {Kw(core.CapacitorPeakKw)}");
            if (core.FieldCoils > 0) sb.AppendLine($"  Coils      {Kw(PowerBudget.FieldCoilKw)} each if on before ignition");
            sb.AppendLine($"  Feeds      {(core.OutputNetwork is { } o ? $"Network {o}" : "nothing")}");
            sb.AppendLine(core.CanRecharge
                ? $"  Recharges  {NetworkList(core.RechargeNetworks)} (MHD switch {(core.MhdSwitchOn ? "on" : "off")})"
                : "  Recharges  nothing (no MHD)");
        }

        if (r.Unconnected.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Not connected");
            foreach (var g in Group(r.Unconnected))
                sb.AppendLine($"    {Kw(g.Kw),12}  {(g.Count > 1 ? $"{g.Name} × {g.Count}" : g.Name)}");
        }

        try { Clipboard.SetText(sb.ToString()); } catch { /* clipboard may be locked by another app */ }
    }

    // ---- formatting

    /// <summary>A power figure in the unit the game's own battery panel would pick (<c>GetLoadString</c>), with
    /// megawatts added for the reactor's coils.</summary>
    internal static string Kw(double kw) => Math.Abs(kw) switch
    {
        >= 1000 => (kw / 1000).ToString("0.##", CultureInfo.CurrentCulture) + " MW",
        >= 10 => kw.ToString("#,0", CultureInfo.CurrentCulture) + " kW",
        >= 0.1 => kw.ToString("0.##", CultureInfo.CurrentCulture) + " kW",
        >= 0.001 => (kw * 1000).ToString("0.##", CultureInfo.CurrentCulture) + " W",
        0 => "0 W",
        _ => (kw * 1e6).ToString("0.##", CultureInfo.CurrentCulture) + " mW",
    };

    internal static string Kwh(double kwh) =>
        (kwh >= 100 ? kwh.ToString("#,0", CultureInfo.CurrentCulture) : kwh.ToString("0.##", CultureInfo.CurrentCulture)) + " kWh";

    /// <summary>Game time in the coarsest unit that still says something.</summary>
    internal static string Duration(double hours) => hours switch
    {
        < 1.0 / 60 => $"{hours * 3600:0} s",
        < 1.5 => $"{hours * 60:0} min",
        < 48 => hours.ToString("0.#", CultureInfo.CurrentCulture) + " h",
        < 24 * 365 => (hours / 24).ToString("0.#", CultureInfo.CurrentCulture) + " days",
        _ => "over a year",
    };

    private static string NetworkList(IReadOnlyList<int> numbers) => numbers.Count switch
    {
        0 => "no network",
        1 => $"Network {numbers[0]}",
        _ => "Networks " + string.Join(", ", numbers.Take(numbers.Count - 1)) + " and " + numbers[^1],
    };

    private static string Plural(int n, string noun) => n == 1 ? $"{noun}" : $"{n} {noun}s";

    // ---- small builders

    private static TextBlock Header(string text) => new()
    {
        Text = text, Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11, Margin = new Thickness(0, 16, 0, 6),
    };

    private static TextBlock Note(string text, Thickness margin, Brush? brush = null) => new()
    {
        Text = text, Foreground = brush ?? Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = margin,
    };

    private static void AddSlot(Grid grid, int column, string caption, string value)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Bottom };
        sp.Children.Add(new TextBlock { Text = value, Foreground = Ink, FontSize = 18, FontWeight = FontWeights.SemiBold });
        sp.Children.Add(new TextBlock { Text = caption, Foreground = Dim, FontSize = 10, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(sp, column);
        grid.Children.Add(sp);
    }
}
