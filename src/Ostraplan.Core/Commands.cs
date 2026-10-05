namespace Ostraplan.Core;

public interface IDocCommand
{
    void Do(ShipDocument doc);
    void Undo(ShipDocument doc);

    /// <summary>
    /// Add what this command changes to <paramref name="into"/>, so an undo or redo of it can be shown on the plan
    /// (#76). One answer serves both directions: what an undo takes off the plan is what the redo puts back, so this
    /// reports everything the command handles and <see cref="ChangeSet.Resolve"/> sorts out what is there afterwards.
    ///
    /// <para>Part of the interface rather than an opt-in like <see cref="IAuditDescribable"/>, so an edit added later
    /// cannot leave an undo with nothing to point at.</para>
    /// </summary>
    void Touch(ChangeSet into);
}

/// <summary>
/// A command that can say what it did in one sentence: the part's friendly name, its tile and rotation, and batch
/// counts, rather than the bare type name. The same sentence goes to the activity log (so a filed bug report can be
/// traced) and to the status bar after an undo or redo (#76), so it is written for the person using the app: past
/// tense, friendly names, no game internals. <see cref="AuditLog.Describe"/> is the way to ask for it.
/// </summary>
public interface IAuditDescribable
{
    /// <param name="friendlyOf">Resolves a def name to its friendly name; may return null/empty when unknown.</param>
    string Describe(Func<string, string?> friendlyOf);
}

/// <summary>Shared wording for <see cref="IAuditDescribable"/>, so every command renders tiles, rotations and
/// batches the same way.</summary>
internal static class AuditFmt
{
    public static string Name(Func<string, string?> f, string def) => f(def) is { Length: > 0 } n ? n : def;
    public static string At(int x, int y) => $"at {x}, {y}";

    /// <summary>", turned 90°" for a part placed at an angle, nothing when it sits square.</summary>
    public static string Turned(int rot) => rot == 0 ? "" : $", turned {rot}°";

    public static string By(int dx, int dy) => $"by {dx:+0;-0;0}, {dy:+0;-0;0}";

    /// <summary>"1 item" or "3 items".</summary>
    public static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

    /// <summary>"3 parts (Wall, Nav Station)": a batch of defs by count and name, at most three names shown.</summary>
    public static string Batch(IEnumerable<string> defs, Func<string, string?> f, string one = "part", string many = "parts")
    {
        var names = defs.Select(d => Name(f, d)).ToList();
        var distinct = names.Distinct(StringComparer.Ordinal).ToList();
        var shown = string.Join(", ", distinct.Take(3)) + (distinct.Count > 3 ? " and others" : "");
        return $"{Count(names.Count, one, many)} ({shown})";
    }

    /// <summary>A description lower-cased at the front, for use after a prefix ("Undid: placed …"). Every
    /// description opens on its verb, so the first letter is never part of a name.</summary>
    public static string Uncap(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
}

/// <summary>How a command reached the stack — a fresh edit, an undo, or a redo. Drives the audit line.</summary>
public enum CommandAction { Do, Undo, Redo }

/// <summary>Classic undo/redo stack; Push executes. Dirty tracks the saved position.</summary>
public sealed class CommandStack
{
    private readonly Stack<IDocCommand> _undo = new();
    private readonly Stack<IDocCommand> _redo = new();
    private int _savedDepth;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool Dirty => _undo.Count != _savedDepth;

    public event Action? StateChanged;

    /// <summary>Raised for every command that reaches the stack (fresh edit, undo, or redo), so a
    /// listener can log it. Seeding a document outside the stack (the primary airlock) doesn't fire.</summary>
    public event Action<IDocCommand, CommandAction>? Applied;

    /// <summary>
    /// While true the stack refuses every change to its document (#74): <see cref="Push"/> does not run the command,
    /// and <see cref="Undo"/> and <see cref="Redo"/> do nothing, each raising <see cref="Refused"/> instead. It is the
    /// backstop behind a locked tab. The window stops the edits it can see coming at the gesture, but an edit route
    /// added later, or one reached from a child window, goes through here whether or not anyone remembered the lock.
    ///
    /// <para><see cref="PushExecuted"/> is refused too, but it cannot put the document back, because the command has
    /// already run. Whatever calls it has to be stopped before it edits, which is what <c>ShipCanvas.ReadOnly</c> is
    /// for.</para>
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>Raised when a change is refused because the stack is <see cref="ReadOnly"/>.</summary>
    public event Action? Refused;

    public void Push(ShipDocument doc, IDocCommand cmd)
    {
        if (ReadOnly) { Refused?.Invoke(); return; }
        cmd.Do(doc);
        PushExecuted(cmd);
    }

    /// <summary>Record a command whose Do already ran (live paint strokes commit this way).</summary>
    public void PushExecuted(IDocCommand cmd)
    {
        if (ReadOnly) { Refused?.Invoke(); return; }
        _undo.Push(cmd);
        _redo.Clear();
        if (_savedDepth > _undo.Count - 1) _savedDepth = -1;   // saved state no longer reachable
        StateChanged?.Invoke();
        Applied?.Invoke(cmd, CommandAction.Do);
    }

    /// <summary>What an undo would take back, the next one first: the Undo button's history list (#76).</summary>
    public IReadOnlyList<IDocCommand> UndoSteps => _undo.ToArray();

    /// <summary>What a redo would put back, the next one first.</summary>
    public IReadOnlyList<IDocCommand> RedoSteps => _redo.ToArray();

    /// <summary>
    /// Take back the last <paramref name="steps"/> edits, newest first, and hand back what was undone in that order
    /// so the caller can show it. Empty when there was nothing to undo, or when the stack is <see cref="ReadOnly"/>.
    /// More than one step is a jump down the history list, and the document repaints and re-scans once for it.
    /// </summary>
    public IReadOnlyList<IDocCommand> Undo(ShipDocument doc, int steps = 1) =>
        Step(doc, _undo, _redo, steps, CommandAction.Undo);

    /// <summary>Put back the next <paramref name="steps"/> undone edits; the mirror of <see cref="Undo"/>.</summary>
    public IReadOnlyList<IDocCommand> Redo(ShipDocument doc, int steps = 1) =>
        Step(doc, _redo, _undo, steps, CommandAction.Redo);

    private IReadOnlyList<IDocCommand> Step(
        ShipDocument doc, Stack<IDocCommand> from, Stack<IDocCommand> to, int steps, CommandAction action)
    {
        if (from.Count == 0 || steps < 1) return [];
        if (ReadOnly) { Refused?.Invoke(); return []; }
        var done = new List<IDocCommand>(Math.Min(steps, from.Count));
        using (doc.SuspendChanged())
            while (done.Count < steps && from.Count > 0)
            {
                var cmd = from.Pop();
                if (action == CommandAction.Undo) cmd.Undo(doc);
                else cmd.Do(doc);
                to.Push(cmd);
                done.Add(cmd);
            }
        StateChanged?.Invoke();
        foreach (var cmd in done) Applied?.Invoke(cmd, action);
        return done;
    }

    public void MarkSaved()
    {
        _savedDepth = _undo.Count;
        StateChanged?.Invoke();
    }

    public void Reset()
    {
        _undo.Clear();
        _redo.Clear();
        _savedDepth = 0;
        StateChanged?.Invoke();
    }
}

/// <summary>Several commands as one undo step (multi-duplicate, multi-rotate).</summary>
public sealed class CompositeCommand(IReadOnlyList<IDocCommand> commands) : IDocCommand, IAuditDescribable
{
    public void Do(ShipDocument doc)
    {
        using var _ = doc.SuspendChanged();   // one repaint/scan for the whole batch
        foreach (var cmd in commands) cmd.Do(doc);
    }

    public void Undo(ShipDocument doc)
    {
        using var _ = doc.SuspendChanged();
        for (var i = commands.Count - 1; i >= 0; i--) commands[i].Undo(doc);
    }

    public void Touch(ChangeSet into)
    {
        foreach (var cmd in commands) cmd.Touch(into);
    }

    /// <summary>
    /// A batch of one kind reads as one sentence: a paint stroke is forty <see cref="PlaceCommand"/>s, and "Placed 40
    /// parts (Wall)" says what it did where forty tiles would not. A small mixed batch is spelled out in full, since a
    /// form swap is a remove and a place and both halves are worth seeing; a large one names its first step.
    /// </summary>
    public string Describe(Func<string, string?> friendlyOf)
    {
        if (commands.Count > 1 && commands.All(c => c is PlaceCommand))
            return $"Placed {AuditFmt.Batch(commands.Cast<PlaceCommand>().Select(c => c.Placement.DefName), friendlyOf)}";
        if (commands.Count > 1 && commands.All(c => c is PlaceLooseCommand))
            return $"Dropped {AuditFmt.Batch(commands.Cast<PlaceLooseCommand>().Select(c => c.Obj.DefName), friendlyOf, "loose item", "loose items")}";
        if (commands.Count > 1 && commands.All(c => c is RemoveLooseCommand))
            return $"Removed {AuditFmt.Batch(commands.Cast<RemoveLooseCommand>().Select(c => c.Obj.DefName), friendlyOf, "loose item", "loose items")}";

        var parts = commands.OfType<IAuditDescribable>().Select(c => c.Describe(friendlyOf)).ToList();
        if (parts.Count == 0) return $"Made {AuditFmt.Count(commands.Count, "edit", "edits")}";
        if (parts.Count == 1) return parts[0];
        if (parts.Count <= 3)
            return string.Join(", ", parts.SkipLast(1).Select((p, i) => i == 0 ? p : AuditFmt.Uncap(p)))
                   + " and " + AuditFmt.Uncap(parts[^1]);
        return $"{parts[0]}, and {parts.Count - 1} more edits";
    }
}

public sealed class PlaceCommand(Placement placement) : IDocCommand, IAuditDescribable
{
    public Placement Placement => placement;
    public void Do(ShipDocument doc) => doc.Add(placement);
    public void Undo(ShipDocument doc) => doc.Remove(placement);
    public void Touch(ChangeSet into) => into.Add(placement);
    public string Describe(Func<string, string?> f) =>
        $"Placed {AuditFmt.Name(f, placement.DefName)} {AuditFmt.At(placement.X, placement.Y)}{AuditFmt.Turned(placement.Rot)}";
}

/// <summary>
/// Swap a part's contained cargo tree — the inventory editor's add or remove. The caller computes the new tree
/// (via <see cref="CargoEdit"/>) and hands both trees in, so Do/Undo are a plain assignment either way. One
/// command covers add and remove because both are just "the container's contents are now this tree".
/// </summary>
public sealed class SetCargoCommand(Placement placement, IReadOnlyList<CargoItem> before, IReadOnlyList<CargoItem> after) : IDocCommand, IAuditDescribable
{
    public void Do(ShipDocument doc) => doc.SetCargo(placement, after);
    public void Undo(ShipDocument doc) => doc.SetCargo(placement, before);
    public void Touch(ChangeSet into) => into.Add(placement);
    public string Describe(Func<string, string?> f) =>
        $"Changed the contents of {AuditFmt.Name(f, placement.DefName)} {AuditFmt.At(placement.X, placement.Y)} " +
        $"from {AuditFmt.Count(before.Count, "item", "items")} to {after.Count}";
}

/// <summary>
/// Swap a nav console's screen arrangement — the arrange dialog's result. Like <see cref="SetCargoCommand"/> the
/// caller hands in both maps, so Do/Undo are a plain assignment; null means "back to the arrangement the game
/// itself would produce".
/// </summary>
public sealed class SetNavLayoutCommand(
    Placement placement, IReadOnlyDictionary<string, string>? before, IReadOnlyDictionary<string, string>? after)
    : IDocCommand, IAuditDescribable
{
    public void Do(ShipDocument doc) => doc.SetNavLayout(placement, after);
    public void Undo(ShipDocument doc) => doc.SetNavLayout(placement, before);
    public void Touch(ChangeSet into) => into.Add(placement);
    public string Describe(Func<string, string?> f) =>
        after is null
            ? $"Reset the screen layout of {AuditFmt.Name(f, placement.DefName)}"
            : $"Arranged the screen of {AuditFmt.Name(f, placement.DefName)} " +
              $"({after.Count(e => e.Value.Length > 0)} of {AuditFmt.Count(after.Count, "module", "modules")} on screen)";
}

/// <summary>
/// Swap a container's fill — the fill dialog's result. Like <see cref="SetCargoCommand"/> the caller hands in
/// both maps, so Do/Undo are a plain assignment; null means "back to the amounts the def ships with".
/// </summary>
public sealed class SetFillCommand(
    Placement placement, IReadOnlyDictionary<string, double>? before, IReadOnlyDictionary<string, double>? after)
    : IDocCommand, IAuditDescribable
{
    public void Do(ShipDocument doc) => doc.SetFill(placement, after);
    public void Undo(ShipDocument doc) => doc.SetFill(placement, before);
    public void Touch(ChangeSet into) => into.Add(placement);
    public string Describe(Func<string, string?> f) =>
        after is null
            ? $"Reset the fill of {AuditFmt.Name(f, placement.DefName)} to stock"
            : $"Filled {AuditFmt.Name(f, placement.DefName)} with {ContainerFill.TotalMols(after):#,##0.##} mol of gas";
}

/// <summary>
/// Delete a batch of parts. Undo puts each one back at the index it held, not on the end: document order decides
/// what the build-order check takes to have been built first, and it is the order the export writes
/// <c>aItems</c> in, so appending on the way back changes the design (see <see cref="ShipDocument.Restore"/>).
/// </summary>
public sealed class RemoveCommand(IReadOnlyList<Placement> placements) : IDocCommand, IAuditDescribable
{
    // Captured in Do rather than at construction, so a redo re-reads the positions the parts hold by then.
    private (int Index, long Seq)[] _slots = [];

    public void Do(ShipDocument doc)
    {
        using var _ = doc.SuspendChanged();
        _slots = [.. placements.Select(p => (doc.IndexOf(p), doc.OrderOf(p.Id)))];
        foreach (var p in placements) doc.Remove(p);
    }

    public void Undo(ShipDocument doc)
    {
        using var _ = doc.SuspendChanged();
        // Lowest slot first. Each index was taken with every one of these parts still in the list, so it is only
        // correct again once the ones below it are back and before the ones above it are.
        foreach (var i in Enumerable.Range(0, placements.Count).OrderBy(i => _slots[i].Index))
            doc.Restore(placements[i], _slots[i].Index, _slots[i].Seq);
    }

    public void Touch(ChangeSet into) => into.Add(placements);

    public string Describe(Func<string, string?> f) =>
        placements.Count == 1
            ? $"Removed {AuditFmt.Name(f, placements[0].DefName)} {AuditFmt.At(placements[0].X, placements[0].Y)}"
            : $"Removed {AuditFmt.Batch(placements.Select(p => p.DefName), f)}";
}

/// <summary>
/// Send parts to the end of the build order, leaving their poses and their draw order alone.
///
/// <para>The sweep in <see cref="ProblemScan"/> looks for <i>some</i> order that builds every part legally, but it
/// never un-places anything, so it cannot find an order that needs a part to go down after something already
/// standing where the part wants clear space. A rack under an overhead bin is the reported case: legal in game if
/// built in the right sequence, and unreachable for a sweep that meets the rack first. This is how a design says
/// which one goes up last (#67).</para>
///
/// <para><b>Its reach is one build rank.</b> The sweep sorts by build rank first and only breaks ties on document
/// order, so this moves a part to the end of its own class (docking, floors, walls, fixtures) rather than to the
/// end of the ship. That is the order the game builds in anyway.</para>
/// </summary>
public sealed class BuildLastCommand(IReadOnlyList<Placement> placements) : IDocCommand, IAuditDescribable
{
    // Where each part sat before, captured in Do so a redo re-reads the positions they hold by then.
    private int[] _before = [];

    public void Do(ShipDocument doc)
    {
        using var _ = doc.SuspendChanged();
        _before = [.. placements.Select(doc.IndexOf)];
        // Lowest first, each to the very end, so the batch keeps the relative order it already had.
        foreach (var i in Ascending()) doc.SetBuildIndex(placements[i], doc.Placements.Count - 1);
    }

    public void Undo(ShipDocument doc)
    {
        using var _ = doc.SuspendChanged();
        // Lowest original index first, for the reason RemoveCommand.Undo gives: a slot is only correct again
        // once everything that belongs below it is back.
        foreach (var i in Ascending()) doc.SetBuildIndex(placements[i], _before[i]);
    }

    private IEnumerable<int> Ascending() =>
        Enumerable.Range(0, placements.Count).OrderBy(i => _before[i]);

    public void Touch(ChangeSet into) => into.Add(placements);

    public string Describe(Func<string, string?> f) =>
        placements.Count == 1
            ? $"Set {AuditFmt.Name(f, placements[0].DefName)} {AuditFmt.At(placements[0].X, placements[0].Y)} to build last"
            : $"Set {AuditFmt.Batch(placements.Select(p => p.DefName), f)} to build last";
}

/// <summary>
/// Shift a batch of parts by a fixed offset. Undo restores the poses <b>and</b> the given-ness the parts
/// held beforehand: moving an imported part re-authors it (the placement law then judges it, and the bill
/// counts it as new construction), so an undo that only put the tiles back would leave the design
/// permanently changed by a nudge nobody kept.
/// </summary>
public sealed class MoveCommand(IReadOnlyList<Placement> placements, int dx, int dy) : IDocCommand, IAuditDescribable
{
    private readonly bool[] _given = [.. placements.Select(p => p.IsGiven)];

    public void Do(ShipDocument doc)
    {
        using var _ = doc.SuspendChanged();
        foreach (var p in placements) doc.MoveTo(p, p.X + dx, p.Y + dy);
    }

    public void Undo(ShipDocument doc)
    {
        using var _ = doc.SuspendChanged();
        for (var i = 0; i < placements.Count; i++)
            doc.MoveTo(placements[i], placements[i].X - dx, placements[i].Y - dy, _given[i]);
    }

    public void Touch(ChangeSet into) => into.Add(placements);

    public string Describe(Func<string, string?> f) =>
        placements.Count == 1
            ? $"Moved {AuditFmt.Name(f, placements[0].DefName)} {AuditFmt.By(dx, dy)}"
            : $"Moved {AuditFmt.Batch(placements.Select(p => p.DefName), f)} {AuditFmt.By(dx, dy)}";
}

/// <summary>
/// Apply explicit (x,y,rot) poses to a batch of parts as one step — the group rotation of
/// a multi-part selection, where every part both moves and turns. Reversible to the parts'
/// prior poses and given-ness (stored at construction, before Do runs; see <see cref="MoveCommand"/>).
/// </summary>
public sealed class SetPosesCommand : IDocCommand, IAuditDescribable
{
    private readonly Placement[] _parts;
    private readonly (int X, int Y, int Rot)[] _after;
    private readonly (int X, int Y, int Rot)[] _before;
    private readonly bool[] _given;

    public string Describe(Func<string, string?> f) =>
        _parts.Length == 1
            ? $"Moved {AuditFmt.Name(f, _parts[0].DefName)} to {_after[0].X}, {_after[0].Y}{AuditFmt.Turned(GridMath.Norm(_after[0].Rot))}"
            : $"Moved and turned {AuditFmt.Batch(_parts.Select(p => p.DefName), f)}";

    public void Touch(ChangeSet into) => into.Add(_parts);

    public SetPosesCommand(IReadOnlyList<(Placement Part, int X, int Y, int Rot)> poses)
    {
        _parts = new Placement[poses.Count];
        _after = new (int, int, int)[poses.Count];
        _before = new (int, int, int)[poses.Count];
        _given = new bool[poses.Count];
        for (var i = 0; i < poses.Count; i++)
        {
            _parts[i] = poses[i].Part;
            _after[i] = (poses[i].X, poses[i].Y, poses[i].Rot);
            _before[i] = (poses[i].Part.X, poses[i].Part.Y, poses[i].Part.Rot);
            _given[i] = poses[i].Part.IsGiven;
        }
    }

    public void Do(ShipDocument doc)
    {
        using var _ = doc.SuspendChanged();
        for (var i = 0; i < _parts.Length; i++) doc.SetPose(_parts[i], _after[i].X, _after[i].Y, _after[i].Rot);
    }

    public void Undo(ShipDocument doc)
    {
        using var _ = doc.SuspendChanged();
        for (var i = 0; i < _parts.Length; i++) doc.SetPose(_parts[i], _before[i].X, _before[i].Y, _before[i].Rot, _given[i]);
    }
}

/// <summary>Rotation preserving the footprint center (as close as integer tiles allow).</summary>
public sealed class RotateCommand : IDocCommand, IAuditDescribable
{
    private readonly Placement _p;
    private readonly (int X, int Y, int Rot) _before;
    private readonly (int X, int Y, int Rot) _after;
    private readonly bool _given;   // given-ness before the turn, restored on undo (see MoveCommand)

    public string Describe(Func<string, string?> f) =>
        $"Rotated {AuditFmt.Name(f, _p.DefName)} {AuditFmt.At(_after.X, _after.Y)} to {_after.Rot}°";

    public void Touch(ChangeSet into) => into.Add(_p);

    public RotateCommand(ShipDocument doc, Placement p, int delta)
    {
        _p = p;
        _before = (p.X, p.Y, p.Rot);
        _given = p.IsGiven;
        var (w, h) = doc.FootprintOf(p);
        var newRot = GridMath.Norm(p.Rot + delta);
        var part = doc.Part(p);
        var (nw, nh) = part is null ? (w, h) : GridMath.Size(part.Item.Width, part.Item.Height, newRot);
        _after = (p.X + (w - nw) / 2, p.Y + (h - nh) / 2, newRot);
    }

    public void Do(ShipDocument doc) => doc.SetPose(_p, _after.X, _after.Y, _after.Rot);
    public void Undo(ShipDocument doc) => doc.SetPose(_p, _before.X, _before.Y, _before.Rot, _given);
}

// ---- zone commands (crew/trade zones — see ShipZone) ----

/// <summary>Add a new zone to the design.</summary>
public sealed class CreateZoneCommand(ShipZone zone) : IDocCommand, IAuditDescribable
{
    public ShipZone Zone => zone;
    public void Do(ShipDocument doc) => doc.AddZone(zone);
    public void Undo(ShipDocument doc) => doc.RemoveZone(zone);
    public void Touch(ChangeSet into) => into.AddTiles(zone.Tiles);
    public string Describe(Func<string, string?> f) => $"Created zone “{zone.Name}”";
}

/// <summary>Delete a zone, remembering its list position so undo restores order exactly.</summary>
public sealed class DeleteZoneCommand : IDocCommand, IAuditDescribable
{
    private readonly ShipZone _zone;
    private readonly int _index;
    public DeleteZoneCommand(ShipDocument doc, ShipZone zone) { _zone = zone; _index = doc.IndexOfZone(zone); }
    public void Do(ShipDocument doc) => doc.RemoveZone(_zone);
    public void Undo(ShipDocument doc) => doc.InsertZone(_index < 0 ? doc.Zones.Count : _index, _zone);
    public void Touch(ChangeSet into) => into.AddTiles(_zone.Tiles);
    public string Describe(Func<string, string?> f) => $"Deleted zone “{_zone.Name}”";
}

/// <summary>Replace a zone's covered tiles — one paint/erase/box/room-fill stroke, committed as a single step.
/// The caller snapshots the before/after tile sets (copies), so Do/Undo are plain assignments.</summary>
public sealed class SetZoneTilesCommand(ShipZone zone, IReadOnlyCollection<(int X, int Y)> before, IReadOnlyCollection<(int X, int Y)> after) : IDocCommand, IAuditDescribable
{
    public void Do(ShipDocument doc) => doc.SetZoneTiles(zone, after);
    public void Undo(ShipDocument doc) => doc.SetZoneTiles(zone, before);

    // The tiles the stroke changed hands, not the whole zone: an undone stroke at one end of a long corridor zone
    // should point at that end.
    public void Touch(ChangeSet into)
    {
        var changed = new HashSet<(int X, int Y)>(before);
        changed.SymmetricExceptWith(after);
        into.AddTiles(changed);
    }

    public string Describe(Func<string, string?> f) =>
        $"Painted zone “{zone.Name}” from {AuditFmt.Count(before.Count, "tile", "tiles")} to {after.Count}";
}

/// <summary>Replace a zone's editable non-tile fields (rename / recolour / type / role / advanced) as one step.</summary>
public sealed class SetZoneMetaCommand(ShipZone zone, ZoneMeta before, ZoneMeta after) : IDocCommand, IAuditDescribable
{
    public void Do(ShipDocument doc) => doc.SetZoneMeta(zone, after);
    public void Undo(ShipDocument doc) => doc.SetZoneMeta(zone, before);
    public void Touch(ChangeSet into) => into.AddTiles(zone.Tiles);
    public string Describe(Func<string, string?> f) =>
        before.Name != after.Name ? $"Renamed zone “{before.Name}” to “{after.Name}”"
                                   : $"Changed zone “{after.Name}”";
}

// ---- device-link commands (signal connections — see DeviceLink) ----

/// <summary>
/// The parts at the two ends of a wire, looked up when the command first runs. A link holds only ids, so without
/// this a wiring step could neither name its parts in the log nor point at them after an undo. Looked up in Do
/// rather than at construction, as <see cref="RemoveCommand"/> captures its slots; a wire is only ever made or cut
/// between two parts on the plan, so both are there at that moment.
/// </summary>
internal sealed class LinkEnds(Guid source, Guid target)
{
    private Placement? _source;
    private Placement? _target;

    public void Resolve(ShipDocument doc)
    {
        _source ??= doc.ById(source);
        _target ??= doc.ById(target);
    }

    public void Touch(ChangeSet into)
    {
        if (_source is { } s) into.Add(s);
        if (_target is { } t) into.Add(t);
    }

    public string Source(Func<string, string?> f) => Name(_source, f);
    public string Target(Func<string, string?> f) => Name(_target, f);

    private static string Name(Placement? p, Func<string, string?> f) => p is null ? "a part" : AuditFmt.Name(f, p.DefName);
}

/// <summary>Add a signal connection between two devices.</summary>
public sealed class AddLinkCommand(DeviceLink link) : IDocCommand, IAuditDescribable
{
    private readonly LinkEnds _ends = new(link.Source, link.Target);

    public void Do(ShipDocument doc)
    {
        _ends.Resolve(doc);
        doc.AddLink(link);
    }

    public void Undo(ShipDocument doc) => doc.RemoveLink(link);
    public void Touch(ChangeSet into) => _ends.Touch(into);
    public string Describe(Func<string, string?> f) => $"Connected {_ends.Source(f)} to {_ends.Target(f)}";
}

/// <summary>Remove a signal connection.</summary>
public sealed class RemoveLinkCommand(DeviceLink link) : IDocCommand, IAuditDescribable
{
    private readonly LinkEnds _ends = new(link.Source, link.Target);

    public void Do(ShipDocument doc)
    {
        _ends.Resolve(doc);
        doc.RemoveLink(link);
    }

    public void Undo(ShipDocument doc) => doc.AddLink(link);
    public void Touch(ChangeSet into) => _ends.Touch(into);
    public string Describe(Func<string, string?> f) => $"Disconnected {_ends.Source(f)} from {_ends.Target(f)}";
}

// ---- sensor-link commands (a sensor driving a device — see SensorLink) ----

/// <summary>
/// Point a device at the sensor it should follow. A device has a single <c>strInput01</c>, so this <b>displaces</b>
/// whatever sensor it followed before; both halves are one undo step, or undoing a re-point would leave the device
/// following nothing rather than following what it used to.
/// </summary>
public sealed class AddSensorLinkCommand(SensorLink link, SensorLink? displaced) : IDocCommand, IAuditDescribable
{
    private readonly LinkEnds _ends = new(link.Source, link.Target);

    // The sensor the device stops following. Only its source end matters: the target is this link's target.
    private readonly LinkEnds? _was = displaced is { } d ? new(d.Source, d.Target) : null;

    /// <summary>The link this one pushed off the target, or null when the device was unwired. Resolved by the
    /// caller through <see cref="SensorLinks.Replacing"/> before the command is pushed.</summary>
    public SensorLink? Displaced => displaced;

    public void Do(ShipDocument doc)
    {
        _ends.Resolve(doc);
        _was?.Resolve(doc);
        if (displaced is { } old) doc.RemoveSensorLink(old);
        doc.AddSensorLink(link);
    }

    public void Undo(ShipDocument doc)
    {
        doc.RemoveSensorLink(link);
        if (displaced is { } old) doc.AddSensorLink(old);
    }

    public void Touch(ChangeSet into)
    {
        _ends.Touch(into);
        _was?.Touch(into);   // the old sensor changed too: it no longer drives anything here
    }

    public string Describe(Func<string, string?> f) =>
        $"Set {_ends.Target(f)} to follow {_ends.Source(f)}" + (_was is { } was ? $" instead of {was.Source(f)}" : "");
}

/// <summary>Stop a device following its sensor.</summary>
public sealed class RemoveSensorLinkCommand(SensorLink link) : IDocCommand, IAuditDescribable
{
    private readonly LinkEnds _ends = new(link.Source, link.Target);

    public void Do(ShipDocument doc)
    {
        _ends.Resolve(doc);
        doc.RemoveSensorLink(link);
    }

    public void Undo(ShipDocument doc) => doc.AddSensorLink(link);
    public void Touch(ChangeSet into) => _ends.Touch(into);
    public string Describe(Func<string, string?> f) => $"Stopped {_ends.Target(f)} following {_ends.Source(f)}";
}

/// <summary>Set a device's own panel settings — bus knob and modes (see <see cref="DeviceSettings"/>).</summary>
public sealed class SetDeviceSettingsCommand(Placement part, DeviceSettings? before, DeviceSettings? after)
    : IDocCommand, IAuditDescribable
{
    public void Do(ShipDocument doc) => doc.SetDeviceSettings(part, after);
    public void Undo(ShipDocument doc) => doc.SetDeviceSettings(part, before);
    public void Touch(ChangeSet into) => into.Add(part);

    // The bus reads as the panel's own Off / Auto / On, which is what the enum is named.
    public string Describe(Func<string, string?> f)
    {
        var s = after ?? DeviceSettings.Default;
        var modes = new[] { (s.Turbo, "turbo"), (s.Reverse, "reverse"), (s.Slow, "slow mode") }
            .Where(m => m.Item1).Select(m => m.Item2).ToList();
        return $"Set {AuditFmt.Name(f, part.DefName)} {AuditFmt.At(part.X, part.Y)} to {s.Bus}" +
               (modes.Count > 0 ? $" with {string.Join(" and ", modes)}" : "");
    }
}

/// <summary>Set a reactor's own control panel — knobs, switches and sliders (see <see cref="ReactorSettings"/>).</summary>
public sealed class SetReactorSettingsCommand(Placement part, ReactorSettings? before, ReactorSettings? after)
    : IDocCommand, IAuditDescribable
{
    public void Do(ShipDocument doc) => doc.SetReactorSettings(part, after);
    public void Undo(ShipDocument doc) => doc.SetReactorSettings(part, before);
    public void Touch(ChangeSet into) => into.Add(part);

    // The bus in capitals because the game's knob is labelled that way (OFF, BATT, CHRG), as the panel shows it.
    public string Describe(Func<string, string?> f)
    {
        var s = after ?? ReactorSettings.Default;
        return $"Changed the panel of {AuditFmt.Name(f, part.DefName)} {AuditFmt.At(part.X, part.Y)} " +
               $"(bus {s.Bus.ToString().ToUpperInvariant()}, ignition {(s.Ignition ? "on" : "off")})";
    }
}

/// <summary>Set a weapon's MFD page — firing group, firing mode and a cannon's target select (see
/// <see cref="WeaponSettings"/>).</summary>
public sealed class SetWeaponSettingsCommand(Placement part, WeaponSettings? before, WeaponSettings? after)
    : IDocCommand, IAuditDescribable
{
    public void Do(ShipDocument doc) => doc.SetWeaponSettings(part, after);
    public void Undo(ShipDocument doc) => doc.SetWeaponSettings(part, before);
    public void Touch(ChangeSet into) => into.Add(part);
    public string Describe(Func<string, string?> f)
    {
        var group = after?.Group is { } g
            ? $"firing group {WeaponPanel.ToDisplay(g)}"
            : "its stock firing group";
        return $"Set {AuditFmt.Name(f, part.DefName)} {AuditFmt.At(part.X, part.Y)} to {group}";
    }
}

// ---- loose-object commands (items dropped on the floor — see LooseObject) ----

/// <summary>Drop a loose item onto a tile.</summary>
public sealed class PlaceLooseCommand(LooseObject obj) : IDocCommand, IAuditDescribable
{
    public LooseObject Obj => obj;
    public void Do(ShipDocument doc) => doc.AddLoose(obj);
    public void Undo(ShipDocument doc) => doc.RemoveLoose(obj);
    public void Touch(ChangeSet into) => into.Add(obj);
    public string Describe(Func<string, string?> f) =>
        $"Dropped {AuditFmt.Name(f, obj.DefName)}{(obj.Quantity > 1 ? $" ×{obj.Quantity}" : "")} {AuditFmt.At(obj.X, obj.Y)}";
}

/// <summary>Retune a loot spawner's control panel (#55).</summary>
public sealed class SetSpawnerCommand(LooseObject obj, SpawnerSettings? before, SpawnerSettings? after)
    : IDocCommand, IAuditDescribable
{
    public void Do(ShipDocument doc) => doc.SetSpawner(obj, after);
    public void Undo(ShipDocument doc) => doc.SetSpawner(obj, before);
    public void Touch(ChangeSet into) => into.Add(obj);

    // The target by its name, which is what the spawner panel and its picker show.
    public string Describe(Func<string, string?> f) =>
        $"Changed {AuditFmt.Name(f, obj.DefName)} {AuditFmt.At(obj.X, obj.Y)} to spawn {(after ?? SpawnerSettings.Default).Target}";
}

/// <summary>Remove a loose item from its tile. Undo puts it back at its own index, for the reason
/// <see cref="RemoveCommand"/> gives.</summary>
public sealed class RemoveLooseCommand(LooseObject obj) : IDocCommand, IAuditDescribable
{
    private int _index = -1;
    private long _seq;

    public LooseObject Obj => obj;

    public void Do(ShipDocument doc)
    {
        _index = doc.IndexOfLoose(obj);
        _seq = doc.OrderOf(obj.Id);
        doc.RemoveLoose(obj);
    }

    public void Undo(ShipDocument doc) => doc.RestoreLoose(obj, _index, _seq);
    public void Touch(ChangeSet into) => into.Add(obj);
    public string Describe(Func<string, string?> f) =>
        $"Removed loose {AuditFmt.Name(f, obj.DefName)} {AuditFmt.At(obj.X, obj.Y)}";
}

/// <summary>
/// Reposition loose items as one undo step — the loose twin of <see cref="SetPosesCommand"/>, and what a group
/// move, rotate or flip pushes for the loose half of a mixed selection. Poses are set in place, so each object
/// keeps its identity and the selection still points at it when the drag lands.
///
/// <para>The caller clears the destinations first (see <see cref="ShipDocument.LooseFreeAt"/>): one loose item per
/// tile is the overlay's one hard invariant, and a transform that would break it is refused before it is pushed
/// rather than half-applied here.</para>
/// </summary>
public sealed class SetLoosePosesCommand : IDocCommand, IAuditDescribable
{
    private readonly LooseObject[] _objs;
    private readonly (int X, int Y, int Rot)[] _after;
    private readonly (int X, int Y, int Rot)[] _before;

    public string Describe(Func<string, string?> f) =>
        _objs.Length == 1
            ? $"Moved loose {AuditFmt.Name(f, _objs[0].DefName)} to {_after[0].X}, {_after[0].Y}{AuditFmt.Turned(GridMath.Norm(_after[0].Rot))}"
            : $"Moved and turned {AuditFmt.Batch(_objs.Select(o => o.DefName), f, "loose item", "loose items")}";

    public void Touch(ChangeSet into)
    {
        foreach (var o in _objs) into.Add(o);
    }

    public SetLoosePosesCommand(IReadOnlyList<(LooseObject Obj, int X, int Y, int Rot)> poses)
    {
        _objs = new LooseObject[poses.Count];
        _after = new (int, int, int)[poses.Count];
        _before = new (int, int, int)[poses.Count];
        for (var i = 0; i < poses.Count; i++)
        {
            _objs[i] = poses[i].Obj;
            _after[i] = (poses[i].X, poses[i].Y, poses[i].Rot);
            _before[i] = (poses[i].Obj.X, poses[i].Obj.Y, poses[i].Obj.Rot);
        }
    }

    public void Do(ShipDocument doc) => doc.SetLoosePoses(Batch(_after));
    public void Undo(ShipDocument doc) => doc.SetLoosePoses(Batch(_before));

    private List<(LooseObject, int, int, int)> Batch((int X, int Y, int Rot)[] poses)
    {
        var batch = new List<(LooseObject, int, int, int)>(_objs.Length);
        for (var i = 0; i < _objs.Length; i++) batch.Add((_objs[i], poses[i].X, poses[i].Y, poses[i].Rot));
        return batch;
    }
}

/// <summary>Change a loose item's stacked quantity (Change Quantity). Set in place, so the object's identity — and
/// thus the selection pointing at it — survives.</summary>
/// <summary>
/// Swap what a loose deck item holds — the inventory editor's result for a backpack or suit lying on the floor.
/// The mirror of <see cref="SetCargoCommand"/> for a <see cref="LooseObject"/>: the caller hands in both trees, so
/// Do/Undo are a plain assignment.
/// </summary>
public sealed class SetLooseCargoCommand(LooseObject obj, IReadOnlyList<CargoItem> before, IReadOnlyList<CargoItem> after)
    : IDocCommand, IAuditDescribable
{
    public void Do(ShipDocument doc) => doc.SetLooseCargo(obj, after);
    public void Undo(ShipDocument doc) => doc.SetLooseCargo(obj, before);
    public void Touch(ChangeSet into) => into.Add(obj);
    public string Describe(Func<string, string?> f) =>
        $"Changed the contents of loose {AuditFmt.Name(f, obj.DefName)} {AuditFmt.At(obj.X, obj.Y)} " +
        $"from {AuditFmt.Count(before.Count, "item", "items")} to {after.Count}";
}

public sealed class SetLooseQuantityCommand(LooseObject obj, int before, int after) : IDocCommand, IAuditDescribable
{
    public void Do(ShipDocument doc) => doc.SetLooseQuantity(obj, after);
    public void Undo(ShipDocument doc) => doc.SetLooseQuantity(obj, before);
    public void Touch(ChangeSet into) => into.Add(obj);
    public string Describe(Func<string, string?> f) =>
        $"Changed the quantity of {AuditFmt.Name(f, obj.DefName)} {AuditFmt.At(obj.X, obj.Y)} from {before} to {after}";
}

/// <summary>Which of the draw-order menu items a <see cref="SetZOrderCommand"/> came from.</summary>
public enum ZOrderStep { Forward, Back, Reset }

/// <summary>
/// Re-stack what shares a tile: the bias changes a Move Back / Move Forward / Reset order produced (see
/// <see cref="ZOrder"/>), applied as one undo step because a nudge writes an explicit order across the whole pile,
/// not just the part you nudged. Purely cosmetic — no geometry moves, so nothing is re-analysed.
///
/// <para><paramref name="item"/> is the one the menu was opened on. It is named separately because it need not be
/// among the changes at all: a nudge rewrites the biases of whatever in the pile is out of sequence, which can be
/// everything but the item itself.</para>
/// </summary>
public sealed class SetZOrderCommand(RenderItem item, IReadOnlyList<ZOrder.BiasChange> changes, ZOrderStep step)
    : IDocCommand, IAuditDescribable
{
    public void Do(ShipDocument doc) => Apply(doc, redo: true);
    public void Undo(ShipDocument doc) => Apply(doc, redo: false);

    private void Apply(ShipDocument doc, bool redo)
    {
        using var _ = doc.SuspendChanged();   // one repaint for the pile, not one per member
        foreach (var c in changes)
        {
            var bias = redo ? c.After : c.Before;
            if (c.Item.Placement is { } p) doc.SetZBias(p, bias);
            else if (c.Item.Loose is { } lo) doc.SetZBias(lo, bias);
        }
    }

    // The item rather than the pile: the rest of the pile only moved to make room.
    public void Touch(ChangeSet into) => into.Add(item);

    public string Describe(Func<string, string?> f)
    {
        var what = $"{AuditFmt.Name(f, item.DefName)} {AuditFmt.At(item.X, item.Y)}";
        return step switch
        {
            ZOrderStep.Forward => $"Moved {what} forward",
            ZOrderStep.Back => $"Moved {what} back",
            _ => $"Reset the draw order of {what}",
        };
    }
}

/// <summary>Give a placed part a name of its own, or clear it back to the stock one (see
/// <see cref="Rename"/>). Nothing about the part's geometry changes, so no re-analysis is implied.</summary>
public sealed class SetCustomNameCommand(Placement placement, string? before, string? after) : IDocCommand, IAuditDescribable
{
    public void Do(ShipDocument doc) => doc.SetCustomName(placement, after);
    public void Undo(ShipDocument doc) => doc.SetCustomName(placement, before);
    public void Touch(ChangeSet into) => into.Add(placement);
    public string Describe(Func<string, string?> f) =>
        after is null
            ? $"Cleared the name on {AuditFmt.Name(f, placement.DefName)} {AuditFmt.At(placement.X, placement.Y)}"
            : $"Named {AuditFmt.Name(f, placement.DefName)} {AuditFmt.At(placement.X, placement.Y)} \"{after}\"";
}

/// <summary>Give a loose deck item a name of its own, or clear it back to the stock one — the loose twin of
/// <see cref="SetCustomNameCommand"/> (see <see cref="Rename"/>). Nothing about the item's pose changes, so no
/// re-analysis is implied.</summary>
public sealed class SetLooseCustomNameCommand(LooseObject obj, string? before, string? after) : IDocCommand, IAuditDescribable
{
    public void Do(ShipDocument doc) => doc.SetCustomName(obj, after);
    public void Undo(ShipDocument doc) => doc.SetCustomName(obj, before);
    public void Touch(ChangeSet into) => into.Add(obj);
    public string Describe(Func<string, string?> f) =>
        after is null
            ? $"Cleared the name on the loose {AuditFmt.Name(f, obj.DefName)}"
            : $"Named the loose {AuditFmt.Name(f, obj.DefName)} \"{after}\"";
}
