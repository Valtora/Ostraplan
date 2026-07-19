namespace Ostraplan.Core;

public interface IDocCommand
{
    void Do(ShipDocument doc);
    void Undo(ShipDocument doc);
}

/// <summary>
/// A command that can render itself as a detailed one-line activity-log entry — the affected part's friendly
/// name, its tile and rotation, and batch counts — instead of the bare type name. <see cref="AuditLog"/> uses
/// this (given a def→friendly-name resolver) so the trail reads "Place Nav Station @(12,7)" rather than a
/// context-free "Place", which is what makes a filed bug report traceable. Commands that carry no useful detail
/// (device links, which hold only ids) simply don't implement it and fall back to the terse type name.
/// </summary>
public interface IAuditDescribable
{
    /// <param name="friendlyOf">Resolves a def name to its friendly name; may return null/empty when unknown.</param>
    string Describe(Func<string, string?> friendlyOf);
}

/// <summary>Shared formatting helpers for <see cref="IAuditDescribable"/> log lines, so every command renders
/// tiles, rotations and batches the same way.</summary>
internal static class AuditFmt
{
    public static string Name(Func<string, string?> f, string def) => f(def) is { Length: > 0 } n ? n : def;
    public static string At(int x, int y) => $"@({x},{y})";
    public static string Rot(int rot) => rot == 0 ? "" : $" r{rot}";
    public static string By(int dx, int dy) => $"({dx:+0;-0;+0},{dy:+0;-0;+0})";

    /// <summary>A compact "×N (Name, Name, …)" summary of a batch of defs, distinct friendly names capped at 3.</summary>
    public static string Batch(IEnumerable<string> defs, Func<string, string?> f)
    {
        var names = defs.Select(d => Name(f, d)).ToList();
        var distinct = names.Distinct(StringComparer.Ordinal).ToList();
        var shown = string.Join(", ", distinct.Take(3)) + (distinct.Count > 3 ? ", …" : "");
        return $"×{names.Count} ({shown})";
    }
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

    public void Push(ShipDocument doc, IDocCommand cmd)
    {
        cmd.Do(doc);
        PushExecuted(cmd);
    }

    /// <summary>Record a command whose Do already ran (live paint strokes commit this way).</summary>
    public void PushExecuted(IDocCommand cmd)
    {
        _undo.Push(cmd);
        _redo.Clear();
        if (_savedDepth > _undo.Count - 1) _savedDepth = -1;   // saved state no longer reachable
        StateChanged?.Invoke();
        Applied?.Invoke(cmd, CommandAction.Do);
    }

    public void Undo(ShipDocument doc)
    {
        if (_undo.Count == 0) return;
        var cmd = _undo.Pop();
        cmd.Undo(doc);
        _redo.Push(cmd);
        StateChanged?.Invoke();
        Applied?.Invoke(cmd, CommandAction.Undo);
    }

    public void Redo(ShipDocument doc)
    {
        if (_redo.Count == 0) return;
        var cmd = _redo.Pop();
        cmd.Do(doc);
        _undo.Push(cmd);
        StateChanged?.Invoke();
        Applied?.Invoke(cmd, CommandAction.Redo);
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

    // Spell out a small batch (a form swap is Remove + Place, worth seeing in full); summarise a large one.
    public string Describe(Func<string, string?> friendlyOf)
    {
        var parts = commands.OfType<IAuditDescribable>().Select(c => c.Describe(friendlyOf)).ToList();
        if (parts.Count == 0) return $"{commands.Count} edits";
        if (parts.Count <= 3) return string.Join(" + ", parts);
        return $"{parts.Count} edits: {parts[0]}, …";
    }
}

public sealed class PlaceCommand(Placement placement) : IDocCommand, IAuditDescribable
{
    public Placement Placement => placement;
    public void Do(ShipDocument doc) => doc.Add(placement);
    public void Undo(ShipDocument doc) => doc.Remove(placement);
    public string Describe(Func<string, string?> f) =>
        $"Place {AuditFmt.Name(f, placement.DefName)} {AuditFmt.At(placement.X, placement.Y)}{AuditFmt.Rot(placement.Rot)}";
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
    public string Describe(Func<string, string?> f) =>
        $"Edit contents of {AuditFmt.Name(f, placement.DefName)} ({before.Count} → {after.Count} items)";
}

public sealed class RemoveCommand(IReadOnlyList<Placement> placements) : IDocCommand, IAuditDescribable
{
    public void Do(ShipDocument doc)
    {
        using var _ = doc.SuspendChanged();
        foreach (var p in placements) doc.Remove(p);
    }

    public void Undo(ShipDocument doc)
    {
        using var _ = doc.SuspendChanged();
        foreach (var p in placements) doc.Add(p);
    }

    public string Describe(Func<string, string?> f) =>
        placements.Count == 1
            ? $"Remove {AuditFmt.Name(f, placements[0].DefName)} {AuditFmt.At(placements[0].X, placements[0].Y)}"
            : $"Remove {AuditFmt.Batch(placements.Select(p => p.DefName), f)}";
}

public sealed class MoveCommand(IReadOnlyList<Placement> placements, int dx, int dy) : IDocCommand, IAuditDescribable
{
    public void Do(ShipDocument doc)
    {
        using var _ = doc.SuspendChanged();
        foreach (var p in placements) doc.MoveTo(p, p.X + dx, p.Y + dy);
    }

    public void Undo(ShipDocument doc)
    {
        using var _ = doc.SuspendChanged();
        foreach (var p in placements) doc.MoveTo(p, p.X - dx, p.Y - dy);
    }

    public string Describe(Func<string, string?> f) =>
        placements.Count == 1
            ? $"Move {AuditFmt.Name(f, placements[0].DefName)} by {AuditFmt.By(dx, dy)}"
            : $"Move {AuditFmt.Batch(placements.Select(p => p.DefName), f)} by {AuditFmt.By(dx, dy)}";
}

/// <summary>
/// Apply explicit (x,y,rot) poses to a batch of parts as one step — the group rotation of
/// a multi-part selection, where every part both moves and turns. Reversible to the parts'
/// prior poses (stored at construction, before Do runs).
/// </summary>
public sealed class SetPosesCommand : IDocCommand, IAuditDescribable
{
    private readonly Placement[] _parts;
    private readonly (int X, int Y, int Rot)[] _after;
    private readonly (int X, int Y, int Rot)[] _before;

    public string Describe(Func<string, string?> f) =>
        _parts.Length == 1
            ? $"Move/rotate {AuditFmt.Name(f, _parts[0].DefName)} {AuditFmt.At(_after[0].X, _after[0].Y)} → r{_after[0].Rot}"
            : $"Transform {AuditFmt.Batch(_parts.Select(p => p.DefName), f)}";

    public SetPosesCommand(IReadOnlyList<(Placement Part, int X, int Y, int Rot)> poses)
    {
        _parts = new Placement[poses.Count];
        _after = new (int, int, int)[poses.Count];
        _before = new (int, int, int)[poses.Count];
        for (var i = 0; i < poses.Count; i++)
        {
            _parts[i] = poses[i].Part;
            _after[i] = (poses[i].X, poses[i].Y, poses[i].Rot);
            _before[i] = (poses[i].Part.X, poses[i].Part.Y, poses[i].Part.Rot);
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
        for (var i = 0; i < _parts.Length; i++) doc.SetPose(_parts[i], _before[i].X, _before[i].Y, _before[i].Rot);
    }
}

/// <summary>Rotation preserving the footprint center (as close as integer tiles allow).</summary>
public sealed class RotateCommand : IDocCommand, IAuditDescribable
{
    private readonly Placement _p;
    private readonly (int X, int Y, int Rot) _before;
    private readonly (int X, int Y, int Rot) _after;

    public string Describe(Func<string, string?> f) =>
        $"Rotate {AuditFmt.Name(f, _p.DefName)} {AuditFmt.At(_after.X, _after.Y)} → r{_after.Rot}";

    public RotateCommand(ShipDocument doc, Placement p, int delta)
    {
        _p = p;
        _before = (p.X, p.Y, p.Rot);
        var (w, h) = doc.FootprintOf(p);
        var newRot = GridMath.Norm(p.Rot + delta);
        var part = doc.Part(p);
        var (nw, nh) = part is null ? (w, h) : GridMath.Size(part.Item.Width, part.Item.Height, newRot);
        _after = (p.X + (w - nw) / 2, p.Y + (h - nh) / 2, newRot);
    }

    public void Do(ShipDocument doc) => doc.SetPose(_p, _after.X, _after.Y, _after.Rot);
    public void Undo(ShipDocument doc) => doc.SetPose(_p, _before.X, _before.Y, _before.Rot);
}

// ---- zone commands (crew/trade zones — see ShipZone) ----

/// <summary>Add a new zone to the design.</summary>
public sealed class CreateZoneCommand(ShipZone zone) : IDocCommand, IAuditDescribable
{
    public ShipZone Zone => zone;
    public void Do(ShipDocument doc) => doc.AddZone(zone);
    public void Undo(ShipDocument doc) => doc.RemoveZone(zone);
    public string Describe(Func<string, string?> f) => $"Create zone “{zone.Name}”";
}

/// <summary>Delete a zone, remembering its list position so undo restores order exactly.</summary>
public sealed class DeleteZoneCommand : IDocCommand, IAuditDescribable
{
    private readonly ShipZone _zone;
    private readonly int _index;
    public DeleteZoneCommand(ShipDocument doc, ShipZone zone) { _zone = zone; _index = doc.IndexOfZone(zone); }
    public void Do(ShipDocument doc) => doc.RemoveZone(_zone);
    public void Undo(ShipDocument doc) => doc.InsertZone(_index < 0 ? doc.Zones.Count : _index, _zone);
    public string Describe(Func<string, string?> f) => $"Delete zone “{_zone.Name}”";
}

/// <summary>Replace a zone's covered tiles — one paint/erase/box/room-fill stroke, committed as a single step.
/// The caller snapshots the before/after tile sets (copies), so Do/Undo are plain assignments.</summary>
public sealed class SetZoneTilesCommand(ShipZone zone, IReadOnlyCollection<(int X, int Y)> before, IReadOnlyCollection<(int X, int Y)> after) : IDocCommand, IAuditDescribable
{
    public void Do(ShipDocument doc) => doc.SetZoneTiles(zone, after);
    public void Undo(ShipDocument doc) => doc.SetZoneTiles(zone, before);
    public string Describe(Func<string, string?> f) =>
        $"Paint zone “{zone.Name}” ({before.Count} → {after.Count} tiles)";
}

/// <summary>Replace a zone's editable non-tile fields (rename / recolour / type / role / advanced) as one step.</summary>
public sealed class SetZoneMetaCommand(ShipZone zone, ZoneMeta before, ZoneMeta after) : IDocCommand, IAuditDescribable
{
    public void Do(ShipDocument doc) => doc.SetZoneMeta(zone, after);
    public void Undo(ShipDocument doc) => doc.SetZoneMeta(zone, before);
    public string Describe(Func<string, string?> f) =>
        before.Name != after.Name ? $"Rename zone “{before.Name}” → “{after.Name}”"
                                   : $"Edit zone “{after.Name}”";
}

// ---- device-link commands (signal connections — see DeviceLink) ----

/// <summary>Add a signal connection between two devices.</summary>
public sealed class AddLinkCommand(DeviceLink link) : IDocCommand
{
    public void Do(ShipDocument doc) => doc.AddLink(link);
    public void Undo(ShipDocument doc) => doc.RemoveLink(link);
}

/// <summary>Remove a signal connection.</summary>
public sealed class RemoveLinkCommand(DeviceLink link) : IDocCommand
{
    public void Do(ShipDocument doc) => doc.RemoveLink(link);
    public void Undo(ShipDocument doc) => doc.AddLink(link);
}

// ---- loose-object commands (items dropped on the floor — see LooseObject) ----

/// <summary>Drop a loose item onto a tile.</summary>
public sealed class PlaceLooseCommand(LooseObject obj) : IDocCommand, IAuditDescribable
{
    public LooseObject Obj => obj;
    public void Do(ShipDocument doc) => doc.AddLoose(obj);
    public void Undo(ShipDocument doc) => doc.RemoveLoose(obj);
    public string Describe(Func<string, string?> f) =>
        $"Drop {AuditFmt.Name(f, obj.DefName)}{(obj.Quantity > 1 ? $" ×{obj.Quantity}" : "")} {AuditFmt.At(obj.X, obj.Y)}";
}

/// <summary>Remove a loose item from its tile.</summary>
public sealed class RemoveLooseCommand(LooseObject obj) : IDocCommand, IAuditDescribable
{
    public void Do(ShipDocument doc) => doc.RemoveLoose(obj);
    public void Undo(ShipDocument doc) => doc.AddLoose(obj);
    public string Describe(Func<string, string?> f) =>
        $"Remove loose {AuditFmt.Name(f, obj.DefName)} {AuditFmt.At(obj.X, obj.Y)}";
}

/// <summary>Change a loose item's stacked quantity (Change Quantity). Set in place, so the object's identity — and
/// thus the selection pointing at it — survives.</summary>
public sealed class SetLooseQuantityCommand(LooseObject obj, int before, int after) : IDocCommand, IAuditDescribable
{
    public void Do(ShipDocument doc) => doc.SetLooseQuantity(obj, after);
    public void Undo(ShipDocument doc) => doc.SetLooseQuantity(obj, before);
    public string Describe(Func<string, string?> f) =>
        $"Set {AuditFmt.Name(f, obj.DefName)} quantity {before} → {after}";
}
