using System.IO;
using Ostraplan.Core;

namespace Ostraplan.App.Bundle;

/// <summary>
/// One design in the pack as the editor holds it: the entry from the <c>.oplanmod</c>, where its <c>.oplan</c>
/// actually is, and the design itself once it has been read.
///
/// <para><b>A member is a path, and the file on disk is what gets exported.</b> Nothing here reads an open tab.
/// A pack is a document that has to mean the same thing tomorrow as it does now, and a design half-edited in a
/// tab is not what its file says. Where a design is open with unsaved changes the editor says so on the row, so
/// nobody is surprised by which version went into the mod.</para>
/// </summary>
internal sealed class BundleMember
{
    public required BundleEntry Entry { get; init; }

    /// <summary>The design's absolute path, resolved from the entry against the pack's own location.</summary>
    public required string Path { get; set; }

    /// <summary>The design, or null when it could not be used. <see cref="Problem"/> then says why.</summary>
    public ShipDocument? Doc { get; set; }

    public OplanMeta Meta { get; set; } = new();

    /// <summary>Why this member cannot be exported, or null when it can. A member in this state is kept in the
    /// list rather than dropped: a mod whose parts are not loaded today is one the user will want to fix, not one
    /// they want quietly removed from their pack.</summary>
    public string? Problem { get; set; }

    /// <summary>Set when the design is also open in a tab with unsaved edits. Advisory only.</summary>
    public bool OpenWithUnsavedEdits { get; set; }

    /// <summary>What the ship is called in the pack: the override if there is one, else the design's own name.</summary>
    public string Name =>
        Entry.NameOverride is { Length: > 0 } over ? over.Trim()
        : Meta.Name is { Length: > 0 } own ? own
        : System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>The name the game keys the ship on: the replacement target where there is one (it is the override
    /// key), else <see cref="Name"/>.</summary>
    public string StrName => Entry.Replaces is { Length: > 0 } r ? r.Trim() : Name;

    public int PartCount => Doc?.Placements.Count ?? 0;

    /// <summary>The row's second line: what this design is, or what is wrong with it.</summary>
    public string Detail =>
        Problem is { Length: > 0 } p ? p
        : OpenWithUnsavedEdits
            ? $"{PartCount} parts. Open with unsaved changes: the saved file is what will be exported."
            : $"{PartCount} parts" + (Entry.Replaces is { Length: > 0 } r ? $", replacing \"{r}\"" : "");

    /// <summary>
    /// Read a design for the pack. Everything a mod export cannot do anything with is refused here rather than at
    /// the write: an apartment, an empty design, and above all one whose parts are not in the loaded data, because
    /// those parts are dropped on load and the mod would ship a ship with holes in it and say nothing.
    /// </summary>
    public static BundleMember Read(BundleEntry entry, string path, Catalog catalog)
    {
        var member = new BundleMember { Entry = entry, Path = path };
        if (!File.Exists(path))
        {
            member.Problem = "This design is not where the pack says it is. Find it again, or take it out.";
            return member;
        }

        try
        {
            var file = OplanFile.Load(path);
            var (doc, missing) = file.ToDocument(catalog);
            member.Meta = file.Meta;

            if (missing.Count > 0)
                member.Problem =
                    $"{missing.Count} part(s) in this design are not in your current game and mods data. A mod " +
                    "export would leave them out, so this cannot go in a pack until they are loaded.";
            else if (doc.IsResidence)
                member.Problem = "This is an apartment. An apartment reaches the game through a Real Estate " +
                                 "broker, so it cannot be part of a ship mod.";
            else if (doc.Placements.Count == 0)
                member.Problem = "This design has no parts in it.";
            else
                member.Doc = doc;
        }
        catch (Exception ex)
        {
            member.Problem = "This design could not be read: " + ex.Message;
        }

        return member;
    }
}
