using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>A ship picked as the starting point for a retrofit: its display name and the bill for its whole
/// layout. The host produces it (only the host knows about templates, saves and designs); the report only nets
/// against it.</summary>
public sealed record RetrofitPick(string Name, Bom Bom);

/// <summary>
/// The bill of materials, in either of two modes.
///
/// <para><b>From scratch</b> (the default): how many of each buildable part the design uses, one install kit
/// each, grouped by build tab. Non-buildable structure — raw hull, fixed systems, the primary airlock — is
/// tallied but not listed, since the player can't build it.</para>
///
/// <para><b>Retrofit</b>: the same bill netted against a ship you already have, so the figures become what the
/// conversion costs rather than what the design costs. Picking a starting ship switches the list to a diff, with
/// the kits to obtain and the kits the job hands back on each line. It is always the whole design that is netted:
/// comparing a selection against a whole ship would answer nothing.</para>
///
/// <para>"Copy list" puts the current mode's bill on the clipboard as plain text.</para>
/// </summary>
public sealed class MaterialsReportWindow : Window
{
    private static Brush Ink => ThemeManager.Ink;
    private static Brush Dim => ThemeManager.Dim;
    private static Brush Accent => ThemeManager.Accent;
    private static Brush Good => ThemeManager.Good;

    private readonly Bom _bom;
    private readonly string _scope;
    private readonly Bom _wholeShip;
    private readonly Func<Window, Task<RetrofitPick?>>? _pickStartingShip;
    private readonly ContentControl _host = new();

    /// <summary>The retrofit currently being shown, or null in from-scratch mode.</summary>
    private RetrofitBom? _retrofit;

    public MaterialsReportWindow(Bom bom, string scope, Bom wholeShip,
        Func<Window, Task<RetrofitPick?>>? pickStartingShip = null)
    {
        _bom = bom;
        _scope = scope;
        _wholeShip = wholeShip;
        _pickStartingShip = pickStartingShip;

        Title = "Bill of Materials";
        Width = 500; Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _host };
        Rebuild();
    }

    /// <summary>Build the body for whichever mode is current. Cheap enough to redo whole: the bills are already
    /// computed, so this only lays out rows.</summary>
    private void Rebuild()
    {
        var body = new StackPanel { Margin = new Thickness(18) };
        if (_retrofit is { } r) BuildRetrofit(body, r);
        else BuildFromScratch(body);
        AddButtons(body);
        _host.Content = body;
    }

    // ---- from scratch ----

    private void BuildFromScratch(Panel body)
    {
        body.Children.Add(new TextBlock { Text = "BILL OF MATERIALS", Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11 });
        body.Children.Add(new TextBlock
        {
            Text = $"{_bom.BuildableCount} part{Plural(_bom.BuildableCount)} · {_bom.DistinctParts} type{Plural(_bom.DistinctParts)}",
            Foreground = Accent, FontSize = 26, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 2, 0, 2),
        });
        body.Children.Add(new TextBlock
        {
            Text = _scope + " · each part is one install kit of its own uninstalled form",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        AddRetrofitBar(body);

        // parts grouped by build tab, in tab order
        if (_bom.Lines.Count == 0)
            body.Children.Add(new TextBlock { Text = "Nothing buildable placed yet.", Foreground = Dim, Margin = new Thickness(0, 4, 0, 4) });
        else
            foreach (var cat in Catalog.Categories)
            {
                var rows = _bom.Lines.Where(l => l.Category == cat).ToList();
                if (rows.Count == 0) continue;
                body.Children.Add(Header($"{cat} — {rows.Sum(r => r.Count)}"));
                foreach (var line in rows) body.Children.Add(Row(line.Friendly, $"×{line.Count}", Dim));
            }

        if (_bom.NonBuildableCount > 0)
            body.Children.Add(new TextBlock
            {
                Text = $"\n{_bom.NonBuildableCount} placed part{Plural(_bom.NonBuildableCount)} (raw hull, fixed systems, "
                     + "the primary airlock) have no build recipe and aren't listed.",
                Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
            });
    }

    /// <summary>The invitation to switch modes, shown above the from-scratch list. Absent when the host has no
    /// way to read a ship in (no game install), because the button would only ever fail.</summary>
    private void AddRetrofitBar(Panel body)
    {
        if (_pickStartingShip is null) return;

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        var pick = new Button { Content = "Retrofit from…", Padding = new Thickness(12, 3, 12, 3) };
        pick.Click += async (_, _) => await PickStartingShip();
        bar.Children.Add(pick);
        body.Children.Add(bar);
        body.Children.Add(new TextBlock
        {
            Text = "Net this bill against a ship you already have — a design, a ship template, or your ship in a "
                 + "save — to see what the conversion costs instead of what the design costs.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10),
        });
    }

    // ---- retrofit ----

    private void BuildRetrofit(Panel body, RetrofitBom r)
    {
        body.Children.Add(new TextBlock { Text = "RETROFIT BILL OF MATERIALS", Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11 });
        body.Children.Add(new TextBlock
        {
            Text = r.NoChange
                ? "nothing to obtain"
                : $"+{r.NeededCount} to obtain · −{r.RecoveredCount} recovered",
            Foreground = Accent, FontSize = 26, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 2, 0, 2),
        });
        body.Children.Add(new TextBlock
        {
            Text = $"turning “{r.FromShip}” into this design · whole ship · each part is one install kit of its "
                 + "own uninstalled form",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var change = new Button { Content = "Change ship…", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0) };
        change.Click += async (_, _) => await PickStartingShip();
        var clear = new Button { Content = "Back to from-scratch", Padding = new Thickness(12, 3, 12, 3) };
        clear.Click += (_, _) => { _retrofit = null; Rebuild(); };
        bar.Children.Add(change);
        bar.Children.Add(clear);
        body.Children.Add(bar);

        if (r.NoChange)
            body.Children.Add(new TextBlock
            {
                Text = "The ship already carries exactly the parts this design calls for. Anything that moved is "
                     + "labour, not material: uninstalling a part yields the same kit re-installing it consumes.",
                Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
            });

        foreach (var cat in Catalog.Categories)
        {
            var rows = r.Lines.Where(l => l.Category == cat).ToList();
            if (rows.Count == 0) continue;
            var need = rows.Sum(l => l.Needed);
            var back = rows.Sum(l => l.Recovered);
            body.Children.Add(Header(need == 0 && back == 0 ? $"{cat} — unchanged" : $"{cat} — +{need} / −{back}"));
            foreach (var line in rows) body.Children.Add(RetrofitRow(line));
        }

        var nonBuildableDelta = r.NonBuildableTo - r.NonBuildableFrom;
        if (r.NonBuildableFrom > 0 || r.NonBuildableTo > 0)
            body.Children.Add(new TextBlock
            {
                Text = $"\nNon-buildable structure (raw hull, fixed systems, the primary airlock): "
                     + $"{r.NonBuildableFrom} on the ship, {r.NonBuildableTo} in the design"
                     + (nonBuildableDelta == 0
                         ? ". Unchanged."
                         : $" — {Math.Abs(nonBuildableDelta)} {(nonBuildableDelta > 0 ? "more" : "fewer")}. "
                           + "These have no build recipe, so the difference is not something you can buy: it needs "
                           + "a hull that already has it, or a shipyard.")
                     + " They are not listed above.",
                Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
            });

        body.Children.Add(new TextBlock
        {
            Text = "\nMaterials only. A part that just moves costs no kit but still costs the work to uninstall "
                 + "and re-install it, and a recovered kit assumes the part comes off intact.",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        });
    }

    private async Task PickStartingShip()
    {
        if (_pickStartingShip is null) return;
        Mouse.OverrideCursor = Cursors.Wait;
        RetrofitPick? pick;
        try { pick = await _pickStartingShip(this); }
        finally { Mouse.OverrideCursor = null; }
        if (pick is null) return;   // cancelled, or already reported

        _retrofit = BillOfMaterials.Retrofit(pick.Bom, _wholeShip, pick.Name);
        Rebuild();
    }

    // ---- shared ----

    private void AddButtons(Panel body)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var copy = new Button { Content = "Copy list", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => CopyToClipboard();
        var close = new Button { Content = "Close", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        close.Click += (_, _) => Close();
        buttons.Children.Add(copy);
        buttons.Children.Add(close);
        body.Children.Add(buttons);
    }

    private void CopyToClipboard()
    {
        var sb = new StringBuilder();
        if (_retrofit is { } r) WriteRetrofit(sb, r);
        else WriteFromScratch(sb);
        try { Clipboard.SetText(sb.ToString()); } catch { /* clipboard may be locked by another app */ }
    }

    private void WriteFromScratch(StringBuilder sb)
    {
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
    }

    /// <summary>The retrofit bill as a diff: a leading sign per line, so it reads the way it looks on screen and
    /// pastes into an issue or a note without losing which way each figure points.</summary>
    private static void WriteRetrofit(StringBuilder sb, RetrofitBom r)
    {
        sb.AppendLine($"Retrofit bill of materials: \"{r.FromShip}\" -> this design (whole ship)");
        sb.AppendLine($"{r.NeededCount} kits to obtain, {r.RecoveredCount} recovered");
        sb.AppendLine();
        foreach (var cat in Catalog.Categories)
        {
            var rows = r.Lines.Where(l => l.Category == cat).ToList();
            if (rows.Count == 0) continue;
            sb.AppendLine($"{cat}");
            foreach (var line in rows)
            {
                var sign = line.Delta > 0 ? '+' : line.Delta < 0 ? '-' : ' ';
                var amount = line.Delta == 0 ? "" : Math.Abs(line.Delta).ToString();
                sb.AppendLine($"  {sign}{amount,-4} {line.Friendly}  ({line.From} -> {line.To})");
            }
        }
        if (r.NonBuildableFrom > 0 || r.NonBuildableTo > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Non-buildable structure: {r.NonBuildableFrom} on the ship, {r.NonBuildableTo} in the design.");
        }
    }

    private static string Plural(int n) => n == 1 ? "" : "s";

    private static TextBlock Header(string text) => new()
    {
        Text = text, Foreground = Dim, FontWeight = FontWeights.Bold, FontSize = 11, Margin = new Thickness(0, 12, 0, 4),
    };

    /// <summary>One diff line: the signed kit count, then the part, then the before/after counts that explain it.
    /// An unchanged line stays in the list, dimmed, so the bill reads as the whole ship rather than only its
    /// changed half.</summary>
    private static UIElement RetrofitRow(RetrofitLine line)
    {
        var (mark, brush) = line.Delta > 0 ? ($"+{line.Delta}", Accent)
            : line.Delta < 0 ? ($"−{-line.Delta}", Good)
            : ("=", Dim);

        var dp = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };

        var counts = new TextBlock
        {
            Text = $"{line.From} → {line.To}", Foreground = Dim, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 56, TextAlignment = TextAlignment.Right,
        };
        DockPanel.SetDock(counts, Dock.Right);
        dp.Children.Add(counts);

        var sign = new TextBlock
        {
            Text = mark, Foreground = brush, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 34,
        };
        DockPanel.SetDock(sign, Dock.Left);
        dp.Children.Add(sign);

        dp.Children.Add(new TextBlock
        {
            Text = line.Friendly, Foreground = line.Unchanged ? Dim : Ink, TextWrapping = TextWrapping.Wrap,
        });
        return dp;
    }

    private static UIElement Row(string left, string right, Brush rightBrush)
    {
        var dp = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var r = new TextBlock { Text = right, Foreground = rightBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(r, Dock.Right);
        dp.Children.Add(r);
        dp.Children.Add(new TextBlock { Text = left, Foreground = Ink, TextWrapping = TextWrapping.Wrap });
        return dp;
    }
}
