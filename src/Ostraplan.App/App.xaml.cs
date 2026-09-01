using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ostraplan.Core;

namespace Ostraplan.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Note: Velopack's hooks are handled in Program.Main (the entry point),
        // which runs before this. Nothing update-related belongs here.

        // Theme the chrome before the first window renders (Fluent ThemeMode + the app's own
        // brushes). Read the saved preference; the canvas stays dark regardless (ThemeManager).
        var settings = AppSettings.Load();
        ThemeManager.Apply(settings.Theme);

        // Hook the UI scale before any window exists, so the first one drawn is already scaled.
        UiScale.Install(settings.UiScale);

        // publish self-test: create and show a native-backed window, then exit. This is
        // what catches single-file WPF native-library load failures (the reason
        // IncludeNativeLibrariesForSelfExtract is required) — a bin\Release run can't.
        if (e.Args.Contains("--smoke"))
        {
            var w = new Window
            {
                Width = 200, Height = 120, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
            };
            w.Show();
            w.Close();
            Shutdown(0);
            return;
        }

        // preview render: the Ship Bundle editor holding a small pack, light and dark, so its layout can be
        // eyeballed without clicking through to it. Needs the install (it lists the ships a member could replace).
        if (e.Args.Contains("--bundlesmoke"))
        {
            var dir = e.Args.SkipWhile(a => a != "--bundlesmoke").Skip(1).FirstOrDefault() ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(dir);
            try { RenderBundleEditor(dir); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(dir, "bundlesmoke-error.txt"), ex.ToString()); }
            Shutdown(0);
            return;
        }

        // preview render: draw representative dialogs to PNGs (for eyeballing the modal styling), then exit.
        if (e.Args.Contains("--dlgsmoke"))
        {
            var dir = e.Args.SkipWhile(a => a != "--dlgsmoke").Skip(1).FirstOrDefault() ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(dir);

            // height: 0 renders the card as tall as the message needs, which is what a short one gets. Pass the
            // dialog's own MaxHeight instead to see what a long one does: the body scrolls inside it while the
            // header and the buttons stay put.
            void Render(string mode, DlgKind kind, string title, string body, (string, MessageDialog.Choice)[] buttons, string file, double height = 0)
            {
                ThemeManager.Apply(mode);
                var (root, _) = MessageDialog.BuildLayout(kind, title, body, buttons, _ => { });
                root.Width = 486;
                root.Measure(new Size(486, height > 0 ? height : double.PositiveInfinity));
                var tall = height > 0 ? height : root.DesiredSize.Height;
                root.Arrange(new Rect(0, 0, 486, tall));
                root.UpdateLayout();
                var bmp = new RenderTargetBitmap(486, (int)Math.Ceiling(tall), 96, 96, PixelFormats.Pbgra32);
                bmp.Render(root);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using var fs = File.Create(Path.Combine(dir, file));
                enc.Save(fs);
            }

            var cargo = "You deleted 2 container(s) that still hold 7 cargo item(s).\n" +
                        "Writing this back will permanently delete that cargo.\n\n" +
                        "•   Storage Locker (Med Kit, Wrench, Ration Bar, O2 Canister, Battery, Duct Tape, plus 2 more)\n" +
                        "•   Wall Cabinet (Screwdriver, Fuse)\n\n" +
                        "To keep it, cancel now.\n" +
                        "Empty those containers in game, then import and edit again.";
            (string, MessageDialog.Choice)[] cargoBtns = [("Delete cargo & continue", MessageDialog.Choice.Primary), ("Cancel", MessageDialog.Choice.Cancel)];
            Render("dark", DlgKind.Danger, "Cargo will be permanently deleted", cargo, cargoBtns, "dlg-danger-dark.png");
            Render("light", DlgKind.Danger, "Cargo will be permanently deleted", cargo, cargoBtns, "dlg-danger-light.png");

            var missing = "Vagabond+ uses 3 part(s) that aren't in your current game and mods data.\n" +
                          "They were left out, so this design is incomplete.\n\n" +
                          "•   ItmWaterRecycler01\n•   ItmWasteTank01\n•   ItmFilter02\n\n" +
                          "It depends on these mods.\n\n•   Ship's Water\n\n" +
                          "Install or subscribe to those mods and enable them, then reopen this design.\n" +
                          "Run Ostrasort to confirm they're subscribed, enabled, and in a working load order.\n\n" +
                          "Until then the design is read only, so saving is disabled.";
            Render("dark", DlgKind.Warning, "This design is missing mods", missing,
                [("OK", MessageDialog.Choice.Cancel)], "dlg-warning-dark.png");

            // The same warning for a design that leans on a lot of mods. A message is as long as the list it has
            // to name, so this is the case the card is capped for.
            var manyMods = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"•   Some Mod Or Other {i} [37{i:0000}00{i}]"));
            var longMissing = "Raven uses 2 part(s) that aren't in your current game and mods data.\n" +
                              "They were left out, so this design is incomplete.\n\n" +
                              "•   ItmWaterTankMedium\n•   ItmWaterTankWasteMedium\n\n" +
                              "It depends on these mods.\n\n" + manyMods + "\n\n" +
                              "To get them back: install or subscribe to those mods and enable them, then reopen this design.";
            Render("dark", DlgKind.Warning, "This design is missing mods", longMissing,
                [("OK", MessageDialog.Choice.Cancel)], "dlg-warning-scroll-dark.png", 600);

            Render("dark", DlgKind.Info, "Save changes?", "Vagabond+ has unsaved changes.",
                [("Save", MessageDialog.Choice.Primary), ("Don't save", MessageDialog.Choice.Secondary), ("Cancel", MessageDialog.Choice.Cancel)],
                "dlg-info-dark.png");

            RenderFill(dir);
            Shutdown(0);
            return;
        }

        // preview render: draw the inventory viewer (a synthesized backpack + the first real save container) to
        // PNGs for eyeballing the grid + paper-doll layout, then exit. Needs the game install.
        if (e.Args.Contains("--invsmoke"))
        {
            var dir = e.Args.SkipWhile(a => a != "--invsmoke").Skip(1).FirstOrDefault() ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(dir);
            try
            {
                var env = GameEnv.Locate(null);
                var index = DataIndex.Load(env);
                var catalog = Catalog.Build(index);
                var sprites = new SpriteCache();

                void RenderInv(string file, string def, string friendly, IReadOnlyList<CargoItem> cargo,
                    ShipDocument? doc = null, CommandStack? stack = null, Placement? root = null,
                    LooseObject? rootLoose = null)
                {
                    var win = new InventoryWindow(catalog, sprites, def, friendly, cargo, doc, stack, root, rootLoose);
                    var panel = win.PreviewContent;
                    panel.Background = ThemeManager.WindowBg;
                    const int w = 620;
                    panel.Measure(new Size(w, double.PositiveInfinity));
                    panel.Arrange(new Rect(0, 0, w, panel.DesiredSize.Height));
                    panel.UpdateLayout();
                    var h = Math.Max(1, (int)Math.Ceiling(panel.DesiredSize.Height));
                    var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(panel);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    using var fs = File.Create(Path.Combine(dir, file));
                    enc.Save(fs);
                }

                // a synthesized backpack: a 4x4 grid (trencher + a 16-round ammo stack) plus a paper-doll of pockets
                var pouch = new CargoItem("s1", "PocketPouchSmall01", "Small Pouch", true,
                    [new CargoItem("s1a", "ItmDrinkPouch01", "Drink Pouch", false, [])]) { SlotName = "pocket_pouchSm01" };
                var cargo = new List<CargoItem>
                {
                    pouch,
                    new("g1", "ItmTrencherChipotlePorkCheeseSpread", "Trencher", false, []) { GridX = 0, GridY = 0 },
                    new("g2", "ItmAmmo9mm", "9mm Ammo", false, []) { GridX = 1, GridY = 0, Stack = 16 },
                };
                RenderInv("inv-backpack.png", "ItmBackpack01", "Backpack: Pearson", cargo);
                RenderInv("inv-empty.png", "ItmBackpack01", "Backpack (empty)", []);   // an empty container still shows its grid

                // an EDITABLE backpack: the same content but with the editor affordances (+ Add item… header,
                // removable tiles) — confirms the edit UI constructs without throwing.
                var editDoc = new ShipDocument(catalog);
                var editStack = new CommandStack();
                var editBp = new Placement { DefName = "ItmBackpack01" };
                new PlaceCommand(editBp).Do(editDoc);
                new SetCargoCommand(editBp, editBp.Cargo, cargo).Do(editDoc);
                RenderInv("inv-edit.png", "ItmBackpack01", "Backpack (editing)", editBp.Cargo, editDoc, editStack, editBp);

                // a DECK item being edited: an EVA suit lying on the floor, whose whole capacity is the four
                // compartments on its paper-doll — it declares no grid at all. Confirms the loose-host editor
                // constructs, and that an item with slots and no grid still draws something usable.
                var deckDoc = new ShipDocument(catalog);
                var deckStack = new CommandStack();
                var suit = new LooseObject { DefName = "OutfitEVA01", X = 0, Y = 0 };
                new PlaceLooseCommand(suit).Do(deckDoc);
                RenderInv("inv-deck-suit.png", suit.DefName, catalog.Lookup(suit.DefName)?.Friendly ?? suit.DefName,
                    suit.Cargo, deckDoc, deckStack, rootLoose: suit);

                // rotation smoke: a tall (1×5) missile unrotated vs rotated 90°, so an aspect/squish regression is
                // obvious — a faithful rotation is a rigid turn, so the sprite's proportions must not change.
                CargoItem Rot(string def, int rot)
                {
                    var (gw, gh) = catalog.Lookup(def)?.InvSize ?? (1, 1);
                    return new CargoItem("a", def, catalog.Lookup(def)?.Friendly, false, []) { GridRot = rot, GridW = gw, GridH = gh };
                }
                RenderInv("inv-rot0.png", "ItmBackpack01", "Missile rot 0", [Rot("ItmAmmoMissile01", 0)]);
                RenderInv("inv-rot90.png", "ItmBackpack01", "Missile rot 90", [Rot("ItmAmmoMissile01", 90)]);

                // rotation-aware capacity: the 3×5 Polaris decoy launcher filled to capacity with 1×3 missiles.
                // Three stand upright and two lie flat across the band left over — an upright-only packer stops
                // at three and calls it full.
                if (catalog.Lookup("ItmShipWeaponDecoyLauncher01") is { ContainerGrid: { } dlGrid } dl
                    && catalog.Lookup("ItmAmmoDecoyMissile01") is { } decoy)
                {
                    var full = CargoEdit.Add([], null, dlGrid, decoy, CargoEdit.MaxAddable([], null, dlGrid, decoy), catalog);
                    RenderInv("inv-decoy-launcher.png", dl.DefName, dl.Friendly, full ?? []);
                }

                // the first real save container that actually holds cargo
                foreach (var save in SaveImport.ListSaves(env))
                {
                    try
                    {
                        var doc = SaveEditImport.ImportForEditing(save, catalog).Doc;
                        // prefer a container that holds a stack (to exercise the ×N-not-a-container rendering)
                        if ((doc.Placements.Where(pl => pl.Cargo.Any(c => c.IsStack)).OrderByDescending(pl => pl.Cargo.Count).FirstOrDefault()
                             ?? doc.Placements.Where(pl => pl.Cargo.Count > 0).OrderByDescending(pl => pl.Cargo.Count).FirstOrDefault()) is { } p)
                        {
                            RenderInv("inv-real.png", p.DefName, catalog.Lookup(p.DefName)?.Friendly ?? p.DefName, p.Cargo);

                            // The item info panel, on a real item off a real save — the one that has factions to
                            // show, since a synthesized item never belongs to any.
                            var subject = doc.Placements.SelectMany(pl => pl.Cargo)
                                .FirstOrDefault(c => c.Factions.Count > 0) ?? p.Cargo[0];
                            var info = new CargoInfoWindow(() => CargoInfo.For(subject, doc), _ => { });
                            info.Measure(new Size(340, double.PositiveInfinity));
                            info.Arrange(new Rect(0, 0, 340, info.DesiredSize.Height));
                            info.UpdateLayout();
                            if (info.Content is FrameworkElement content)
                            {
                                content.Measure(new Size(340, double.PositiveInfinity));
                                content.Arrange(new Rect(0, 0, 340, content.DesiredSize.Height));
                                content.UpdateLayout();
                                var ih = Math.Max(1, (int)Math.Ceiling(content.DesiredSize.Height));
                                var ibmp = new RenderTargetBitmap(340, ih, 96, 96, PixelFormats.Pbgra32);
                                ibmp.Render(content);
                                var ienc = new PngBitmapEncoder();
                                ienc.Frames.Add(BitmapFrame.Create(ibmp));
                                using var ifs = File.Create(Path.Combine(dir, "inv-item-info.png"));
                                ienc.Save(ifs);
                            }
                            break;
                        }
                    }
                    catch { /* not a player-ship save */ }
                }
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(dir, "invsmoke-error.txt"), ex.ToString()); }
            Shutdown(0);
            return;
        }

        // preview render: draw the item manifest off a real save's ship to PNGs, collapsed and expanded, so the
        // table's columns can be held against each other without driving the window. Needs the game install.
        if (e.Args.Contains("--mansmoke"))
        {
            var dir = e.Args.SkipWhile(a => a != "--mansmoke").Skip(1).FirstOrDefault() ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(dir);
            try
            {
                var env = GameEnv.Locate(null);
                var catalog = Catalog.Build(DataIndex.Load(env));

                void RenderManifest(string file, ShipDocument doc, int expand, bool byLocation = false)
                {
                    var win = new ManifestWindow(doc, new CommandStack(), _ => { });
                    if (byLocation) win.PreviewByLocation();
                    else if (expand > 0) win.PreviewOpen(expand);
                    if (win.PreviewContent is not { } panel) return;
                    panel.Background = ThemeManager.WindowBg;
                    const int w = 700, h = 820;   // the window's own default size, so this is what the user sees
                    panel.Measure(new Size(w, h));
                    panel.Arrange(new Rect(0, 0, w, h));
                    panel.UpdateLayout();
                    var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(panel);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    using var fs = File.Create(Path.Combine(dir, file));
                    enc.Save(fs);
                }

                // Find the ship first and render second. A render that throws is OUR bug and has to reach the error
                // file; folding it into the "not a player-ship save" catch hid a re-parenting crash behind a
                // half-written set of PNGs.
                ShipDocument? subject = null;
                foreach (var save in SaveImport.ListSaves(env))
                {
                    try
                    {
                        var doc = SaveEditImport.ImportForEditing(save, catalog).Doc;
                        if (ItemManifest.Build(doc).IsEmpty) continue;   // a ship carrying nothing proves nothing here
                        subject = doc;
                        break;
                    }
                    catch { /* not a player-ship save */ }
                }
                if (subject is not null)
                {
                    RenderManifest("manifest-closed.png", subject, 0);
                    RenderManifest("manifest-open.png", subject, 3);
                    // The other grouping, which is a different table rather than the same one rearranged: its
                    // columns and indentation are the thing worth holding against the by-type render.
                    RenderManifest("manifest-location.png", subject, 0, byLocation: true);
                }
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(dir, "mansmoke-error.txt"), ex.ToString()); }
            Shutdown(0);
            return;
        }

        // preview render: draw the nav console's arrange board (a console stocked with the standard set) to a PNG
        // for eyeballing the screen layout against the game's own. Needs the game install.
        if (e.Args.Contains("--navsmoke"))
        {
            var dir = e.Args.SkipWhile(a => a != "--navsmoke").Skip(1).FirstOrDefault() ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(dir);
            try
            {
                var env = GameEnv.Locate(null);
                var catalog = Catalog.Build(DataIndex.Load(env));
                var doc = new ShipDocument(catalog);
                var console = new Placement { DefName = "ItmStationNav" };
                new PlaceCommand(console).Do(doc);
                NavConsole.StockEmptyConsoles(doc, catalog);

                var win = new NavArrangeWindow(catalog, doc, new CommandStack(), console, "Nav Station");

                void Shot(string file)
                {
                    var panel = win.PreviewContent;
                    panel.Background = ThemeManager.WindowBg;
                    panel.Measure(new Size(1100, double.PositiveInfinity));
                    panel.Arrange(new Rect(0, 0, panel.DesiredSize.Width, panel.DesiredSize.Height));
                    panel.UpdateLayout();
                    var bmp = new RenderTargetBitmap(
                        Math.Max(1, (int)Math.Ceiling(panel.DesiredSize.Width)),
                        Math.Max(1, (int)Math.Ceiling(panel.DesiredSize.Height)), 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(panel);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    using var fs = File.Create(Path.Combine(dir, file));
                    enc.Save(fs);
                }

                Shot("nav-arrange.png");
                // mid-drag: flight dynamics out of the tray and held over the map, which it cannot share — the
                // panel should follow the cursor in the "will not fit" colour
                win.PreviewDrag("ItmNavModFlightDynamics", 330, 240);
                Shot("nav-arrange-drag.png");
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(dir, "navsmoke-error.txt"), ex.ToString()); }
            Shutdown(0);
            return;
        }

        // render self-test: a strip of real parts at descending condition, so the wear port can be held up against
        // the game's own rendering of the same part at the same figure. The constants behind it live in compiled
        // GPU code (see WearShader), so no data test can catch them drifting and this is the check that can.
        // Needs the game install.
        // preview render: the palette's category strip, in both themes and at three different selections. Two
        // things it is here to prove, both of which have shipped as bugs before: that the TabItem style still
        // chains to Fluent (one that does not falls back to the light Aero2 template), and that a category keeps
        // its position when another is selected.
        if (e.Args.Contains("--palsmoke"))
        {
            var dir = e.Args.SkipWhile(a => a != "--palsmoke").Skip(1).FirstOrDefault() ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(dir);
            try
            {
                // Built from markup mirroring MainWindow.xaml's, rather than by assembling controls in code:
                // the toggle style is what is under test, and a style that has fallen off the Fluent chain still
                // builds and still runs. It only looks wrong.
                //
                // This replaced a TabControl. Its wrapped headers were laid out by a TabPanel, which moves the
                // row holding the selected header down against the content, so the strip rearranged itself on
                // every click. ItemsPanel cannot fix it: Fluent's TabControl template hard-codes its TabPanel,
                // and overriding ItemsPanel with a StackPanel here produced a byte-identical render.
                string[] headers =
                    ["FAV/REC", "All", "HULL", "HVAC", "POWR", "SENS", "CTRL", "FURN", "APPS", "MISC", "ITEMS", "SPECIAL"];

                const string tabStyle = """
                    <ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                                        xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
                      <Style x:Key='PaletteTab' TargetType='ToggleButton' BasedOn='{StaticResource {x:Type ToggleButton}}'>
                        <Setter Property='Padding' Value='7,2'/>
                        <Setter Property='Margin' Value='0,0,3,3'/>
                        <Setter Property='FontSize' Value='11'/>
                        <Setter Property='MinWidth' Value='0'/>
                      </Style>
                    </ResourceDictionary>
                    """;

                void RenderStrip(string mode, int selected, string file)
                {
                    ThemeManager.Apply(mode);
                    var res = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(tabStyle);
                    var strip = new System.Windows.Controls.WrapPanel { Width = 330 };
                    for (var i = 0; i < headers.Length; i++)
                        strip.Children.Add(new System.Windows.Controls.Primitives.ToggleButton
                        {
                            Content = headers[i],
                            Style = (Style)res["PaletteTab"],
                            IsChecked = i == selected,
                        });

                    var host = new System.Windows.Controls.Border
                    {
                        Background = ThemeManager.PanelBg, Padding = new Thickness(8), Child = strip,
                    };
                    host.Measure(new Size(346, double.PositiveInfinity));
                    host.Arrange(new Rect(0, 0, 346, host.DesiredSize.Height));
                    host.UpdateLayout();
                    var h = Math.Max(1, (int)Math.Ceiling(host.DesiredSize.Height));
                    var bmp = new RenderTargetBitmap(346, h, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(host);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    using var fs = File.Create(Path.Combine(dir, file));
                    enc.Save(fs);
                }

                RenderStrip("dark", 1, "palette-strip-dark-all.png");
                RenderStrip("dark", 7, "palette-strip-dark-furn.png");
                RenderStrip("dark", 10, "palette-strip-dark-items.png");
                RenderStrip("light", 1, "palette-strip-light-all.png");
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(dir, "palsmoke-error.txt"), ex.ToString()); }
            Shutdown(0);
            return;
        }

        // preview render: draw a page of backdrops (#43) so the composited locale art can be eyeballed without
        // clicking through Settings for each of the thirty-odd of them. Needs the game install.
        if (e.Args.Contains("--bgsmoke"))
        {
            var dir = e.Args.SkipWhile(a => a != "--bgsmoke").Skip(1).FirstOrDefault() ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(dir);
            try
            {
                var env = GameEnv.Locate(null);
                var catalog = Catalog.Build(DataIndex.Load(env));
                var brushes = new BackdropBrushes(new SpriteCache());

                var samples = new List<(string Label, BackdropSettings Settings)>
                {
                    ("Default", BackdropSettings.Default),
                    ("White", BackdropSettings.Default with { Solid = "#FFFFFF" }),
                    ("Checker", BackdropSettings.Default with { Kind = BackdropKind.Checker }),
                };
                samples.AddRange(ParallaxCatalog.All(catalog).Select(l =>
                    (l.Display, BackdropSettings.Default with { Kind = BackdropKind.Locale, Locale = l.Name })));

                const int cell = 200, pad = 10, labelH = 16;
                var cols = 6;
                var rows = (samples.Count + cols - 1) / cols;
                int w = cols * (cell + pad) + pad, h = rows * (cell + pad + labelH) + pad;

                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.DimGray, null, new Rect(0, 0, w, h));
                    for (var i = 0; i < samples.Count; i++)
                    {
                        var (label, sample) = samples[i];
                        var x = pad + i % cols * (cell + pad);
                        var y = pad + i / cols * (cell + pad + labelH);
                        var visualFor = brushes.For(sample, catalog);
                        dc.DrawRectangle(visualFor.Brush, null, new Rect(x, y, cell, cell));
                        dc.DrawText(
                            new FormattedText(
                                visualFor.IsLight ? label + " (dark ink)" : label,
                                System.Globalization.CultureInfo.InvariantCulture,
                                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Brushes.White,
                                VisualTreeHelper.GetDpi(visual).PixelsPerDip),
                            new Point(x, y + cell + 2));
                    }
                }
                var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                target.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(target));
                using var bgFs = File.Create(Path.Combine(dir, "backdrops.png"));
                encoder.Save(bgFs);
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(dir, "bgsmoke-error.txt"), ex.ToString()); }
            Shutdown(0);
            return;
        }

        if (e.Args.Contains("--wearsmoke"))
        {
            var dir = e.Args.SkipWhile(a => a != "--wearsmoke").Skip(1).FirstOrDefault() ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(dir);
            try
            {
                var env = GameEnv.Locate(null);
                var catalog = Catalog.Build(DataIndex.Load(env));
                var sprites = new SpriteCache();

                // One sheet part, one plain fixture, and one that ships its own damaged texture, so all three
                // branches of the composite are on the page rather than only the flat-tint one.
                // The last two ship their own strImgDamaged, so the second-texture branch is on the page rather
                // than only the flat-tint one the walls take.
                string[] defs = ["ItmWall1x1", "ItmFloorGrate01", "ItmAtmoScrubber01", "ItmBattery02"];
                double[] conditions = [1.00, 0.85, 0.80, 0.60, 0.40, 0.20, 0.05];

                const int cell = 64, pad = 8, labelH = 18;
                var cols = conditions.Length;
                var rows = defs.Length;
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.Black, null,
                        new Rect(0, 0, cols * (cell + pad) + pad, rows * (cell + pad + labelH) + pad));
                    for (var r = 0; r < rows; r++)
                    {
                        if (catalog.Lookup(defs[r]) is not { } part) continue;
                        for (var c = 0; c < cols; c++)
                        {
                            var x = pad + c * (cell + pad);
                            var y = pad + r * (cell + pad + labelH);
                            // Each sample sits at its own world position, exactly as it would on a deck, so the
                            // strip shows the position-dependence rather than one pattern repeated.
                            var bmp = part.Item.HasSpriteSheet
                                ? sprites.WornSheetCell(part, 0, 0, conditions[c], c * 3.0, -r * 3.0, catalog)
                                : sprites.WornSprite(part, conditions[c], c * 3.0, -r * 3.0, catalog);
                            dc.DrawImage(bmp, new Rect(x, y, cell, cell));
                            dc.DrawText(
                                new FormattedText(
                                    $"{part.Friendly} {conditions[c] * 100:0}%",
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Brushes.White,
                                    VisualTreeHelper.GetDpi(visual).PixelsPerDip),
                                new Point(x, y + cell + 2));
                        }
                    }
                }
                var target = new RenderTargetBitmap(
                    cols * (cell + pad) + pad, rows * (cell + pad + labelH) + pad, 96, 96, PixelFormats.Pbgra32);
                target.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(target));
                using var wearFs = File.Create(Path.Combine(dir, "wear-strip.png"));
                encoder.Save(wearFs);
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(dir, "wearsmoke-error.txt"), ex.ToString()); }
            Shutdown(0);
            return;
        }

        // render self-test: render a real ship's room map to SVG, validate it parses as XML, and write it out
        // for eyeballing, then exit. Confirms the SVG serializer (embedded sprite layer + vector annotations)
        // produces well-formed output. Needs the game install.
        if (e.Args.Contains("--svgsmoke"))
        {
            var dir = e.Args.SkipWhile(a => a != "--svgsmoke").Skip(1).FirstOrDefault() ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(dir);
            try
            {
                var env = GameEnv.Locate(null);
                var index = DataIndex.Load(env);
                var catalog = Catalog.Build(index);
                var sprites = new SpriteCache();
                var specs = RoomCertifier.LoadSpecs(index);

                ShipDocument? doc = null;
                foreach (var save in SaveImport.ListSaves(env))
                {
                    try
                    {
                        var d = SaveEditImport.ImportForEditing(save, catalog).Doc;
                        if (d.Placements.Count > 0) { doc = d; break; }
                    }
                    catch { /* not a player-ship save */ }
                }
                doc ??= TemplateImport.LoadFile(TemplateImport.ListShipFiles(index)[0].Path, catalog).Doc;

                var canvas = new ShipCanvas { Sprites = sprites };
                canvas.SetDocument(doc);

                // render at each editing orientation (0/90/180/270) — every SVG must parse, and the raster
                // dimensions must swap at 90°/270° (the snapshot follows the plan-view rotation)
                var report = new System.Text.StringBuilder();
                (int W, int H) baseDims = (0, 0), basePlain = (0, 0);
                for (var i = 0; i < 4; i++)
                {
                    var rot = i * 90;
                    var svg = canvas.RenderRatingSnapshotSvg(specs)
                              ?? throw new InvalidOperationException("RenderRatingSnapshotSvg returned null (empty design?).");
                    var xdoc = System.Xml.Linq.XDocument.Parse(svg);   // throws if not well-formed XML
                    var root = xdoc.Root!;
                    var (w, h) = ((int)root.Attribute("width")!, (int)root.Attribute("height")!);
                    var rtb = canvas.RenderRatingSnapshot(specs)!;     // raster rating path
                    var plain = canvas.RenderSnapshot()!;              // plain PNG snapshot follows orientation too
                    if (i == 0) { baseDims = (w, h); basePlain = (plain.PixelWidth, plain.PixelHeight); }
                    var expect = rot is 90 or 270 ? (baseDims.H, baseDims.W) : baseDims;
                    var expectPlain = rot is 90 or 270 ? (basePlain.H, basePlain.W) : basePlain;
                    var ok = (w, h) == expect && rtb.PixelWidth == w && rtb.PixelHeight == h
                             && (plain.PixelWidth, plain.PixelHeight) == expectPlain;
                    report.AppendLine($"rot {rot}: svg {w}x{h}, raster {rtb.PixelWidth}x{rtb.PixelHeight}, plain {plain.PixelWidth}x{plain.PixelHeight} (expect {expect.Item1}x{expect.Item2}, plain {expectPlain.Item1}x{expectPlain.Item2}) -> {(ok ? "OK" : "MISMATCH")}");
                    if (!ok) throw new InvalidOperationException($"orientation {rot} dims wrong:\n{report}");
                    if (i == 0) File.WriteAllText(Path.Combine(dir, "room-map.svg"), svg, new System.Text.UTF8Encoding(false));
                    if (i == 1) File.WriteAllText(Path.Combine(dir, "room-map-rot90.svg"), svg, new System.Text.UTF8Encoding(false));
                    canvas.RotateView(90);
                }
                File.WriteAllText(Path.Combine(dir, "svgsmoke-ok.txt"),
                    $"parsed OK · {doc.Placements.Count} parts\n{report}");
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(dir, "svgsmoke-error.txt"), ex.ToString()); }
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                Directory.CreateDirectory(AppSettings.Dir);
                File.AppendAllText(Path.Combine(AppSettings.Dir, "error.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {args.Exception}\r\n");
                // Also drop a marker into the activity trail, so a bug report's timeline shows the crash next to
                // the actions that led to it. The full stack trace stays in error.log (folded into the report).
                AuditLog.Add($"CRASH: {args.Exception.GetType().Name}: {args.Exception.Message}");
            }
            catch { /* logging must never take the app down */ }
            Dlg.Show(args.Exception.Message, "Ostraplan - unexpected error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // Once running as the Velopack-managed install, tidy away the old
        // pre-Velopack self-install (%LOCALAPPDATA%\Programs\Ostraplan) and its
        // stale shortcuts so a dead duplicate can't be launched. No-op for a dev
        // or portable copy, and once-only.
        LegacyInstall.Cleanup();

        new MainWindow().Show();
    }

    /// <summary>
    /// Preview render of the fill editor, light and dark, on a synthetic canister and a synthetic torch tank —
    /// so the shared-budget gauge and the two sections can be eyeballed without a game install or a ship to open.
    /// Part of <c>--dlgsmoke</c>; like the rest of that flag it asserts nothing.
    /// </summary>
    /// <summary>
    /// Draw the Ship Bundle editor holding a small pack: two designs that can be exported and one that cannot,
    /// so the refusal row is in the picture too. Light and dark, both at the window's own size.
    ///
    /// <para>Part of <c>--bundlesmoke</c>; like every other preview render it asserts nothing. It exists because
    /// the editor is a window with a lot in it, and eyeballing a PNG beats clicking through five dialogs to reach
    /// a layout change.</para>
    /// </summary>
    private static void RenderBundleEditor(string dir)
    {
        var env = GameEnv.Locate(null);
        var index = DataIndex.Load(env);
        var catalog = Catalog.Build(index);
        var specs = RoomCertifier.LoadSpecs(index);
        var settings = new AppSettings();

        // A pack of three, written beside the PNGs so the render has something real to list.
        var designs = Path.Combine(dir, "designs");
        Directory.CreateDirectory(designs);
        var wall = catalog.ByDefName.ContainsKey("ItmWall1x1") ? "ItmWall1x1" : catalog.ByDefName.Keys.First();

        string Design(string name, int parts, string def)
        {
            var file = new OplanFile
            {
                Meta = new OplanMeta { Name = name, Author = "Valtora" },
                Parts = [.. Enumerable.Range(0, parts).Select(i => new OplanPart { Def = def, X = i % 12, Y = i / 12 })],
            };
            var path = Path.Combine(designs, name + ".oplan");
            file.Save(path);
            return path;
        }

        var pack = new BundleFile
        {
            Mod = new BundleModMeta
            {
                Name = "Working Hulls", Author = "Valtora", Version = "1.0.0",
                Notes = "Three hulls that earn their keep.",
            },
            Ships =
            [
                new BundleEntry
                {
                    Path = Design("Kestrel", 41, wall),
                    Delivery = new DeliveryPlan { BrokerPools = ["RandomShipBrokerOKLG"], BrokerWeight = 0.05 },
                    Wear = new BundleWear { On = true, Target = 0.88 },
                },
                new BundleEntry
                {
                    Path = Design("Harrier", 96, wall),
                    Delivery = new DeliveryPlan { StartingShip = true, StartStation = "OKLG", StartMortgage = 250000 },
                },
                new BundleEntry { Path = Design("Barge", 12, "ItmFromAModYouRemoved") },
            ],
        };
        var packPath = Path.Combine(dir, "Working Hulls.oplanmod");
        pack.Save(packPath);

        foreach (var mode in new[] { "dark", "light" })
        {
            ThemeManager.Apply(mode);
            var window = new Bundle.BundleWindow(catalog, index, env, settings, specs, new SpriteCache(), _ => false);
            window.OpenPack(packPath);

            var root = (FrameworkElement)window.Content;
            Shot(root, window.Width, window.Height, Path.Combine(dir, $"bundle-{mode}.png"));

            var review = new Bundle.BundleReviewDialog(
                catalog, index, env, settings, specs, null,
                new BundleOptions("Working Hulls", "Valtora", "", "1.0.0", GameEnv.VerifiedGameVersion,
                    env.ModsDir ?? dir, []),
                register: true);
            review.RenderSample();
            Shot((FrameworkElement)review.Content, review.Width, review.Height,
                Path.Combine(dir, $"bundle-review-{mode}.png"));
        }
    }

    /// <summary>Measure, arrange and encode a window's content at its own size. Part of <c>--bundlesmoke</c>.</summary>
    private static void Shot(FrameworkElement root, double width, double height, string path)
    {
        root.Width = width;
        root.Height = height;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();

        var bmp = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static void RenderFill(string dir)
    {
        var canister = new PayloadSpec(0.787, 41400, 293,
        [
            new PayloadLine("StatGasMolO2", "Oxygen Gas", 13373, 13375, IsGas: true),
            new PayloadLine("StatGasMolN2", "Nitrogen Gas", 0, 13375, IsGas: true),
            new PayloadLine("StatGasMolCO2", "Carbon Dioxide Gas", 0, 13375, IsGas: true),
        ]);
        // a fuel tank: its own reactant and nothing else — no gas section at all (ContainerFill.Describe)
        var torch = new PayloadSpec(40.4, 500, 4,
        [
            new PayloadLine("StatLiqD2O", "Deuterium (Liquid)", 44722.8, 44722.8, IsGas: false),
        ]);
        var bare = new Catalog { Parts = [], ByDefName = new Dictionary<string, PartDef>(), Loots = new Dictionary<string, LootDef>(), Triggers = new Dictionary<string, CondTriggerDef>(), Warnings = [] };

        foreach (var (mode, spec, name, file) in new[]
                 {
                     ("dark", canister, "Oxygen Tank (RTA)", "fill-canister-dark.png"),
                     ("light", canister, "Oxygen Tank (RTA)", "fill-canister-light.png"),
                     ("dark", torch, "Deuterium Tank", "fill-torch-dark.png"),
                 })
        {
            ThemeManager.Apply(mode);
            var dlg = new FillDialog(name, spec, null, bare);
            var root = (FrameworkElement)dlg.Content;
            root.Width = 560;
            root.Measure(new Size(560, double.PositiveInfinity));
            root.Arrange(new Rect(0, 0, 560, root.DesiredSize.Height));
            root.UpdateLayout();
            var bmp = new RenderTargetBitmap(560, (int)Math.Ceiling(root.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
            bmp.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(Path.Combine(dir, file));
            enc.Save(fs);
        }
    }
}
