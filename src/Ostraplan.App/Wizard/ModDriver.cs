using System.Globalization;
using System.IO;
using Ostraplan.Core;

namespace Ostraplan.App.Wizard;

/// <summary>
/// The mod destination: a <c>data/ships</c> mod folder, shareable and save-independent.
///
/// <para>Unlike the two save destinations, this one's write is monolithic — <see cref="ShipExport.Write"/> builds
/// and writes in a single call — so Review builds with <see cref="ShipExport.Build"/> for the report and the
/// commit rebuilds internally. That is only honest because the wear seed is pinned at Review and handed to the
/// write, so the rebuild damages exactly the parts the report described.</para>
/// </summary>
public sealed class ModDriver : ExportDriver
{
    private WearOptions _pinnedWear = WearOptions.Pristine;
    private string? _replaceTarget;
    private string _modDir = "";
    private ShipDelivery _delivery = ShipDelivery.None;

    public override ExportDestination Destination => ExportDestination.Mod;
    public override string Name => "As a mod";
    public override string Blurb =>
        "A shareable mod folder that works in any save. The ship can be sold at a kiosk or given to a new Shipbreaker.";
    public override string CommitVerb => "Export";

    /// <summary>Always available. A design with parts can always be written as a mod, which is what makes this the
    /// fallback the other two destinations point at when they cannot be used.</summary>
    /// <summary>
    /// A residence cannot be a ship mod. Every route this destination offers puts the design in front of a
    /// <b>ship</b> broker (kiosk stock, Special Offer, a Shipbreaker starting ship, a derelict field), and the
    /// game buys a residence through a Real Estate broker instead, which is a different loot shape entirely: a
    /// <c>strType: "station"</c> self-reference plus an <c>Itm&lt;STATION&gt;ResBrokerInv</c> append
    /// (GAME-INTERNALS §19). Exporting one here would produce a mod that sells an apartment as a vessel.
    /// </summary>
    public override string? Unavailable(WizardSession session) =>
        session.Doc.IsResidence
            ? "A residence can't be a ship mod. Use \"Into a save game\" to place it at a station."
            : null;

    // ---- review ----

    public override async Task<BuildOutcome> BuildAsync(WizardSession session)
    {
        var plan = session.Plan;
        _pinnedWear = PinSeed(plan.Wear);

        // When replacing, the override key is the target ship's real strName, resolved from the file rather than
        // assumed from its filename: a mod or multi-ship file need not match.
        _replaceTarget = plan.Mod.ReplaceShip is { } rs
            ? TemplateImport.ResolveShipStrName(rs.Path) ?? rs.Name
            : null;
        var strName = _replaceTarget ?? plan.ShipName;
        // Blank means the game's own varied names, replacement or not: a mod's ship is one the game hands out,
        // and the design name is a file name rather than something to paint on a hull.
        var publicName = ShipExport.ResolvePublicName(plan.Identity.PublicName, ShipExport.VariedNames);
        _delivery = BuildDelivery(plan, publicName);

        var parent = Parent(session);
        _modDir = Path.Combine(parent, ShipExport.SanitizeName(
            ShipExport.ResolveModName(plan.Mod.ModName, plan.ShipName, _replaceTarget)));

        var meta = plan.Identity with { PublicName = publicName };
        var (ship, rating, roomCount, buildWarnings) =
            await BuildOffThread(session.Doc, session.Catalog, session.Specs, strName, meta, _pinnedWear);

        var facts = new List<ReviewFact>
        {
            new("Ship", $"{plan.ShipName}  ({ship.AItems.Length} parts, {roomCount} certified room(s))"),
            new("Rating", string.IsNullOrEmpty(rating.Display) ? "None" : rating.Display),
            new("In-game name", publicName == ShipExport.VariedNames
                ? "a different game-generated name per spawn"
                : publicName),
            new("Condition", _pinnedWear.Enabled
                ? $"worn to ~{_pinnedWear.TargetCondition * 100:0}% average (parts vary, none below 10%)"
                : "pristine"),
        };
        if (_replaceTarget is not null)
            facts.Add(new ReviewFact("Replaces", $"the existing ship \"{_replaceTarget}\""));
        facts.Add(new ReviewFact("Obtainable via", Describe(_delivery) is { Length: > 0 } d
            ? d
            : "nothing (ship file only, no route)"));
        facts.Add(new ReviewFact("Preview art", "a ship image plus a thumbnail per certified room"));
        facts.Add(new ReviewFact("Writes to", _modDir));
        facts.Add(new ReviewFact("Registering", plan.Mod.RegisterWithOstrasort
            ? "by Ostrasort, right after the write"
            : "left to you"));

        var warnings = new List<string>(buildWarnings);
        if (_delivery.Derelicts.Count > 0)
            warnings.Add("Derelicts only appear in a NEW GAME, not in saves you already have.");

        var acks = new List<string>();
        if (Directory.Exists(_modDir) && Directory.EnumerateFileSystemEntries(_modDir).Any())
            acks.Add(FolderExistsAck(Path.GetFileName(_modDir)));

        return new BuildOutcome(facts, warnings, acks);
    }

    /// <summary>The acknowledgement for exporting over a mod folder that already has files in it. The ship pack
    /// asks the same thing.</summary>
    internal static string FolderExistsAck(string folder) =>
        $"\"{folder}\" already exists. Its ships will be replaced and removed routes deleted. Other files are kept.";

    /// <summary>Every parameter is plain data, so the lambda's closure holds nothing UI-owned and the capture guard
    /// has nothing to reject. See <see cref="ExportDriver"/>.</summary>
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

        // Rendered here, on the UI thread, and handed over as plain PNG bytes: the canvas and its sprite atlas are
        // thread-affine, so nothing about the renderer may cross into the background write.
        var preview = session.RenderPreview?.Invoke();

        var opts = new ExportOptions(
            plan.ShipName, plan.Mod.Author, plan.Mod.Notes, plan.Mod.Version,
            session.Env.InstalledVersion ?? GameEnv.VerifiedGameVersion, Parent(session),
            plan.Identity.PublicName, plan.Identity.Make, plan.Identity.Model, plan.Identity.Year,
            plan.Identity.Designation, plan.Identity.Description, _delivery, _replaceTarget, plan.Mod.ModName,
            _pinnedWear,   // the seed Review built with, so the rebuild inside Write lands in the same place
            preview);

        var result = await WriteOffThread(session.Doc, session.Catalog, session.Specs, opts, session.Index);

        session.Settings.ExportAuthor = plan.Mod.Author;
        if (!plan.Mod.StagedIntoMods) session.Settings.LastExportDir = plan.Mod.Folder;
        session.Settings.Save();
        AuditLog.Add($"Exported mod \"{plan.ShipName}\" to {result.ModDir}.");

        var lines = new List<string>
        {
            $"{result.PartCount} parts, {result.RoomCount} certified room(s), rating " +
            $"{(string.IsNullOrEmpty(result.Rating.Display) ? "None" : result.Rating.Display)}.",
        };
        if (_pinnedWear.Enabled)
            lines.Add($"Worn to ~{_pinnedWear.TargetCondition * 100:0}% average condition (parts vary, none below 10%).");
        if (result.PreviewCount > 0)
            lines.Add($"Preview art: 1 ship image and {result.PreviewCount - 1} room thumbnail(s).");
        if (_replaceTarget is not null) lines.Add($"Replaces the existing ship \"{_replaceTarget}\".");
        if (Describe(_delivery) is { Length: > 0 } delivery) lines.Add("Obtainable via: " + delivery + ".");
        lines.Add("");
        lines.Add($"Written to {result.ModDir}");

        if (plan.Mod.RegisterWithOstrasort)
        {
            lines.Add("");
            lines.AddRange(await RegisterWithOstrasort(session, plan.ShipName, result));
            return new DoneReport($"Exported {plan.ShipName}.", lines);
        }

        lines.Add("");
        lines.AddRange(plan.Mod.StagedIntoMods
            ?
            [
                "It's in the game's Mods folder.",
                "Register it with Ostrasort (or ModTools) to see it in game.",
            ]
            : new[]
            {
                "Copy this folder into Ostranauts_Data\\Mods.",
                "Then register it with Ostrasort (or ModTools) to see it in game.",
            });
        return new DoneReport($"Exported {plan.ShipName}.", lines);
    }

    private static Task<ExportResult> WriteOffThread(
        ShipDocument doc, Catalog catalog, IReadOnlyList<RoomSpecDef> specs, ExportOptions opts, DataIndex index) =>
        Ui.OffThread(() => ShipExport.Write(doc, catalog, specs, opts, index));

    // ---- Ostrasort ----

    private static Task<IReadOnlyList<string>> RegisterWithOstrasort(
        WizardSession session, string shipName, ExportResult result) =>
        OstrasortRegistration.RunAsync(session.Owner, session.Settings, session.Env, shipName, result.TouchedLootPools);

    // ---- helpers ----

    private static string Parent(WizardSession session) =>
        session.Plan.Mod.StagedIntoMods ? session.Env.ModsDir : session.Plan.Mod.Folder!;

    private static ShipDelivery BuildDelivery(ExportPlan plan, string publicName) =>
        plan.Mod.Delivery.ToDelivery(
            publicName is { Length: > 0 } && publicName != ShipExport.VariedNames ? publicName : plan.ShipName,
            plan.Identity.Description);

    /// <summary>A one-line human summary of the chosen delivery options, or "" when the ship file goes out on its
    /// own.</summary>
    private static string Describe(ShipDelivery d)
    {
        var parts = new List<string>();
        if (d.BrokerPools.Count > 0) parts.Add($"{d.BrokerPools.Count} broker kiosk(s)");
        if (d.SpecialOfferPools.Count > 0) parts.Add($"{d.SpecialOfferPools.Count} Special Offer slot(s)");
        if (d.StartingShip)
            parts.Add(d.StartingShipExclusive ? "Shipbreaker starting ship (guaranteed)" : "Shipbreaker starting ship");
        if (d.Derelicts.Count > 0)
            parts.Add($"{d.Derelicts.Count} derelict field(s): " + string.Join(", ", d.Derelicts.Select(Band)));
        return string.Join(", ", parts);
    }

    private static string Band(string pool) =>
        KioskExport.DerelictPools.FirstOrDefault(p => p.Pool == pool).Label ?? pool;
}
