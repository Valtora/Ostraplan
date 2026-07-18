using System;
using System.Linq;
using Ostraplan.Core;
using Xunit;
using static Ostraplan.Tests.Fixtures;

namespace Ostraplan.Tests;

/// <summary>
/// Unit tests for <see cref="LightNetwork"/> — the v2 shadow-cast lighting behind Light Viz. The synthetic cases
/// exercise the visibility model precisely (a non-Blank light illuminates its compartment but not behind a wall,
/// reaches its radius in the open, is placed at its sub-tile position, and a Blank light casts nothing), and a
/// real-data case guards the aLights / lights.json / colors.json resolution that feeds it.
/// </summary>
public class LightNetworkTests
{
    // A game-free catalog with a floor, a wall, a white lamp (a floor that emits a real light) and a Blank lamp
    // (a floor whose light casts no illumination). Radius defaults to the game's 6 tiles.
    private static Catalog LightCatalog(byte intensity = 100, double radius = 6)
    {
        var f = new Fixtures();
        f.Color("White", 230, 231, 255, intensity);
        f.Color("Blank", 255, 0, 255, 0);
        f.Light("LampLight", "White", radius: radius);
        f.Light("BlankLight", "Blank");
        f.Floor();
        f.Wall();
        f.Part("Lamp", tileConds: ["IsFloor", "IsFloorSealed"], startingConds: ["IsInstalled"],
            category: "FURN", lights: ["LampLight"]);
        f.Part("BlankLamp", tileConds: ["IsFloor", "IsFloorSealed"], startingConds: ["IsInstalled"],
            category: "FURN", lights: ["BlankLight"]);
        return f.Build();
    }

    private static LightOverlay Build(Catalog cat, ShipDocument doc) =>
        LightNetwork.Build(ShipGrid.FromDocument(doc, cat), cat);

    /// <summary>Ray-casting point-in-polygon: is document point (px, py) inside the visibility polygon?</summary>
    private static bool InPoly(System.Collections.Generic.IReadOnlyList<(double X, double Y)> poly, double px, double py)
    {
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var (xi, yi) = poly[i];
            var (xj, yj) = poly[j];
            if (yi > py != yj > py && px < (xj - xi) * (py - yi) / (yj - yi) + xi)
                inside = !inside;
        }
        return inside;
    }

    [Fact]
    public void Light_illuminates_its_room_but_not_behind_a_wall()
    {
        var cat = LightCatalog();
        // Two enclosed cells split by a middle wall: room A = {(1,1),(2,1)} with the lamp, room B = {(4,1)}.
        var doc = Doc(cat,
            P("Wall", 0, 0), P("Wall", 1, 0), P("Wall", 2, 0), P("Wall", 3, 0), P("Wall", 4, 0), P("Wall", 5, 0),
            P("Wall", 0, 1), P("Lamp", 1, 1), P("Floor", 2, 1), P("Wall", 3, 1), P("Floor", 4, 1), P("Wall", 5, 1),
            P("Wall", 0, 2), P("Wall", 1, 2), P("Wall", 2, 2), P("Wall", 3, 2), P("Wall", 4, 2), P("Wall", 5, 2));

        var light = Assert.Single(Build(cat, doc).Lights);

        Assert.True(InPoly(light.Polygon, 2.5, 1.5), "a floor tile in the lamp's room is lit");
        Assert.False(InPoly(light.Polygon, 4.5, 1.5), "the room behind the wall is not lit");
    }

    [Fact]
    public void An_unobstructed_light_reaches_its_radius()
    {
        var cat = LightCatalog(radius: 6);
        var doc = Doc(cat, P("Lamp", 5, 7));   // a lone lamp in open space

        var light = Assert.Single(Build(cat, doc).Lights);

        var maxReach = light.Polygon.Max(p => Math.Sqrt(
            Math.Pow(p.X - light.CenterX, 2) + Math.Pow(p.Y - light.CenterY, 2)));
        Assert.True(Math.Abs(maxReach - 6) < 0.01, $"open rays reach the 6-tile radius (got {maxReach})");
    }

    [Fact]
    public void A_blank_colour_light_casts_nothing()
    {
        var cat = LightCatalog();
        var doc = Doc(cat, P("Floor", 0, 0), P("BlankLamp", 1, 0), P("Floor", 2, 0));

        Assert.True(Build(cat, doc).IsEmpty);
    }

    [Fact]
    public void A_ship_with_no_lights_yields_an_empty_overlay()
    {
        var cat = LightCatalog();
        var doc = Doc(cat, P("Floor", 0, 0), P("Wall", 1, 0), P("Floor", 2, 0));

        Assert.True(Build(cat, doc).IsEmpty);
    }

    [Fact]
    public void The_light_centre_is_its_sub_tile_document_position()
    {
        var cat = LightCatalog();
        var doc = Doc(cat, P("Lamp", 5, 7));   // ptPos (0,0) → the centre of tile (5,7)

        var light = Assert.Single(Build(cat, doc).Lights);

        Assert.Equal(5.5, light.CenterX, 3);
        Assert.Equal(7.5, light.CenterY, 3);
    }

    [SkippableFact]
    public void Real_catalog_resolves_light_fixtures()
    {
        var g = TestData.RequireGame();

        // the data tables loaded
        Assert.NotEmpty(g.Catalog.LightDefs);
        Assert.True(g.Catalog.ColorTable.TryGetValue("WhiteLightCeiling", out var c) && c.A == 100,
            "ceiling-white intensity (alpha) should be 100");

        // a known fixture light resolves with its real radius + colour
        Assert.True(g.Catalog.LightDefs.TryGetValue("Ceiling1x1White", out var ld), "Ceiling1x1White light def missing");
        Assert.Equal("WhiteLightCeiling", ld!.Color);
        Assert.Equal(18, ld.Radius);

        // buildable fixtures resolve casting lights through aLights → lights.json → colors.json
        var casters = g.Catalog.Parts.Count(p => g.Catalog.LightsFor(p).Any(l => l.CastsLight));
        Assert.True(casters >= 5, $"only {casters} buildable parts resolved a casting light");
    }
}
