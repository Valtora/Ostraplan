using System.Windows;
using Ostraplan.Core;

namespace Ostraplan.App;

/// <summary>
/// Hand a staged mod to Ostrasort: locate it (prompting once and remembering the path), register the mod
/// (<c>--apply</c>), then merge kiosk-loot conflicts (<c>--patch</c>) if the export wrote any loot pools.
///
/// <para><b>Ostraplan never writes <c>loading_order.json</c> itself</b>; this only drives the tool that owns it.
/// It lives outside the export wizard because a ship pack is staged the same way a single design is, and the
/// hand-off is the same hand-off.</para>
/// </summary>
public static class OstrasortRegistration
{
    /// <summary>Register <paramref name="modName"/> with Ostrasort and report what happened, as lines for a Done
    /// pane. <paramref name="touchedLootPools"/> runs the conflict patch afterwards.</summary>
    public static async Task<IReadOnlyList<string>> RunAsync(
        Window owner, AppSettings settings, GameEnv env, string modName, bool touchedLootPools)
    {
        var exe = OstrasortLauncher.Detect(settings);
        if (exe is null)
        {
            if (!Dlg.Confirm(owner, DlgKind.Info, "Locate Ostrasort",
                    "Ostraplan couldn't find Ostrasort.exe. Point it at your Ostrasort.exe to register the mod " +
                    "(or cancel and register it yourself later).", "Locate…"))
                return NotRegistered;
            exe = OstrasortLauncher.Prompt(owner);
            if (exe is null) return NotRegistered;
            settings.OstrasortPath = exe;
            settings.Save();
        }

        OstrasortRun apply, patch = new(false, 0, "", null);
        apply = await OstrasortLauncher.RunAsync(exe, env.GameRoot, env.ModsDir, patch: false);
        if (apply.Ok && touchedLootPools)
            patch = await OstrasortLauncher.RunAsync(exe, env.GameRoot, env.ModsDir, patch: true);

        // a remembered path that failed to launch is likely stale, so clear it and re-detect or prompt next time
        if (!apply.Launched && settings.OstrasortPath == exe)
        {
            settings.OstrasortPath = null;
            settings.Save();
        }

        AuditLog.Add($"Ostrasort register \"{modName}\": apply exit {apply.ExitCode}" +
                     (touchedLootPools ? $", patch exit {patch.ExitCode}" : ""));

        if (!apply.Launched)
            return
            [
                $"Ostrasort could not be launched: {apply.Error}",
                "Register the mod yourself with Ostrasort or ModTools.",
            ];

        var lines = new List<string>
        {
            apply.Ok ? "Registered with Ostrasort." : $"Ostrasort reported exit {apply.ExitCode}.",
        };
        if (touchedLootPools)
            lines.Add(patch.Ok
                ? "Kiosk-loot conflicts patched (if any)."
                : $"The loot patch step reported exit {patch.ExitCode}. Check Ostrasort if another ship mod shares those kiosks.");
        lines.Add("Launch Ostranauts and check the MODS screen to confirm it loaded.");
        return lines;
    }

    private static readonly string[] NotRegistered =
    [
        "Not registered: you cancelled the Ostrasort step.",
        "It won't appear in game until you register it. Run Ostrasort (or ModTools), or export again with " +
        "\"Register with Ostrasort\" ticked.",
    ];
}
