using System.Text.Json;

namespace Ostraplan.Core;

/// <summary>
/// One depth layer of a parallax backdrop: the images the game may draw at that depth, each with the chance it is
/// the one drawn. A layer's <c>aCOs</c> entry is a <c>'|'</c>-separated option list, each option written as the
/// game's usual <c>name=chancexamount</c> equation, so <c>"PlxDebrisSm03=0.35x1|blank=1.0x1"</c> is "a small rock
/// about a third of the time, otherwise nothing".
///
/// <para>The number that matters is the <b>chance</b>, before the 'x' (<see cref="LootDef.CondChance"/>), not the
/// amount after it. Every option in the stock data has an amount of 1, so reading the wrong half gives a layer
/// whose options are all equally likely and looks plausible while being wrong.</para>
///
/// <para>The options are tried <b>in order</b>, each at its own chance, rather than being normalised into one
/// weighted draw. That is what a trailing <c>blank=1.0</c> means: a certainty, and therefore a terminator, which
/// is only coherent if the list is walked until something takes. Under a normalised reading a weight of 1.0
/// against two of 0.35 would be an ordinary option rather than the fallback it plainly is.</para>
///
/// <para><c>blank</c> is the game's own name for the empty option and is kept rather than dropped at parse time,
/// because dropping it would change the odds: a layer that is usually empty would draw a rock every time.</para>
/// </summary>
public sealed record ParallaxLayer(IReadOnlyList<(string Image, double Chance)> Options)
{
    /// <summary>The game's name for "draw nothing at this depth".</summary>
    public const string Blank = "blank";

    /// <summary>
    /// The image this layer draws, or null when it draws nothing. <paramref name="roll"/> is called once per
    /// option tried and must return a value in [0, 1). Taking a roll source rather than a <see cref="Random"/>
    /// is what lets a test state the rolls outright instead of reverse-engineering a seed.
    /// </summary>
    public string? Pick(Func<double> roll)
    {
        foreach (var (image, chance) in Options)
            if (chance >= 1.0 || roll() < chance)
                return image == Blank ? null : image;
        return null;   // every option declined: the layer is empty, same as blank
    }

    /// <summary>Parse one <c>aCOs</c> entry. An option with no readable chance is a certainty, matching
    /// <c>Loot.ParseCondEquation</c>, which treats a missing chance as 1.</summary>
    public static ParallaxLayer Parse(string entry)
    {
        var options = new List<(string, double)>();
        foreach (var option in entry.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = LootDef.CondName(option);
            if (name.Length == 0) continue;
            options.Add((name, LootDef.CondChance(option)));
        }
        return new ParallaxLayer(options);
    }
}

/// <summary>
/// One named backdrop from <c>data/loot/loot_parallax.json</c>, as an ordered list of <see cref="ParallaxLayer"/>
/// running back to front. The game draws these as full-screen tiling sprites at increasing depth; Ostraplan draws
/// the same images composited into one tile (see the App's backdrop brushes), which works because every layer
/// image the stock data names is square at 1920, 960, 640 or 2 pixels, and each of those divides 1920 exactly.
/// </summary>
public sealed record ParallaxLocale(string Name, IReadOnlyList<ParallaxLayer> Layers)
{
    /// <summary>The prefix every backdrop entry carries. Stripped for display.</summary>
    public const string NamePrefix = "TXTParallax";

    /// <summary>
    /// A readable name for a picker. <c>TXTParallaxVenusGrav</c> becomes "Venus, orbit" and
    /// <c>TXTParallaxEarthAtmosphere</c> "Earth, in atmosphere", because the game's two suffixes are the
    /// distinction that matters to somebody choosing a backdrop and neither reads as English on its own. Anything
    /// else is de-camel-cased, so a modded entry still gets a sensible label rather than being hidden.
    /// </summary>
    public string Display
    {
        get
        {
            var bare = Name.StartsWith(NamePrefix, StringComparison.Ordinal) ? Name[NamePrefix.Length..] : Name;
            if (bare.Length == 0) return Name;

            if (bare.EndsWith("Grav", StringComparison.Ordinal))
                return Words(bare[..^"Grav".Length]) + ", orbit";
            if (bare.EndsWith("Atmosphere", StringComparison.Ordinal))
                return Words(bare[..^"Atmosphere".Length]) + ", in atmosphere";
            return Words(bare);
        }
    }

    /// <summary>"OKLGBoneyard" -> "OKLG Boneyard": split before a capital that starts a new word, keeping runs of
    /// capitals (the station tickers, which are four-letter codes) together.</summary>
    private static string Words(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 4);
        for (var i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i])
                && (!char.IsUpper(s[i - 1]) || (i + 1 < s.Length && char.IsLower(s[i + 1]))))
                sb.Append(' ');
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The images to draw, back to front, for this locale. Layers that roll blank are left out, so the result is
    /// shorter than <see cref="Layers"/> whenever a locale has optional depths.
    ///
    /// <para><paramref name="seed"/> fixes the composition. Ostraplan derives it from the locale's own name so a
    /// backdrop looks the same on every launch and in every screenshot, which the game does not do and does not
    /// need to: it re-rolls per visit because the player is passing through, while a design sits in front of the
    /// same backdrop for hours.</para>
    /// </summary>
    public IReadOnlyList<string> Resolve(int seed)
    {
        var rng = new Random(seed);
        var picked = new List<string>(Layers.Count);
        foreach (var layer in Layers)
            if (layer.Pick(rng.NextDouble) is { } image)
                picked.Add(image);
        return picked;
    }

    /// <summary>
    /// The seed <see cref="Resolve"/> is called with: the one, of the first <see cref="SeedSearch"/>, that draws
    /// the most layers.
    ///
    /// <para>An arbitrary fixed seed is not good enough, because a locale's character often lives in its
    /// low-chance layers. Asteroid Field is a starfield plus four independent 35% chances at a rock, so better
    /// than one seed in six leaves it indistinguishable from empty space, and a user who picks "Asteroid Field"
    /// and gets no asteroids has been given a broken feature rather than an unlucky roll. The game gets away with
    /// rolling because it re-rolls every visit; a backdrop that is chosen once and then looked at for hours does
    /// not get a second chance.</para>
    ///
    /// <para>Deterministic all the same, which is the point: the search runs over a fixed range in a fixed order
    /// and ties break to the lowest seed, so a locale composites identically on every machine and in every
    /// screenshot.</para>
    /// </summary>
    public int RepresentativeSeed()
    {
        var (best, bestCount) = (0, -1);
        for (var seed = 0; seed < SeedSearch; seed++)
        {
            var count = Resolve(seed).Count;
            if (count > bestCount) (best, bestCount) = (seed, count);
            if (bestCount == Layers.Count) break;   // nothing can beat every layer drawing
        }
        return best;
    }

    /// <summary>How many seeds <see cref="RepresentativeSeed"/> considers. Small: the search stops early the
    /// moment every layer draws, and the locales that never reach that are the ones with a genuinely unlikely
    /// layer, where a wider search buys a rock nobody would notice.</summary>
    public const int SeedSearch = 64;
}

/// <summary>
/// The backdrops the loaded game data offers, read from the <c>loot</c> type by name prefix.
///
/// <para>Read off <see cref="Catalog.Index"/> rather than <see cref="Catalog.Loots"/> on purpose.
/// <see cref="LootDef"/> maps each <c>aCOs</c> entry through <see cref="LootDef.CondName"/>, which stops at the
/// first <c>'='</c> and so keeps only the first option of a layer, discarding the alternatives and their weights.
/// That is the right shape for a condition loot and the wrong one for this, which is the whole reason the raw
/// element is parsed here.</para>
/// </summary>
public static class ParallaxCatalog
{
    /// <summary>Every backdrop the data declares, in data order. Empty for a synthetic catalog with no index, and
    /// for an install whose data a mod has emptied.</summary>
    public static IReadOnlyList<ParallaxLocale> All(Catalog catalog)
    {
        if (catalog.Index is not { } index) return [];
        var locales = new List<ParallaxLocale>();
        foreach (var (name, raw) in index.Type("loot"))
        {
            if (!name.StartsWith(ParallaxLocale.NamePrefix, StringComparison.Ordinal)) continue;
            var layers = Json.StrArray(raw.El, "aCOs")
                .Select(ParallaxLayer.Parse)
                .Where(l => l.Options.Count > 0)
                .ToArray();
            if (layers.Length > 0) locales.Add(new ParallaxLocale(name, layers));
        }
        locales.Sort((a, b) => string.CompareOrdinal(a.Display, b.Display));
        return locales;
    }

    /// <summary>The named backdrop, or null when this install's data does not declare it (a mod was removed, or
    /// the name was typed into settings by hand).</summary>
    public static ParallaxLocale? Find(Catalog catalog, string? name) =>
        name is { Length: > 0 } ? All(catalog).FirstOrDefault(l => l.Name == name) : null;

    /// <summary>The absolute path of each image <paramref name="locale"/> resolves to, back to front, with any the
    /// install cannot supply left out. A missing image is a mod that shipped a data file without its art, and one
    /// gap should cost that layer rather than the whole backdrop.</summary>
    public static IReadOnlyList<string> ResolveImages(Catalog catalog, ParallaxLocale locale, int seed) =>
        catalog.Index is { } index
            ? locale.Resolve(seed).Select(index.ResolveImage).OfType<string>().ToArray()
            : [];
}
