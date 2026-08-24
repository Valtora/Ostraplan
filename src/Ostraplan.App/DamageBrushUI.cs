using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// The Damage Brush: drag across the plan and everything the stroke touches takes the condition you set, either
/// one figure or a range rolled per object.
///
/// <para><b>This paints the design, unlike Simulate, which measures it.</b> A strike's damage lives in a
/// <see cref="DamageState"/> beside the document and is thrown away when the window closes; a painted condition
/// is authored, goes in the <c>.oplan</c>, and reaches the game through both write paths. The two windows look
/// alike and are opposites, which is why this one commits through the undo stack and that one cannot.</para>
///
/// <para>A stroke is <b>one undo step</b> however many tiles it crossed, the same as a paint stroke from the
/// palette. The commands are executed live so the canvas shows the wear as the mouse moves, then handed over on
/// release for the stack to record as a batch (see <see cref="Committed"/>). <b>Shift and drag</b> bounds an
/// area instead: nothing is painted while the box is being sized, and the whole rectangle goes down on release
/// as one stroke.</para>
/// </summary>
public sealed class DamageBrushWindow : Window
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;

    private readonly ShipCanvas _board;

    /// <summary>The stroke in progress. Its rules are Core's (<see cref="DamageStroke"/>); this window supplies
    /// the brush the sliders are set to and hands the finished stroke to the host. Its RNG is seeded once per
    /// window rather than per stroke, so re-painting the same corridor twice does not hand back the identical set
    /// of rolls.</summary>
    private readonly DamageStroke _stroke;
    private readonly RadioButton _fixed;
    private readonly RadioButton _range;
    private readonly Slider _one;
    private readonly Slider _low;
    private readonly Slider _high;
    private readonly TextBlock _oneLabel = new();
    private readonly TextBlock _rangeLabel = new();
    private readonly TextBlock _statusLine = new();
    private readonly CheckBox _includeLoose;

    /// <summary>A finished stroke, for the host to record as one undo step. The commands have already run.</summary>
    public event Action<IReadOnlyList<IDocCommand>>? Committed;

    public DamageBrushWindow(ShipCanvas board, ShipDocument doc)
    {
        _board = board;
        _stroke = new DamageStroke(doc, new Random());

        Title = "Damage Brush";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;
        ResizeMode = ResizeMode.NoResize;

        _fixed = Choice("One condition for everything the brush touches");
        _range = Choice("A range, rolled per object as it paints");

        _one = ConditionSlider(60);
        _low = ConditionSlider(25);
        _high = ConditionSlider(70);

        _includeLoose = new CheckBox
        {
            Content = "Include loose items lying on the deck",
            IsChecked = true, Foreground = Ink, Margin = new Thickness(0, 10, 0, 0),
        };

        Content = BuildBody();
        _range.IsChecked = true;
        Sync();

        _board.DamagePainted += OnPainted;
        _board.DamageAreaPainted += OnAreaPainted;
        _board.DamageStrokeFinished += OnStrokeFinished;
        Loaded += (_, _) => _board.SetDamageBrush(true);
        Closed += (_, _) =>
        {
            _board.DamagePainted -= OnPainted;
            _board.DamageAreaPainted -= OnAreaPainted;
            _board.DamageStrokeFinished -= OnStrokeFinished;
            _board.SetDamageBrush(false);
        };
    }

    /// <summary>The brush as the model sees it. Read per object rather than cached, so moving a slider mid-stroke
    /// takes effect on the next tile instead of at the next stroke.</summary>
    private ConditionBrush Brush =>
        _fixed.IsChecked == true
            ? ConditionBrush.Fixed(_one.Value / 100.0)
            : ConditionBrush.Range(_low.Value / 100.0, _high.Value / 100.0);

    private void OnPainted((int X, int Y) cell)
    {
        _stroke.PaintTile(cell.X, cell.Y, Brush, _includeLoose.IsChecked == true);
        Report();
    }

    private void OnAreaPainted((int X, int Y) a, (int X, int Y) b)
    {
        _stroke.PaintArea(a.X, a.Y, b.X, b.Y, Brush, _includeLoose.IsChecked == true);
        Report();
    }

    private void OnStrokeFinished()
    {
        if (_stroke.Commands.Count == 0) return;
        Committed?.Invoke(_stroke.Commands.ToList());
        _stroke.Reset();
    }

    /// <summary>The stroke so far, not the tile just crossed: an area paints its whole rectangle in one go, and
    /// a freehand stroke's last tile says nothing about the corridor behind it. A stroke that has done nothing at
    /// all leaves the previous stroke's line up rather than blanking it.</summary>
    private void Report()
    {
        var (painted, skipped) = _stroke.Totals;
        if (painted == 0 && skipped == 0) return;
        _statusLine.Text = skipped == 0
            ? $"Painted {painted}."
            : $"Painted {painted}, left {skipped} that cannot take wear.";
    }

    // ---- chrome ----

    private UIElement BuildBody()
    {
        var root = new StackPanel { Margin = new Thickness(14) };

        root.Children.Add(new TextBlock
        {
            Text = "Drag across the plan to paint, or hold Shift and drag to box an area and paint all of it at "
                 + "once. Everything the stroke touches takes the condition below, and a part driven to nothing "
                 + "breaks into its damaged form the way the game breaks it.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });

        root.Children.Add(_fixed);
        root.Children.Add(_one);
        root.Children.Add(_oneLabel);

        root.Children.Add(_range);
        root.Children.Add(_low);
        root.Children.Add(_high);
        root.Children.Add(_rangeLabel);

        root.Children.Add(_includeLoose);

        root.Children.Add(new TextBlock
        {
            Text = "Nothing shows below 20% damage: the game draws no wear until a part is under 80% condition, "
                 + "so a lived-in look wants a range that reaches well past it.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
        });

        _statusLine.Foreground = Ink;
        _statusLine.FontSize = 12;
        _statusLine.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(_statusLine);

        return root;
    }

    private RadioButton Choice(string label)
    {
        var r = new RadioButton
        {
            Content = label, GroupName = "damageBrush", Foreground = Ink, Margin = new Thickness(0, 6, 0, 2),
        };
        r.Checked += (_, _) => Sync();
        return r;
    }

    private Slider ConditionSlider(double value)
    {
        var s = new Slider
        {
            Minimum = 0, Maximum = 100, Value = value,
            TickFrequency = 1, IsSnapToTickEnabled = true,
            Margin = new Thickness(24, 4, 8, 0),
        };
        s.ValueChanged += (_, _) => Sync();
        return s;
    }

    private void Sync()
    {
        var isFixed = _fixed.IsChecked == true;
        _one.IsEnabled = isFixed;
        _one.Opacity = _oneLabel.Opacity = isFixed ? 1.0 : 0.4;
        _low.IsEnabled = _high.IsEnabled = !isFixed;
        _low.Opacity = _high.Opacity = _rangeLabel.Opacity = isFixed ? 0.4 : 1.0;

        // The two ends cannot cross. Nudging whichever the user is not holding is less surprising than refusing
        // the drag, and ConditionBrush.Range would silently reorder them anyway.
        if (_low.Value > _high.Value)
        {
            if (_low.IsFocused) _high.Value = _low.Value;
            else _low.Value = _high.Value;
        }

        _oneLabel.Text = $"Condition: {_one.Value:0}%{Note(_one.Value)}";
        _oneLabel.Foreground = Ink;
        _oneLabel.FontSize = 12;
        _oneLabel.Margin = new Thickness(24, 4, 0, 6);

        _rangeLabel.Text = $"Condition: {_low.Value:0}% to {_high.Value:0}%{Note(_high.Value)}";
        _rangeLabel.Foreground = Ink;
        _rangeLabel.FontSize = 12;
        _rangeLabel.Margin = new Thickness(24, 4, 0, 6);
    }

    /// <summary>The two figures worth calling out on the slider: the point below which the game draws nothing, and
    /// the point at which a part stops existing as itself.</summary>
    private static string Note(double percent) =>
        percent >= (1.0 - WearShader.Threshold) * 100.0 ? "  ·  too healthy to show wear"
        : percent <= 0 ? "  ·  breaks the part"
        : "";
}
