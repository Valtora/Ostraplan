using Ostraplan.Core;

namespace Ostraplan.App.Wizard;

/// <summary>Which way the design leaves Ostraplan. The wizard's first step picks one, and the rail then shows only
/// that destination's steps.</summary>
public enum ExportDestination
{
    /// <summary>A <c>data/ships</c> mod folder: shareable, save-independent, obtainable via a kiosk.</summary>
    Mod,

    /// <summary>A new owned ship written into a copy of a save (see <see cref="SaveGrant"/>).</summary>
    NewShipInSave,

    /// <summary>The ship this design was imported from, rewritten in its own save (see <see cref="SaveEdit"/>).</summary>
    UpdateShipInSave,
}

/// <summary>The mod destination's settings: what the mod is, which ship it replaces if any, how the ship becomes
/// obtainable in game, and where the folder is written.</summary>
public sealed class ModPlan
{
    /// <summary>The mod's name (its <c>mod_info</c> name and folder), separate from the ship. Blank lets the
    /// exporter re-derive the default (<c>ShipExport.ResolveModName</c>).</summary>
    public string ModName { get; set; } = "";

    public string Author { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Notes { get; set; } = "";

    /// <summary>The existing ship this design replaces, or null to add a new one. Held as the picked entry rather
    /// than its name because the real override key is resolved from the file, not the filename.</summary>
    public ShipFileEntry? ReplaceShip { get; set; }

    public List<string> BrokerPools { get; set; } = [];
    public double BrokerWeight { get; set; } = 0.05;
    public List<string> SpecialOfferPools { get; set; } = [];
    public bool StartingShip { get; set; }
    public bool StartingShipExclusive { get; set; }
    public string StartStation { get; set; } = "OKLG";
    public double StartMortgage { get; set; }

    /// <summary>The weight a starting ship is offered at, read from the game's own pool. Not a user control.</summary>
    public double StartWeight { get; set; } = 0.16;

    /// <summary>True to stage into the game's Mods folder; false to write to <see cref="Folder"/>.</summary>
    public bool StagedIntoMods { get; set; } = true;

    public string? Folder { get; set; }
    public bool RegisterWithOstrasort { get; set; }
}

/// <summary>The new-ship-in-a-save destination's settings: which save, and what to charge.</summary>
public sealed class NewShipPlan
{
    public string? SaveName { get; set; }
    public bool Charge { get; set; }
    public double Price { get; set; }
}

/// <summary>The update-a-ship destination's settings: where to write, and what the edit costs.</summary>
public sealed class UpdatePlan
{
    /// <summary>True = rewrite the original save in place; false = write a copy.</summary>
    public bool InPlace { get; set; }

    /// <summary>Back the original up before an in-place write. Only meaningful when <see cref="InPlace"/>.</summary>
    public bool Backup { get; set; } = true;

    public bool Deduct { get; set; }
    public double Multiplier { get; set; } = EditCost.DefaultMultiplier;
}

/// <summary>
/// Everything the export wizard collects, in one mutable object. Steps read it on entry and write it back on the
/// way out; drivers read it to drive the engine; the shell persists the remembered parts to
/// <see cref="AppSettings"/>.
///
/// <para><b>Deliberately free of WPF types.</b> A driver closes over this and over Core objects, never over a
/// control, so <see cref="Ui.OffThread"/>'s capture guard has nothing to reject. Keeping it that way is what stops
/// the v0.43.1 thread-affinity failure recurring across a wizard that reads twenty controls before a build.</para>
/// </summary>
public sealed class ExportPlan
{
    /// <summary>Bumped whenever a step writes a change back. A driver records the revision it built at, so
    /// editing an earlier step and returning to Review rebuilds rather than showing a stale result.</summary>
    public int Revision { get; private set; }

    public void Touch() => Revision++;

    public ExportDestination Destination { get; set; } = ExportDestination.Mod;

    /// <summary>The design's name. For the mod destination this is the ship's <c>strName</c>; for a granted ship
    /// it is only a fallback display name, because a save ship's strName is its registration.</summary>
    public string ShipName { get; set; } = "";

    /// <summary>The in-game identity, shared by all three destinations and persisted with the design rather than
    /// in settings (it travels in the <c>.oplan</c> via <see cref="OplanMeta"/>).</summary>
    public ExportMetadata Identity { get; set; } = new();

    /// <summary>The condition to bake in. Its <see cref="WearOptions.Seed"/> is null here: the driver pins a seed
    /// when Review builds and reuses that same value at commit, or the two would damage different parts.</summary>
    public WearOptions Wear { get; set; } = WearOptions.Vanilla;

    public ModPlan Mod { get; } = new();
    public NewShipPlan NewShip { get; } = new();
    public UpdatePlan Update { get; } = new();
}
