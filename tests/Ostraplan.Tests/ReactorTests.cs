using System.Linq;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// Fusion reactors on the LIVE install. Two things must hold, and both are Law-relevant:
/// <list type="number">
///   <item>The palette must build the real reactor-core <b>condowner</b> (<c>ItmFusionReactorCore01Off</c>),
///   not the condowner-less glow-state orphan item (<c>ItmFusionReactorCore01On</c>) — same sockets, but no
///   mass/value/conds, so it would silently wreck rating, room value, the bill of materials and export.</item>
///   <item>The staged build — sealed pad with a vacuum-exposed centre → 2 field coils → 4 reactor segments
///   (one core placement) → components attached to the core's inputs — must validate through
///   <see cref="CheckFit"/> exactly as the socket data dictates: coils need the vacuum centre, the core needs
///   the coils, a component needs the core.</item>
/// </list>
/// Every test skips on a machine without the game.
/// </summary>
public class ReactorTests
{
    private const string CoreOff = "ItmFusionReactorCore01Off";   // the installable condowner (has IsOff, real conds)
    private const string CoreOn = "ItmFusionReactorCore01On";     // the glow-state ITEM with no condowner
    private const string Coils = "ItmFusionFieldCoils01";         // operational form the palette builds
    private const string Laser = "ItmFusionLaserArray01";         // operational form
    private const string Floor = "ItmFloorGrate01";               // 1×1 sealed floor (socket-adds TILFloor)

    [SkippableFact]
    public void Reactor_core_builds_as_the_real_condowner_not_the_orphan_item()
    {
        var g = TestData.RequireGame();

        // the condowner-less On-state item must NOT be what the palette offers...
        Assert.DoesNotContain(g.Catalog.Parts, p => p.DefName == CoreOn);
        // ...it must build the real installable Off condowner instead
        Assert.Contains(g.Catalog.Parts, p => p.DefName == CoreOff);

        var core = g.Catalog.ByDefName[CoreOff];
        // and that part carries the reactor's real identity + mass + value (the orphan had none of these)
        Assert.Contains("IsFusionReactorCore", core.StartingConds);
        Assert.True(core.StartingCondValues.GetValueOrDefault("StatMass") > 100,
            "reactor core has no mass — the orphan-item swap is back (maneuver rating would read 0 kg for it)");
        Assert.True(core.BasePrice > 0, "reactor core has no base price (room value / bill of materials would be 0)");
        Assert.NotEqual(CoreOff, core.Friendly);        // a real friendly name, not the raw internal def
        Assert.DoesNotContain(CoreOn, core.Friendly);
    }

    [SkippableFact]
    public void Reactor_components_resolve_with_mass_and_all_socket_loots_resolve()
    {
        var g = TestData.RequireGame();

        // the components swap to their real operational condowners — each keeps its mass (the fix's other half)
        foreach (var def in new[] { Coils, Laser, "ItmFusionCorePump01", "ItmFusionCryoPump01", "ItmFusionPelletFeeder01" })
        {
            Assert.True(g.Catalog.ByDefName.TryGetValue(def, out var part), $"{def} not resolvable");
            Assert.True(part!.StartingConds.Length > 0, $"{def} has no starting conds (orphan-item bug)");
            Assert.True(part.StartingCondValues.GetValueOrDefault("StatMass") > 0, $"{def} has no mass");
        }

        // Every socket loot the reactor chain tests against MUST resolve to condition names. An unresolved
        // req/forbid loot is an UNCONSTRAINED cell — the cardinal Law false positive (Ostraplan would allow a
        // placement the game refuses). This locks the whole dependency chain (coils → core → components).
        foreach (var loot in new[]
        {
            "TILFusionFieldCoilsFixtureAdds",    // coils add this → core's centre req reads it
            "TILFusionReactorCoreFixtureAdds",   // core adds this → components' attach req read it
            "TILFusionReactorCoreFixtureForbids",
            "TILFusionReactorPartsFixtureAdds",
            "TILSubFloorFixture",
            "TILFloorFixtureForbids",             // coils' vacuum-centre forbid (forbids floor at the centre)
        })
            Assert.True(g.Catalog.LootConds(loot).Count > 0, $"reactor socket loot '{loot}' resolves to nothing");
    }

    [SkippableFact]
    public void Reactor_build_chain_enforces_its_dependencies()
    {
        var g = TestData.RequireGame();
        var cat = g.Catalog;
        Skip.IfNot(new[] { CoreOff, Coils, Laser, Floor }.All(cat.ByDefName.ContainsKey),
            "reactor parts / floor not in this install");

        var coils = cat.ByDefName[Coils];
        var core = cat.ByDefName[CoreOff];
        var laser = cat.ByDefName[Laser];

        // A generous sealed-floor field with a VACUUM hole at the reactor centre (5×5 pad top-left (10,10),
        // centre (12,12)). The floor extends above the pad so a component body has floor to stand on.
        var doc = new ShipDocument(cat);
        for (var x = 8; x <= 16; x++)
            for (var y = 6; y <= 16; y++)
                if (!(x == 12 && y == 12))   // leave the reactor centre open to space
                    new PlaceCommand(new Placement { DefName = Floor, X = x, Y = y }).Do(doc);

        // --- field coils: need the vacuum-exposed centre (the "3×3 exposed to vacuum" rule) ---
        Assert.True(CheckFit.Check(doc, coils, 10, 10, 0, includeEnvelope: false).Ok,
            "field coils should place on a 5×5 sealed pad with a vacuum-exposed centre");

        var plug = new Placement { DefName = Floor, X = 12, Y = 12 };
        new PlaceCommand(plug).Do(doc);      // seal the centre → no longer vacuum
        var plugged = CheckFit.Check(doc, coils, 10, 10, 0, includeEnvelope: false);
        Assert.False(plugged.Ok, "field coils must reject a floored (non-vacuum) centre");
        Assert.Contains((12, 12), plugged.FailedCells);
        new RemoveCommand([plug]).Do(doc);   // re-open the centre to vacuum

        // --- reactor core (the 4 segments, one placement): needs the field coils installed under it ---
        Assert.False(CheckFit.Check(doc, core, 10, 10, 0, includeEnvelope: false).Ok,
            "reactor core must reject a bare pad (no field coils to seat on)");
        new PlaceCommand(new Placement { DefName = Coils, X = 10, Y = 10 }).Do(doc);
        Assert.True(CheckFit.Check(doc, core, 10, 10, 0, includeEnvelope: false).Ok,
            "reactor core should place on the installed field coils");
        new PlaceCommand(new Placement { DefName = CoreOff, X = 10, Y = 10 }).Do(doc);

        // --- a component attaches to a reactor-core input tile (all components share this head-req pattern) ---
        // laser is 1×3; top-left (12,8) → body cells (12,8)/(12,9) on floor, head (12,10) on the core's edge.
        Assert.True(CheckFit.Check(doc, laser, 12, 8, 0, includeEnvelope: false).Ok,
            "laser array should attach to the reactor core with its body on floor");
        // without a core beneath it, the same placement has nothing to attach to
        var noCoreDoc = new ShipDocument(cat);
        for (var x = 8; x <= 16; x++)
            for (var y = 6; y <= 16; y++)
                new PlaceCommand(new Placement { DefName = Floor, X = x, Y = y }).Do(noCoreDoc);
        Assert.False(CheckFit.Check(noCoreDoc, laser, 12, 8, 0, includeEnvelope: false).Ok,
            "a reactor component must not attach where there is no reactor core");
    }

    [SkippableFact]
    public void Reactor_failure_reasons_teach_the_build_order()
    {
        // The staged-build hints must surface even when a generic rule fails on the same pose
        // (first-cell-wins used to bury "build the Field Coils first" under "needs a sealed floor").
        var g = TestData.RequireGame();
        var cat = g.Catalog;
        Skip.IfNot(new[] { CoreOff, Coils, Laser, Floor }.All(cat.ByDefName.ContainsKey),
            "reactor parts / floor not in this install");

        var doc = new ShipDocument(cat);
        for (var x = 8; x <= 16; x++)
            for (var y = 6; y <= 16; y++)
                new PlaceCommand(new Placement { DefName = Floor, X = x, Y = y }).Do(doc);

        // core on a fully-floored pad (no coils, centre not even vacuum): the coils hint must win
        var core = CheckFit.Check(doc, cat.ByDefName[CoreOff], 10, 10, 0, includeEnvelope: false);
        Assert.False(core.Ok);
        Assert.Contains("Field Coils", core.Reason);

        // component with no core beneath: points at the missing core
        var laser = CheckFit.Check(doc, cat.ByDefName[Laser], 12, 8, 0, includeEnvelope: false);
        Assert.False(laser.Ok);
        Assert.Contains("Reactor Core", laser.Reason);

        // coils over a floored centre: the floor is called out in plain words, not "blocked by IsFloor"
        var coils = CheckFit.Check(doc, cat.ByDefName[Coils], 10, 10, 0, includeEnvelope: false);
        Assert.False(coils.Ok);
        Assert.Contains("floor is in the way", coils.Reason);
    }

    [SkippableFact]
    public void A_reactor_authored_dependent_first_scans_clean()
    {
        // Regression (Dancing Jack): the constructibility sim ranks every reactor part as a "fixture", so a
        // single ordered pass built them in document order — and a real ship lists the components long before
        // the coils/core they seat on, so each component false-flagged "needs an installed Fusion Reactor Core
        // beneath". The sim now sweeps to a fixed point, finding the coils → core → component order, so a valid
        // reactor authored in dependent-first order must raise no reactor-related Blocking problem.
        var g = TestData.RequireGame();
        var cat = g.Catalog;
        Skip.IfNot(new[] { CoreOff, Coils, Laser, Floor }.All(cat.ByDefName.ContainsKey),
            "reactor parts / floor not in this install");

        var doc = new ShipDocument(cat);
        // sealed pad with a vacuum-exposed centre (as the coils require), floor extending above for the laser body
        for (var x = 8; x <= 16; x++)
            for (var y = 6; y <= 16; y++)
                if (!(x == 12 && y == 12))
                    new PlaceCommand(new Placement { DefName = Floor, X = x, Y = y }).Do(doc);
        // authored DEPENDENT-FIRST — the dependents are added before their bases, the exact order that broke
        new PlaceCommand(new Placement { DefName = Laser, X = 12, Y = 8, Rot = 0 }).Do(doc);    // needs the core (added last)
        new PlaceCommand(new Placement { DefName = CoreOff, X = 10, Y = 10, Rot = 0 }).Do(doc); // needs the coils (added after it)
        new PlaceCommand(new Placement { DefName = Coils, X = 10, Y = 10, Rot = 0 }).Do(doc);   // the base of the chain

        var blocking = ProblemScan.Scan(doc, cat)
            .Where(p => p.Severity == ProblemSeverity.Blocking &&
                        (p.Title.Contains("Reactor Core") || p.Title.Contains("Field Coils") || p.Title.Contains("occupied")))
            .ToList();
        Assert.True(blocking.Count == 0,
            "a valid reactor authored dependent-first should not block: " + string.Join(" | ", blocking.Select(p => p.Title)));
    }
}
