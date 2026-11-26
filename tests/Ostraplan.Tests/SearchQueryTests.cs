using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The part search's words and exclusions. Asked for on Discord: searching "rcs" also lists every loose RCS form,
/// which is noise when you are building, and "rcs -loose" should drop them.
/// </summary>
public class SearchQueryTests
{
    private const string Thruster = "RCS Thruster";
    private const string ThrusterDef = "ItmRCS01";
    private const string Loose = "RCS Thruster (Loose)";
    private const string LooseDef = "ItmRCS01Loose";

    [Fact]
    public void An_empty_search_matches_everything()
    {
        Assert.True(SearchQuery.Parse("").IsEmpty);
        Assert.True(SearchQuery.Parse("   ").IsEmpty);
        Assert.True(SearchQuery.Parse(null).Matches(Thruster, ThrusterDef));
    }

    [Fact]
    public void One_word_matches_as_a_substring_of_either_name_like_before()
    {
        var q = SearchQuery.Parse("rcs");
        Assert.True(q.Matches(Thruster, ThrusterDef));
        Assert.True(q.Matches(Loose, LooseDef));
        Assert.True(SearchQuery.Parse("Itmrcs01").Matches(Thruster, ThrusterDef));   // the internal name counts too
        Assert.False(SearchQuery.Parse("pump").Matches(Thruster, ThrusterDef));
    }

    [Fact]
    public void Words_match_in_any_order_and_all_of_them_must()
    {
        Assert.True(SearchQuery.Parse("thruster rcs").Matches(Thruster, ThrusterDef));
        Assert.False(SearchQuery.Parse("rcs pump").Matches(Thruster, ThrusterDef));
    }

    [Fact]
    public void A_minus_word_leaves_out_parts_that_name_it()
    {
        var q = SearchQuery.Parse("rcs -loose");
        Assert.True(q.Matches(Thruster, ThrusterDef));
        Assert.False(q.Matches(Loose, LooseDef));
    }

    [Fact]
    public void An_exclusion_checks_the_internal_name_as_well()
    {
        // "(Damaged)" on screen, "Dmg" underneath: either name is enough to leave the part out.
        Assert.False(SearchQuery.Parse("-dmg").Matches("Heater (Damaged)", "ItmHeater01Dmg"));
    }

    [Fact]
    public void Exclusions_alone_still_filter()
    {
        var q = SearchQuery.Parse("-loose");
        Assert.False(q.IsEmpty);
        Assert.True(q.Matches(Thruster, ThrusterDef));
        Assert.False(q.Matches(Loose, LooseDef));
    }

    [Fact]
    public void A_lone_minus_is_ignored_while_the_word_is_still_being_typed()
    {
        Assert.True(SearchQuery.Parse("rcs -").Matches(Loose, LooseDef));
        Assert.True(SearchQuery.Parse("-").IsEmpty);
    }
}
