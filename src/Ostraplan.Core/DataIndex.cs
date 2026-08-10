using System.IO;
using System.Text;
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
        ["items", "condowners", "installables", "cooverlays", "loot", "condtrigs", "rooms", "guipropmaps", "tickers", "slots", "powerinfos", "lights", "colors", "parallax", "interactions"];

    public required GameEnv Env { get; init; }
    public required IReadOnlyList<ModSource> Sources { get; init; }
    public required List<DataWarning> Warnings { get; init; }

    /// <summary>Is this source label the game's own data? Looked up rather than compared against "core", so a mod
    /// that happens to call itself that cannot pass its own defects off as unfixable.</summary>
    public bool IsCoreSource(string label) =>
        Sources.FirstOrDefault(s => s.Label == label)?.IsCore ?? false;

    /// <summary>Files that only loaded after <see cref="EscapeControlCharsInStrings"/> mended them. Deliberately
    /// <b>not</b> warnings: nothing is lost, nothing is actionable, and counting them buried the one warning that
    /// does need a person. Carried into the bug-report diagnostics so a support case can still see it happened.</summary>
    public List<string> Repaired { get; } = [];

    private static readonly JsonDocumentOptions Lenient = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

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

                if (Parse(File.ReadAllText(file), source, rel) is not { } doc) continue;

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

        LoadImages(source);
    }

    /// <summary>
    /// Read one data file the way the game's own loader effectively does, in three steps, each a fallback from the
    /// last. Returns null (with a warning recorded) when the file cannot be read at all.
    ///
    /// <list type="number">
    /// <item>Strict, per the JSON spec.</item>
    /// <item>Lenient (trailing commas, comments), which is a real complaint: those are authoring slips the game's
    /// own load would ERROR on, so they stay warnings.</item>
    /// <item>Mended, for raw control characters inside string literals. Core game data does this routinely: a
    /// <c>strDesc</c> in <c>data/interactions</c> is written with real line breaks inside the quotes, which
    /// <see cref="JsonDocument"/> rejects and the game's own parser accepts. Eight core files were dropped over it
    /// on a stock 1.0.0.7 install, taking twelve interactions that station kiosks, an express transit door and a
    /// plot crate actually reference — so the Walk overlay read those fittings as having no actions at all.</item>
    /// </list>
    ///
    /// <para>The mend is safe because it is validated, not assumed: the repaired text still has to parse, and a
    /// repair that produces nonsense simply falls through to the warning. It also cannot change meaning, since the
    /// only edit is replacing a control character with its own escape sequence, which denotes the same character.
    /// The result is what a tolerant parser would have produced from the file as written.</para>
    /// </summary>
    private JsonDocument? Parse(string text, ModSource source, string rel)
    {
        try { return JsonDocument.Parse(text); }
        catch (JsonException) { /* fall through */ }

        JsonException lenientFailure;
        try
        {
            var doc = JsonDocument.Parse(text, Lenient);
            Warnings.Add(new DataWarning(source.Label, $"{rel} parses only leniently - the game load would ERROR.", source.IsCore));
            return doc;
        }
        catch (JsonException e) { lenientFailure = e; }

        if (EscapeControlCharsInStrings(text) is { } mended)
        {
            try
            {
                var doc = JsonDocument.Parse(mended, Lenient);
                Repaired.Add($"{source.Label}: {rel} (raw control characters inside strings)");
                return doc;
            }
            catch (JsonException) { /* not the problem, or not the only one */ }
        }

        Warnings.Add(new DataWarning(source.Label, $"{rel} invalid JSON - {lenientFailure.Message}", source.IsCore));
        return null;
    }

    /// <summary>
    /// Replace every raw control character that appears <b>inside a JSON string literal</b> with its escape
    /// sequence, leaving everything outside a string (the file's own newlines and indentation) untouched. Returns
    /// null when there was nothing to mend, so an unaffected file is never rewritten or re-parsed.
    /// </summary>
    /// <remarks>Tracks the string state itself rather than using a regex, because whether a quote opens or closes a
    /// string depends on the backslash before it, which a regex cannot see.</remarks>
    public static string? EscapeControlCharsInStrings(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        var inString = false;
        var afterBackslash = false;
        var mended = false;

        foreach (var ch in text)
        {
            if (!inString)
            {
                if (ch == '"') inString = true;
                sb.Append(ch);
                continue;
            }
            if (afterBackslash) { sb.Append(ch); afterBackslash = false; continue; }   // escaped: \" is not a close
            if (ch == '\\') { sb.Append(ch); afterBackslash = true; continue; }
            if (ch == '"') { sb.Append(ch); inString = false; continue; }
            if (ch >= ' ') { sb.Append(ch); continue; }

            mended = true;
            sb.Append(ch switch
            {
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\b' => "\\b",
                '\f' => "\\f",
                _ => $"\\u{(int)ch:x4}",
            });
        }

        return mended ? sb.ToString() : null;
    }

    private void LoadImages(ModSource source)
    {
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
