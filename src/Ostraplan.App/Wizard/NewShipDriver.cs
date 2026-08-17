using System.IO;
using System.Text.Json.Nodes;
using Ostraplan.Core;

namespace Ostraplan.App.Wizard;

/// <summary>
/// The new-ship destination: the design lands in a <b>copy</b> of a save as a ship the player already owns, parked
/// a few kilometres off wherever they are.
///
/// <para>Build and write are genuinely separate here, so Review's artifact is the one written — no rebuild, and
/// therefore no chance of the registration, the spawn point or the wear differing from what was reported. None of
/// those three can be reproduced from settings alone: the registration is minted from a GUID.</para>
/// </summary>
public sealed class NewShipDriver : ExportDriver
{
    private GrantContext? _ctx;
    private string _regId = "";
    private JsonObject? _ship;
    private GrantReport? _report;
    private WearOptions _pinnedWear = WearOptions.Pristine;
    private string _outDir = "";
    private IReadOnlyDictionary<string, JsonNode>? _sourceCos;
    private IReadOnlyList<ResidenceStation> _stations = [];
    private ResidenceStation? _station;

    /// <summary>
    /// The source ship's condition owners when this run is carrying its real condition across, or null when it is
    /// rolling fresh wear. Requires the design to have come from a save (its parts name the items they were), and
    /// that save to still be located.
    /// </summary>
    private static IReadOnlyDictionary<string, JsonNode>? SourceCos(WizardSession session) =>
        session.Plan.NewShip.KeepSourceCondition
        && session.SaveContext is { } src
        && session.Doc.Placements.Any(p => p.OriginStrID is not null)
            ? src.CosById
            : null;

    public override ExportDestination Destination => ExportDestination.NewShipInSave;
    public override string Name => "Into a save game";
    public override string Blurb =>
        "Adds the design to a copy of a save as something you already own. Your original save is never modified.";
    public override string CommitVerb => "Add ship";

    public override string BlurbFor(WizardSession session) => session.ByKind(
        "Adds the design to a copy of a save as a brand-new ship you own, parked a few kilometres out and "
        + "reachable by P.A.S.S. ferry. Your original save is never modified.",
        "Adds the design to a copy of a save as an apartment you own at a station of your choosing, reached "
        + "through that station's transit kiosk. Your original save is never modified.");

    public override string? Unavailable(WizardSession session) =>
        session.Saves.Count == 0 ? "No save games found." : null;

    /// <summary>The save read for costing, or null until one is picked. The price step reads the balance off it.</summary>
    public GrantContext? Context => _ctx;

    /// <summary>The stations in the chosen save a residence could attach to, best first. Empty for a vessel run
    /// (they are never read) and for a save with no stations in it, which is what blocks a residence grant.</summary>
    public IReadOnlyList<ResidenceStation> Stations => _stations;

    /// <summary>The station a residence will attach to, or null for a vessel or before one is chosen.</summary>
    public ResidenceStation? Station => _station;

    /// <summary>Choose the station a residence attaches to. Invalidates any cached build, since the registration,
    /// the position and the homeowner cond all follow from it.</summary>
    public void UseStation(WizardSession session, ResidenceStation? station)
    {
        if (ReferenceEquals(_station, station)) return;
        _station = station;
        session.Plan.NewShip.StationRegId = station?.RegId;
        Invalidate(session);
    }

    // ---- picking a save ----

    /// <summary>On selecting this destination, re-read whatever save was used last time so the price step opens
    /// already costed. A remembered save that has since been deleted is not an error here: the step will ask.</summary>
    public override async Task<string?> PrepareAsync(WizardSession session)
    {
        if (_ctx is not null) return null;
        var remembered = session.Saves.FirstOrDefault(s =>
            string.Equals(s.Name, session.Plan.NewShip.SaveName, StringComparison.Ordinal));
        if (remembered is null) return null;
        await UseSaveAsync(session, remembered);
        return null;
    }

    /// <summary>
    /// Read the chosen save so the grant can be costed and described before anything is written. Returns null on
    /// success, or the reason this save cannot take a grant at all — which belongs on the step, now, rather than
    /// after the user has committed to a write.
    /// </summary>
    public async Task<string?> UseSaveAsync(WizardSession session, SaveEntry save)
    {
        _ctx = null;
        _stations = [];
        _station = null;
        Invalidate(session);
        try
        {
            _ctx = await ReadOffThread(save);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return ex.Message;
        }
        session.Plan.NewShip.SaveName = save.Name;

        // A residence needs a station to hang off, and which stations exist is a property of the save, so the
        // list is read here with the context rather than when the step happens to be shown. Failing to read it
        // is not a failed grant: the step reports "no stations", which is the actionable form of the same thing.
        if (session.Doc.IsResidence)
        {
            _stations = await ListStationsOffThread(save, session.Index);
            _station = _stations.FirstOrDefault(s =>
                           string.Equals(s.RegId, session.Plan.NewShip.StationRegId, StringComparison.Ordinal))
                       ?? ResidenceGrant.Preferred(_stations);
            session.Plan.NewShip.StationRegId = _station?.RegId;
        }
        return null;
    }

    private static Task<GrantContext> ReadOffThread(SaveEntry save) =>
        Ui.OffThread(() => SaveGrant.ReadContext(save));

    private static Task<IReadOnlyList<ResidenceStation>> ListStationsOffThread(SaveEntry save, DataIndex index) =>
        Ui.OffThread<IReadOnlyList<ResidenceStation>>(() =>
        {
            try { return ResidenceGrant.ListStations(save.ZipPath, index); }
            catch (Exception ex) when (ex is InvalidDataException or IOException) { return []; }
        });

    /// <summary>Throw away any cached build. Changing save invalidates far more than the settings do.</summary>
    private void Invalidate(WizardSession session)
    {
        _ship = null;
        _report = null;
        session.Plan.Touch();
    }

    // ---- review ----

    public override async Task<BuildOutcome> BuildAsync(WizardSession session)
    {
        if (_ctx is not { } ctx)
            throw new InvalidDataException("Pick a save game to add the ship to.");

        var plan = session.Plan;
        var residence = session.Doc.IsResidence ? _station : null;
        if (session.Doc.IsResidence && residence is null)
            throw new InvalidDataException("Pick the station this residence belongs to.");

        _pinnedWear = PinSeed(plan.Wear);
        _regId = residence is { } home
            ? ResidenceGrant.MintRegId(ctx.ExistingRegIds, home.RegId)
            : SaveGrant.MintRegId(ctx.ExistingRegIds, ctx.PlayerShipRegId);
        _outDir = SaveGrant.SuggestCopyDir(ctx);

        _sourceCos = SourceCos(session);
        var opts = new GrantOptions(plan.ShipName, plan.Identity, _pinnedWear, Random.Shared.Next(), _sourceCos)
        {
            Residence = residence,
        };
        (_ship, _report) = await BuildOffThread(
            session.Doc, session.Catalog, session.Specs, _regId, ctx.Anchor, opts, ctx.Epoch);

        var price = plan.NewShip.Charge ? plan.NewShip.Price : 0;
        var facts = new List<ReviewFact>
        {
            new(residence is null ? "Ship" : "Residence", $"{_report.PublicName}  ({_report.RegId})"),
            new("Size", $"{_report.ItemCount} parts, {_report.RoomCount} certified room(s)"),
        };

        // The Ship Rating is a vessel figure and a residence has none worth printing; what a residence has
        // instead is what a Real Estate broker would value it at, which is the number that decides whether the
        // price below is generous or absurd.
        if (residence is { } station)
        {
            facts.Add(new("Broker value", $"{Money(ResidenceGrant.Price(_ship))} before any kiosk discount"));
            facts.Add(new("Condition", ConditionSummary()));
            facts.Add(new("Placed at", $"{station.DisplayName} ({station.RegId}), locked to the station"));
            facts.Add(new("Reached by", station.HasTransitRoute
                ? $"the station's transit kiosk (route \"{station.TransitNodeName}\")"
                : "NOTHING — this station has no residence transit route"));
        }
        else
        {
            facts.Add(new("Rating", string.IsNullOrEmpty(_report.Rating.Display) ? "None" : _report.Rating.Display));
            facts.Add(new("Condition", ConditionSummary()));
            facts.Add(new("Parked", $"{_report.DistanceKm:0.0} km from your ship, undocked, within P.A.S.S. ferry range"));
        }

        facts.Add(new("Cost", price > 0
            ? $"{Money(price)}, leaving {Money(ctx.Balance - price)} of {Money(ctx.Balance)}"
            : $"a gift. Your balance stays at {Money(ctx.Balance)}"));
        facts.Add(new("Writes to", $"a new save named {Path.GetFileName(_outDir)}"));
        facts.Add(new("Original save", $"\"{ctx.SaveName}\" is not modified"));

        var warnings = new List<string>(_report.Warnings);
        if (residence is { HasTransitRoute: false } stranded)
            warnings.Add(
                $"{stranded.DisplayName} has no residence transit route in the game's data (no \"{stranded.TransitNodeName}\" " +
                "node), so this apartment will exist and be yours but nothing will be able to reach it. Vanilla "
                + "Mercury Volanus is the known case. Pick another station unless a mod adds the route.");

        return new BuildOutcome(facts, warnings, []);
    }

    private static Task<(JsonObject Ship, GrantReport Report)> BuildOffThread(
        ShipDocument doc, Catalog catalog, IReadOnlyList<RoomSpecDef> specs, string regId,
        GrantAnchor anchor, GrantOptions opts, double epoch) =>
        Ui.OffThread(() => SaveGrant.BuildShip(doc, catalog, specs, regId, anchor, opts, epoch));

    // ---- commit ----

    public override async Task<DoneReport> WriteAsync(WizardSession session)
    {
        if (_ctx is not { } ctx || _ship is not { } ship || _report is not { } built)
            throw new InvalidDataException("Nothing has been built yet.");

        var plan = session.Plan;
        var price = plan.NewShip.Charge ? plan.NewShip.Price : 0;
        var residence = session.Doc.IsResidence ? _station : null;
        var (outDir, report) = await WriteOffThread(ctx, _regId, ship, built, price, _outDir, residence);

        // the context has now claimed the ship and taken the deduction, so it cannot be written again
        _ctx = null;
        _ship = null;
        SaveSettings(session);
        AuditLog.Add(
            $"Granted {(residence is null ? "ship" : "residence")} \"{plan.ShipName}\" ({report.RegId}) into {outDir}.");

        var lines = new List<string>
        {
            residence is null
                ? $"{report.ItemCount} parts, {report.RoomCount} certified room(s), rating " +
                  $"{(string.IsNullOrEmpty(report.Rating.Display) ? "None" : report.Rating.Display)}."
                : $"{report.ItemCount} parts, {report.RoomCount} certified room(s).",
        };
        if (_sourceCos is not null)
            lines.Add("Each part kept the condition it had on the original ship.");
        else if (_pinnedWear.Enabled)
            lines.Add($"Worn to ~{_pinnedWear.TargetCondition * 100:0}% average condition (parts vary, none below 10%).");
        if (report.Charged is { } charged)
            lines.Add($"Charged {Money(charged)}, leaving {Money(report.ResultingBalance ?? 0)}.");
        if (residence is { } home)
        {
            lines.Add($"Registered to you at {home.DisplayName}, and you are now a homeowner there.");
            lines.Add(home.HasTransitRoute
                ? "Take the station's transit kiosk to reach it."
                : "WARNING: this station has no residence transit route, so nothing can reach it.");
        }
        else
        {
            lines.Add($"Parked {report.DistanceKm:0.0} km from your ship. Take the P.A.S.S. ferry to board it.");
        }
        lines.Add("");
        lines.Add($"Load this save to see it: {Path.GetFileName(outDir)}");
        lines.Add("In the game's Load menu, press Refresh if it isn't listed.");
        lines.Add("Your original save is unchanged.");

        return new DoneReport($"\"{report.PublicName}\" ({report.RegId}) is in your save.", lines);
    }

    /// <summary>How the ship's condition was decided, for the Review pane. Carrying the real condition is the one
    /// worth naming explicitly: it is the difference between moving a ship and minting a fresh copy of it.</summary>
    private string ConditionSummary() =>
        _sourceCos is not null
            ? "each part as it really is on the original ship"
            : _pinnedWear.Enabled
                ? $"worn to ~{_pinnedWear.TargetCondition * 100:0}% average (parts vary, none below 10%)"
                : "pristine";

    private static Task<(string OutputDir, GrantReport Report)> WriteOffThread(
        GrantContext ctx, string regId, JsonObject ship, GrantReport report, double price, string outDir,
        ResidenceStation? residence) =>
        Ui.OffThread(() => SaveGrant.WriteGrant(ctx, regId, ship, report, price, outDir, overwrite: false, residence));

    private static void SaveSettings(WizardSession session) => session.Settings.Save();

    private static string Money(double v) =>
        "$" + v.ToString("#,##0.##", System.Globalization.CultureInfo.InvariantCulture);
}
