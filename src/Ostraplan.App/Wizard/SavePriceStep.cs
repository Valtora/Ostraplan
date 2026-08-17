using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Ostraplan.Core;

namespace Ostraplan.App.Wizard;

/// <summary>
/// Which save to add the ship to, and what to charge for it.
///
/// <para>Picking a save reads it there and then, so the price is checked against a real balance and a save that
/// cannot take a grant at all says so here rather than after the user has committed to a write. That read parses
/// the save's biggest record, so it runs off the UI thread behind a wait cursor.</para>
/// </summary>
public sealed class SavePriceStep : WizardStep
{
    private readonly ComboBox _picker, _station;
    private readonly TextBlock _intro, _status, _balance, _problem, _stationNote;
    private readonly StackPanel _stationRow;
    private readonly CheckBox _charge;
    private readonly TextBox _price;

    private WizardSession? _session;
    private string? _readFailure;
    private bool _reading;
    private bool _syncing;

    public override string Title => "Save & price";

    public override bool CanAdvance => !_reading;

    public SavePriceStep()
    {
        var body = Body();

        _intro = Note(body, AddNote);

        Header(body, "SAVE GAME");
        _picker = Add(body, new ComboBox { DisplayMemberPath = nameof(SaveEntry.Name), MaxDropDownHeight = 240 });
        _picker.SelectionChanged += (_, _) => OnSavePicked();
        _status = Note(body, "Pick the save to add the ship to.");
        _problem = Problem(body);

        // Residence only. A vessel is placed relative to the player and has no station to choose, so the whole
        // row stays collapsed rather than showing a disabled control nobody has to think about.
        _stationRow = Add(body, new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 14, 0, 0) });
        _stationRow.Children.Add(new TextBlock
        {
            Text = "STATION", Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11,
            Margin = new Thickness(0, 0, 0, 3),
        });
        _station = new ComboBox { DisplayMemberPath = nameof(ResidenceStation.DisplayName), MaxDropDownHeight = 240 };
        _station.SelectionChanged += (_, _) => OnStationPicked();
        _stationRow.Children.Add(_station);
        _stationNote = new TextBlock
        {
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        };
        _stationRow.Children.Add(_stationNote);

        _charge = Add(body, new CheckBox
        {
            Content = "Charge for the ship (deduct from your credits)", Foreground = Ink, IsEnabled = false,
            Margin = new Thickness(0, 16, 0, 2),
        });
        _charge.Checked += (_, _) => { SyncPrice(); OnChanged(); };
        _charge.Unchecked += (_, _) => { SyncPrice(); OnChanged(); };

        var priceRow = Add(body, new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 4, 0, 0) });
        priceRow.Children.Add(new TextBlock
        {
            Text = "Price ($):", Foreground = Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        _price = SmallBox("0", 110);
        _price.IsEnabled = false;
        _price.TextChanged += (_, _) => { SyncPrice(); OnChanged(); };
        priceRow.Children.Add(_price);

        _balance = Add(body, new TextBlock
        {
            Foreground = Ink, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 6, 0, 0),
        });
        Note(body,
            "Leave this unticked and the ship is a gift. Ticked, the price comes off your character's balance, " +
            "which is how you simulate buying it.", indent: 24);

        Content = body;
    }

    private const string AddNote =
        "Adds this design to a copy of the save as a new ship you own, parked a few kilometres away and " +
        "reachable by P.A.S.S. ferry. The original save is never modified.";

    private const string AddResidenceNote =
        "Adds this design to a copy of the save as an apartment you own at a station, reached through that " +
        "station's transit kiosk. The original save is never modified.";

    public override void Enter(WizardSession session)
    {
        _session = session;
        var plan = session.Plan.NewShip;

        // Name the save the design came from when there is one. On a transfer this is the whole question the step
        // is asking — which save it goes to, as against the one it came out of — and the two are easy to confuse
        // in a list of similarly-named autosaves.
        var residence = session.Doc.IsResidence;
        var note = residence ? AddResidenceNote : AddNote;
        _intro.Text = session.SourceSave is { } src
            ? $"This {(residence ? "residence" : "ship")} was read out of \"{src.SaveName}\". " +
              $"Pick the save to add it to. {note}"
            : note;
        _stationRow.Visibility = residence ? Visibility.Visible : Visibility.Collapsed;

        _syncing = true;   // assigning SelectedItem raises SelectionChanged, which would re-read the save
        try
        {
            _picker.ItemsSource = session.Saves;
            _picker.SelectedItem = session.Saves.FirstOrDefault(s =>
                string.Equals(s.Name, plan.SaveName, StringComparison.Ordinal));
        }
        finally
        {
            _syncing = false;
        }

        _charge.IsChecked = plan.Charge;
        _price.Text = plan.Price.ToString("0.##", CultureInfo.InvariantCulture);
        // The driver may already hold a save and its stations, read by PrepareAsync from the remembered choice,
        // so fill the list here as well as after a pick or the picker opens empty on a step the user never
        // touched.
        SyncStations();
        SyncPrice();
    }

    private async void OnSavePicked()
    {
        if (_syncing || _session is not { } session) return;
        if (_picker.SelectedItem is not SaveEntry save) return;

        _readFailure = null;
        _reading = true;
        _status.Text = "Reading the save…";
        ShowProblem(_problem, null);
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            _readFailure = await ((NewShipDriver)session.Driver).UseSaveAsync(session, save);
        }
        finally
        {
            _reading = false;
            Mouse.OverrideCursor = null;
        }

        ShowProblem(_problem, _readFailure);
        SyncStations();
        SyncPrice();
        OnChanged();
    }

    /// <summary>Refill the station list from the driver's read of the chosen save, keeping the driver's own
    /// choice selected. Silent for a vessel run, whose list is never populated.</summary>
    private void SyncStations()
    {
        if (_session is not { } session || !session.Doc.IsResidence) return;
        if (session.Driver is not NewShipDriver driver) return;

        _syncing = true;
        try
        {
            _station.ItemsSource = driver.Stations;
            _station.SelectedItem = driver.Station;
        }
        finally
        {
            _syncing = false;
        }
        SyncStationNote();
    }

    private void OnStationPicked()
    {
        if (_syncing || _session is not { } session) return;
        if (session.Driver is NewShipDriver driver)
            driver.UseStation(session, _station.SelectedItem as ResidenceStation);
        SyncStationNote();
        OnChanged();
    }

    /// <summary>Say what the chosen station means, and name the one thing that would make the apartment useless:
    /// a station the game has no residence transit route to. Vanilla Mercury Volanus sells apartments and has no
    /// such route, so this is a real case rather than a defensive one.</summary>
    private void SyncStationNote()
    {
        if (_session is not { } session || session.Driver is not NewShipDriver driver) return;

        if (driver.Context is null)
        {
            _stationNote.Text = "Pick a save first.";
            _stationNote.Foreground = Dim;
        }
        else if (driver.Stations.Count == 0)
        {
            _stationNote.Text = "This save has no stations, so there is nowhere to put a residence.";
            _stationNote.Foreground = ThemeManager.Warn;
        }
        else if (_station.SelectedItem is not ResidenceStation s)
        {
            _stationNote.Text = "Pick the station this residence belongs to.";
            _stationNote.Foreground = Dim;
        }
        else if (!s.HasTransitRoute)
        {
            _stationNote.Text =
                $"{s.DisplayName} has no residence transit route in the game's data, so an apartment here would " +
                "be yours but unreachable. Pick another station unless a mod adds the route.";
            _stationNote.Foreground = ThemeManager.Warn;
        }
        else
        {
            _stationNote.Text =
                $"The apartment is registered at {s.DisplayName} and reached from its transit kiosk. You become a " +
                "homeowner there, which is what unlocks the route.";
            _stationNote.Foreground = Dim;
        }
    }

    /// <summary>Enable the price controls only once a usable save is chosen, and show the resulting balance as it
    /// will be after the write.</summary>
    private void SyncPrice()
    {
        var ctx = _session?.Driver is NewShipDriver d ? d.Context : null;
        var usable = ctx is not null;
        _charge.IsEnabled = usable;
        _price.IsEnabled = usable && _charge.IsChecked == true;

        if (ctx is null)
        {
            _balance.Text = "";
            if (_readFailure is null && !_reading)
                _status.Text = _session?.Saves.Count == 0
                    ? "No save games found."
                    : "Pick the save to add the ship to.";
            return;
        }

        _status.Text = _session?.Doc.IsResidence == true
            ? "The apartment is placed on its station, not parked in space."
            : $"The ship will appear near {ctx.PlayerShipRegId}, about 3 to 5 km out.";
        var price = Price;
        _balance.Text = _charge.IsChecked != true
            ? $"Balance: {Money(ctx.Balance)} (unchanged, it's a gift)"
            : price > ctx.Balance
                ? $"Balance: {Money(ctx.Balance)} — not enough for {Money(price)}."
                : $"Balance: {Money(ctx.Balance)}  →  {Money(ctx.Balance - price)}";
    }

    private double Price =>
        _charge.IsChecked == true
        && double.TryParse(_price.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0
            ? v : 0;

    public override string? Validate()
    {
        if (_readFailure is not null) return ShowProblem(_problem, _readFailure);
        if (_session?.Driver is not NewShipDriver { Context: { } ctx } driver)
            return ShowProblem(_problem, "Pick a save game to add the ship to.");
        if (_session.Doc.IsResidence)
        {
            if (driver.Stations.Count == 0)
                return ShowProblem(_problem,
                    "This save has no stations in it, so there is nowhere to put a residence. Pick another save.");
            if (driver.Station is null)
                return ShowProblem(_problem, "Pick the station this residence belongs to.");
        }
        return Price > ctx.Balance
            ? ShowProblem(_problem,
                $"That price is more than the character has ({Money(ctx.Balance)}). Lower it, or untick " +
                "\"Charge for the ship\".")
            : ShowProblem(_problem, null);
    }

    public override void Leave(WizardSession session)
    {
        var plan = session.Plan.NewShip;
        plan.Charge = _charge.IsChecked == true;
        plan.Price = Price;
        if (session.Doc.IsResidence && session.Driver is NewShipDriver driver)
            plan.StationRegId = driver.Station?.RegId;
    }

    private static string Money(double v) => "$" + v.ToString("#,##0.##", CultureInfo.InvariantCulture);
}
