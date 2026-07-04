using System.IO;
using System.Text.Json;

namespace Ostraplan.Core;

/// <summary>
/// The effective game data as the game itself would see it: every source in
/// loading_order.json applied in order, later (type, strName) replacing earlier
/// whole-object, and images resolved so that the latest-loaded mod wins (the
/// game prepends each mod to its image search list - DataHandler.LoadMod).
/// </summary>
public sealed class DataIndex
{
    // only the folders Ostraplan consumes today; extend as later phases need more
    private static readonly string[] WantedTypes =
        ["items", "condowners", "installables", "cooverlays", "loot", "condtrigs", "rooms"];

    public required GameEnv Env { get; init; }
    public required IReadOnlyList<ModSource> Sources { get; init; }
    public required List<string> Warnings { get; init; }

    private readonly Dictionary<string, Dictionary<string, (JsonElement El, string Origin)>> _byType = new();
    private readonly Dictionary<string, string> _images = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, (JsonElement El, string Origin)> Type(string type) =>
        _byType.TryGetValue(type, out var d) ? d : new Dictionary<string, (JsonElement, string)>();

    /// <summary>Absolute path for an item's strImg value ("tiles/ItmWallSheet"), or null if no PNG exists.</summary>
    public string? ResolveImage(string? strImg)
    {
        if (string.IsNullOrWhiteSpace(strImg)) return null;
        var rel = strImg.Replace('\\', '/') + ".png";
        return _images.TryGetValue(rel, out var abs) ? abs : null;
    }

    public static DataIndex Load(GameEnv env)
    {
        var order = LoadOrder.Read(env);
        var index = new DataIndex { Env = env, Sources = order.Sources, Warnings = order.Warnings };
        foreach (var source in order.Sources)
            index.LoadSource(source, order.IgnorePatterns);
        return index;
    }

    private void LoadSource(ModSource source, string[] ignorePatterns)
    {
        foreach (var type in WantedTypes)
        {
            var typeDir = Path.Combine(source.DataDir, type);
            if (!Directory.Exists(typeDir)) continue;

            var dict = _byType.TryGetValue(type, out var d)
                ? d
                : _byType[type] = new Dictionary<string, (JsonElement, string)>(StringComparer.Ordinal);

            foreach (var file in Directory.EnumerateFiles(typeDir, "*.json", SearchOption.AllDirectories))
            {
                var rel = LoadOrder.Sanitize(Path.GetRelativePath(source.DataDir, file));
                if (ignorePatterns.Any(p => rel.Contains(p, StringComparison.Ordinal)))
                    continue;   // same skip the game applies via aIgnorePatterns

                JsonDocument doc;
                var text = File.ReadAllText(file);
                try
                {
                    doc = JsonDocument.Parse(text);   // strict, like the game
                }
                catch (JsonException)
                {
                    try
                    {
                        doc = JsonDocument.Parse(text, new JsonDocumentOptions
                        { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                        Warnings.Add($"{source.Label}: {rel} parses only leniently - the game load would ERROR.");
                    }
                    catch (JsonException e)
                    {
                        Warnings.Add($"{source.Label}: {rel} invalid JSON - {e.Message}");
                        continue;
                    }
                }

                using (doc)
                {
                    var objects = doc.RootElement.ValueKind == JsonValueKind.Array
                        ? doc.RootElement.EnumerateArray().ToArray()
                        : [doc.RootElement];
                    foreach (var obj in objects)
                    {
                        if (obj.ValueKind != JsonValueKind.Object) continue;
                        if (!obj.TryGetProperty("strName", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                            continue;
                        dict[nameEl.GetString()!] = (obj.Clone(), source.Label);   // later source wins
                    }
                }
            }
        }

        if (Directory.Exists(source.ImagesDir))
        {
            foreach (var file in Directory.EnumerateFiles(source.ImagesDir, "*.png", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(source.ImagesDir, file).Replace('\\', '/');
                _images[rel] = file;   // later source wins, matching the game's search order
            }
        }
    }
}
