using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// One tip, in a card over the bottom-right corner of the plan (#39). A card because the plan has to stay usable
/// with it open: a tip arrives at startup, often just as somebody starts work. It also opens from the bulb in the
/// toolbar.
///
/// <para>The card owns no policy. It shows what it is given and raises an event for each button, and
/// <see cref="MainWindow"/> decides what comes next through <see cref="TipPicker"/>. Its brushes are resource
/// references rather than the <see cref="ThemeManager"/> statics, because it lives as long as the window does and a
/// theme switch has to reach it.</para>
///
/// <para>None of its buttons take focus. Keyboard focus stays on the plan, so WASD and the overlay keys keep working
/// after a click on the card, and a stray Space cannot press a button nobody is looking at.</para>
/// </summary>
public sealed class TipCard : Border
{
    private readonly TextBlock _header;
    private readonly TextBlock _text;
    private readonly Button _previous;
    private readonly Button _next;

    public event Action? PreviousRequested;
    public event Action? NextRequested;
    public event Action? HideRequested;
    public event Action? AllRequested;
    public event Action? CloseRequested;

    /// <summary>The tip on the card, or null while it is closed.</summary>
    public Tip? Current { get; private set; }

    public bool IsOpen => Current is not null;

    public TipCard()
    {
        // A fixed width, so stepping through tips of different lengths does not make the card jump about.
        Width = 380;
        Padding = new Thickness(14, 8, 8, 10);
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        Visibility = Visibility.Collapsed;
        Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.35, Color = Colors.Black };
        SetResourceReference(BackgroundProperty, "PanelBg");
        SetResourceReference(BorderBrushProperty, "PanelBorder");

        _header = new TextBlock
        {
            FontSize = 11, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _header.SetResourceReference(TextBlock.ForegroundProperty, "Dim");

        var close = CardButton("✕", "Close the tip. Esc closes it too.", () => CloseRequested?.Invoke());
        close.Padding = new Thickness(7, 1, 7, 1);

        var top = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        top.Children.Add(close);
        top.Children.Add(_header);

        _text = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 4, 6, 10) };
        _text.SetResourceReference(TextBlock.ForegroundProperty, "Ink");

        _previous = CardButton("‹", "The tip before this one.", () => PreviousRequested?.Invoke());
        _next = CardButton("›", "The next tip.", () => NextRequested?.Invoke());
        foreach (var arrow in new[] { _previous, _next })
        {
            arrow.FontSize = 16;
            arrow.Padding = new Thickness(11, 0, 11, 2);
        }
        _next.Margin = new Thickness(4, 0, 0, 0);
        var all = CardButton("All tips…", "Every tip, and which topics you see.", () => AllRequested?.Invoke());
        var hide = CardButton("Hide this tip", "Never show this tip again. Help ▸ Tips brings it back.",
            () => HideRequested?.Invoke());
        hide.Margin = new Thickness(6, 0, 0, 0);

        var buttons = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(_previous, Dock.Left);
        DockPanel.SetDock(_next, Dock.Left);
        DockPanel.SetDock(hide, Dock.Right);
        DockPanel.SetDock(all, Dock.Right);
        buttons.Children.Add(_previous);
        buttons.Children.Add(_next);
        buttons.Children.Add(hide);
        buttons.Children.Add(all);

        var body = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        body.Children.Add(top);
        body.Children.Add(buttons);
        body.Children.Add(_text);
        Child = body;
    }

    /// <summary>Put a tip on the card and show it. <paramref name="position"/> and <paramref name="count"/> are among
    /// the tips the user has not turned off, which is the list the arrows step through.</summary>
    public void Show(Tip tip, int position, int count)
    {
        Current = tip;
        var topics = string.Join(", ", tip.Tags.Select(Tips.Label));
        _header.Text = $"TIP {position} OF {count}  ·  {topics.ToUpperInvariant()}";
        _text.Text = tip.Text;
        _previous.IsEnabled = _next.IsEnabled = count > 1;
        Visibility = Visibility.Visible;
    }

    public void Close()
    {
        Current = null;
        Visibility = Visibility.Collapsed;
    }

    private static Button CardButton(string label, string tip, Action click)
    {
        var button = new Button
        {
            Content = label, Padding = new Thickness(9, 2, 9, 2), FontSize = 12, Focusable = false, ToolTip = tip,
        };
        button.Click += (_, _) => click();
        return button;
    }
}

/// <summary>
/// Help ▸ Tips (#39): every tip in the order the card shows them, with a switch for each topic and for each tip.
/// The other half of the controls, when and whether tips appear, is in Settings ▸ Tips; this window is about which.
///
/// <para>Changes apply as they are made, like Settings, and go back to the main window through the callback, which
/// saves them and moves the card on if it was showing a tip that has just been turned off. Reading a tip here does
/// not count as having seen it: the list shows every tip at once, and treating that as seen would end startup tips
/// for anybody who opened it.</para>
/// </summary>
public sealed class TipHubWindow : Window
{
    private readonly IReadOnlyList<Tip> _tips;
    private readonly TipSettings _settings;
    private readonly Action _changed;
    private readonly List<Row> _rows = [];

    private sealed record Row(Tip Tip, FrameworkElement Element, CheckBox Box, TextBlock Note);

    public TipHubWindow(IReadOnlyList<Tip> tips, TipSettings settings, Action changed)
    {
        _tips = tips;
        _settings = settings;
        _changed = changed;

        Title = "Tips";
        Width = 640;
        Height = 700;
        MinWidth = 460;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = ThemeManager.WindowBg;

        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new TextBlock
        {
            Text = "Every tip, in the order they appear. Untick a topic or a tip to stop it appearing.",
            Foreground = ThemeManager.Ink, TextWrapping = TextWrapping.Wrap,
        });
        if (!settings.Enabled)
            head.Children.Add(new TextBlock
            {
                Text = "Tips are off. Turn them on in Settings ▸ Tips.",
                Foreground = ThemeManager.Warn, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            });
        head.Children.Add(new TextBlock
        {
            Text = "TOPICS", Foreground = ThemeManager.Dim, FontSize = 11, FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 14, 0, 4),
        });
        var topics = new WrapPanel();
        foreach (var tag in Enum.GetValues<TipTag>())
        {
            var count = tips.Count(t => t.Tags.Contains(tag));
            if (count == 0) continue;
            var box = new CheckBox
            {
                Content = $"{Tips.Label(tag)} ({count})", IsChecked = !settings.IsTagHidden(tag),
                Foreground = ThemeManager.Ink, Margin = new Thickness(0, 0, 16, 6),
            };
            var t = tag;
            box.Click += (_, _) =>
            {
                _settings.SetTagHidden(t, box.IsChecked != true);
                Refresh();
                _changed();
            };
            topics.Children.Add(box);
        }
        head.Children.Add(topics);
        head.Children.Add(new TextBlock
        {
            Text = "TIPS", Foreground = ThemeManager.Dim, FontSize = 11, FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 10, 0, 0),
        });

        var list = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        foreach (var tip in tips) list.Children.Add(BuildRow(tip));

        var startOver = new Button
        {
            Content = "Start over", Padding = new Thickness(14, 4, 14, 4),
            ToolTip = "Mark every tip unseen, so tips begin again from the first.",
        };
        startOver.Click += (_, _) =>
        {
            _settings.Seen.Clear();
            _settings.Last = null;
            Refresh();
            _changed();
        };
        var close = new Button { Content = "Close", Padding = new Thickness(20, 4, 20, 4), IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();

        var foot = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(startOver, Dock.Left);
        DockPanel.SetDock(close, Dock.Right);
        foot.Children.Add(startOver);
        foot.Children.Add(close);

        // A DockPanel so the list scrolls between a header and buttons that stay put (CONVENTIONS).
        var root = new DockPanel { Margin = new Thickness(20, 16, 20, 14) };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(head);
        root.Children.Add(foot);
        root.Children.Add(new ScrollViewer
        {
            Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        Content = root;

        Refresh();
    }

    private FrameworkElement BuildRow(Tip tip)
    {
        var box = new CheckBox { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 8, 0) };
        box.Click += (_, _) =>
        {
            _settings.SetTipHidden(tip.Id, box.IsChecked != true);
            Refresh();
            _changed();
        };

        var text = new TextBlock { Text = tip.Text, Foreground = ThemeManager.Ink, TextWrapping = TextWrapping.Wrap };
        var note = new TextBlock
        {
            Foreground = ThemeManager.Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
        };
        var words = new StackPanel();
        words.Children.Add(text);
        words.Children.Add(note);

        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(box, Dock.Left);
        row.Children.Add(box);
        row.Children.Add(words);
        _rows.Add(new Row(tip, row, box, note));
        return row;
    }

    /// <summary>Bring every row in line with the settings: its tick, whether a topic is hiding it, and whether it has
    /// been seen.</summary>
    private void Refresh()
    {
        foreach (var row in _rows)
        {
            var hiddenBy = row.Tip.Tags.Where(_settings.IsTagHidden).Select(Tips.Label).ToList();
            var byTopic = hiddenBy.Count > 0;
            row.Box.IsChecked = !_settings.IsTipHidden(row.Tip.Id);
            row.Box.IsEnabled = !byTopic;
            row.Element.Opacity = byTopic || _settings.IsTipHidden(row.Tip.Id) ? 0.5 : 1;

            var parts = new List<string> { string.Join(", ", row.Tip.Tags.Select(Tips.Label)) };
            if (byTopic) parts.Add("hidden with " + string.Join(" and ", hiddenBy));
            else if (!_settings.Seen.Contains(row.Tip.Id)) parts.Add("not seen yet");
            row.Note.Text = string.Join("  ·  ", parts);
        }
    }
}
