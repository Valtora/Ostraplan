using System.Globalization;

namespace Ostraplan.Core;

/// <summary>What the plan is drawn on.</summary>
public enum BackdropKind
{
    /// <summary>One flat colour, <see cref="BackdropSettings.Solid"/>.</summary>
    Solid = 0,

    /// <summary>A two-colour checkerboard, for reading a dark hull against something that is never the hull's own
    /// colour. The high-contrast case the missing-texture checker is named for.</summary>
    Checker = 1,

    /// <summary>One of the game's own parallax backdrops (see <see cref="ParallaxLocale"/>).</summary>
    Locale = 2,
}

/// <summary>
/// The plan's backdrop and its grid markings. App-wide rather than per design, the same as the theme and the UI
/// scale, because it is about the person looking at the plan rather than about the ship: a design opened on
/// somebody else's machine should look the way <b>they</b> set it up.
///
/// <para>Colours are stored as <c>#RRGGBB</c> strings so <c>settings.json</c> stays legible and hand-editable,
/// which is the same reason the theme is stored as a word rather than an enum ordinal.</para>
/// </summary>
public sealed record BackdropSettings
{
    public BackdropKind Kind { get; init; } = BackdropKind.Solid;

    /// <summary>The flat colour, and the ground the checkerboard's dark squares use.</summary>
    public string Solid { get; init; } = DefaultSolid;

    /// <summary>The checkerboard's second colour.</summary>
    public string CheckerAlt { get; init; } = DefaultCheckerAlt;

    /// <summary>The checkerboard's square size, in logical pixels. Pixels rather than tiles because the backdrop
    /// is anchored to the viewport and does not zoom with the plan, so it has no size in tiles to be measured in.
    /// Clamped to <see cref="MinCheckerSquare"/>..<see cref="MaxCheckerSquare"/>.</summary>
    public int CheckerSquare { get; init; } = 64;

    /// <summary>The <see cref="ParallaxLocale.Name"/> to draw, or null for none chosen yet.</summary>
    public string? Locale { get; init; }

    /// <summary>How far the locale art is darkened towards black, 0 (untouched) to 1 (black). The art is drawn at
    /// full strength in game, where nothing is laid over it; here a ship sits on top of it and has to stay the
    /// thing being read, which is what this is for.</summary>
    public double LocaleDimming { get; init; } = DefaultLocaleDimming;

    /// <summary>Draw a brighter grid line every this many tiles, or 0 for none. The sense-of-scale marking: a
    /// 20-wide hull is hard to judge against a uniform one-tile grid.</summary>
    public int CoarseGrid { get; init; }

    // ---- defaults ----

    /// <summary>The colour the plan was drawn on before any of this was a choice, and still the default.</summary>
    public const string DefaultSolid = "#14161A";

    /// <summary>A medium purple against the near-black ground: distinct from every floor and wall skin the game
    /// ships, which is the point of a missing-texture checker.</summary>
    public const string DefaultCheckerAlt = "#3A2A52";

    public const double DefaultLocaleDimming = 0.45;

    public const int MinCheckerSquare = 8;
    public const int MaxCheckerSquare = 512;
    public const int MaxCoarseGrid = 100;

    public static readonly BackdropSettings Default = new();

    /// <summary>This settings object with every field forced into range. Settings arrive from a JSON file a user
    /// may have edited, so nothing downstream should have to defend itself against a checkerboard of zero tiles
    /// (an infinite loop) or a dimming of -4 (a brighter-than-white backdrop).</summary>
    public BackdropSettings Clamped() => this with
    {
        Solid = Backdrop.NormaliseColour(Solid, DefaultSolid),
        CheckerAlt = Backdrop.NormaliseColour(CheckerAlt, DefaultCheckerAlt),
        CheckerSquare = Math.Clamp(CheckerSquare, MinCheckerSquare, MaxCheckerSquare),
        LocaleDimming = Math.Clamp(LocaleDimming, 0, 1),
        CoarseGrid = Math.Clamp(CoarseGrid, 0, MaxCoarseGrid),
    };
}

/// <summary>One offered colour: what it looks like and what to call it.</summary>
public sealed record BackdropSwatch(string Name, string Hex);

/// <summary>
/// Colour handling for the backdrop: the offered palette, hex parsing, and the luminance test that decides
/// whether the plan's overlays have to be drawn in dark ink instead of light.
/// </summary>
public static class Backdrop
{
    /// <summary>
    /// Above this relative luminance the backdrop is treated as light and the canvas draws its grid, hover ring
    /// and labels in dark ink. Everything on the plan was white or near-white on a near-black ground for the whole
    /// of the app's life, so a white backdrop erases the grid entirely without this.
    ///
    /// <para>Set at the midpoint of the WCAG luminance range rather than lower, because the overlays are drawn at
    /// low alpha: a mid grey carries faint white better than it carries faint black.</para>
    /// </summary>
    public const double LightThreshold = 0.5;

    /// <summary>Relative luminance of an sRGB colour, per WCAG 2.x. Used on a flat colour directly, and on the
    /// mean pixel of a composited locale backdrop.</summary>
    public static double Luminance(byte r, byte g, byte b) =>
        0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);

    private static double Linear(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary>Whether the plan's overlays need dark ink to be visible on this colour.</summary>
    public static bool IsLight(byte r, byte g, byte b) => Luminance(r, g, b) > LightThreshold;

    /// <summary>Parse <c>#RRGGBB</c> (with or without the hash, and case-insensitive). Null when it is not a
    /// colour, which is what a hand-edited settings file is allowed to contain.</summary>
    public static (byte R, byte G, byte B)? ParseColour(string? hex)
    {
        if (hex is null) return null;
        var s = hex.Trim().TrimStart('#');
        if (s.Length != 6) return null;
        return byte.TryParse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
            && byte.TryParse(s[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
            && byte.TryParse(s[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)
                ? (r, g, b)
                : null;
    }

    /// <summary><paramref name="hex"/> in canonical <c>#RRGGBB</c> form, or <paramref name="fallback"/> when it is
    /// not a colour at all.</summary>
    public static string NormaliseColour(string? hex, string fallback) =>
        ParseColour(hex) is { } c ? Format(c.R, c.G, c.B) : fallback;

    public static string Format(byte r, byte g, byte b) =>
        string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}");

    private static readonly (string Name, byte R, byte G, byte B)[] Hues =
    [
        ("Red",    0xC0, 0x39, 0x2B),
        ("Orange", 0xD3, 0x54, 0x00),
        ("Yellow", 0xC9, 0xA2, 0x27),
        ("Green",  0x27, 0x86, 0x5A),
        ("Blue",   0x2C, 0x6F, 0xB5),
        ("Purple", 0x7A, 0x4F, 0xA3),
        ("Pink",   0xB8, 0x4A, 0x7D),
    ];

    /// <summary>
    /// The offered palette: the app's own default, black and white, three greys, and seven hues at three
    /// brightnesses each. The hues are muted rather than saturated because a backdrop is looked at all day behind
    /// the work, and a pure red one is not.
    ///
    /// <para>Declared after <see cref="Hues"/> deliberately. Static field initialisers run in declaration order,
    /// so building the palette above the table it reads leaves <see cref="Hues"/> null and the whole type fails to
    /// initialise, which surfaces as a TypeInitializationException from the first property touched rather than as
    /// anything pointing at the order.</para>
    /// </summary>
    public static IReadOnlyList<BackdropSwatch> Palette { get; } = BuildPalette();

    private static List<BackdropSwatch> BuildPalette()
    {
        var palette = new List<BackdropSwatch>
        {
            new("Ostraplan dark", BackdropSettings.DefaultSolid),
            new("Black", "#000000"),
            new("Dark grey", "#1E2126"),
            new("Grey", "#4A4F57"),
            new("Light grey", "#C8CCD2"),
            new("White", "#FFFFFF"),
        };
        foreach (var (name, r, g, b) in Hues)
        {
            palette.Add(new BackdropSwatch($"Dark {name.ToLowerInvariant()}", Format(Scale(r, 0.45), Scale(g, 0.45), Scale(b, 0.45))));
            palette.Add(new BackdropSwatch(name, Format(r, g, b)));
            palette.Add(new BackdropSwatch($"Light {name.ToLowerInvariant()}", Format(Lighten(r), Lighten(g), Lighten(b))));
        }
        return palette;
    }

    private static byte Scale(byte channel, double by) => (byte)Math.Clamp(Math.Round(channel * by), 0, 255);

    /// <summary>Toward white by 55%, which keeps the hue recognisable where scaling up the channels would wash
    /// the whole set out to much the same pale nothing.</summary>
    private static byte Lighten(byte channel) => (byte)Math.Clamp(Math.Round(channel + (255 - channel) * 0.55), 0, 255);
}
