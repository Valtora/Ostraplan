using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>Everything the report needs from a run: what the design brings, the bodies it can be flown at, and the
/// RCS thrust the propulsion analysis already measured.</summary>
public sealed record FlightReport(
    FlightProfile Profile, IReadOnlyList<CelestialBody> Bodies, double RcsThrustNewtons, string DesignName);

/// <summary>
/// Flight Dynamics: what a design does in air, at a place you choose.
///
/// <para>The game answers this only on a running ship, in the nav console's Flight Dynamics module, and only for
/// wherever the ship happens to be. Here the environment is an input: pick a body and an altitude and the report
/// fills in the local gravity, pressure, density and temperature from the game's own atmosphere tables, then flies
/// the design through them at an airspeed and attitude you set. Every environment figure is editable afterwards,
/// so "what if the air were thicker" is one number away from "50 km above Venus".</para>
///
/// <para>The maths is <see cref="FlightDynamics"/>'s port of the game's own, which is why lift falls off as the
/// <i>square</i> of mass and why rotors die in vacuum. Nothing here is a simulation: it evaluates the game's
/// expressions at a point the user chooses, the same way the propulsion block reports a peak acceleration.</para>
///
/// <para>Modeless (see <see cref="ReportWindow"/>): the profile is measured against the ship, so a re-run
/// refreshes the open window through <see cref="SetReport"/>. The environment and flight controls belong to the
/// window, not to the run, so they survive a re-run.</para>
/// </summary>
public sealed class FlightWindow : ReportWindow
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;
    private static Brush Accent => ThemeManager.Accent;
    private static Brush Good => ThemeManager.Good;
    private static Brush Warn => ThemeManager.Warn;

    private const string Dash = "--";

    private readonly AppSettings _settings;

    private FlightReport? _report;

    // environment
    private readonly ComboBox _bodyBox = new();
    private readonly Slider _altitude = new();
    private readonly TextBox _gravityBox = new();
    private readonly TextBox _pressureBox = new();
    private readonly TextBox _densityBox = new();
    private readonly TextBlock _airLine = new();
    private readonly TextBlock _altitudeLabel = new();

    // flight
    private readonly Slider _airspeed = new();
    private readonly Slider _aoa = new();
    private readonly Slider _attitude = new();
    private readonly TextBlock _airspeedLabel = new();
    private readonly TextBlock _aoaLabel = new();
    private readonly TextBlock _attitudeLabel = new();

    // readouts
    private readonly TextBlock _lift = new();
    private readonly TextBlock _drag = new();
    private readonly TextBlock _rotors = new();
    private readonly TextBlock _support = new();
    private readonly TextBlock _verdict = new();
    private readonly TextBlock _detail = new();
    private readonly StackPanel _notes = new();

    /// <summary>Set while the environment boxes are being refilled from the body/altitude, so writing them back
    /// does not read as the user overriding them.</summary>
    private bool _filling;

    public FlightWindow(AppSettings settings)
    {
        _settings = settings;

        Title = "Flight Dynamics";
        Width = Math.Min(600, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(860, SystemParameters.WorkArea.Height - 40);
    }

    /// <summary>Install a run's measurements, replacing whatever this window was showing. The user's chosen
    /// environment and flight point are left alone: they describe where the ship is flying, not what it is.</summary>
    public void SetReport(FlightReport report)
    {
        var first = _report is null;
        _report = report;
        if (first) SetBody(BuildBody());
        else Refresh();
        if (first) RestoreSettings();
    }

    // ---- layout ----

    private UIElement BuildBody()
    {
        var body = new StackPanel { Margin = new Thickness(18) };

        body.Children.Add(new TextBlock { Text = "FLIGHT DYNAMICS", Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11 });
        body.Children.Add(_verdict);
        _verdict.Foreground = Accent;
        _verdict.FontSize = 26;
        _verdict.FontWeight = FontWeights.Bold;
        _verdict.Margin = new Thickness(0, 2, 0, 2);
        _verdict.TextWrapping = TextWrapping.Wrap;

        body.Children.Add(new TextBlock
        {
            Text = "What the design does in air. The game shows this only on a flying ship; here you choose where "
                 + "it flies.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });

        // ---- readouts ----
        var slots = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 6) };
        slots.Children.Add(Slot("Lift", _lift));
        slots.Children.Add(Slot("Drag", _drag));
        slots.Children.Add(Slot("Rotors", _rotors));
        slots.Children.Add(Slot("Holds", _support));
        body.Children.Add(slots);

        _detail.Foreground = Dim;
        _detail.FontSize = 11;
        _detail.TextWrapping = TextWrapping.Wrap;
        _detail.Margin = new Thickness(0, 2, 0, 6);
        body.Children.Add(_detail);
        body.Children.Add(_notes);

        // ---- environment ----
        body.Children.Add(Header("WHERE"));

        _bodyBox.Margin = new Thickness(0, 0, 0, 6);
        _bodyBox.DisplayMemberPath = nameof(CelestialBody.Name);
        _bodyBox.ItemsSource = _report!.Bodies;
        _bodyBox.SelectionChanged += (_, _) => { RescaleAltitude(); FillFromBody(); };
        body.Children.Add(_bodyBox);

        body.Children.Add(SliderRow("Altitude", _altitude, _altitudeLabel, 0, 100, 1, () => FillFromBody()));

        _airLine.Foreground = Dim;
        _airLine.FontSize = 11;
        _airLine.TextWrapping = TextWrapping.Wrap;
        _airLine.Margin = new Thickness(0, 2, 0, 6);
        body.Children.Add(_airLine);

        var fields = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 4) };
        fields.Children.Add(Field("Gravity (m/s²)", _gravityBox));
        fields.Children.Add(Field("Pressure (kPa)", _pressureBox));
        fields.Children.Add(Field("Density (kg/m³)", _densityBox));
        body.Children.Add(fields);

        foreach (var box in new[] { _gravityBox, _pressureBox, _densityBox })
            box.TextChanged += (_, _) => { if (!_filling) Refresh(); };

        var reset = new Button
        {
            Content = "Reset to the body's own figures", Padding = new Thickness(12, 3, 12, 3),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0),
        };
        reset.Click += (_, _) => FillFromBody();
        body.Children.Add(reset);
        body.Children.Add(new TextBlock
        {
            Text = "These three are what the maths actually uses, so you can overtype any of them and fly a place "
                 + "the game does not have. Pressure drives rotor efficiency; density drives lift and drag; "
                 + "gravity is what both are measured against.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        });

        // ---- flight point ----
        body.Children.Add(Header("HOW IT IS FLYING"));
        body.Children.Add(SliderRow("Airspeed", _airspeed, _airspeedLabel, 0, 1000, 5, Refresh));
        body.Children.Add(SliderRow("Angle of attack", _aoa, _aoaLabel, 0, 180, 1, Refresh));
        body.Children.Add(SliderRow("Nose off horizontal", _attitude, _attitudeLabel, 0, 180, 1, Refresh));
        body.Children.Add(new TextBlock
        {
            Text = "Airspeed is measured against the air, which moves with the body. Angle of attack is the ship's "
                 + "facing against its own motion: 0 is nose-on, 90 is broadside, and lift dies at both 90 and "
                 + "straight up.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var copy = new Button { Content = "Copy report", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => CopyToClipboard();
        var close = new Button { Content = "Close", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        close.Click += (_, _) => Close();
        buttons.Children.Add(copy);
        buttons.Children.Add(close);
        body.Children.Add(buttons);

        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body };
    }

    /// <summary>Put the window back where it was left last time: the same body, altitude and flight point, so
    /// reopening the report does not mean setting it all up again.</summary>
    private void RestoreSettings()
    {
        var bodies = _report!.Bodies;
        if (bodies.Count == 0)
        {
            // No body in the loaded data declares an atmosphere at all. Stock data has eight, so this means the
            // install's star_systems folder is missing or a mod replaced it with something without air.
            _airLine.Text = "No body in your game data declares an atmosphere, so there is nowhere to fly. "
                          + "Type gravity, pressure and density in by hand to use the report anyway.";
            return;
        }

        var saved = bodies.FirstOrDefault(b => b.Name == _settings.FlightBody);
        _bodyBox.SelectedItem = saved ?? bodies.FirstOrDefault(b => b.Name == "Venus") ?? bodies[0];
        RescaleAltitude();
        _altitude.Value = Math.Clamp(_settings.FlightAltitudeKm, _altitude.Minimum, _altitude.Maximum);
        _airspeed.Value = Math.Clamp(_settings.FlightAirspeed, _airspeed.Minimum, _airspeed.Maximum);
        _aoa.Value = Math.Clamp(_settings.FlightAngleOfAttack, _aoa.Minimum, _aoa.Maximum);
        _attitude.Value = Math.Clamp(_settings.FlightAttitude, _attitude.Minimum, _attitude.Maximum);
        FillFromBody();
    }

    private void Persist()
    {
        if (Body is not { } b) return;
        _settings.FlightBody = b.Name;
        _settings.FlightAltitudeKm = _altitude.Value;
        _settings.FlightAirspeed = _airspeed.Value;
        _settings.FlightAngleOfAttack = _aoa.Value;
        _settings.FlightAttitude = _attitude.Value;
        _settings.Save();
    }

    private CelestialBody? Body => _bodyBox.SelectedItem as CelestialBody;

    /// <summary>Re-scale the altitude slider to the selected body's authored ceiling — Titan's air runs to 600 km
    /// and Mars's to a few dozen, so one fixed range would be useless on both.</summary>
    private void RescaleAltitude()
    {
        if (Body is not { } b) return;
        var ceiling = Math.Max(1, b.MaxAltitudeKm);
        _altitude.Maximum = ceiling;
        _altitude.TickFrequency = ceiling / 20;
        if (_altitude.Value > ceiling) _altitude.Value = ceiling;
    }

    /// <summary>Fill the three environment fields from the selected body at the selected altitude, and refresh.</summary>
    private void FillFromBody()
    {
        if (Body is not { } b) return;
        var air = b.SampleAt(_altitude.Value);

        _filling = true;
        _gravityBox.Text = b.GravityAt(_altitude.Value).ToString("0.###", CultureInfo.CurrentCulture);
        _pressureBox.Text = air.PressureKPa.ToString("0.###", CultureInfo.CurrentCulture);
        _densityBox.Text = air.DensityKgPerM3.ToString("0.#####", CultureInfo.CurrentCulture);
        _filling = false;

        var composition = air.Present.Take(4)
            .Select(gg => $"{gg.Key} {gg.Value:0.##} kPa")
            .ToList();
        _airLine.Text = air.IsAtmosphere
            ? $"{b.Name} at {_altitude.Value:0} km: {air.TempK:0} K, "
              + (composition.Count > 0 ? string.Join(", ", composition) : "no gases")
              + $". Air runs to {b.MaxAltitudeKm:0} km here; surface gravity is {b.SurfaceGravity:0.##} m/s²."
            : $"{b.Name} at {_altitude.Value:0} km is effectively vacuum ({air.PressureKPa:0.###} kPa). "
              + $"Its air runs to {b.MaxAltitudeKm:0} km.";

        Refresh();
    }

    // ---- the live figures ----

    private FlightPoint? Point()
    {
        if (_report is null) return null;
        return new FlightPoint(
            _report.Profile,
            Parse(_gravityBox.Text), Parse(_densityBox.Text), Parse(_pressureBox.Text),
            Body?.SampleAt(_altitude.Value).TempK ?? 0,
            _airspeed.Value, _aoa.Value, _attitude.Value,
            _report.RcsThrustNewtons);
    }

    private void Refresh()
    {
        _altitudeLabel.Text = $"{_altitude.Value:0} km";
        _airspeedLabel.Text = $"{_airspeed.Value:0} m/s";
        _aoaLabel.Text = $"{_aoa.Value:0}°";
        _attitudeLabel.Text = $"{_attitude.Value:0}°";

        if (Point() is not { } p) return;
        var profile = p.Profile;

        _lift.Text = p.LiftAccel > 0 ? Gs(p.LiftAccel) : Dash;
        _drag.Text = p.DragAccel > 0 ? Gs(p.DragAccel) : Dash;
        _rotors.Text = p.RotorAccel > 0 ? Gs(p.RotorAccel) : Dash;
        _support.Text = p.Gravity > 0 ? $"{p.SupportRatio * 100:0}%" : Dash;
        _support.Foreground = p.Gravity <= 0 ? Ink : p.Holds ? Good : Warn;

        _verdict.Text = !p.InAtmosphere
            ? "Vacuum: no lift, no drag, no rotors"
            : p.Holds
                ? "Holds altitude"
                : $"Sinks — {p.SupportRatio * 100:0}% of the {p.Gravity:0.##} m/s² it needs";
        _verdict.Foreground = !p.InAtmosphere ? Dim : p.Holds ? Good : Warn;

        var lines = new List<string>
        {
            $"Mass {profile.Mass:#,0} kg · aero coefficient {profile.AeroCoefficient:#,0} from "
            + $"{profile.AeroParts} part{S(profile.AeroParts)} · grid {profile.NCols}×{profile.NRows} "
            + $"({profile.SizeMetres:0.#} m), so {profile.DragAreaFront:0.#} m² nose-on and "
            + $"{profile.DragAreaSide:0.#} m² broadside.",
        };

        if (profile.HasRotors)
            lines.Add($"Rotors: {profile.RotorsActive} of {profile.RotorsPresent} active, rated "
                + $"{profile.RotorThrust:#,0} kN, delivering {p.RotorThrustNewtons / 1000:#,0} kN at "
                + $"{p.RotorEfficiency:0.00}× efficiency ({p.PressureKPa:0.#} kPa / 100). "
                + $"Turbo would give {p.RotorThrustTurboNewtons / 1000:#,0} kN "
                + $"({Gs(p.RotorAccelTurbo)}), which is a switch at the console rather than a design choice.");

        if (p.LiftCapped)
            lines.Add($"Lift is at the game's ceiling of ten local gravities; it would otherwise read "
                + $"{Gs(p.LiftAccelRaw)}.");
        if (p.DragCapped)
            lines.Add("Drag is at the game's clamp of 2000 m/s². Past here the flight model, not the ship, is "
                + "what is holding it together.");

        if (p.HoverAirspeed is { } hover && p.InAtmosphere)
            lines.Add(hover <= _airspeed.Maximum
                ? $"Wings alone carry it at {hover:#,0} m/s at this attitude."
                : $"Wings alone would need {hover:#,0} m/s at this attitude, which is past anything it will reach.");
        else if (p.InAtmosphere && profile.AeroCoefficient > 0)
            lines.Add("At this attitude the lift term cancels, so wings carry nothing whatever the speed.");

        if (p.RcsAccel > 0)
            lines.Add($"RCS adds {Gs(p.RcsAccel)} if you point it up, which the game's mixed engine mode does "
                + "alongside the rotors. It burns reaction mass to do it.");

        lines.Add("Lift divides by mass twice in the game's own expression, so doubling a design's mass quarters "
            + "its lift. That, and not wing area, is usually what decides whether a design flies.");

        _detail.Text = string.Join(" ", lines);

        _notes.Children.Clear();
        foreach (var note in profile.Notes)
            _notes.Children.Add(new TextBlock
            {
                Text = note, Foreground = Warn, FontSize = 11,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2),
            });
    }

    /// <summary>Remember the flight point on the way out. Deliberately not on every change: a slider raises
    /// ValueChanged continuously while it is dragged, and writing the settings file on each tick would put a disk
    /// write behind every pixel of travel.</summary>
    protected override void OnClosed(EventArgs e)
    {
        Persist();
        base.OnClosed(e);
    }

    // ---- clipboard ----

    private void CopyToClipboard()
    {
        if (Point() is not { } p || _report is null) return;
        var profile = p.Profile;

        var sb = new StringBuilder();
        sb.AppendLine($"Flight dynamics: {_report.DesignName}");
        sb.AppendLine($"{Body?.Name ?? "custom"} at {_altitude.Value:0} km, {_airspeed.Value:0} m/s, "
            + $"AoA {_aoa.Value:0}°, nose {_attitude.Value:0}° off horizontal");
        sb.AppendLine();
        sb.AppendLine($"  Gravity        {p.Gravity:0.###} m/s²");
        sb.AppendLine($"  Pressure       {p.PressureKPa:0.###} kPa");
        sb.AppendLine($"  Density        {p.Density:0.#####} kg/m³");
        sb.AppendLine($"  Temperature    {p.TempK:0} K");
        sb.AppendLine();
        sb.AppendLine($"  Mass           {profile.Mass:#,0} kg");
        sb.AppendLine($"  Aero           {profile.AeroCoefficient:#,0} from {profile.AeroParts} part(s)");
        sb.AppendLine($"  Drag area      {profile.DragAreaFront:0.#} m² nose-on, {profile.DragAreaSide:0.#} m² broadside");
        sb.AppendLine($"  Rotors         {profile.RotorsActive}/{profile.RotorsPresent} active, {profile.RotorThrust:#,0} kN rated");
        sb.AppendLine();
        sb.AppendLine($"  Lift           {Gs(p.LiftAccel)}{(p.LiftCapped ? " (capped)" : "")}");
        sb.AppendLine($"  Drag           {Gs(p.DragAccel)}{(p.DragCapped ? " (clamped)" : "")}");
        sb.AppendLine($"  Rotor thrust   {p.RotorThrustNewtons / 1000:#,0} kN = {Gs(p.RotorAccel)}");
        sb.AppendLine($"  RCS            {Gs(p.RcsAccel)}");
        sb.AppendLine($"  Support        {p.SupportRatio * 100:0}% of local gravity — {(p.Holds ? "holds" : "sinks")}");
        if (p.HoverAirspeed is { } hover) sb.AppendLine($"  Wings alone    {hover:#,0} m/s to carry it");
        foreach (var note in profile.Notes) sb.AppendLine($"  ! {note}");

        try { Clipboard.SetText(sb.ToString()); } catch { /* clipboard may be locked by another app */ }
    }

    // ---- small builders ----

    private static string Gs(double accel) => $"{accel / Propulsion.StandardGravity:0.00}G";

    private static string S(int n) => n == 1 ? "" : "s";

    private static double Parse(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) && v > 0 ? v : 0;

    private static TextBlock Header(string text) => new()
    {
        Text = text, Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11, Margin = new Thickness(0, 16, 0, 6),
    };

    private static UIElement Slot(string caption, TextBlock value)
    {
        value.Foreground = Ink;
        value.FontSize = 18;
        value.FontWeight = FontWeights.SemiBold;
        var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Bottom };
        sp.Children.Add(value);
        sp.Children.Add(new TextBlock { Text = caption, Foreground = Dim, FontSize = 10 });
        return sp;
    }

    private static UIElement Field(string caption, TextBox box)
    {
        box.VerticalContentAlignment = VerticalAlignment.Center;
        box.TextAlignment = TextAlignment.Right;
        box.Margin = new Thickness(0, 0, 8, 0);
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = caption, Foreground = Dim, FontSize = 10, Margin = new Thickness(0, 0, 0, 2) });
        sp.Children.Add(box);
        return sp;
    }

    /// <summary>A captioned slider with its value shown to the right, so the number is readable while dragging.</summary>
    private static UIElement SliderRow(string caption, Slider slider, TextBlock label, double min, double max,
        double tick, Action onChange)
    {
        slider.Minimum = min;
        slider.Maximum = max;
        slider.TickFrequency = tick;
        slider.IsSnapToTickEnabled = false;
        slider.VerticalAlignment = VerticalAlignment.Center;
        slider.ValueChanged += (_, _) => onChange();

        label.Foreground = Ink;
        label.FontSize = 11;
        label.MinWidth = 62;
        label.TextAlignment = TextAlignment.Right;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(8, 0, 0, 0);

        var dp = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var name = new TextBlock
        {
            Text = caption, Foreground = Dim, FontSize = 11, MinWidth = 128,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(name, Dock.Left);
        DockPanel.SetDock(label, Dock.Right);
        dp.Children.Add(name);
        dp.Children.Add(label);
        dp.Children.Add(slider);
        return dp;
    }
}
