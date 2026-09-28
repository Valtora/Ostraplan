namespace Ostraplan.App.Wizard;

/// <summary>
/// The two things every in-place confirmation has to say, shared so they cannot drift apart between the
/// destination that edits a ship and the one that adds a ship.
///
/// <para>Each destination still writes its own dialog around them: the titles and the button say different things
/// because the actions are different, but the warning about a running game and the account of what a backup does
/// or does not get you are the same facts either way, and getting either wrong costs the user a save.</para>
/// </summary>
internal static class InPlaceWrite
{
    /// <summary>The running-game warning, or empty when Ostranauts is not running. The game holds the whole save
    /// in memory and writes it back on its own schedule, so an in-place write into a loaded save is undone by the
    /// next autosave. Detection is the reason this belongs in the final confirmation rather than in the Review
    /// pane, which the user may have read minutes earlier.</summary>
    public static string GameRunningWarning() =>
        System.Diagnostics.Process.GetProcessesByName("Ostranauts").Length > 0
            ? "Ostranauts is running. If this save is loaded, its next autosave will undo this.\n" +
              "Only continue if the game is at the Main Menu.\n\n"
            : "";

    /// <summary>What the backup choice actually buys, in the terms of <paramref name="what"/> (the thing being
    /// written: "edit", "ship", "apartment").</summary>
    public static string BackupExplanation(bool backup, string what) =>
        backup
            ? $"Ostraplan first backs this save up as a separate save beside it, then writes your {what} into the original.\n" +
              "If it goes wrong, load the backup."
            : $"No backup: this writes your {what} straight into the original save, with nothing to roll back to.";

    /// <summary>The Done pane's last line: what became of the original save. <paramref name="backupName"/> is the
    /// backup save's folder name, or null when none was taken.</summary>
    public static string Outcome(bool inPlace, string? backupName, string what) =>
        !inPlace
            ? "Your original save is unchanged."
            : backupName is not null
                ? $"Your original save was backed up first as a separate save, {backupName}."
                : $"No backup was made. The {what} was written into the original save.";
}
