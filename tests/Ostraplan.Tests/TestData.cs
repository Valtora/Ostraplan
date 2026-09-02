using System.IO;
using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>Shared, once-per-run load of the live game data; null when no install on this machine.</summary>
internal static class TestData
{
    private static readonly Lazy<(GameEnv Env, DataIndex Index, Catalog Catalog)?> Lazily = new(() =>
    {
        try
        {
            var env = GameEnv.Locate(null);
            var index = DataIndex.Load(env);
            return (env, index, Catalog.Build(index));
        }
        catch (DirectoryNotFoundException)
        {
            return null;   // machine without the game: game-data tests skip
        }
    });

    public static (GameEnv Env, DataIndex Index, Catalog Catalog)? Game => Lazily.Value;

    /// <summary>
    /// The live game data, or a VISIBLE skip when there's no install on this machine (needs a
    /// <c>[SkippableFact]</c>/<c>[SkippableTheory]</c> caller). Use this in place of an
    /// <c>if (TestData.Game is not { } g) return;</c> early-return so a run without the game reports
    /// "skipped" (honest) rather than a false green. Genuinely game-dependent tests (parity corpus, real
    /// prices, sprite rendering) call this; anything exercisable with a synthetic <see cref="Fixtures"/>
    /// catalog should not need the install at all.
    /// </summary>
    public static (GameEnv Env, DataIndex Index, Catalog Catalog) RequireGame()
    {
        Skip.IfNot(Game is not null, "requires a local Ostranauts install");
        return Game!.Value;
    }

    /// <summary>
    /// Load a ship template by file name, for a test that needs a real authored hull rather than a synthetic one.
    ///
    /// <para><b>Name a template the game ships.</b> A test that reaches for a hull from a mod passes only on the
    /// machine that has that mod, and fails on every other with nothing to say why: an unsubscribed mod took
    /// eleven of these down at once, hours after the mod went, and the failure read as a rendering bug. Core's
    /// 220 templates are on every install that has the game at all.</para>
    ///
    /// <para>Skips rather than fails when the template is not there, so a heavily modded install that has
    /// overridden it out reports honestly instead of throwing out of a LINQ lookup.</para>
    /// </summary>
    public static ShipDocument Template(
        (GameEnv Env, DataIndex Index, Catalog Catalog) g, string fileName, out string name)
    {
        var found = TemplateImport.ListShipFiles(g.Index)
            .FirstOrDefault(x => string.Equals(x.Name, fileName, StringComparison.OrdinalIgnoreCase));
        Skip.If(found is null, $"the \"{fileName}\" ship template is not in this install's data");
        name = found!.Name;
        return TemplateImport.LoadFile(found.Path, g.Catalog).Doc;
    }

    /// <inheritdoc cref="Template"/>
    public static ShipDocument Template((GameEnv Env, DataIndex Index, Catalog Catalog) g, string fileName) =>
        Template(g, fileName, out _);
}
