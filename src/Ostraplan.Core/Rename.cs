using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ostraplan.Core;

/// <summary>
/// The game's own object rename, read and written.
///
/// <para>Ostranauts lets a player rename any non-human object (<c>CondOwner.Rename</c>), which is how a hold full
/// of identical racks becomes "spare tool storage" and "spare reactor parts". It is stored as a <b>GUI-prop-map
/// panel</b> called <c>Rename</c> carrying a single <c>strName</c> key, alongside whatever other panels the item
/// has (<c>Electrical</c> on a wired device), and <c>Ship.SpawnItems</c> re-applies it on load through
/// <c>CondOwner.CheckForRename</c>. So it travels in <c>aItems[].aGPMSettings</c> in both a ship template and a
/// save, and needs no invention on Ostraplan's part.</para>
///
/// <para>Core ships already use it — the stock <i>Babak Refit</i> carries 51 of these names, "Pressurization SB"
/// on an electrical box and "Bow DPP Port" on an air pump among them — which is why import reads it as well as
/// export writing it. Before this, every such name was dropped on import.</para>
/// </summary>
public static class Rename
{
    /// <summary>The GPM panel the game keys a rename on (<c>CondOwner.Rename</c>).</summary>
    public const string Panel = "Rename";

    /// <summary>The single key inside that panel holding the chosen name.</summary>
    public const string NameKey = "strName";

    /// <summary>How long a name authored <b>in Ostraplan</b> may be. The game sets no limit; this one exists so a
    /// pasted essay cannot make a tooltip unreadable or bloat every write of the design. It applies to the rename
    /// box only — a longer name read off an imported ship is carried verbatim (see <see cref="OrNull"/>), or a
    /// no-op save write-back would truncate a name the player gave in game.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// Normalise a name the user typed: trimmed, collapsed to <see cref="MaxLength"/>, and <b>null when it is
    /// empty</b>. Null is the only representation of "no custom name", so a blank box clears the rename rather
    /// than writing an empty panel the game would have to interpret.
    /// </summary>
    public static string? Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var trimmed = name.Trim();
        return trimmed.Length <= MaxLength ? trimmed : trimmed[..MaxLength].TrimEnd();
    }

    /// <summary>
    /// A <b>stored</b> name as the game itself keeps it: null when null or empty, otherwise verbatim — no trim, no
    /// cap. <c>CondOwner.Rename</c> treats null/"" as "clear the panel" and stores anything else untouched, so this
    /// is the rule for names read off a ship or carried through the <c>.oplan</c>, where changing so much as a
    /// space would make a no-op write-back rewrite the player's own data.
    /// </summary>
    public static string? OrNull(string? name) => string.IsNullOrEmpty(name) ? null : name;

    /// <summary>
    /// Can this part be renamed? Anything that resolves to a def can. The game allows it on every object that is
    /// not a person (<c>CondOwner.Rename</c>), so Ostraplan offers it wherever the game does — on a
    /// <see cref="LooseObject"/> lying on the deck as much as on a <see cref="Placement"/>, since the game draws no
    /// such distinction and a design can mean a name on either (#38).
    ///
    /// <para>This was once narrowed to <b>containers</b> and <b>devices</b> (anything carrying a control panel,
    /// which is what a GUI-prop-map declaration means), on the theory that a name is only ever useful on something
    /// you would go looking for. The theory does not survive the data: the secondary airlock, every gas canister,
    /// the signal beacon, the cargo lift and every <i>damaged</i> variant of a part that does have a panel are none
    /// of those things, and each was left unnameable. Import already reads a name off any item at all
    /// (<see cref="FromItem"/>), so the narrow rule also left a name given in game to such a part shown in the
    /// inspector but impossible to edit or clear.</para>
    /// </summary>
    public static bool CanRename(PartDef? part) => part is not null;

    /// <summary>
    /// What a name <b>typed by the user</b> means for a part: the cleaned name (see <see cref="Clean"/>), or null
    /// when the box was left empty <i>or</i> holds the part's own stock name. Both mean "no rename": a part called
    /// what its def calls it carries no <c>Rename</c> panel, which is how the game stores it, and typing the stock
    /// name back is the obvious way to undo a rename in a field that shows you that name to begin with.
    /// </summary>
    public static string? Typed(string? input, PartDef? part) =>
        Clean(input) is { } name && name != part?.Friendly ? name : null;

    /// <summary>The name to show for a placement: its custom name when it has one, else its def's own.</summary>
    public static string Display(Placement placement, PartDef? part) =>
        placement.CustomName ?? part?.Friendly ?? placement.DefName;

    /// <summary>The same for a loose deck item (see <see cref="LooseObject.CustomName"/>). A separate overload
    /// rather than a shared interface: the two are unrelated types by design (structure against overlay), and the
    /// name is the only thing they have in common.</summary>
    public static string Display(LooseObject loose, PartDef? part) =>
        loose.CustomName ?? part?.Friendly ?? loose.DefName;

    /// <summary>
    /// The custom name baked into one <c>aItems</c> entry's <c>aGPMSettings</c>, or null when it carries none.
    /// <paramref name="item"/> is the item object from a ship template or a save. The value comes back
    /// <b>verbatim</b> (see <see cref="OrNull"/>): the game stores whatever the player typed, and normalising it
    /// here would make a no-op write-back rewrite it.
    /// </summary>
    /// <remarks>
    /// <c>dictGUIPropMap</c> is a <b>flat</b> array of alternating keys and values, which is how the game's
    /// <c>DataHandler.ConvertStringArrayToDict</c> reads it — its pair loop drops a trailing unpaired key, so an
    /// odd-length panel is read the same way here rather than off by one. When an item carries several
    /// <c>Rename</c> panels, the game's load merges them per key with the <b>last</b> winning
    /// (<c>Ship.CreatePart</c>), so the last panel's name is the one returned.
    /// </remarks>
    public static string? FromItem(JsonElement item)
    {
        if (!item.TryGetProperty("aGPMSettings", out var panels) || panels.ValueKind != JsonValueKind.Array)
            return null;

        string? found = null;
        foreach (var panel in panels.EnumerateArray())
        {
            if (panel.ValueKind != JsonValueKind.Object || Json.Str(panel, "strName") != Panel) continue;
            if (!panel.TryGetProperty("dictGUIPropMap", out var map) || map.ValueKind != JsonValueKind.Array) continue;

            var flat = map.EnumerateArray().ToArray();
            for (var i = 0; i + 1 < flat.Length; i += 2)
                if (flat[i].ValueKind == JsonValueKind.String && flat[i].GetString() == NameKey)
                    found = OrNull(flat[i + 1].ValueKind == JsonValueKind.String ? flat[i + 1].GetString() : null);
        }
        return found;
    }

    /// <summary>The <c>Rename</c> panel for a name, in the game's flat key/value shape, as a mutable JSON node for
    /// the save-edit writer.</summary>
    public static JsonObject PanelNode(string name) => new()
    {
        ["strName"] = Panel,
        ["dictGUIPropMap"] = new JsonArray(NameKey, name),
    };
}
