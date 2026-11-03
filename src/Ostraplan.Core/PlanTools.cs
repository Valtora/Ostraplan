namespace Ostraplan.Core;

/// <summary>
/// What a left click on the plan does, as the tool strip down the left of the canvas offers it (#79). Ordered top of
/// the strip to bottom.
///
/// <para>Every one of these was reachable before the strip, but some only through a key or a modifier held with the
/// mouse: the eyedropper on Alt, the hand on Space, the eraser on X. The strip puts each on a button that stays lit
/// while it is the tool in use. The keys are unchanged.</para>
/// </summary>
public enum PlanTool
{
    /// <summary>Click selects, drag moves the selection or boxes a new one. What the plan does with nothing in hand.</summary>
    Select,

    /// <summary>A part is armed: click places it, drag paints it.</summary>
    Place,

    /// <summary>Drag moves the view, as Space+drag and a middle drag do.</summary>
    Pan,

    /// <summary>The next click picks up the part under it as the brush, as Alt+click does.</summary>
    Pick,

    /// <summary>Click or drag deletes the topmost reachable thing on each tile (X).</summary>
    Erase,
}

public static class PlanTools
{
    /// <summary>
    /// The tool the strip shows lit. The eraser, the pick and the hand each say what the next click does whatever is
    /// in hand, so they outrank a held brush; a held brush outranks plain selection. Null while a click belongs to
    /// something the strip has no button for (a wiring pick, painting a zone), so no button claims a click it does
    /// not decide.
    /// </summary>
    public static PlanTool? Active(bool erase, bool pick, bool pan, bool armed, bool otherMode) =>
        erase ? PlanTool.Erase
        : pick ? PlanTool.Pick
        : pan ? PlanTool.Pan
        : armed ? PlanTool.Place
        : otherMode ? null
        : PlanTool.Select;

    /// <summary>The bold first line of a tool's tooltip: its name, and the key that reaches it where there is one.</summary>
    public static string Title(PlanTool tool) => tool switch
    {
        PlanTool.Select => "Select (Esc)",
        PlanTool.Place => "Place",
        PlanTool.Pan => "Pan (hold Space)",
        PlanTool.Pick => "Pick up a part (Alt+click)",
        PlanTool.Erase => "Erase (X)",
        _ => tool.ToString(),
    };

    /// <summary>The sentence under the title: what a click does with the tool in hand.</summary>
    public static string Tip(PlanTool tool) => tool switch
    {
        PlanTool.Select => "Click to select, drag to move the selection or box-select. Puts down whatever is in hand.",
        PlanTool.Place => "Place the part armed in the palette. With nothing armed, this arms the last part you placed.",
        PlanTool.Pan => "Drag to move the view. A middle drag or WASD pans with any tool.",
        PlanTool.Pick => "Click a part on the plan to arm it at its own rotation. A middle click does the same.",
        PlanTool.Erase => "Click or drag to delete the topmost part on each tile, as one undo step. Hidden and locked layers are left alone.",
        _ => "",
    };
}
