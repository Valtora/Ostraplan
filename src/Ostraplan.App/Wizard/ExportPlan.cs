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

    /// <summary>How the ship becomes obtainable in game. Shared with the bundle editor, which asks a ship exactly
    /// the same question (see <see cref="DeliveryPlan"/>).</summary>
    public DeliveryPlan Delivery { get; set; } = new();

    /// <summary>True to stage into the game's Mods folder; false to write to <see cref="Folder"/>.</summary>
    public bool StagedIntoMods { get; set; } = true;

    public string? Folder { get; set; }
    public bool RegisterWithOstrasort { get; set; }
}

/// <summary>The new-ship-in-a-save destination's settings: which save, what to charge, and whether the ship keeps
/// the condition it really has.</summary>
public sealed class NewShipPlan
{
    public string? SaveName { get; set; }
    public bool Charge { get; set; }
    public double Price { get; set; }

    /// <summary>True = add the ship to the original save itself; false = add it to a copy. Separate from
    /// <see cref="UpdatePlan.InPlace"/> so the two destinations remember their own answers.</summary>
    public bool InPlace { get; set; }

    /// <summary>Back the original up before an in-place write. Only meaningful when <see cref="InPlace"/>.</summary>
    public bool Backup { get; set; } = true;

    /// <summary>Carry each part's real damage across from the save the design was imported from, instead of rolling
    /// fresh wear. Defaults on: a design that came from a save is almost always being moved rather than minted, and
    /// a transfer that quietly re-rolled the ship's condition would be wrong in a way nobody would think to check.
    /// Ignored, and never written back, when the run has no source ship to read a condition from.</summary>
    public bool KeepSourceCondition { get; set; } = true;

    /// <summary>For a residence design (<see cref="DocumentKind.Residence"/>), the registration of the station it
    /// attaches to — the half of <c>&lt;STATION&gt;|RES_&lt;n&gt;</c> before the pipe. Null until a station has
    /// been picked, and unused for a vessel, which is placed relative to the player instead.</summary>
    public string? StationRegId { get; set; }
}

/// <summary>The update-a-ship destination's settings: where to write, and what the edit costs.</summary>
public sealed class UpdatePlan
{
    /// <summary>True = rewrite the original save in place; false = write a copy.</summary>
    public bool InPlace { get; set; }

    /// <summary>Back the original up before an in-place write. Only meaningful when <see cref="InPlace"/>.</summary>
    public bool Backup { get; set; } = true;

    public bool Deduct { get; set; }

    /// <summary>What a newly-added part (and any authored cargo) costs, as a multiple of its base value.</summary>
    public double NewMultiplier { get; set; } = EditCost.DefaultNewMultiplier;

    /// <summary>What a moved part costs, as a multiple of its base value. Separate from <see cref="NewMultiplier"/>
    /// so a refit that shifts a lot of parts without conjuring any need not be priced like a rebuild.</summary>
    public double MovedMultiplier { get; set; } = EditCost.DefaultMovedMultiplier;
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

    /// <summary>True once the user has moved the condition control themselves. A derelict export otherwise turns
    /// wear off on their behalf, because the game damages a wreck on load and baking more on top of that is
    /// double-damage nobody asked for. Their own choice always wins.</summary>
    public bool WearChosen { get; set; }

    public ModPlan Mod { get; } = new();
    public NewShipPlan NewShip { get; } = new();
    public UpdatePlan Update { get; } = new();

    // ---- last-used settings ----

    /// <summary>
    /// Start from what was used last time, so a repeat export is one click. The design's own fields — its name and
    /// in-game identity — come from the <c>.oplan</c>, never from settings, because they belong to the design
    /// rather than to this machine.
    /// </summary>
    public static ExportPlan FromSettings(AppSettings settings, OplanMeta meta, SaveSourceRef? sourceSave)
    {
        var plan = new ExportPlan
        {
            ShipName = meta.Name,
            Identity = new ExportMetadata(meta.PublicName, meta.Make, meta.Model, meta.Year, meta.Designation,
                meta.Description),
        };

        if (settings.LastExport is not { } last) return plan;

        plan.Destination = last.Destination switch
        {
            "newShip" => ExportDestination.NewShipInSave,
            // only offer to reopen on the update destination for a design that still came from a save
            "update" when sourceSave is not null => ExportDestination.UpdateShipInSave,
            _ => ExportDestination.Mod,
        };
        plan.Wear = new WearOptions(last.WearOn, last.WearTarget);

        plan.Mod.Version = last.ModVersion;
        plan.Mod.Author = settings.ExportAuthor ?? meta.Author;
        plan.Mod.Delivery.BrokerPools = [.. last.BrokerPools];
        if (last.BrokerWeight > 0) plan.Mod.Delivery.BrokerWeight = last.BrokerWeight;
        plan.Mod.RegisterWithOstrasort = last.RegisterWithOstrasort;
        plan.Mod.Delivery.SpecialOfferPools = [.. last.SpecialOfferPools];
        plan.Mod.Delivery.DerelictPools = [.. last.DerelictPools];
        if (last.DerelictWeight > 0) plan.Mod.Delivery.DerelictWeight = last.DerelictWeight;
        plan.Mod.Delivery.NoDeliveryRoute = last.NoDeliveryRoute;
        plan.Mod.Delivery.StartingShip = last.StartingShip;
        plan.Mod.Delivery.StartingShipExclusive = last.StartingShipExclusive;
        plan.Mod.Delivery.StartStation = last.StartStation;
        plan.Mod.StagedIntoMods = last.StagedIntoMods;
        plan.Mod.Folder = settings.LastExportDir;
        plan.Mod.RegisterWithOstrasort = last.RegisterWithOstrasort;

        plan.NewShip.SaveName = last.SaveName;
        plan.NewShip.Charge = last.Charge;
        plan.NewShip.Price = last.Price;
        plan.NewShip.InPlace = last.AddInPlace;
        plan.NewShip.Backup = last.AddBackup;

        plan.Update.InPlace = last.InPlace;
        plan.Update.Backup = last.Backup;
        plan.Update.Deduct = last.Deduct;
        plan.Update.NewMultiplier = last.NewCostMultiplier;
        plan.Update.MovedMultiplier = last.MovedCostMultiplier;

        return plan;
    }

    /// <summary>Remember this run. The caller persists; this only fills the object in.</summary>
    public void SaveTo(AppSettings settings)
    {
        var last = settings.LastExport ??= new LastExport();
        last.Destination = Destination switch
        {
            ExportDestination.NewShipInSave => "newShip",
            ExportDestination.UpdateShipInSave => "update",
            _ => "mod",
        };
        last.WearOn = Wear.Enabled;
        last.WearTarget = Wear.TargetCondition;

        last.ModVersion = Mod.Version;
        last.BrokerPools = [.. Mod.Delivery.BrokerPools];
        last.BrokerWeight = Mod.Delivery.BrokerWeight ?? 0;
        last.SpecialOfferPools = [.. Mod.Delivery.SpecialOfferPools];
        last.DerelictPools = [.. Mod.Delivery.DerelictPools];
        last.DerelictWeight = Mod.Delivery.DerelictWeight ?? 0;
        last.NoDeliveryRoute = Mod.Delivery.NoDeliveryRoute;
        last.StartingShip = Mod.Delivery.StartingShip;
        last.StartingShipExclusive = Mod.Delivery.StartingShipExclusive;
        last.StartStation = Mod.Delivery.StartStation;
        last.StagedIntoMods = Mod.StagedIntoMods;
        last.RegisterWithOstrasort = Mod.RegisterWithOstrasort;

        last.SaveName = NewShip.SaveName;
        last.Charge = NewShip.Charge;
        last.Price = NewShip.Price;
        last.AddInPlace = NewShip.InPlace;
        last.AddBackup = NewShip.Backup;

        last.InPlace = Update.InPlace;
        last.Backup = Update.Backup;
        last.Deduct = Update.Deduct;
        last.NewCostMultiplier = Update.NewMultiplier;
        last.MovedCostMultiplier = Update.MovedMultiplier;
    }
}
