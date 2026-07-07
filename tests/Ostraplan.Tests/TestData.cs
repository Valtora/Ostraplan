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
}
