using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The game's parallax backdrops (#43), read from the <c>TXTParallax*</c> loots. The weighted layer syntax is the
/// part worth testing off the install, because <see cref="LootDef"/> cannot carry it: its <c>aCOs</c> mapping
/// stops at the first <c>'='</c> and so keeps one option out of a layer's several.
/// </summary>
public class ParallaxTests
{
    /// <summary>Feed a layer a fixed sequence of rolls, so a test states the dice rather than hunting a seed.</summary>
    private static Func<double> Rolls(params double[] values)
    {
        var i = 0;
        return () => values[i++];
    }

    [Fact]
    public void A_layer_keeps_every_option_and_its_chance()
    {
        var layer = ParallaxLayer.Parse("PlxDebrisSm03=0.35x1|PlxDebrisSm04=0.35x1|blank=1.0x1");

        Assert.Equal(3, layer.Options.Count);
        Assert.Equal("PlxDebrisSm03", layer.Options[0].Image);
        // The chance is the number BEFORE the 'x'. Reading the amount after it gives 1 for every option in the
        // stock data, which looks plausible and makes every layer draw its first option every time.
        Assert.Equal(0.35, layer.Options[0].Chance, 3);
        Assert.Equal(ParallaxLayer.Blank, layer.Options[2].Image);
        Assert.Equal(1.0, layer.Options[2].Chance, 3);
    }

    [Fact]
    public void A_certain_option_is_taken_without_consuming_a_roll()
    {
        var layer = ParallaxLayer.Parse("Starfield_Transparent01=1.0x1");
        Assert.Equal("Starfield_Transparent01", layer.Pick(Rolls()));   // would throw if it rolled
    }

    [Fact]
    public void Options_are_tried_in_order_each_at_its_own_chance()
    {
        const string entry = "A=0.35x1|B=0.35x1|blank=1.0x1";

        Assert.Equal("A", ParallaxLayer.Parse(entry).Pick(Rolls(0.10)));             // A takes it
        Assert.Equal("B", ParallaxLayer.Parse(entry).Pick(Rolls(0.90, 0.10)));       // A declines, B takes it
        Assert.Null(ParallaxLayer.Parse(entry).Pick(Rolls(0.90, 0.90)));             // both decline, blank
    }

    [Fact]
    public void Blank_is_a_real_option_that_draws_nothing()
    {
        // Dropping blank at parse time would change the odds: this layer is usually empty, and a parser that
        // removed the option would draw the rock every time instead.
        var layer = ParallaxLayer.Parse("PlxDebris05=0.2x1|blank=1.0x1");

        Assert.Equal("PlxDebris05", layer.Pick(Rolls(0.1)));
        Assert.Null(layer.Pick(Rolls(0.5)));
    }

    [Fact]
    public void An_option_with_no_chance_is_a_certainty()
    {
        Assert.Equal("A", ParallaxLayer.Parse("A|B").Pick(Rolls()));
    }

    [Fact]
    public void A_layer_whose_options_all_decline_draws_nothing()
    {
        // No trailing blank, so there is nothing to fall through to. It must not throw or draw the last option.
        Assert.Null(ParallaxLayer.Parse("A=0.1x1|B=0.1x1").Pick(Rolls(0.9, 0.9)));
    }

    [Fact]
    public void The_same_locale_always_composites_the_same_way()
    {
        var locale = new ParallaxLocale("TXTParallaxOKLGBoneyard",
        [
            ParallaxLayer.Parse("Starfield_Transparent01=1.0x1"),
            ParallaxLayer.Parse("PlxWreck04=0.35x1|PlxWreck05=0.35x1|PlxWreck06=1.0x1"),
            ParallaxLayer.Parse("PlxDebrisSm03=0.35x1|blank=1.0x1"),
        ]);

        var seed = locale.RepresentativeSeed();
        Assert.Equal(locale.Resolve(seed), locale.Resolve(seed));
        // Determined by the layers alone, in a fixed order, so the same locale composites identically on every
        // machine and in every screenshot. Nothing is stored and nothing is rolled at startup.
        Assert.Equal(seed, new ParallaxLocale(locale.Name, locale.Layers).RepresentativeSeed());
    }

    [Fact]
    public void A_locale_whose_interest_is_in_unlikely_layers_still_shows_it()
    {
        // Asteroid Field's shape: a certain starfield plus four independent 35% chances at a rock. An arbitrary
        // fixed seed leaves it empty better than one time in six, and somebody who picks "Asteroid Field" and
        // gets bare space has been handed a broken feature rather than an unlucky roll.
        var field = new ParallaxLocale("TXTParallaxAsteroidField",
        [
            ParallaxLayer.Parse("Starfield_Transparent01=1.0x1"),
            ParallaxLayer.Parse("PlxDebrisSm03=0.35x1|blank=1.0x1"),
            ParallaxLayer.Parse("PlxDebrisSm04=0.35x1|blank=1.0x1"),
            ParallaxLayer.Parse("PlxDebrisSm03=0.35x1|blank=1.0x1"),
        ]);

        var drawn = field.Resolve(field.RepresentativeSeed());
        Assert.Equal(field.Layers.Count, drawn.Count);   // every layer found something to draw
    }

    [Fact]
    public void A_locale_that_can_never_fill_every_layer_still_settles_on_one_seed()
    {
        // An impossible layer must not make the search run away or throw; it just never improves the best.
        var awkward = new ParallaxLocale("TXTParallaxAwkward",
        [
            ParallaxLayer.Parse("Sure=1.0x1"),
            ParallaxLayer.Parse("Never=0.0x1"),
        ]);

        var seed = awkward.RepresentativeSeed();
        Assert.InRange(seed, 0, ParallaxLocale.SeedSearch - 1);
        Assert.Equal(["Sure"], awkward.Resolve(seed));
    }

    [Fact]
    public void A_resolved_locale_names_only_layers_that_drew_something()
    {
        var locale = new ParallaxLocale("TXTParallaxTest",
        [
            ParallaxLayer.Parse("Keep=1.0x1"),
            ParallaxLayer.Parse("blank=1.0x1"),
            ParallaxLayer.Parse("AlsoKeep=1.0x1"),
        ]);

        Assert.Equal(["Keep", "AlsoKeep"], locale.Resolve(0));
    }

    [Theory]
    [InlineData("TXTParallaxVenusGrav", "Venus, orbit")]
    [InlineData("TXTParallaxEarthAtmosphere", "Earth, in atmosphere")]
    [InlineData("TXTParallaxOKLGBoneyard", "OKLG Boneyard")]
    [InlineData("TXTParallaxDeepSpace", "Deep Space")]
    [InlineData("TXTParallaxMarsSub", "Mars Sub")]
    [InlineData("SomeModBackdrop", "Some Mod Backdrop")]
    public void A_backdrop_gets_a_name_a_person_can_choose_from(string name, string display) =>
        Assert.Equal(display, new ParallaxLocale(name, []).Display);

    [SkippableFact]
    public void Every_backdrop_the_install_ships_resolves_to_art_on_disk()
    {
        var g = TestData.RequireGame();
        var locales = ParallaxCatalog.All(g.Catalog);
        Skip.If(locales.Count == 0, "This install's data declares no parallax backdrops.");

        foreach (var locale in locales)
        {
            var picked = locale.Resolve(locale.RepresentativeSeed());
            var images = ParallaxCatalog.ResolveImages(g.Catalog, locale, locale.RepresentativeSeed());

            // Every picked layer resolves to a file. A gap here is a mod that shipped data without its art, which
            // the renderer survives by dropping that layer, but stock data should never have one.
            Assert.Equal(picked.Count, images.Count);
            Assert.False(string.IsNullOrWhiteSpace(locale.Display));
        }
    }

    [SkippableFact]
    public void The_stock_backdrops_are_all_there_and_findable_by_name()
    {
        var g = TestData.RequireGame();
        var locales = ParallaxCatalog.All(g.Catalog);
        Skip.If(locales.Count == 0, "This install's data declares no parallax backdrops.");

        Assert.Contains(locales, l => l.Name == "TXTParallaxDeepSpace");
        Assert.NotNull(ParallaxCatalog.Find(g.Catalog, "TXTParallaxDeepSpace"));
        Assert.Null(ParallaxCatalog.Find(g.Catalog, "TXTParallaxNoSuchPlace"));
        Assert.Null(ParallaxCatalog.Find(g.Catalog, null));
    }
}
