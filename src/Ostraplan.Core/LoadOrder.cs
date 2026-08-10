using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ostraplan.Core;

/// <summary>One data source, in load order: core, a local mod folder, or a Workshop item.</summary>
public sealed record ModSource(string Label, string RootDir, bool IsCore, string Raw)
{
    /// <summary>RootDir holds data\ and images\ (StreamingAssets for core, the mod folder otherwise).</summary>
    public string DataDir => Path.Combine(RootDir, "data");
    public string ImagesDir => Path.Combine(RootDir, "images");
}

/// <summary>
/// One complaint about the loaded game data, attributed to the source that carries it.
///
/// <para><see cref="Core"/> marks a defect in the <b>game's own</b> data. Ostraplan never writes that data and a
/// player cannot fix it, so those are logged and carried into a bug report but never counted in the toolbar's
/// data-warning badge. Putting an unfixable core defect in front of someone only teaches them the badge is noise,
/// which is exactly when the next one, about a mod they can actually do something about, needs reading. Core's
/// stock 1.0.0.7 <c>FloorLDPH04AInstall</c> gap is the case in point: permanent, harmless, and not the user's.</para>
/// </summary>
/// <param name="Source">The source's label, as it appears in the load order ("core", a mod's name).</param>
/// <param name="Message">What is wrong, without the source prefix.</param>
/// <param name="Core">True when <paramref name="Source"/> is the game's own data.</param>
public sealed record DataWarning(string Source, string Message, bool Core)
{
    public override string ToString() => $"{Source}: {Message}";
}

/// <summary>
/// Read-only view of loading_order.json, resolved to on-disk folders exactly as
/// the game does: "core", local folder names (optional "|edit"), absolute paths
/// for Workshop subscriptions. Ostraplan NEVER writes this file - registration
/// belongs to ModTools/Ostrasort.
/// </summary>
public sealed class LoadOrder
{
    public required IReadOnlyList<ModSource> Sources { get; init; }   // core first, then mods in order
    public required string[] IgnorePatterns { get; init; }            // sanitized, from [0].aIgnorePatterns
    public required List<DataWarning> Warnings { get; init; }

    /// <summary>The load order's own problems are the player's setup, never core's — a mod folder that isn't there
    /// is something they can put back — so these are attributed to the file itself and always surface.</summary>
    private const string OrderSource = "loading_order.json";

    public static LoadOrder Read(GameEnv env)
    {
        var warnings = new List<DataWarning>();
        void Warn(string message) => warnings.Add(new DataWarning(OrderSource, message, Core: false));
        var sources = new List<ModSource>();
        string[] patterns = [];

        // no loading_order.json = the game's default core-only load
        if (!File.Exists(env.LoadingOrderPath))
        {
            sources.Add(Core(env));
            return new LoadOrder { Sources = sources, IgnorePatterns = patterns, Warnings = warnings };
        }

        List<string> order = [];
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(env.LoadingOrderPath));
            if (root is JsonArray arr && arr.Count > 0)
            {
                if (arr[0]?["aLoadOrder"] is JsonArray orderArr)
                    order = orderArr.Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList();
                if (arr[0]?["aIgnorePatterns"] is JsonArray patArr)
                    patterns = patArr.Select(n => Sanitize(n?.GetValue<string>() ?? ""))
                                     .Where(s => s.Length > 0).ToArray();
            }
            else
            {
                Warn("not a top-level JSON array - falling back to core only.");
            }
        }
        catch (JsonException e)
        {
            Warn($"unreadable ({e.Message}) - falling back to core only.");
        }

        if (!order.Contains("core")) order.Insert(0, "core");

        foreach (var raw in order)
        {
            if (raw == "core")
            {
                sources.Add(Core(env));
                continue;
            }

            if (raw.Length > 2 && raw[1] == ':')   // absolute path = subscribed Workshop item
            {
                if (!Directory.Exists(raw))
                {
                    Warn($"Workshop entry not on disk, skipped: {raw}");
                    continue;
                }
                var id = Path.GetFileName(raw.TrimEnd('\\', '/'));
                sources.Add(new ModSource($"{DisplayName(raw) ?? "?"} [{id}]", raw, IsCore: false, raw));
                continue;
            }

            var edit = raw.EndsWith("|edit", StringComparison.Ordinal);
            var name = edit ? raw[..^5] : raw;
            var dir = Path.Combine(env.ModsDir, name);
            if (!Directory.Exists(dir))
            {
                Warn($"Local mod folder missing, skipped: {name}");
                continue;
            }
            sources.Add(new ModSource(DisplayName(dir) ?? name, dir, IsCore: false, raw));
        }

        return new LoadOrder { Sources = sources, IgnorePatterns = patterns, Warnings = warnings };
    }

    private static ModSource Core(GameEnv env) =>
        new("core", env.StreamingAssetsDir, IsCore: true, "core");

    private static string? DisplayName(string modDir)
    {
        var path = Path.Combine(modDir, "mod_info.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0
                ? doc.RootElement[0]
                : doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("strName", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()
                : null;
        }
        catch { return null; }
    }

    /// <summary>The game runs its PathSanitize over patterns; forward slashes are the common form.</summary>
    internal static string Sanitize(string s) => s.Replace('\\', '/').Trim();
}
