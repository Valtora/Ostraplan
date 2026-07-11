using System.Diagnostics;
using System.IO;
using System.Reflection;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// Self-adopting updater. The download stays manual (the update prompt opens the
/// GitHub release page), but when the user runs that freshly downloaded exe this
/// makes it supersede the installed copy at <see cref="SelfInstall.InstalledExePath"/>:
/// if it is a newer build than the installed one, it overwrites the installed binary
/// with itself, refreshes the existing shortcuts, and relaunches from the install
/// location — so an old Desktop/Start-Menu shortcut never opens a stale binary again,
/// and the user never has to re-run the self-install by hand. Detection is by the exe's
/// embedded version, never its filename (a download may land as "Ostraplan-v0.29.0.exe"
/// or "Ostraplan (1).exe"). Runs at startup before the main window opens.
///
/// <para>Unlike Ostrasort, Ostraplan has no single-instance signal and its documents can
/// hold unsaved edits, so it never force-kills a running copy: if the installed exe is
/// locked (an instance is running from there), it asks the user to close it and retry
/// rather than risk discarding their work.</para>
/// </summary>
public static class Updater
{
    /// <summary>A newer running exe that should replace the installed copy.</summary>
    public sealed record PendingUpdate(string RunningExe, string InstalledExe, string RunningVersion, string InstalledVersion);

    /// <summary>This running build's version, from the assembly's informational version (git hash stripped).</summary>
    internal static string RunningVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "0.0";

    /// <summary>Canonical version parse shared with the launch update check: strip a leading v and any +build/-suffix.</summary>
    internal static Version Parse(string s) =>
        Version.TryParse((s ?? "").TrimStart('v', 'V').Split('+', '-')[0], out var v) ? v : new Version(0, 0);

    /// <summary>Pure, testable: is <paramref name="runningVer"/> strictly newer than <paramref name="installedVer"/>?</summary>
    public static bool ShouldAdopt(string runningVer, string installedVer) => Parse(runningVer) > Parse(installedVer);

    /// <summary>
    /// Returns a pending update when the running exe should adopt the install
    /// location. Null (do nothing) unless ALL hold: we know our own path and are
    /// not already the installed copy; an installed copy exists to update; we are
    /// not a developer build running from a bin\ output folder; and our version is
    /// strictly newer than the installed one.
    /// </summary>
    public static PendingUpdate? Detect()
    {
        var cur = SelfInstall.CurrentExePath;
        if (cur is not { Length: > 0 }) return null;                 // dotnet-run host etc. — can't self-replace
        if (SelfInstall.IsInstalled()) return null;                  // we ARE the installed copy — nothing to adopt
        if (IsDevBuildPath(cur)) return null;                        // bin\Debug|Release dev launch — don't nag
        var installedExe = SelfInstall.InstalledExePath;
        if (!File.Exists(installedExe)) return null;                 // no prior install (first-run offer handles this)

        var installedVer = ReadExeVersion(installedExe);
        if (installedVer is null) return null;
        if (!ShouldAdopt(RunningVersion, installedVer)) return null;

        return new PendingUpdate(cur, installedExe, RunningVersion, installedVer);
    }

    /// <summary>Shows the themed confirm and, if accepted, performs the swap + relaunch. True = handed off (caller should exit).</summary>
    public static bool PromptAndApply(PendingUpdate pu)
    {
        var accepted = Dlg.Confirm(null, DlgKind.Info, "Update your installed Ostraplan?",
            $"You're running v{pu.RunningVersion}, and your installed copy is v{pu.InstalledVersion}.\n\n" +
            "Ostraplan will replace the installed copy, refresh your shortcuts, and restart — so your existing " +
            "shortcuts always open the latest version.",
            confirmVerb: "Update and restart", cancelVerb: "Just run this copy");
        if (!accepted) return false;
        return Apply(pu);
    }

    /// <summary>
    /// Overwrites the installed exe with the running one, refreshes any existing
    /// shortcuts, and relaunches the installed copy. If the installed exe is locked
    /// (an instance is running from there), it asks the user to close it and retry
    /// rather than force-killing (which could discard unsaved edits). Returns true
    /// when the relaunch happened (caller must exit this process); false on failure
    /// or cancel (caller falls through to running this downloaded copy in place).
    /// </summary>
    private static bool Apply(PendingUpdate pu)
    {
        try
        {
            AuditLog.Add($"Update: adopting install location (running v{pu.RunningVersion} over installed v{pu.InstalledVersion}).");

            while (!WaitForReplaceable(pu.InstalledExe, TimeSpan.FromSeconds(2)))
            {
                // Something is holding the installed exe — almost always a running installed instance.
                if (!Dlg.Confirm(null, DlgKind.Warning, "Ostraplan is still open",
                        "The installed copy of Ostraplan is currently running, so it can't be replaced.\n\n" +
                        "Close it, then click Retry. Anything unsaved there will prompt you to save first.",
                        confirmVerb: "Retry", cancelVerb: "Just run this copy"))
                {
                    AuditLog.Add("Update: user declined to close the running installed copy — kept the downloaded copy.");
                    return false;
                }
            }

            CopyWithRetry(pu.RunningExe, pu.InstalledExe);
            foreach (var s in SelfInstall.RefreshShortcuts()) AuditLog.Add($"Update: refreshed shortcut {s}.");

            Process.Start(new ProcessStartInfo(pu.InstalledExe) { UseShellExecute = true });
            AuditLog.Add($"Update: replaced installed copy and relaunched v{pu.RunningVersion}.");
            return true;
        }
        catch (Exception e)
        {
            AuditLog.Add($"Update: automatic update failed: {e.Message}");
            Dlg.Error(null, "Update failed",
                "Ostraplan couldn't replace the installed copy automatically:\n\n" + e.Message +
                "\n\nYou can keep using this downloaded copy.");
            return false;
        }
    }

    /// <summary>Reads a build's version from its embedded resource, without launching it. Null if unreadable.</summary>
    private static string? ReadExeVersion(string exePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            return info.ProductVersion ?? info.FileVersion;
        }
        catch { return null; }
    }

    /// <summary>A dev launch (dotnet build/publish output) lives under a bin\ folder — never treat it as an update.</summary>
    private static bool IsDevBuildPath(string exePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? "";
        return dir.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) ||
               dir.EndsWith(@"\bin", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True once the file can be opened for exclusive write = no process is holding it (the running exe releases it on exit).</summary>
    private static bool WaitForReplaceable(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            try
            {
                using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException) { Thread.Sleep(150); }
            catch (UnauthorizedAccessException) { Thread.Sleep(150); }
        } while (DateTime.UtcNow < deadline);
        return false;
    }

    /// <summary>Overwrite the installed exe, retrying briefly through transient locks (AV scans just after unlock).</summary>
    private static void CopyWithRetry(string source, string dest)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { File.Copy(source, dest, overwrite: true); return; }
            catch (IOException) when (attempt < 5) { Thread.Sleep(200); }
        }
    }
}
