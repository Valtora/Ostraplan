using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// A themed modal editor for a zone's non-tile fields — name, type (Haul/Barter/Forbid as independent toggles,
/// matching the in-game editor), target role, colour, and an Advanced group for content zones (encounter
/// triggers, owner/target person-specs). Hand-built like the other dialogs (no XAML); colour is a preset swatch
/// grid plus a hex box (WPF ships no colour picker and the app pulls no NuGet packages). Result is null on cancel.
/// </summary>
public sealed class ZoneEditorDialog : Window
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;

    /// <summary>Player-facing target roles (label → the game's PersonSpec). A content-specific target is entered
    /// in the Advanced "Target override" box instead.</summary>
    private static readonly (string Label, string Spec)[] Roles =
    [
        ("Captain and crew", "ZoneCaptainAndCrew"),
        ("Crew only", "ZoneCrew"),
        ("Captain only", "ZoneCaptain"),
    ];

    /// <summary>A spread of distinct overlay tints for quick picking (also used to colour new zones by index).</summary>
    public static readonly ZoneColor[] Presets =
    [
        new(0.85, 0.24, 0.24, 1), new(0.90, 0.55, 0.15, 1), new(0.94, 0.78, 0.13, 1),
        new(0.29, 0.73, 0.36, 1), new(0.16, 0.73, 0.62, 1), new(0.29, 0.56, 0.89, 1),
        new(0.55, 0.36, 0.90, 1), new(0.90, 0.35, 0.68, 1), new(0.60, 0.64, 0.69, 1),
    ];

    private readonly TextBox _name;
    private readonly CheckBox _haul, _barter, _forbid, _trigger, _triggerOwner;
    private readonly ComboBox _role;
    private readonly TextBox _owner, _target, _category, _hex;
    private readonly Border _preview;
    private ZoneColor _color;

    public ZoneMeta? Result { get; private set; }

    public ZoneEditorDialog(Window owner, string title, ZoneMeta initial)
    {
        Owner = owner;
        Title = title;
        Width = 390;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;
        ResizeMode = ResizeMode.NoResize;

        _color = initial.Color;

        var root = new StackPanel { Margin = new Thickness(16) };

        root.Children.Add(Header("NAME"));
        _name = Field(initial.Name);
        root.Children.Add(_name);

        root.Children.Add(Header("TYPE"));
        _haul = Check("Haul (stockpile)", initial.TileConds.Contains(ShipZone.CondHaul));
        _barter = Check("Barter", initial.TileConds.Contains(ShipZone.CondBarter));
        _forbid = Check("Forbid (no-go)", initial.TileConds.Contains(ShipZone.CondForbid));
        var types = new StackPanel();
        types.Children.Add(_haul);
        types.Children.Add(_barter);
        types.Children.Add(_forbid);
        root.Children.Add(types);

        root.Children.Add(Header("APPLIES TO"));
        _role = new ComboBox { Margin = new Thickness(0, 0, 0, 2) };
        foreach (var (label, _) in Roles) _role.Items.Add(new ComboBoxItem { Content = label });
        _role.SelectedIndex = Math.Max(0, Array.FindIndex(Roles, r => r.Spec == initial.TargetPSpec));
        root.Children.Add(_role);

        root.Children.Add(Header("COLOUR"));
        var swatches = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var preset in Presets)
        {
            var btn = new Button
            {
                Width = 26, Height = 22, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(0),
                Background = SolidOf(preset), BorderBrush = ThemeManager.PanelBorder, BorderThickness = new Thickness(1),
                Tag = preset, Cursor = System.Windows.Input.Cursors.Hand,
            };
            btn.Click += (_, _) => SetColor((ZoneColor)btn.Tag!);
            swatches.Children.Add(btn);
        }
        root.Children.Add(swatches);
        var colorRow = new StackPanel { Orientation = Orientation.Horizontal };
        _preview = new Border { Width = 40, Height = 24, Background = SolidOf(_color), BorderBrush = ThemeManager.PanelBorder, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0) };
        _hex = Field(ToHex(_color));
        _hex.Width = 100;
        _hex.LostFocus += (_, _) => ApplyHex();
        colorRow.Children.Add(_preview);
        colorRow.Children.Add(new TextBlock { Text = "#RRGGBB", Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        colorRow.Children.Add(_hex);
        root.Children.Add(colorRow);

        // ---- Advanced (content zones): triggers + raw person-specs ----
        _owner = Field(initial.PersonSpec ?? "");
        _target = Field(initial.TargetPSpec is { } t && Array.TrueForAll(Roles, r => r.Spec != t) ? t : "");
        _category = Field(string.Join(", ", initial.CategoryConds));
        _trigger = Check("Encounter trigger (IsZoneTrigger)", initial.TileConds.Contains(ShipZone.CondTrigger));
        _triggerOwner = Check("Trigger on owner (bTriggerOnOwner)", initial.TriggerOnOwner);

        var adv = new StackPanel();
        adv.Children.Add(Hint("For station/quest content. Leave blank for a normal player zone."));
        adv.Children.Add(_trigger);
        adv.Children.Add(_triggerOwner);
        adv.Children.Add(Header("OWNER PERSONSPEC"));
        adv.Children.Add(_owner);
        adv.Children.Add(Header("TARGET OVERRIDE (PERSONSPEC)"));
        adv.Children.Add(_target);
        adv.Children.Add(Hint("Overrides “Applies to” when set (e.g. FlotillaBroker)."));
        adv.Children.Add(Header("CATEGORY CONDS (comma-separated)"));
        adv.Children.Add(_category);
        adv.Children.Add(Hint("A Trigger* name for a trigger zone, or an item filter for a stockpile."));
        var advExpander = new Expander { Header = "Advanced", Margin = new Thickness(0, 12, 0, 0), Foreground = Ink, Content = adv };
        if (_trigger.IsChecked == true || !string.IsNullOrEmpty(_owner.Text) || !string.IsNullOrEmpty(_target.Text) || _category.Text.Length > 0)
            advExpander.IsExpanded = true;
        root.Children.Add(advExpander);

        // ---- buttons ----
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var ok = new Button { Content = "OK", Padding = new Thickness(18, 4, 18, 4), IsDefault = true, Background = ThemeManager.AccentBg, Foreground = ThemeManager.AccentText };
        ok.Click += (_, _) => { Result = Build(); DialogResult = true; };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);

        Content = root;
    }

    private ZoneMeta Build()
    {
        ApplyHex();
        var conds = new List<string>();
        if (_haul.IsChecked == true) conds.Add(ShipZone.CondHaul);
        if (_barter.IsChecked == true) conds.Add(ShipZone.CondBarter);
        if (_forbid.IsChecked == true) conds.Add(ShipZone.CondForbid);
        if (_trigger.IsChecked == true) conds.Add(ShipZone.CondTrigger);

        var target = _target.Text.Trim();
        if (target.Length == 0) target = Roles[Math.Max(0, _role.SelectedIndex)].Spec;

        var category = _category.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var owner = _owner.Text.Trim();

        return new ZoneMeta(
            string.IsNullOrWhiteSpace(_name.Text) ? "zone" : _name.Text.Trim(),
            _color, conds, category,
            owner.Length == 0 ? null : owner, target,
            _triggerOwner.IsChecked == true);
    }

    private void SetColor(ZoneColor c)
    {
        _color = c;
        _preview.Background = SolidOf(c);
        _hex.Text = ToHex(c);
    }

    private void ApplyHex()
    {
        if (TryParseHex(_hex.Text, out var c)) { _color = c with { A = _color.A }; _preview.Background = SolidOf(_color); }
        else _hex.Text = ToHex(_color);   // reject: snap back to the current colour
    }

    // ---- small themed builders ----

    private static TextBlock Header(string text) => new()
    {
        Text = text, Foreground = ThemeManager.Dim, FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 3),
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text, Foreground = ThemeManager.Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2),
    };

    private static TextBox Field(string text) => new()
    {
        Text = text, Padding = new Thickness(4, 3, 4, 3), Background = ThemeManager.FieldBg, Foreground = ThemeManager.Ink,
        BorderBrush = ThemeManager.PanelBorder, BorderThickness = new Thickness(1),
    };

    private static CheckBox Check(string text, bool on) => new()
    {
        Content = text, IsChecked = on, Foreground = ThemeManager.Ink, Margin = new Thickness(0, 2, 0, 2),
    };

    // ---- colour helpers ----

    private static byte B(double v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);
    internal static SolidColorBrush SolidOf(ZoneColor c) => new(Color.FromRgb(B(c.R), B(c.G), B(c.B)));
    private static string ToHex(ZoneColor c) => $"#{B(c.R):X2}{B(c.G):X2}{B(c.B):X2}";

    private static bool TryParseHex(string s, out ZoneColor c)
    {
        c = default;
        s = s.Trim();
        if (!s.StartsWith('#')) s = "#" + s;
        try
        {
            if (ColorConverter.ConvertFromString(s) is Color col) { c = new ZoneColor(col.R / 255.0, col.G / 255.0, col.B / 255.0, 1); return true; }
        }
        catch (FormatException) { }
        return false;
    }
}
