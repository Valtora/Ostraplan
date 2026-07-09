using System.Diagnostics;
using System.IO;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>The outcome of one headless Ostrasort run: whether it launched, its exit code, and its captured
/// output. Exit codes follow Ostrasort's contract — 0 = nothing to do, 2 = actions applied/suggested, 1 = error.</summary>
public sealed record OstrasortRun(bool Launched, int ExitCode, string Output, string? Error)
{
    public bool Ok => Launched && ExitCode is 0 or 2;   // 2 is success too (it means "did something")
}

/// <summary>
/// Finds and drives Ostrasort — the sibling tool that owns <c>loading_order.json</c> — so Ostraplan can
/// register a freshly-exported mod without ever writing the load order itself (single-writer discipline).
/// Detection prefers a remembered path, then a few conventional locations, then a one-time file picker.
/// Invocation is always headless (<c>--headless</c>): <c>--apply</c> registers the mod + sorts the order,
/// and an optional <c>--patch</c> follow-up merges kiosk-loot conflicts with other ship mods.
/// </summary>
public static class OstrasortLauncher
{
    private const string ExeName = "Ostrasort.exe";

    /// <summary>Find Ostrasort.exe: a remembered valid path first, then the standard per-user install location
    /// (<c>%LOCALAPPDATA%\Programs\Ostrasort</c>), then conventional locations near this exe (a co-located release,
    /// or the dev workspace's <c>Ostrasort/publish</c> up the tree). Null if not found — the caller then offers
    /// <see cref="Prompt"/>. A remembered path that no longer exists is ignored.</summary>
    public static string? Detect(AppSettings settings)
    {
        if (settings.OstrasortPath is { Length: > 0 } saved && File.Exists(saved)) return saved;

        // the default install location: %LOCALAPPDATA%\Programs\Ostrasort\Ostrasort.exe
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (localAppData.Length > 0)
        {
            var installed = Path.Combine(localAppData, "Programs", "Ostrasort", ExeName);
            if (File.Exists(installed)) return installed;
        }

        // walk up from this exe looking for a co-located Ostrasort.exe, or the workspace sibling's built exe
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(dir, ExeName),
                         Path.Combine(dir, "Ostrasort", "publish", ExeName),
                         Path.Combine(dir, "Ostrasort", "bin", "Release", "net10.0-windows", ExeName),
                     })
                if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }

    /// <summary>Ask the user to point at Ostrasort.exe once; the chosen path is returned (the caller persists it).</summary>
    public static string? Prompt(System.Windows.Window owner)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Locate Ostrasort.exe",
            Filter = "Ostrasort (Ostrasort.exe)|Ostrasort.exe|Executable (*.exe)|*.exe",
            CheckFileExists = true,
        };
        return dlg.ShowDialog(owner) == true ? dlg.FileName : null;
    }

    /// <summary>Run Ostrasort headlessly against the given install. <paramref name="patch"/> chooses the
    /// operation: <c>--patch</c> (merge loot conflicts) when true, else <c>--apply</c> (register + sort). Output
    /// and errors are captured, never shown in a window (<c>--headless</c> guarantees no GUI). Ostraplan passes
    /// the game + mods folders it already resolved so Ostrasort need not re-probe Steam.</summary>
    public static async Task<OstrasortRun> RunAsync(string exePath, string gameRoot, string modsDir, bool patch)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
        };
        psi.ArgumentList.Add("--headless");
        psi.ArgumentList.Add(patch ? "--patch" : "--apply");
        psi.ArgumentList.Add("--game");
        psi.ArgumentList.Add(gameRoot);
        psi.ArgumentList.Add("--mods");
        psi.ArgumentList.Add(modsDir);

        try
        {
            using var proc = new Process { StartInfo = psi };
            proc.Start();
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            var outText = (await stdout).Trim();
            var errText = (await stderr).Trim();
            return new OstrasortRun(true, proc.ExitCode, outText, errText.Length > 0 ? errText : null);
        }
        catch (Exception ex)
        {
            return new OstrasortRun(false, -1, "", ex.Message);
        }
    }
}
