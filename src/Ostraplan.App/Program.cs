using System.IO;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// The application entry point. Its whole reason to exist ahead of WPF's own
/// startup is Velopack: <see cref="Velopack.VelopackApp"/> must run as the very
/// first thing so the installer's install / update / uninstall hooks (passed as
/// special command-line args) are handled and the process exits for those - no
/// window ever flashes up during an install or update. On a normal launch it
/// returns immediately and we boot the WPF app as usual.
///
/// <para>The hooks register Ostraplan's file types with Explorer and remove them again
/// (see <see cref="ShellIntegration"/>). After them, a launch that was given files and
/// finds Ostraplan already running hands the files to that window and exits, before any
/// WPF startup is paid for (see <see cref="SingleInstance"/>).</para>
///
/// <para>WPF populates <c>StartupEventArgs.Args</c> from the command line itself,
/// so <see cref="App.OnStartup"/> still sees the smoke-render args without us
/// threading them through here.</para>
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Velopack.VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => ShellIntegration.OnInstalled())
            .OnAfterUpdateFastCallback(_ => ShellIntegration.OnUpdated())
            .OnBeforeUninstallFastCallback(_ => ShellIntegration.OnUninstalling())
            .Run();

        if (!SingleInstance.App.Start(LaunchArgs.Files(args, File.Exists))) return 0;

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
