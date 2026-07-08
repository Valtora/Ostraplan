namespace Ostraplan.Core;

public interface IDocCommand
{
    void Do(ShipDocument doc);
    void Undo(ShipDocument doc);
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
public sealed class CompositeCommand(IReadOnlyList<IDocCommand> commands) : IDocCommand
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
}

public sealed class PlaceCommand(Placement placement) : IDocCommand
{
    public Placement Placement => placement;
    public void Do(ShipDocument doc) => doc.Add(placement);
    public void Undo(ShipDocument doc) => doc.Remove(placement);
}

/// <summary>
/// Swap a part's contained cargo tree — the inventory editor's add or remove. The caller computes the new tree
/// (via <see cref="CargoEdit"/>) and hands both trees in, so Do/Undo are a plain assignment either way. One
/// command covers add and remove because both are just "the container's contents are now this tree".
/// </summary>
public sealed class SetCargoCommand(Placement placement, IReadOnlyList<CargoItem> before, IReadOnlyList<CargoItem> after) : IDocCommand
{
    public void Do(ShipDocument doc) => doc.SetCargo(placement, after);
    public void Undo(ShipDocument doc) => doc.SetCargo(placement, before);
}

public sealed class RemoveCommand(IReadOnlyList<Placement> placements) : IDocCommand
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
}

public sealed class MoveCommand(IReadOnlyList<Placement> placements, int dx, int dy) : IDocCommand
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
}

/// <summary>
/// Apply explicit (x,y,rot) poses to a batch of parts as one step — the group rotation of
/// a multi-part selection, where every part both moves and turns. Reversible to the parts'
/// prior poses (stored at construction, before Do runs).
/// </summary>
public sealed class SetPosesCommand : IDocCommand
{
    private readonly Placement[] _parts;
    private readonly (int X, int Y, int Rot)[] _after;
    private readonly (int X, int Y, int Rot)[] _before;

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
public sealed class RotateCommand : IDocCommand
{
    private readonly Placement _p;
    private readonly (int X, int Y, int Rot) _before;
    private readonly (int X, int Y, int Rot) _after;

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
public sealed class CreateZoneCommand(ShipZone zone) : IDocCommand
{
    public ShipZone Zone => zone;
    public void Do(ShipDocument doc) => doc.AddZone(zone);
    public void Undo(ShipDocument doc) => doc.RemoveZone(zone);
}

/// <summary>Delete a zone, remembering its list position so undo restores order exactly.</summary>
public sealed class DeleteZoneCommand : IDocCommand
{
    private readonly ShipZone _zone;
    private readonly int _index;
    public DeleteZoneCommand(ShipDocument doc, ShipZone zone) { _zone = zone; _index = doc.IndexOfZone(zone); }
    public void Do(ShipDocument doc) => doc.RemoveZone(_zone);
    public void Undo(ShipDocument doc) => doc.InsertZone(_index < 0 ? doc.Zones.Count : _index, _zone);
}

/// <summary>Replace a zone's covered tiles — one paint/erase/box/room-fill stroke, committed as a single step.
/// The caller snapshots the before/after tile sets (copies), so Do/Undo are plain assignments.</summary>
public sealed class SetZoneTilesCommand(ShipZone zone, IReadOnlyCollection<(int X, int Y)> before, IReadOnlyCollection<(int X, int Y)> after) : IDocCommand
{
    public void Do(ShipDocument doc) => doc.SetZoneTiles(zone, after);
    public void Undo(ShipDocument doc) => doc.SetZoneTiles(zone, before);
}

/// <summary>Replace a zone's editable non-tile fields (rename / recolour / type / role / advanced) as one step.</summary>
public sealed class SetZoneMetaCommand(ShipZone zone, ZoneMeta before, ZoneMeta after) : IDocCommand
{
    public void Do(ShipDocument doc) => doc.SetZoneMeta(zone, after);
    public void Undo(ShipDocument doc) => doc.SetZoneMeta(zone, before);
}
