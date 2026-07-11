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

        // Theme the chrome before the first window renders (Fluent ThemeMode + the app's own
        // brushes). Read the saved preference; the canvas stays dark regardless (ThemeManager).
        ThemeManager.Apply(AppSettings.Load().Theme);

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

        // Self-adopting update: if the user ran a freshly downloaded newer exe, replace the installed
        // copy (%LOCALAPPDATA%\Programs\Ostraplan), refresh shortcuts, and relaunch from there — so old
        // shortcuts never open a stale binary. Handed off ⇒ this process exits. No-op for a dev/installed
        // launch or when there's nothing newer to adopt.
        if (Updater.Detect() is { } pending && Updater.PromptAndApply(pending))
        {
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
                (int W, int H) baseDims = (0, 0);
                for (var i = 0; i < 4; i++)
                {
                    var rot = i * 90;
                    var svg = canvas.RenderRatingSnapshotSvg(specs)
                              ?? throw new InvalidOperationException("RenderRatingSnapshotSvg returned null (empty design?).");
                    var xdoc = System.Xml.Linq.XDocument.Parse(svg);   // throws if not well-formed XML
                    var root = xdoc.Root!;
                    var (w, h) = ((int)root.Attribute("width")!, (int)root.Attribute("height")!);
                    var rtb = canvas.RenderRatingSnapshot(specs)!;     // raster path too
                    if (i == 0) baseDims = (w, h);
                    var expect = rot is 90 or 270 ? (baseDims.H, baseDims.W) : baseDims;
                    var ok = (w, h) == expect && rtb.PixelWidth == w && rtb.PixelHeight == h;
                    report.AppendLine($"rot {rot}: svg {w}x{h}, raster {rtb.PixelWidth}x{rtb.PixelHeight}, expect {expect.Item1}x{expect.Item2} -> {(ok ? "OK" : "MISMATCH")}");
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
            }
            catch { /* logging must never take the app down */ }
            Dlg.Show(args.Exception.Message, "Ostraplan - unexpected error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        new MainWindow().Show();
    }
}
