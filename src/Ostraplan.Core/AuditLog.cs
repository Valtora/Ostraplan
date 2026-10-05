using System.IO;

namespace Ostraplan.Core;

/// <summary>
/// Ostraplan's on-disk activity trail (<c>%APPDATA%\Ostraplan\audit.log</c>, beside settings.json):
/// a plain-text, most-recent-last record of what the user did this session and before — edits,
/// tool/setting changes, and file operations — so a support request can be reconstructed after the
/// fact. Every line is timestamped and run through <see cref="PathScrub"/>, so no account name or
/// user-profile path is ever written. Writing must never throw into an operation, so all I/O is
/// swallowed. A small in-memory tail feeds the "recent actions" block of a bug report.
/// </summary>
public static class AuditLog
{
    private static readonly object Lock = new();
    private static readonly List<string> Mem = [];   // this session's tail, for embedding in a bug report
    private const int MemCap = 2000;                 // generous: the whole session's trail feeds the diagnostics file
    private static string? _lastTool;                // dedupe consecutive identical tool/brush picks

    public static string Dir => AppSettings.Dir;
    public static string FilePath => Path.Combine(Dir, "audit.log");

    /// <summary>Record one line (timestamped + scrubbed) to the in-memory tail and the file.</summary>
    public static void Add(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {PathScrub.Clean(message)}";
        lock (Lock)
        {
            Mem.Add(line);
            if (Mem.Count > MemCap) Mem.RemoveRange(0, Mem.Count - MemCap);
            try
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch { /* logging must never take an operation down */ }
        }
    }

    /// <summary>A banner marking the start of a run, so the trail is grouped by session.</summary>
    public static void Session(string appVersion) => Add($"──── Ostraplan v{appVersion} started ────");

    /// <summary>Log a command edit (place/move/delete/paint/…) as a Do, Undo or Redo. The optional
    /// <paramref name="friendlyOf"/> resolver turns a def name into its friendly name so a describable command
    /// records what/where (e.g. "placed Nav Station at 12, 7") rather than a context-free "Place".</summary>
    public static void Command(CommandAction action, IDocCommand cmd, Func<string, string?>? friendlyOf = null) =>
        Add(Label(action, cmd, friendlyOf));

    /// <summary>
    /// What a command did, in one sentence: its own description when it is <see cref="IAuditDescribable"/> and a
    /// resolver is supplied, otherwise the terse type name (minus the "Command" suffix). The activity log, the status
    /// bar after an undo and the Undo button's history list all read this, so the three never disagree (#76).
    /// </summary>
    public static string Describe(IDocCommand cmd, Func<string, string?>? friendlyOf = null)
    {
        if (cmd is IAuditDescribable d && friendlyOf is not null) return d.Describe(friendlyOf);
        var name = cmd.GetType().Name;
        return name.EndsWith("Command", StringComparison.Ordinal) ? name[..^"Command".Length] : name;
    }

    /// <summary>The audit line for a command: <see cref="Describe"/> behind an Edit, Undo or Redo prefix.</summary>
    public static string Label(CommandAction action, IDocCommand cmd, Func<string, string?>? friendlyOf = null)
    {
        var what = Describe(cmd, friendlyOf);
        if (cmd is IAuditDescribable && friendlyOf is not null) what = AuditFmt.Uncap(what);
        return action switch
        {
            CommandAction.Undo => $"Undo: {what}",
            CommandAction.Redo => $"Redo: {what}",
            _ => $"Edit: {what}",
        };
    }

    /// <summary>
    /// The status-bar line after an undo or redo (#76): "Undid: placed Nav Station at 12, 7". A jump down the
    /// history list takes several <paramref name="steps"/> in the order they ran, and names the last of them, which
    /// is the entry that was picked.
    /// </summary>
    public static string Recap(CommandAction action, IReadOnlyList<IDocCommand> steps, Func<string, string?>? friendlyOf = null)
    {
        if (steps.Count == 0) return "";
        var verb = action == CommandAction.Redo ? "Redid" : "Undid";
        var last = AuditFmt.Uncap(Describe(steps[^1], friendlyOf));
        return steps.Count == 1
            ? $"{verb}: {last}"
            : $"{verb} {steps.Count} edits, {(action == CommandAction.Redo ? "as far as" : "as far back as")} {last}";
    }

    /// <summary>Log a tool/brush selection, collapsing consecutive identical picks so re-arming the
    /// same brush isn't recorded as a change.</summary>
    public static void Tool(string toolName)
    {
        lock (Lock)
        {
            if (_lastTool == toolName) return;
            _lastTool = toolName;
        }
        Add($"Tool: {toolName}");
    }

    /// <summary>Log a settings change (theme, game folder, …).</summary>
    public static void Setting(string name, string? value) => Add($"Setting: {name} = {value ?? "(none)"}");

    /// <summary>Most-recent-last lines from this session, for the bug report's action trail.</summary>
    public static IReadOnlyList<string> Recent(int max = 25)
    {
        lock (Lock) return Mem.Count <= max ? Mem.ToArray() : Mem.Skip(Mem.Count - max).ToArray();
    }

    /// <summary>The whole in-memory trail for this session (most-recent-last), for the full diagnostics file.</summary>
    public static IReadOnlyList<string> SessionTrail()
    {
        lock (Lock) return Mem.ToArray();
    }

    /// <summary>Empty the activity log — wipe the in-memory tail and truncate the file on disk.</summary>
    public static void Clear()
    {
        lock (Lock)
        {
            Mem.Clear();
            _lastTool = null;
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, string.Empty);
            }
            catch { /* clearing must never throw */ }
        }
    }
}
