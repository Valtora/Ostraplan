using System.IO;
using System.Runtime.InteropServices;

namespace Ostraplan.App;

/// <summary>
/// Optional, opt-in self-install: copy the running single-file exe into a stable
/// per-user location (%LOCALAPPDATA%\Programs\Ostraplan) and create Desktop /
/// Start Menu shortcuts, so the user has a fixed home for the tool and an easy
/// way to launch it. No admin rights, nothing outside the user profile. This is
/// also the anchor a future built-in updater would write new builds to.
/// (Same pattern as Ostrasort's SelfInstall.)
/// </summary>
public static class SelfInstall
{
    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "Ostraplan");

    public static string InstalledExePath => Path.Combine(InstallDir, "Ostraplan.exe");

    /// <summary>The running exe (the single-file host the user launched), or null if unknown.</summary>
    public static string? CurrentExePath => Environment.ProcessPath;

    /// <summary>True when the running exe already lives at the install location.</summary>
    public static bool IsInstalled() =>
        CurrentExePath is { Length: > 0 } cur && SamePath(cur, InstalledExePath);

    /// <summary>
    /// True when we can offer to install: we know our own path and are not
    /// already running from the install location. (False for a dev/dotnet-run
    /// build whose ProcessPath is the SDK host — it just won't offer, which is fine.)
    /// </summary>
    public static bool CanOfferInstall() =>
        CurrentExePath is { Length: > 0 } cur && !SamePath(cur, InstalledExePath);

    public sealed record Result(string ExePath, bool Copied, List<string> Shortcuts);

    /// <summary>
    /// Copies the running exe to the install location (unless it is already
    /// there) and creates the requested shortcuts. Throws with a human message
    /// on any failure; the caller surfaces it.
    /// </summary>
    public static Result Install(bool desktopShortcut, bool startMenuShortcut)
    {
        var cur = CurrentExePath
            ?? throw new InvalidOperationException("Ostraplan can't determine its own location to install from.");

        Directory.CreateDirectory(InstallDir);
        var copied = false;
        if (!SamePath(cur, InstalledExePath))
        {
            File.Copy(cur, InstalledExePath, overwrite: true);
            copied = true;
        }

        var made = new List<string>();
        if (desktopShortcut) made.Add(CreateShortcut(DesktopShortcutPath));
        if (startMenuShortcut) made.Add(CreateShortcut(StartMenuShortcutPath));

        return new Result(InstalledExePath, copied, made);
    }

    private static string DesktopShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Ostraplan.lnk");

    private static string StartMenuShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Ostraplan.lnk");

    /// <summary>
    /// Re-points the standard Desktop / Start Menu shortcuts at the installed exe,
    /// but ONLY those that already exist — the updater calls this after replacing
    /// the installed binary so a link's icon/target is refreshed without silently
    /// creating shortcuts the user never asked for. Returns the ones refreshed.
    /// (Same as Ostrasort's SelfInstall.RefreshShortcuts.)
    /// </summary>
    public static List<string> RefreshShortcuts()
    {
        var refreshed = new List<string>();
        foreach (var lnk in new[] { DesktopShortcutPath, StartMenuShortcutPath })
            if (File.Exists(lnk))
                refreshed.Add(CreateShortcut(lnk));   // CreateShortcut always targets InstalledExePath
        return refreshed;
    }

    /// <summary>Writes a .lnk pointing at the installed exe via Windows Script Host (no extra dependency).</summary>
    private static string CreateShortcut(string lnkPath)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host is unavailable — can't create a shortcut.");
        dynamic? shell = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Windows Script Host could not be started.");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
            dynamic sc = shell.CreateShortcut(lnkPath);
            sc.TargetPath = InstalledExePath;
            sc.WorkingDirectory = InstallDir;
            sc.IconLocation = InstalledExePath + ",0";
            sc.Description = "Ostraplan - out-of-game Ostranauts ship planner";
            sc.Save();
            return lnkPath;
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\', '/'),
                Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
