using System;
using System.Linq;
using Ostraplan.Core;
using Xunit;
using static Ostraplan.Tests.Fixtures;

namespace Ostraplan.Tests;

/// <summary>
/// Unit tests for the game-exact Light Viz pipeline: <see cref="LightNetwork"/> (scene resolution),
/// <see cref="VisibilityMesh"/> (the ported shadow-mesh geometry) and <see cref="LightComposite"/> (the ported
/// LoSPass shading + screen-blend accumulation). The synthetic cases pin the model's load-bearing behaviours —
/// occlusion comes from <c>aShadowBoxes</c> (glass passes, walls shadow), wall faces get a lit skirt, the falloff
/// matches the disassembled shader curve, and overlapping lights screen-blend — and a real-data case guards the
/// aLights / lights.json / colors.json / parallax resolution that feeds it.
/// </summary>
public class LightNetworkTests
{
    private const byte Alpha = 100;   // test lamp intensity (WhiteLightCeiling's real alpha)

    // A game-free catalog with a floor, an occluding wall, a glass window, a white lamp and a Blank lamp.
    private static Catalog LightCatalog(double radius = 6)
    {
        var f = new Fixtures();
        f.Color("White", 255, 255, 255, Alpha);
        f.Color("Blank", 255, 0, 255, 0);
        f.Light("LampLight", "White", radius: radius);
        f.Light("BlankLight", "Blank");
        f.Floor();
        f.Wall();
        f.Window();
        f.Part("Lamp", tileConds: ["IsFloor", "IsFloorSealed"], startingConds: ["IsInstalled"],
            category: "FURN", lights: ["LampLight"]);
        f.Part("BlankLamp", tileConds: ["IsFloor", "IsFloorSealed"], startingConds: ["IsInstalled"],
            category: "FURN", lights: ["BlankLight"]);
        return f.Build();
    }

    private static LightScene Scene(Catalog cat, ShipDocument doc, SunSettings? sun = null) =>
        LightNetwork.Build(ShipGrid.FromDocument(doc, cat), cat, sun);

    /// <summary>Accumulate the scene over a doc-tile window and return a brightness sampler (tile coords →
    /// the red channel of the accumulated light, 0-255; the test colours are grey so one channel suffices).</summary>
    private static Func<double, double, byte> Lit(LightScene scene, int minX, int minY, int tiles)
    {
        const int ppt = 16;
        var w = tiles * ppt;
        var acc = LightComposite.AccumulateLights(scene, w, w, ppt, minX, minY, null);
        return (tx, ty) =>
        {
            var px = (int)((tx - minX) * ppt);
            var py = (int)((ty - minY) * ppt);
            return acc[(py * w + px) * 3];
        };
    }

    /// <summary>The shader's brightness for a flat surface at distance <paramref name="dist"/> from a light of
    /// <paramref name="radius"/>: diffuse × atten × intensity (see <see cref="LightComposite"/>), 0-255.</summary>
    private static double Expected(double dist, double radius, double intensity = Alpha / 255.0)
    {
        var u2 = dist * dist / (4 * radius * radius);
        const double z = 0.25, f = 3;
        var diffuse = f * z / Math.Sqrt(f * f * (u2 + z * z));
        var atten = 1.0 / (f * f * (u2 + z * z) + 0.1);
        return Math.Clamp(intensity * diffuse * atten, 0, 1) * 255;
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

        var lit = Lit(Scene(cat, doc), -2, -2, 12);
        Assert.True(lit(2.5, 1.5) > 0, "a floor tile in the lamp's room is lit");
        Assert.Equal(0, lit(4.5, 1.5));   // the room behind the wall is dark
    }

    [Fact]
    public void Glass_passes_light_and_wall_faces_get_a_lit_skirt()
    {
        var cat = LightCatalog();
        // Same layout, but the divider is a glass window: the far room IS lit through it.
        var doc = Doc(cat,
            P("Wall", 0, 1), P("Lamp", 1, 1), P("Floor", 2, 1), P("Window", 3, 1), P("Floor", 4, 1), P("Wall", 5, 1));

        var lit = Lit(Scene(cat, doc), -4, -4, 14);
        Assert.True(lit(4.5, 1.5) > 0, "light passes through the glass window");
        // The solid wall at (5,1): its NEAR half is inside the lit skirt (light penetrates max(rx,ry) = 0.5 into
        // a wall face), its far edge is not.
        Assert.True(lit(5.2, 1.5) > 0, "the wall's lit face (skirt) is illuminated");
        Assert.Equal(0, lit(6.5, 1.5));   // behind the wall stays dark
    }

    [Fact]
    public void An_unobstructed_light_reaches_its_radius_and_matches_the_shader_curve()
    {
        var cat = LightCatalog(radius: 6);
        var doc = Doc(cat, P("Lamp", 10, 10));   // a lone lamp in the open; light centre (10.5, 10.5)

        var lit = Lit(Scene(cat, doc), 0, 0, 21);

        // brightness at the centre and at 3 tiles out follows the disassembled LoSPass falloff exactly (±2 for
        // the 8-bit quantisation and the half-pixel sampling offset)
        Assert.InRange(lit(10.5 + 1 / 32.0, 10.5 + 1 / 32.0), Expected(0, 6) - 2, Expected(0, 6) + 2);
        Assert.InRange(lit(13.5, 10.5), Expected(3.0 - 1 / 32.0, 6) - 3, Expected(3.0 + 1 / 32.0, 6) + 3);
        // the lit disc reaches the full radius (rim ring at R−0.5 + its 0.5 skirt) and stops there
        Assert.True(lit(10.5 + 5.9, 10.5) > 0, "just inside the radius is lit");
        Assert.Equal(0, lit(10.5 + 6.2, 10.5));
    }

    [Fact]
    public void Overlapping_lights_screen_blend()
    {
        var cat = LightCatalog();
        var doc = Doc(cat, P("Lamp", 10, 10), P("Floor", 11, 10));
        var one = Lit(Scene(cat, doc), 0, 0, 21)(10.5 + 1 / 32.0, 10.5 + 1 / 32.0);

        var doc2 = Doc(cat, P("Lamp", 10, 10));
        Place(doc2, "Lamp", 10, 10);   // a second identical lamp on the same tile
        var two = Lit(Scene(cat, doc2), 0, 0, 21)(10.5 + 1 / 32.0, 10.5 + 1 / 32.0);

        // screen blend: two = 1 − (1 − one)² in 0-255 space, ±2 for fixed-point rounding
        var expected = 255 - (255.0 - one) * (255.0 - one) / 255.0;
        Assert.InRange(two, expected - 2, expected + 2);
    }

    [Fact]
    public void A_blank_colour_light_casts_nothing()
    {
        var cat = LightCatalog();
        var doc = Doc(cat, P("Floor", 0, 0), P("BlankLamp", 1, 0), P("Floor", 2, 0));

        Assert.Empty(Scene(cat, doc).Lights);
    }

    [Fact]
    public void A_ship_with_no_lights_yields_an_empty_scene()
    {
        var cat = LightCatalog();
        var doc = Doc(cat, P("Floor", 0, 0), P("Wall", 1, 0), P("Floor", 2, 0));

        Assert.Empty(Scene(cat, doc).Lights);
        Assert.Empty(Scene(cat, doc).Glows);
    }

    [Fact]
    public void The_light_centre_is_its_sub_tile_document_position()
    {
        var cat = LightCatalog();
        var doc = Doc(cat, P("Lamp", 5, 7));   // ptPos (0,0) → the centre of tile (5,7)

        var light = Assert.Single(Scene(cat, doc).Lights);

        Assert.Equal(5.5, light.DocX, 3);
        Assert.Equal(7.5, light.DocY, 3);
    }

    [Fact]
    public void Sun_light_shines_from_its_rotated_direction_and_respects_the_hull()
    {
        var f = new Fixtures();
        f.Color("SunYellow", 255, 240, 200, 160);
        f.Light("TestSun", "SunYellow", radius: 1000, px: -250, py: 250);
        f.Parallax("TestSpace", "TestSun");
        f.Floor();
        f.Wall();
        var cat = f.Build();

        // a lone wall in the open: the sun (up-left of the ship in game coords) lights one side, shadows the other
        var doc = Doc(cat, P("Wall", 10, 10), P("Floor", 10, 11), P("Floor", 10, 9));
        var lit = Lit(Scene(cat, doc, new SunSettings("TestSpace", 0)), 0, 0, 21);

        // ptPos (−250, +250) game coords = up-left → doc up-left; the tile above the wall faces the sun
        Assert.True(lit(10.5, 8.5) > 0, "the sun-facing side is lit");
        // pixels shadowed by the wall: sample the far-side diagonal just behind the wall
        Assert.Equal(0, lit(11.2, 11.7));
    }

    [SkippableFact]
    public void Real_catalog_resolves_light_fixtures_and_occluders()
    {
        var g = TestData.RequireGame();

        // the data tables loaded
        Assert.NotEmpty(g.Catalog.LightDefs);
        Assert.NotEmpty(g.Catalog.ParallaxDefs);
        Assert.True(g.Catalog.ColorTable.TryGetValue("WhiteLightCeiling", out var c) && c.A == 100,
            "ceiling-white intensity (alpha) should be 100");

        // a known fixture light resolves with its real radius + colour
        Assert.True(g.Catalog.LightDefs.TryGetValue("Ceiling1x1White", out var ld), "Ceiling1x1White light def missing");
        Assert.Equal("WhiteLightCeiling", ld!.Color);
        Assert.Equal(18, ld.Radius);

        // occluders come from aShadowBoxes: the basic wall blocks (wall-flagged), the window is glass,
        // the thin wall has no boxes at all
        var wall = g.Catalog.ByDefName["ItmWall1x1"];
        Assert.True(wall.Item.IsWallForLight);
        var wallBox = Assert.Single(wall.Item.ShadowBoxes);
        Assert.False(wallBox.Glass);
        Assert.Equal(0.5, wallBox.Rx, 3);

        Assert.True(g.Catalog.ByDefName.TryGetValue("ItmWallWindow1x1", out var window) ||
                    g.Catalog.ByDefName.TryGetValue("ItmWallWindow1x1Sq", out window), "no window def resolved");
        Assert.Contains(window!.Item.ShadowBoxes, b => b.Glass);

        // buildable fixtures resolve casting lights through aLights → lights.json → colors.json
        var casters = g.Catalog.Parts.Count(p => g.Catalog.LightsFor(p).Any(l => l.CastsLight));
        Assert.True(casters >= 5, $"only {casters} buildable parts resolved a casting light");

        // deep-space parallax carries the yellow sun
        Assert.True(g.Catalog.ParallaxDefs.TryGetValue("DeepSpace", out var px) && px!.SunLightNames.Length > 0);
    }
}
