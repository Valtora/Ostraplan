namespace Ostraplan.App;

/// <summary>
/// The application entry point. Its whole reason to exist ahead of WPF's own
/// startup is Velopack: <see cref="Velopack.VelopackApp"/> must run as the very
/// first thing so the installer's install / update / uninstall hooks (passed as
/// special command-line args) are handled and the process exits for those - no
/// window ever flashes up during an install or update. On a normal launch it
/// returns immediately and we boot the WPF app as usual.
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
        Velopack.VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
