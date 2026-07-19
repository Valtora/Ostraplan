using System.IO;
using System.Runtime.InteropServices;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// Best-effort removal of the pre-Velopack self-install. Up to 0.48 Ostraplan
/// copied itself to %LOCALAPPDATA%\Programs\Ostraplan and made Ostraplan.lnk
/// shortcuts aimed there. Velopack now owns install and shortcuts (into
/// %LOCALAPPDATA%\Ostraplan), so once the managed copy is running that old folder
/// is a dead duplicate that could still launch stale code. This deletes it, plus
/// any Desktop / Start Menu shortcut still pointing into it - never Velopack's
/// own, which targets the new install. It runs once (sentinel-gated) and only
/// from the managed install, and swallows every failure.
///
/// <para>Ostraplan's user data already lived in %APPDATA%\Ostraplan, not the
/// Velopack install root, so unlike Ostrasort there is no data to migrate - only
/// this old-install cleanup.</para>
/// </summary>
public static class LegacyInstall
{
    /// <summary>The old opt-in self-install location (0.48 and earlier).</summary>
    private static string OldInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ostraplan");

    /// <summary>The Velopack install root: %LOCALAPPDATA%\Ostraplan (binaries live under its current\ folder).</summary>
    private static string VelopackRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ostraplan");

    /// <summary>True when the running exe lives inside the Velopack install root.</summary>
    private static bool RunningAsManagedInstall()
    {
        var cur = Environment.ProcessPath;
        if (string.IsNullOrEmpty(cur)) return false;
        try
        {
            var root = Path.GetFullPath(VelopackRoot).TrimEnd('\\') + "\\";
            return Path.GetFullPath(cur).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static void Cleanup()
    {
        try
        {
            if (!RunningAsManagedInstall()) return;   // only the installed copy tidies up
            var sentinel = Path.Combine(AppSettings.Dir, ".legacy-install-cleaned");
            if (File.Exists(sentinel)) return;
            Directory.CreateDirectory(AppSettings.Dir);

            RemoveOldShortcuts();
            if (Directory.Exists(OldInstallDir))
            {
                try
                {
                    Directory.Delete(OldInstallDir, recursive: true);
                    AuditLog.Add($"Removed the old self-install at {OldInstallDir}.");
                }
                catch { /* locked / in use - leave it, the sentinel still stops a retry loop */ }
            }
            File.WriteAllText(sentinel, $"{DateTime.UtcNow:o}\n");
        }
        catch { /* best effort */ }
    }

    private static void RemoveOldShortcuts()
    {
        foreach (var lnk in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Ostraplan.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Ostraplan.lnk"),
        })
        {
            try
            {
                if (File.Exists(lnk) && TargetsOldInstall(lnk))
                {
                    File.Delete(lnk);
                    AuditLog.Add($"Removed the old shortcut {lnk}.");
                }
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>Reads a .lnk's target via Windows Script Host; true only if it points into the old install dir.</summary>
    private static bool TargetsOldInstall(string lnkPath)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type is null) return false;
        dynamic? shell = Activator.CreateInstance(type);
        if (shell is null) return false;
        try
        {
            dynamic sc = shell.CreateShortcut(lnkPath);
            string target = sc.TargetPath ?? "";
            return target.StartsWith(OldInstallDir, StringComparison.OrdinalIgnoreCase);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }
}
