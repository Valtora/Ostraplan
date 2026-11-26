namespace Ostraplan.Core;

/// <summary>
/// What a user typed into a part search, read as words rather than one string.
///
/// <para>Every word must appear somewhere in the part's names, in any order, so <c>thruster rcs</c> finds the
/// same parts as <c>rcs thruster</c>. A word starting with <c>-</c> must appear in none of them, so
/// <c>rcs -loose</c> is the RCS parts without their loose forms. Matching is a case-insensitive substring, as the
/// plain search always was, so a one-word query behaves exactly as before.</para>
///
/// <para>A lone <c>-</c> is ignored rather than read as "exclude everything", which is what it would mean
/// literally and is never what someone halfway through typing <c>-loose</c> wants to see.</para>
/// </summary>
public sealed class SearchQuery
{
    private readonly string[] _include;
    private readonly string[] _exclude;

    private SearchQuery(string[] include, string[] exclude)
    {
        _include = include;
        _exclude = exclude;
    }

    /// <summary>The query that matches everything: an empty or blank search.</summary>
    public static SearchQuery All { get; } = new([], []);

    /// <summary>True when the query has no words, so every part matches.</summary>
    public bool IsEmpty => _include.Length == 0 && _exclude.Length == 0;

    public static SearchQuery Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return All;
        var include = new List<string>();
        var exclude = new List<string>();
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (word[0] != '-') include.Add(word);
            else if (word.Length > 1) exclude.Add(word[1..]);
        }
        return include.Count == 0 && exclude.Count == 0 ? All : new SearchQuery([.. include], [.. exclude]);
    }

    /// <summary>True when every included word is in at least one of <paramref name="fields"/> and no excluded
    /// word is in any of them. Null fields are skipped.</summary>
    public bool Matches(params string?[] fields)
    {
        foreach (var word in _include)
            if (!fields.Any(f => f is not null && f.Contains(word, StringComparison.OrdinalIgnoreCase)))
                return false;
        foreach (var word in _exclude)
            if (fields.Any(f => f is not null && f.Contains(word, StringComparison.OrdinalIgnoreCase)))
                return false;
        return true;
    }
}
