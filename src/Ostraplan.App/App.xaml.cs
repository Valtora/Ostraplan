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
