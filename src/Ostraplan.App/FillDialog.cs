using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// Sets how much of what a canister or tank holds.
///
/// <para>The gas lines share <b>one</b> budget, because that is how the game works: a container's pressure is
/// the total moles of every species at once, so filling it with nitrogen leaves less room for oxygen and the
/// species do not each get a slice of the volume (see <see cref="ContainerFill"/>). Each gas slider's own
/// maximum is therefore "everything left, plus what this line already holds" — drag one to the right and the
/// tank is full of that gas; pull it back and the others can take the space. The total can never be pushed past
/// the container's pressure rating, which is a real burst threshold in game rather than a label.</para>
///
/// <para>Liquid and solid payloads (a torch tank's deuterium or helium-3, a modded water tank) sit in their own
/// section below. They have no pressure relationship at all, so each is capped on its own at what the def ships
/// with, which is the only capacity figure the game publishes for them.</para>
/// </summary>
public sealed class FillDialog : Window
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;
    private static Brush FieldBg => ThemeManager.FieldBg;
    private static Brush PanelBorder => ThemeManager.PanelBorder;

    private sealed record Row(PayloadLine Line, Slider Bar, TextBox Box, TextBlock Note);

    private readonly PayloadSpec _spec;
    private readonly Catalog _catalog;
    private readonly List<Row> _rows = [];
    private readonly Border _gauge = new();
    private readonly Rectangle _gaugeFill = new();
    private readonly TextBlock _gaugeText = new();
    private readonly TextBlock _summary = new();
    private bool _suppress;

    /// <summary>The chosen fill, or null when the container was returned to the amounts its def ships with —
    /// which is what <see cref="Placement.Fill"/> being null means, so a stock tank leaves nothing behind.</summary>
    public IReadOnlyDictionary<string, double>? Fill { get; private set; }

    public FillDialog(string friendly, PayloadSpec spec, IReadOnlyDictionary<string, double>? current, Catalog catalog)
    {
        _spec = spec;
        _catalog = catalog;

        Title = "Contents";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = ThemeManager.WindowBg;

        var body = new StackPanel { Margin = new Thickness(18) };
        body.Children.Add(new TextBlock
        {
            Text = friendly, Foreground = Ink, FontSize = 14, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = Subtitle(spec), Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 12),
        });

        // the fill the dialog opens on: what the part currently carries, else what its def ships with
        var start = current is null ? spec.Stock : ContainerFill.Clamp(current, spec);

        if (spec.HasGas)
        {
            body.Children.Add(Gauge());
            body.Children.Add(SectionLabel("GAS", "One shared budget: every species draws on the same pressure."));
            foreach (var line in spec.GasLines) body.Children.Add(BuildRow(line, start, "mol"));
        }

        if (spec.BulkLines.Any())
        {
            // The "no shared budget" half only says something next to a gas section, and a fuel tank has none.
            body.Children.Add(SectionLabel("LIQUIDS AND SOLIDS", spec.HasGas
                ? "Capped on their own — no pressure, no shared budget."
                : "Capped at what a full tank carries."));
            foreach (var line in spec.BulkLines) body.Children.Add(BuildRow(line, start, "kg"));
        }

        _summary.Foreground = Dim;
        _summary.FontSize = 11;
        _summary.Margin = new Thickness(0, 12, 0, 0);
        _summary.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(_summary);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var empty = new Button { Content = "Empty", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
        var stock = new Button { Content = "Reset to stock", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 24, 0) };
        var ok = new Button { Content = "OK", Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        empty.Click += (_, _) => SetAll(_ => 0);
        stock.Click += (_, _) => SetAll(l => l.Stock);
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(empty);
        buttons.Children.Add(stock);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);

        Content = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 720,
        };
        Refresh();
    }

    /// <summary>The container's own figures, so the numbers below have something to mean: what the game rates
    /// this shell at, and (for the cryogenic tanks) the temperature that lets 4 K hold so much more than 293 K.</summary>
    private static string Subtitle(PayloadSpec spec)
    {
        if (!spec.HasGas)
            return "A fuel tank, built around what it carries. The reactor matches its tanks by name, so it takes "
                   + "no gas: anything else in here would be weight the drive cannot use.";
        return $"{Num(spec.VolumeM3)} m³ at {Num(spec.TempK)} K, rated to {Num(spec.PressureMaxKPa)} kPa " +
               $"— {Num(spec.MaxMols)} mol in total. Going past the rating makes a canister burst in game, so it is the ceiling here.";
    }

    private static TextBlock SectionLabel(string text, string hint) => new()
    {
        Foreground = Dim, FontSize = 10, Margin = new Thickness(0, 10, 0, 4),
        Inlines =
        {
            new System.Windows.Documents.Run(text) { FontWeight = FontWeights.SemiBold },
            new System.Windows.Documents.Run("   " + hint),
        },
    };

    /// <summary>The pressure gauge: how much of the shared budget is spoken for, in one bar.</summary>
    private Border Gauge()
    {
        _gaugeFill.Fill = ThemeManager.Accent;
        _gaugeFill.HorizontalAlignment = HorizontalAlignment.Left;
        _gaugeFill.Height = 16;

        var grid = new Grid { Height = 16 };
        grid.Children.Add(_gaugeFill);

        _gaugeText.Foreground = Dim;
        _gaugeText.FontSize = 11;
        _gaugeText.Margin = new Thickness(0, 4, 0, 0);

        _gauge.Background = FieldBg;
        _gauge.BorderBrush = PanelBorder;
        _gauge.BorderThickness = new Thickness(1);
        _gauge.Child = grid;
        _gauge.SizeChanged += (_, _) => Refresh();

        var wrap = new StackPanel();
        wrap.Children.Add(_gauge);
        wrap.Children.Add(_gaugeText);
        return new Border { Child = wrap, Margin = new Thickness(0, 0, 0, 4) };
    }

    private UIElement BuildRow(PayloadLine line, IReadOnlyDictionary<string, double> start, string unit)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(148) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });

        var label = new TextBlock
        {
            Text = line.Label, Foreground = Ink, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = line.Cond,
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var opening = start.GetValueOrDefault(line.Cond);
        var bar = new Slider
        {
            Minimum = 0, Maximum = Math.Max(line.Max, opening), Value = opening,
            Tag = opening,   // the authoritative amount; see Current(Row)
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0),
            IsSnapToTickEnabled = false,
        };
        Grid.SetColumn(bar, 1);
        grid.Children.Add(bar);

        var box = new TextBox
        {
            Background = FieldBg, Foreground = Ink, BorderBrush = PanelBorder, BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2, 4, 2), HorizontalContentAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(box, 2);
        grid.Children.Add(box);

        var note = new TextBlock
        {
            Foreground = Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0), Text = unit,
        };
        Grid.SetColumn(note, 3);
        grid.Children.Add(note);

        var row = new Row(line, bar, box, note);
        _rows.Add(row);

        bar.ValueChanged += (_, _) => { if (!_suppress) { Write(row, bar.Value); Refresh(); } };
        box.TextChanged += (_, _) =>
        {
            if (_suppress || box.Text.Length == 0) return;
            if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                Write(row, v);
                Refresh();
            }
        };
        box.LostFocus += (_, _) => Refresh();   // re-render whatever was typed in the canonical form
        return grid;
    }

    /// <summary>Put a value on a row, clamped to its own ceiling and to whatever the shared gas budget has left.
    /// Clamping on the way in is what makes the budget unbreakable however the number arrives — dragged, typed or
    /// pasted — rather than something a later pass has to tidy up.</summary>
    private void Write(Row row, double value)
    {
        var wanted = Math.Max(0, double.IsFinite(value) ? value : 0);
        row.Bar.Tag = Math.Min(wanted, Ceiling(row));
    }

    /// <summary>The most this row may hold right now: its own maximum, and for a gas also everything the other
    /// gas lines have left unspoken for.</summary>
    private double Ceiling(Row row)
    {
        if (!row.Line.IsGas) return row.Line.Max;
        var others = _rows.Where(r => r.Line.IsGas && r != row).Sum(Current);
        return Math.Max(0, Math.Min(row.Line.Max, _spec.MaxMols - others));
    }

    /// <summary>This row's current amount. Held in the slider's Tag rather than its Value so a Maximum that moves
    /// under it cannot quietly coerce the number the user actually chose.</summary>
    private static double Current(Row row) => row.Bar.Tag is double d ? d : row.Bar.Value;

    private void SetAll(Func<PayloadLine, double> pick)
    {
        foreach (var row in _rows) row.Bar.Tag = Math.Max(0, pick(row.Line));
        Refresh();
    }

    /// <summary>Push the model back onto every control and redraw the readouts. Everything that changes a value
    /// ends here, so the sliders, the boxes, the gauge and the summary can never disagree.</summary>
    private void Refresh()
    {
        _suppress = true;
        try
        {
            foreach (var row in _rows)
            {
                var value = Current(row);
                row.Bar.Maximum = Math.Max(Ceiling(row), value);
                row.Bar.Value = value;
                var text = Num(value);
                if (!row.Box.IsFocused && row.Box.Text != text) row.Box.Text = text;
                row.Note.Text = row.Line.IsGas
                    ? $"mol · {Num(ShipValue.MolarMass(row.Line.Cond[ContainerFill.MolPrefix.Length..]) * value)} kg"
                    : "kg";
            }
        }
        finally { _suppress = false; }

        var fill = Current();
        if (_spec.HasGas)
        {
            var mols = ContainerFill.TotalMols(fill);
            var frac = _spec.MaxMols > 0 ? Math.Clamp(mols / _spec.MaxMols, 0, 1) : 0;
            _gaugeFill.Width = Math.Max(0, (_gauge.ActualWidth - 2) * frac);
            // No warning colour at the top end: the budget cannot be exceeded here, so a full tank is a normal
            // state and not something to shout about. It is what the game itself ships an RTA at.
            _gaugeFill.Fill = ThemeManager.Accent;
            _gaugeText.Text = $"{Num(mols)} of {Num(_spec.MaxMols)} mol  ·  {Num(_spec.PressureFor(mols))} of {Num(_spec.PressureMaxKPa)} kPa"
                              + (frac >= 0.999 ? "  ·  full" : "");
        }

        var worth = $"Contents worth ${ContainerFill.Value(fill, _catalog).ToString("#,##0", CultureInfo.InvariantCulture)}.";
        // the reaction-mass note only means something where there is gas to be reaction mass
        _summary.Text = _spec.HasGas
            ? $"Gas aboard: {Num(ContainerFill.GasMassKg(fill))} kg (all of it counts as RCS reaction mass).  {worth}"
            : worth;
    }

    /// <summary>The dialog's current fill, dropping the zero lines the way <see cref="ContainerFill.Clamp"/> does
    /// so an untouched species never shows up as a line of its own.</summary>
    private Dictionary<string, double> Current()
    {
        var fill = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var row in _rows)
        {
            var v = Current(row);
            if (v > 0) fill[row.Line.Cond] = v;
        }
        return fill;
    }

    private void Accept()
    {
        var fill = ContainerFill.Clamp(Current(), _spec);
        // Back at the stock amounts is "nothing authored here" — the same thing a part that was never touched
        // says — so the design carries no fill for it and the def stays the single source of truth.
        Fill = ContainerFill.IsStock(fill, _spec) ? null : fill;
        DialogResult = true;
    }

    private static string Num(double v) =>
        v == 0 ? "0"
        : Math.Abs(v) >= 100 ? v.ToString("#,##0", CultureInfo.InvariantCulture)
        : Math.Abs(v) >= 1 ? v.ToString("0.##", CultureInfo.InvariantCulture)
        : v.ToString("0.####", CultureInfo.InvariantCulture);
}
