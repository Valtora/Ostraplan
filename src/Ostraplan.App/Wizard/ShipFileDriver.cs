using System.IO;
using Ostraplan.Core;

namespace Ostraplan.App.Wizard;

/// <summary>
/// The ship-file destination: one bare <c>data/ships</c> file, for a modder assembling their own mod. It is the mod
/// destination with everything around the ship taken away (no <c>mod_info.json</c>, no art, no loot), so the ship
/// is built exactly as a mod's would be and only the write differs.
///
/// <para>As with <see cref="ModDriver"/>, Review builds for the report and the commit rebuilds, which is only honest
/// because the wear seed is pinned at Review and handed to the write.</para>
/// </summary>
public sealed class ShipFileDriver : ExportDriver
{
    private WearOptions _pinnedWear = WearOptions.Pristine;

    public override ExportDestination Destination => ExportDestination.ShipFile;
    public override string Name => "As a ship file";
    public override string Blurb =>
        "Just the ship's data/ships .json, for a mod you put together yourself. No mod folder, art or kiosk listing.";
    public override string CommitVerb => "Export";

    /// <summary>A residence goes the way the mod destination sends it: a ship file is a vessel the game spawns, and
    /// an apartment is bought through a Real Estate broker with a different shape entirely.</summary>
    public override string? Unavailable(WizardSession session) =>
        session.Doc.IsResidence
            ? "A residence can't be a ship file. Use \"Into a save game\" to place it at a station."
            : null;

    // ---- review ----

    public override async Task<BuildOutcome> BuildAsync(WizardSession session)
    {
        var plan = session.Plan;
        _pinnedWear = PinSeed(plan.Wear);
        var path = plan.ShipFilePath!;
        var publicName = ShipExport.ResolvePublicName(plan.Identity.PublicName, ShipExport.VariedNames);

        var meta = plan.Identity with { PublicName = publicName };
        var (ship, rating, roomCount, buildWarnings) =
            await BuildOffThread(session.Doc, session.Catalog, session.Specs, plan.ShipName, meta, _pinnedWear);

        var facts = new List<ReviewFact>
        {
            new("Ship", $"{plan.ShipName}  ({ship.AItems.Length} parts, {roomCount} certified room(s))"),
            new("Rating", string.IsNullOrEmpty(rating.Display) ? "None" : rating.Display),
            new("strName", plan.ShipName),
            new("In-game name", publicName == ShipExport.VariedNames
                ? "a different game-generated name per spawn"
                : publicName),
            new("Condition", _pinnedWear.Enabled
                ? $"worn to ~{_pinnedWear.TargetCondition * 100:0}% average (parts vary, none below 10%)"
                : "pristine"),
            new("Contains", "the ship's data/ships entry only: no mod_info.json, preview art or loot"),
            new("Writes to", path),
        };

        var acks = new List<string>();
        if (File.Exists(path)) acks.Add($"\"{Path.GetFileName(path)}\" already exists and will be replaced.");

        return new BuildOutcome(facts, buildWarnings, acks);
    }

    /// <summary>Every parameter is plain data, so the closure holds nothing UI-owned. See <see cref="ExportDriver"/>.</summary>
    private static Task<(ExportedShip Ship, ShipRating Rating, int RoomCount, List<string> Warnings)> BuildOffThread(
        ShipDocument doc, Catalog catalog, IReadOnlyList<RoomSpecDef> specs, string strName,
        ExportMetadata meta, WearOptions wear) =>
        Ui.OffThread(() =>
        {
            var warnings = new List<string>();
            var (ship, rating, rooms) = ShipExport.Build(doc, catalog, specs, strName, warnings, meta, wear);
            return (ship, rating, rooms, warnings);
        });

    // ---- commit ----

    public override async Task<DoneReport> WriteAsync(WizardSession session)
    {
        var plan = session.Plan;
        var result = await WriteOffThread(session.Doc, session.Catalog, session.Specs, plan.ShipFilePath!,
            plan.ShipName, plan.Identity, _pinnedWear);

        AuditLog.Add($"Exported ship file \"{plan.ShipName}\" to {result.Path}.");

        var lines = new List<string>
        {
            $"{result.PartCount} parts, {result.RoomCount} certified room(s), rating " +
            $"{(string.IsNullOrEmpty(result.Rating.Display) ? "None" : result.Rating.Display)}.",
        };
        if (_pinnedWear.Enabled)
            lines.Add($"Worn to ~{_pinnedWear.TargetCondition * 100:0}% average condition (parts vary, none below 10%).");
        lines.Add("");
        lines.Add($"Written to {result.Path}");
        lines.Add("");
        lines.Add("Put it in a mod's data/ships folder. The game knows the ship by its strName, " +
                  $"\"{plan.ShipName}\", whatever the file is called.");
        return new DoneReport($"Exported {plan.ShipName}.", lines);
    }

    private static Task<ShipFileResult> WriteOffThread(
        ShipDocument doc, Catalog catalog, IReadOnlyList<RoomSpecDef> specs, string path, string strName,
        ExportMetadata meta, WearOptions wear) =>
        Ui.OffThread(() => ShipExport.WriteShipFile(doc, catalog, specs, path, strName, meta, wear));
}
