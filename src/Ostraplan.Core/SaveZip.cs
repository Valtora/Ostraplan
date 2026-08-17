namespace Ostraplan.Core;

/// <summary>
/// How a RegID becomes a file name inside a save's zip, and back.
///
/// <para>A save is written as loose files and only then zipped from disk
/// (<c>DotNetZipCompressor.CompressFolder</c>), so every write goes through the game's
/// <c>DataHandler.ReplaceInvalidCharacters</c>, which substitutes the two characters Windows will not accept in
/// a file name: <c>|</c> becomes <c>%</c> and <c>*</c> becomes <c>§</c>. An apartment with RegID
/// <c>BCRS|RES_1</c> is therefore the entry <c>ships/BCRS%RES_1.json</c>, and a stock save shows
/// <c>ships/VORB%Aux.json</c> for the sub-station <c>VORB|Aux</c>.</para>
///
/// <para>The game never decodes: <c>CrewSim.DoLoadGame</c> enumerates the folder and takes each ship's identity
/// from the <c>strRegID</c> in the JSON body, so on the read side the file name is decoration. It is not
/// decoration on the write side. Addressing an entry as <c>ships/&lt;RegID&gt;.json</c> with a pipe in the RegID
/// misses the real entry, which fails loudly on a splice but writes a <b>second</b> record for one RegID on any
/// create-if-absent path. See GAME-INTERNALS §17.</para>
///
/// <para>Ordinary ship RegIDs (<c>J-P3HF</c>) contain neither character, so every mapping here is the identity
/// for them and the helpers are safe to use unconditionally.</para>
/// </summary>
public static class SaveZip
{
    /// <summary>The game's substitution table, in encode order (RegID character → file-name character).</summary>
    private static readonly (char RegId, char File)[] Substitutions = [('|', '%'), ('*', '§')];

    /// <summary>The <c>ships/</c> entry name a RegID is stored under, with the game's character substitutions
    /// applied. Use this anywhere an entry is opened, created or replaced by RegID.</summary>
    public static string ShipEntry(string regId) => $"ships/{EncodeName(regId)}.json";

    /// <summary>A RegID as the game would name its file. Identity for a RegID with no substituted character.</summary>
    public static string EncodeName(string regId)
    {
        foreach (var (from, to) in Substitutions) regId = regId.Replace(from, to);
        return regId;
    }

    /// <summary>A file name back to the RegID it stands for — the inverse of <see cref="EncodeName"/>, which the
    /// game itself never performs but which any code deriving a RegID from an entry name has to.</summary>
    public static string DecodeName(string fileName)
    {
        foreach (var (from, to) in Substitutions) fileName = fileName.Replace(to, from);
        return fileName;
    }

    /// <summary>True when <paramref name="regId"/> is a station sub-module: the game's <c>Ship.InitShip</c> sets
    /// <c>HideFromSystem</c> and <c>_subStation</c> on any RegID containing a pipe, and a player-owned one of
    /// those is an apartment (<c>&lt;STATION&gt;|RES_&lt;n&gt;</c>). See GAME-INTERNALS §19.</summary>
    public static bool IsSubStation(string? regId) => regId is not null && regId.Contains('|');

    /// <summary>The station RegID an apartment hangs off — everything before the pipe, which is what
    /// <c>DataHandler.GetTransitConnections</c> truncates to. Null when the RegID is not a sub-module.</summary>
    public static string? StationOf(string? regId)
    {
        if (regId is null) return null;
        var i = regId.IndexOf('|');
        return i <= 0 ? null : regId[..i];
    }
}
