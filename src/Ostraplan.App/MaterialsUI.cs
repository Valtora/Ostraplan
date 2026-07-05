using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// The bill of materials: how many of each buildable part the design uses (one install kit each),
/// grouped by build tab. Non-buildable structure — raw hull, fixed systems, the primary airlock —
/// is tallied but not listed, since the player can't build it. "Copy list" puts a plain-text bill
/// on the clipboard.
/// </summary>
public sealed class MaterialsReportWindow : Window
{
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0xD8, 0xDD, 0xE4));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xAF));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x6E, 0xB6, 0xFF));

    private readonly Bom _bom;
    private readonly string _scope;

    public MaterialsReportWindow(Bom bom, string scope)
    {
        _bom = bom;
        _scope = scope;

        Title = "Bill of Materials";
        Width = 460; Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x23, 0x26, 0x2C));

        var body = new StackPanel { Margin = new Thickness(18) };

        body.Children.Add(new TextBlock { Text = "BILL OF MATERIALS", Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11 });
        body.Children.Add(new TextBlock
        {
            Text = $"{bom.BuildableCount} part{Plural(bom.BuildableCount)} · {bom.DistinctParts} type{Plural(bom.DistinctParts)}",
            Foreground = Accent, FontSize = 26, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 2, 0, 2),
        });
        body.Children.Add(new TextBlock
        {
            Text = scope + " · each part is one install kit of its own uninstalled form",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        // parts grouped by build tab, in tab order
        if (bom.Lines.Count == 0)
            body.Children.Add(new TextBlock { Text = "Nothing buildable placed yet.", Foreground = Dim, Margin = new Thickness(0, 4, 0, 4) });
        else
            foreach (var cat in Catalog.Categories)
            {
                var rows = bom.Lines.Where(l => l.Category == cat).ToList();
                if (rows.Count == 0) continue;
                body.Children.Add(Header($"{cat} — {rows.Sum(r => r.Count)}"));
                foreach (var line in rows) body.Children.Add(Row(line.Friendly, $"×{line.Count}"));
            }

        if (bom.NonBuildableCount > 0)
            body.Children.Add(new TextBlock
            {
                Text = $"\n{bom.NonBuildableCount} placed part{Plural(bom.NonBuildableCount)} (raw hull, fixed systems, "
                     + "the primary airlock) have no build recipe and aren't listed.",
                Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
            });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var copy = new Button { Content = "Copy list", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => CopyToClipboard();
        var close = new Button { Content = "Close", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        close.Click += (_, _) => Close();
        buttons.Children.Add(copy);
        buttons.Children.Add(close);
        body.Children.Add(buttons);

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body };
    }

    private void CopyToClipboard()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Bill of materials ({_scope})");
        sb.AppendLine($"{_bom.BuildableCount} parts, {_bom.DistinctParts} types");
        sb.AppendLine();
        foreach (var cat in Catalog.Categories)
        {
            var rows = _bom.Lines.Where(l => l.Category == cat).ToList();
            if (rows.Count == 0) continue;
            sb.AppendLine($"{cat}");
            foreach (var line in rows) sb.AppendLine($"  {line.Count,4}x  {line.Friendly}");
        }
        if (_bom.NonBuildableCount > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{_bom.NonBuildableCount} non-buildable parts (no build recipe).");
        }
        try { Clipboard.SetText(sb.ToString()); } catch { /* clipboard may be locked by another app */ }
    }

    private static string Plural(int n) => n == 1 ? "" : "s";

    private static TextBlock Header(string text) => new()
    {
        Text = text, Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11, Margin = new Thickness(0, 12, 0, 4),
    };

    private static UIElement Row(string left, string right)
    {
        var dp = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var r = new TextBlock { Text = right, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(r, Dock.Right);
        dp.Children.Add(r);
        dp.Children.Add(new TextBlock { Text = left, Foreground = Ink, TextWrapping = TextWrapping.Wrap });
        return dp;
    }
}
