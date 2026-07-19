using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Ostraplan.App;

/// <summary>
/// Thin wrapper over Velopack's <see cref="UpdateManager"/> for the app. It checks
/// GitHub Releases for a newer build, downloads it in the background, and (on the
/// user's say-so) applies it and restarts. Delivery is background-download +
/// apply-on-restart: nothing blocks startup, and the swap only happens when the
/// user clicks to restart. Ostraplan documents can hold unsaved edits, so nothing
/// here ever replaces a running copy without the user's explicit restart.
///
/// <para><see cref="Create"/> returns null for a copy that Velopack does not
/// manage - a developer build from bin\, a dotnet-run launch, or a bare exe that
/// was never installed or unpacked from the portable zip - so the update
/// affordance simply never appears there. Every network failure is swallowed by
/// the caller. (Same pattern as Ostrasort's VeloUpdate.)</para>
/// </summary>
public sealed class VeloUpdate
{
    private const string RepoUrl = "https://github.com/Valtora/Ostraplan";

    private readonly UpdateManager _mgr;
    private VeloUpdate(UpdateManager mgr) => _mgr = mgr;

    /// <summary>
    /// A manager for a Velopack-managed copy (installed or portable), or null when
    /// this copy cannot self-update (dev build / not installed). Constructing the
    /// manager does no network I/O; <see cref="UpdateManager.IsInstalled"/> is a
    /// local check for the sibling Update.exe.
    /// </summary>
    public static VeloUpdate? Create()
    {
        try
        {
            var mgr = BuildManager();
            return mgr.IsInstalled ? new VeloUpdate(mgr) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// The update source. Normally GitHub Releases. As a testing seam, when the
    /// OSTRAPLAN_UPDATE_FEED environment variable points at a local vpk-packed
    /// release folder, the updater reads from there instead - so the whole
    /// download / apply / restart path can be exercised locally without a public
    /// release. The variable is unset in production, so this is inert there.
    /// </summary>
    private static UpdateManager BuildManager()
    {
        var feed = Environment.GetEnvironmentVariable("OSTRAPLAN_UPDATE_FEED");
        if (!string.IsNullOrWhiteSpace(feed))
            return new UpdateManager(feed);
        return new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
    }

    /// <summary>
    /// Checks for a newer release and, if there is one, downloads it. Returns the
    /// ready-to-apply update (pass it to <see cref="ApplyAndRestart"/>), or null
    /// when already up to date. Runs off the UI thread.
    /// </summary>
    public async Task<UpdateInfo?> CheckAndDownloadAsync()
    {
        var info = await _mgr.CheckForUpdatesAsync().ConfigureAwait(false);
        if (info is null) return null;                       // already current
        await _mgr.DownloadUpdatesAsync(info).ConfigureAwait(false);
        return info;
    }

    /// <summary>Applies a downloaded update and restarts into it. Does not return on success.</summary>
    public void ApplyAndRestart(UpdateInfo info) => _mgr.ApplyUpdatesAndRestart(info);

    /// <summary>The version a pending update would install, for a UI label.</summary>
    public static string VersionOf(UpdateInfo info) => info.TargetFullRelease.Version.ToString();
}
