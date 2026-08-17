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
            ? "Ostranauts is running.\n" +
              "Writing in place is only safe from the Main Menu.\n" +
              "If this save is loaded, the game will overwrite this on its next autosave.\n\n" +
              "Confirm you are at the Main Menu, not in your loaded game, before continuing.\n\n"
            : "";

    /// <summary>What the backup choice actually buys, in the terms of <paramref name="what"/> (the thing being
    /// written: "edit", "ship", "apartment").</summary>
    public static string BackupExplanation(bool backup, string what) =>
        backup
            ? "Ostraplan first copies this save to a separate backup save in your Saves folder, beside this one, not inside it.\n" +
              $"Then it writes your {what} into the original save, replacing it.\n" +
              $"If the {what} goes wrong, load the backup to recover."
            : $"You unticked the backup, so this writes your {what} straight into the original save, replacing it.\n" +
              "There will be no backup to roll back to if it goes wrong.";

    /// <summary>The Done pane's last line: what became of the original save. <paramref name="backupName"/> is the
    /// backup save's folder name, or null when none was taken.</summary>
    public static string Outcome(bool inPlace, string? backupName, string what) =>
        !inPlace
            ? "Your original save is unchanged."
            : backupName is not null
                ? $"Your original save was backed up first, as a separate save named {backupName}. It sits beside " +
                  "this save in your Saves folder, not inside it, so deleting the written save won't remove it."
                : $"No backup was made (you unticked it), so this wrote the {what} into the original save in place.";
}
