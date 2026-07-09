using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ostraplan.App;

/// <summary>A small stepper dialog for a loose floor item's stacked quantity: type or −/+ a value clamped to
/// [1, stack limit]. Mirrors the add-picker's quantity control, standalone.</summary>
public sealed class LooseQuantityDialog : Window
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;
    private static Brush FieldBg => ThemeManager.FieldBg;
    private static Brush PanelBorder => ThemeManager.PanelBorder;

    private readonly TextBox _qty = new();
    private readonly Button _minus = new();
    private readonly Button _plus = new();
    private readonly int _max;
    private bool _clamping;

    /// <summary>The chosen quantity, clamped to [1, max].</summary>
    public int Quantity => Math.Clamp(int.TryParse(_qty.Text, out var n) ? n : 1, 1, _max);

    public LooseQuantityDialog(string friendly, int current, int max)
    {
        _max = Math.Max(1, max);

        Title = "Change quantity";
        Width = 340;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = ThemeManager.WindowBg;

        var body = new StackPanel { Margin = new Thickness(18) };
        body.Children.Add(new TextBlock { Text = friendly, Foreground = Ink, FontSize = 14, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock
        {
            Text = $"Stacks up to {_max} per tile.", Foreground = Dim, FontSize = 12, Margin = new Thickness(0, 2, 0, 10),
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        row.Children.Add(new TextBlock { Text = "Quantity", Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });

        _minus.Content = "−";
        Stepper(_minus);
        _minus.Click += (_, _) => SetQty(Quantity - 1);
        row.Children.Add(_minus);

        _qty.Width = 60;
        _qty.Background = FieldBg;
        _qty.Foreground = Ink;
        _qty.BorderBrush = PanelBorder;
        _qty.BorderThickness = new Thickness(1);
        _qty.Padding = new Thickness(4, 2, 4, 2);
        _qty.Margin = new Thickness(4, 0, 4, 0);
        _qty.HorizontalContentAlignment = HorizontalAlignment.Center;
        _qty.VerticalContentAlignment = VerticalAlignment.Center;
        _qty.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        _qty.TextChanged += (_, _) => OnTyped();
        row.Children.Add(_qty);

        _plus.Content = "+";
        Stepper(_plus);
        _plus.Click += (_, _) => SetQty(Quantity + 1);
        row.Children.Add(_plus);
        body.Children.Add(row);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = "OK", Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);

        Content = body;
        SetQty(current);
        Loaded += (_, _) => { _qty.Focus(); _qty.SelectAll(); };
    }

    private static void Stepper(Button b)
    {
        b.Width = 26;
        b.MinWidth = 26;
        b.Padding = new Thickness(0);
        b.VerticalContentAlignment = VerticalAlignment.Center;
        b.FontWeight = FontWeights.Bold;
    }

    private void OnTyped()
    {
        if (_clamping || _qty.Text.Length == 0) return;
        var clamped = Math.Clamp(int.TryParse(_qty.Text, out var n) ? n : 1, 1, _max);
        if (clamped.ToString() != _qty.Text) SetQty(clamped);
    }

    private void SetQty(int value)
    {
        var v = Math.Clamp(value, 1, _max);
        _clamping = true;
        _qty.Text = v.ToString();
        _qty.CaretIndex = _qty.Text.Length;
        _clamping = false;
        _minus.IsEnabled = v > 1;
        _plus.IsEnabled = v < _max;
    }
}
