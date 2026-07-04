using System.IO;
using Ostraplan.Core;

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
            return null;   // machine without the game: game-data tests no-op
        }
    });

    public static (GameEnv Env, DataIndex Index, Catalog Catalog)? Game => Lazily.Value;
}
