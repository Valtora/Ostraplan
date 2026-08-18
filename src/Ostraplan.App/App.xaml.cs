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

        // preview render: draw representative dialogs to PNGs (for eyeballing the modal styling), then exit.
        if (e.Args.Contains("--dlgsmoke"))
        {
            var dir = e.Args.SkipWhile(a => a != "--dlgsmoke").Skip(1).FirstOrDefault() ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(dir);

            void Render(string mode, DlgKind kind, string title, string body, (string, MessageDialog.Choice)[] buttons, string file)
            {
                ThemeManager.Apply(mode);
                var (root, _) = MessageDialog.BuildLayout(kind, title, body, buttons, _ => { });
                root.Width = 486;
                root.Measure(new Size(486, double.PositiveInfinity));
                root.Arrange(new Rect(0, 0, 486, root.DesiredSize.Height));
                root.UpdateLayout();
                var bmp = new RenderTargetBitmap(486, (int)Math.Ceiling(root.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
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
                    ShipDocument? doc = null, CommandStack? stack = null, Placement? root = null)
                {
                    var win = new InventoryWindow(catalog, sprites, def, friendly, cargo, doc, stack, root);
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
