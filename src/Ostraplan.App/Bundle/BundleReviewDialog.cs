using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Ostraplan.App.Wizard;
using Ostraplan.Core;

namespace Ostraplan.App.Bundle;

/// <summary>
/// What exporting a pack will actually do, worked out by running the real engine over every design in it, and then
/// the write itself.
///
/// <para>The same two-stage shape the export wizard's Review and Done panes have, and for the same reason:
/// anything randomised has to be pinned between the report and the write, or the report is a lie. Each ship's wear
/// seed is drawn here and handed to the writer, so the parts the report says will be damaged are the parts that
/// are.</para>
/// </summary>
public sealed class BundleReviewDialog : Window
{
    private readonly Catalog _catalog;
    private readonly DataIndex _index;
    private readonly GameEnv _env;
    private readonly AppSettings _settings;
    private readonly IReadOnlyList<RoomSpecDef> _specs;
    private readonly SpriteCache? _sprites;
    private readonly bool _register;

    private BundleOptions _options;

    private readonly StackPanel _facts, _warnings, _acks, _report;
    private readonly TextBlock _status;
    private readonly List<CheckBox> _ackBoxes = [];
    private readonly Button _commit, _close;
    private readonly ScrollViewer _scroll;

    /// <summary>The ship names the write actually put in the mod, or null when nothing was written. The editor
    /// records these in the pack so the next export can take a dropped ship back out of the kiosks.</summary>
    public IReadOnlyList<string>? Written { get; private set; }

    public BundleReviewDialog(
        Catalog catalog, DataIndex index, GameEnv env, AppSettings settings, IReadOnlyList<RoomSpecDef> specs,
        SpriteCache? sprites, BundleOptions options, bool register)
    {
        _catalog = catalog;
        _index = index;
        _env = env;
        _settings = settings;
        _specs = specs;
        _sprites = sprites;
        _options = options;
        _register = register;

        Title = "Export this mod";
        Width = 660;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeManager.WindowBg;

        var body = PaneUi.Body();
        body.Children.Add(new TextBlock
        {
            Text = "Before it is written", Foreground = PaneUi.Ink, FontSize = 15, FontWeight = FontWeights.SemiBold,
        });
        _status = PaneUi.Note(body, "Working out what this will do…");
        _facts = PaneUi.Add(body, new StackPanel { Margin = new Thickness(0, 12, 0, 0) });
        _warnings = PaneUi.Add(body, new StackPanel { Margin = new Thickness(0, 12, 0, 0) });
        _acks = PaneUi.Add(body, new StackPanel { Margin = new Thickness(0, 12, 0, 0) });
        _report = PaneUi.Add(body, new StackPanel { Margin = new Thickness(0, 4, 0, 0) });

        _scroll = new ScrollViewer
        {
            Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(22, 18, 18, 18),
        };

        _commit = new Button
        {
            Content = "Export", Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 8, 0),
            IsEnabled = false, IsDefault = true,
        };
        _commit.Click += async (_, _) => await Commit();
        _close = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), IsCancel = true };
        _close.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(18, 12, 18, 16),
            Children = { _commit, _close },
        };

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(_scroll);
        Content = root;

        Loaded += async (_, _) => await Build();
    }

    // ---- review ----

    private async Task Build()
    {
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            // The seed each ship's wear is rolled with, drawn here and kept for the write, so the report and the
            // mod agree part for part.
            _options = _options with
            {
                Ships = [.. _options.Ships.Select(s => s with
                {
                    Wear = (s.Wear ?? WearOptions.Pristine) with { Seed = Random.Shared.Next() },
                })],
            };

            var built = await BuildOffThread(_catalog, _specs, _options);
            var blocking = await ScanOffThread(_catalog, _options);

            _status.Text = $"{_options.Ships.Count} ship(s) in one mod. Nothing has been written yet.";

            foreach (var (name, parts, rooms, rating, routes) in built.Ships)
                AddFact(name, $"{parts} parts, {rooms} certified room(s), rating " +
                              $"{(string.IsNullOrEmpty(rating) ? "None" : rating)}. {routes}");

            AddFact("Mod", $"\"{_options.ModName}\" {_options.ModVersion} by " +
                           (_options.Author is { Length: > 0 } a ? a : "(no author)"));
            AddFact("Writes to", ModDir());
            AddFact("Preview art", "a ship image plus a thumbnail per certified room, for every ship in the mod");
            AddFact("Registering", _register
                ? "handed to Ostrasort right after the write"
                : "left to you (Ostraplan never edits loading_order.json)");

            foreach (var warning in built.Warnings) AddLine(_warnings, warning, ThemeManager.Warn);
            foreach (var problem in blocking) AddLine(_warnings, problem, ThemeManager.Warn);

            foreach (var ack in Acknowledgements()) AddAck(ack);

            _commit.IsEnabled = _ackBoxes.Count == 0;
        }
        catch (Exception ex)
        {
            _status.Text = "That did not work out.";
            AddLine(_warnings, ex.Message, ThemeManager.Bad);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private record BuiltShip(string Name, int Parts, int Rooms, string Rating, string Routes);

    private record BuiltBundle(IReadOnlyList<BuiltShip> Ships, IReadOnlyList<string> Warnings);

    /// <summary>Every parameter is plain data, so the lambda's closure holds nothing UI-owned and the capture guard
    /// has nothing to reject. See <see cref="ExportDriver"/>.</summary>
    private static Task<BuiltBundle> BuildOffThread(
        Catalog catalog, IReadOnlyList<RoomSpecDef> specs, BundleOptions options) =>
        Ui.OffThread(() =>
        {
            var warnings = new List<string>();
            var ships = new List<BuiltShip>();
            foreach (var ship in options.Ships)
            {
                var shipWarnings = new List<string>();
                var (built, rating, rooms) = ShipExport.Build(
                    ship.Doc, catalog, specs, ship.StrName, shipWarnings, ship.Identity, ship.Wear);
                ships.Add(new BuiltShip(ship.Name, built.AItems.Length, rooms, rating.Display, Describe(ship)));
                warnings.AddRange(shipWarnings.Select(w => $"{ship.Name}: {w}"));
            }

            if (options.Ships.Any(s => s.Routes.Derelicts.Count > 0))
                warnings.Add("Derelict fields are filled when a world is generated, so those ships reach a NEW " +
                             "GAME only. A save you already have will never grow one.");

            return new BuiltBundle(ships, warnings);
        });

    /// <summary>
    /// The design problems Ostraplan already rates as blocking, named per ship. With one design the ship went
    /// without saying; with a pack, "no docking port" has to say which hull.
    /// </summary>
    private static Task<IReadOnlyList<string>> ScanOffThread(Catalog catalog, BundleOptions options) =>
        Ui.OffThread<IReadOnlyList<string>>(() =>
            [.. options.Ships.SelectMany(ship => ProblemScan.Scan(ship.Doc, catalog)
                .Where(p => p.Severity == ProblemSeverity.Blocking)
                .Select(p => $"{ship.Name}: {p.Title}. {p.Detail}"))]);

    private static string Describe(BundleShip ship)
    {
        var d = ship.Routes;
        var parts = new List<string>();
        if (d.BrokerPools.Count > 0) parts.Add($"{d.BrokerPools.Count} kiosk(s)");
        if (d.SpecialOfferPools.Count > 0) parts.Add($"{d.SpecialOfferPools.Count} Special Offer slot(s)");
        if (d.StartingShip) parts.Add("Shipbreaker start");
        if (d.Derelicts.Count > 0) parts.Add($"{d.Derelicts.Count} derelict field(s)");
        if (ship.ReplaceTarget is { Length: > 0 } target) parts.Add($"replaces \"{target}\"");
        return parts.Count == 0
            ? "No route: the ship file goes out on its own."
            : "Obtainable via " + string.Join(", ", parts) + ".";
    }

    /// <summary>What the write will destroy, each of which the user has to tick before Export arms.</summary>
    private List<string> Acknowledgements()
    {
        var acks = new List<string>();
        var modDir = ModDir();

        if (Directory.Exists(modDir) && Directory.EnumerateFileSystemEntries(modDir).Any())
            acks.Add($"A folder named \"{Path.GetFileName(modDir)}\" already exists here. Its data files (ships, " +
                     "and any loot/lifeevents/interactions) will be replaced, and any left over from a route you " +
                     "have since taken away will be deleted. Other files in the folder are left alone.");

        if (BundleExport.OrphanedArt(modDir, _options.PreviouslyWritten ?? [], _options.Ships.Select(s => s.StrName))
            is { Count: > 0 } orphans)
            acks.Add("These ships are no longer in the mod, so their preview art will be deleted: " +
                     string.Join(", ", orphans) + ".");

        return acks;
    }

    private string ModDir() =>
        Path.Combine(_options.DestinationParent, ShipExport.SanitizeName(_options.ModName));

    // ---- commit ----

    private async Task Commit()
    {
        _commit.IsEnabled = false;
        _close.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            // Rendered here, on the UI thread, and handed over as plain PNG bytes: a canvas and its sprite atlas
            // are thread-affine, so nothing about the renderer may cross into the background write. A design that
            // is not open in a tab gets a canvas of its own, which is what lets a pack ship pictures for ships
            // nobody has on screen.
            var withArt = new List<BundleShip>(_options.Ships.Count);
            for (var i = 0; i < _options.Ships.Count; i++)
            {
                var ship = _options.Ships[i];
                _status.Text = $"Drawing preview art: {ship.Name} ({i + 1} of {_options.Ships.Count})…";
                await Dispatcher.Yield(DispatcherPriority.Background);
                withArt.Add(ship with { Preview = RenderPreview(ship.Doc) });
            }

            _status.Text = "Writing the mod…";
            await Dispatcher.Yield(DispatcherPriority.Background);

            var options = _options with { Ships = withArt };
            var result = await WriteOffThread(_catalog, _specs, options, _index);

            _settings.ExportAuthor = options.Author;
            _settings.Save();
            AuditLog.Add($"Exported the ship pack \"{options.ModName}\" ({result.Ships.Count} ships) to {result.ModDir}.");

            Written = [.. result.Ships.Select(s => s.StrName)];
            await ShowReport(options, result);
        }
        catch (Exception ex)
        {
            _status.Text = "Nothing was written.";
            AddLine(_report, ex.Message, ThemeManager.Bad);
            _close.IsEnabled = true;
            _close.Content = "Close";
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private ShipPreview? RenderPreview(ShipDocument doc)
    {
        if (_sprites is null) return null;
        var canvas = new ShipCanvas { Sprites = _sprites };
        canvas.SetDocument(doc);
        return canvas.RenderGamePreview(_specs);
    }

    private static Task<BundleResult> WriteOffThread(
        Catalog catalog, IReadOnlyList<RoomSpecDef> specs, BundleOptions options, DataIndex index) =>
        Ui.OffThread(() => BundleExport.Write(catalog, specs, options, index));

    private async Task ShowReport(BundleOptions options, BundleResult result)
    {
        _facts.Children.Clear();
        _warnings.Children.Clear();
        _acks.Children.Clear();
        _ackBoxes.Clear();
        _status.Text = $"Exported {result.Ships.Count} ship(s) as \"{options.ModName}\".";

        foreach (var ship in result.Ships)
            AddFact(ship.Name, $"{ship.PartCount} parts, {ship.RoomCount} certified room(s), rating " +
                               $"{(string.IsNullOrEmpty(ship.Rating.Display) ? "None" : ship.Rating.Display)}" +
                               (ship.PreviewCount > 0 ? $", {ship.PreviewCount} image(s)" : ""));

        if (result.RemovedArt.Count > 0)
            AddFact("Removed", "preview art for " + string.Join(", ", result.RemovedArt));
        AddFact("Written to", result.ModDir);

        foreach (var warning in result.Warnings) AddLine(_warnings, warning, ThemeManager.Warn);

        var lines = new List<string>();
        if (_register)
            lines.AddRange(await OstrasortRegistration.RunAsync(
                this, _settings, _env, options.ModName, result.TouchedLootPools));
        else if (options.DestinationParent == _env.ModsDir)
            lines.AddRange([
                "It is staged into the game's Mods folder.",
                "Register it with Ostrasort (or ModTools) before it appears in game.",
                "Ostraplan never writes loading_order.json itself.",
            ]);
        else
            lines.AddRange([
                "Copy this folder into Ostranauts_Data\\Mods.",
                "Then register it with Ostrasort (or ModTools) to spawn it in game.",
            ]);

        foreach (var line in lines) AddLine(_report, line, PaneUi.Dim);

        // The button that cancelled the review becomes the one that dismisses the report. It keeps its own Click
        // handler and gains nothing: what the export produced is read off Written, so this dialog never sets
        // DialogResult. It cannot. A second handler that set it ran after the first had already closed the window,
        // which is the one thing WPF refuses outright, and it threw at the end of an export that had otherwise
        // gone perfectly.
        _commit.Visibility = Visibility.Collapsed;
        _close.Content = "Done";
        _close.IsEnabled = true;
        _close.IsCancel = false;
        _scroll.ScrollToTop();
    }

    /// <summary>
    /// Fill the panes with representative content for the <c>--bundlesmoke</c> development render, which has no
    /// one to click through a real export. Asserts nothing and writes nothing.
    /// </summary>
    internal void RenderSample()
    {
        _status.Text = "3 ship(s) in one mod. Nothing has been written yet.";
        AddFact("Kestrel", "41 parts, 3 certified room(s), rating C. Obtainable via 1 kiosk(s).");
        AddFact("Harrier", "96 parts, 6 certified room(s), rating B. Obtainable via Shipbreaker start.");
        AddFact("Mod", "\"Working Hulls\" 1.0.0 by Valtora");
        AddFact("Writes to", ModDir());
        AddFact("Preview art", "a ship image plus a thumbnail per certified room, for every ship in the mod");
        AddFact("Registering", "handed to Ostrasort right after the write");
        AddLine(_warnings, "Harrier: No docking port. Nothing can dock with this design, and it cannot dock " +
                           "anywhere itself.", ThemeManager.Warn);
        AddAck("A folder named \"Working Hulls\" already exists here. Its data files (ships, and any " +
               "loot/lifeevents/interactions) will be replaced, and any left over from a route you have since " +
               "taken away will be deleted. Other files in the folder are left alone.");
        AddAck("These ships are no longer in the mod, so their preview art will be deleted: Barge.");
    }

    // ---- pane furniture ----

    private void AddFact(string label, string value)
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        row.Children.Add(new TextBlock
        {
            Text = label, Foreground = PaneUi.Dim, FontSize = 11, FontWeight = FontWeights.Bold,
        });
        row.Children.Add(new TextBlock
        {
            Text = value, Foreground = PaneUi.Ink, TextWrapping = TextWrapping.Wrap,
        });
        _facts.Children.Add(row);
    }

    private static void AddLine(Panel parent, string text, System.Windows.Media.Brush brush) =>
        parent.Children.Add(new TextBlock
        {
            Text = text, Foreground = brush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

    private void AddAck(string text)
    {
        var box = new CheckBox
        {
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 },
            Foreground = ThemeManager.Warn, Margin = new Thickness(0, 0, 0, 6),
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        box.Checked += (_, _) => SyncCommit();
        box.Unchecked += (_, _) => SyncCommit();
        _ackBoxes.Add(box);
        _acks.Children.Add(box);
    }

    private void SyncCommit() => _commit.IsEnabled = _ackBoxes.All(b => b.IsChecked == true);
}
