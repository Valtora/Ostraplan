namespace Ostraplan.Core;

public interface IDocCommand
{
    void Do(ShipDocument doc);
    void Undo(ShipDocument doc);
}

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
    }

    public void Undo(ShipDocument doc)
    {
        if (_undo.Count == 0) return;
        var cmd = _undo.Pop();
        cmd.Undo(doc);
        _redo.Push(cmd);
        StateChanged?.Invoke();
    }

    public void Redo(ShipDocument doc)
    {
        if (_redo.Count == 0) return;
        var cmd = _redo.Pop();
        cmd.Do(doc);
        _undo.Push(cmd);
        StateChanged?.Invoke();
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
        foreach (var cmd in commands) cmd.Do(doc);
    }

    public void Undo(ShipDocument doc)
    {
        for (var i = commands.Count - 1; i >= 0; i--) commands[i].Undo(doc);
    }
}

public sealed class PlaceCommand(Placement placement) : IDocCommand
{
    public Placement Placement => placement;
    public void Do(ShipDocument doc) => doc.Add(placement);
    public void Undo(ShipDocument doc) => doc.Remove(placement);
}

public sealed class RemoveCommand(IReadOnlyList<Placement> placements) : IDocCommand
{
    public void Do(ShipDocument doc)
    {
        foreach (var p in placements) doc.Remove(p);
    }

    public void Undo(ShipDocument doc)
    {
        foreach (var p in placements) doc.Add(p);
    }
}

public sealed class MoveCommand(IReadOnlyList<Placement> placements, int dx, int dy) : IDocCommand
{
    public void Do(ShipDocument doc)
    {
        foreach (var p in placements) doc.MoveTo(p, p.X + dx, p.Y + dy);
    }

    public void Undo(ShipDocument doc)
    {
        foreach (var p in placements) doc.MoveTo(p, p.X - dx, p.Y - dy);
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
