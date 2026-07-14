using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// Edits the ship's in-game identity — the flavor fields the game shows at the transponder, comms and broker
/// listings (in-game name, make, model, year, designation, description). These live on the design's
/// <see cref="OplanMeta"/>, so they persist in the <c>.oplan</c> and pre-fill the export dialog rather than being
/// re-typed every export. Nothing here changes the layout, rooms or rating; it is pure metadata.
/// </summary>
public sealed class ShipInfoDialog : Window
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;
    private static Brush FieldBg => ThemeManager.FieldBg;

    private readonly TextBox _publicName, _make, _model, _year, _designation, _description;

    public string PublicName => _publicName.Text.Trim();
    public string Make => _make.Text.Trim();
    public string Model => _model.Text.Trim();
    public string Year => _year.Text.Trim();
    public string Designation => _designation.Text.Trim();
    public string Description => _description.Text.Trim();

    public ShipInfoDialog(OplanMeta meta)
    {
        Title = "Ship Info";
        Width = 480;
        MaxHeight = 720;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = ThemeManager.WindowBg;

        var body = new StackPanel { Margin = new Thickness(18) };

        body.Children.Add(new TextBlock
        {
            Text = $"In-game identity for “{meta.Name}”.",
            Foreground = Ink, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2),
        });
        body.Children.Add(new TextBlock
        {
            Text = "Saved with the design and used to pre-fill Export. It doesn't affect the layout, rooms or rating.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        _publicName = Field(body, "In-game name (optional)", meta.PublicName);
        body.Children.Add(new TextBlock
        {
            Text = "Leave blank to use the design name (or, when replacing a ship, the game's usual varied names). " +
                   "Type a name to pin it — it shows at the transponder, comms and broker listings.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
        });

        _make = Field(body, "Make", meta.Make);
        _model = Field(body, "Model", meta.Model);
        _year = Field(body, "Year", meta.Year);
        _designation = Field(body, "Designation (class/role, e.g. \"Salvage Tug\")", meta.Designation);
        _description = Field(body, "Description (optional)", meta.Description, multiline: true);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = "OK", Padding = new Thickness(20, 4, 20, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);

        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    /// <summary>Copy the entered identity onto a meta record (the caller marks the document dirty).</summary>
    public void ApplyTo(OplanMeta meta)
    {
        meta.PublicName = PublicName;
        meta.Make = Make;
        meta.Model = Model;
        meta.Year = Year;
        meta.Designation = Designation;
        meta.Description = Description;
    }

    private static TextBox Field(Panel parent, string label, string value, bool multiline = false)
    {
        parent.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11, Margin = new Thickness(0, 10, 0, 3) });
        var box = new TextBox
        {
            Text = value,
            Foreground = Ink,
            Background = FieldBg,
            BorderBrush = ThemeManager.PanelBorder,
            Padding = new Thickness(5, 3, 5, 3),
            CaretBrush = Ink,
        };
        if (multiline)
        {
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.Height = 64;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        parent.Children.Add(box);
        return box;
    }
}
