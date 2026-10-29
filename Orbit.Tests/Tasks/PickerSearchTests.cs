using Orbit.Application;

namespace Orbit.Tests.Tasks;

/// <summary>Searchable choice lists: how the options narrow as you type (spec §6.2, §13 item 121).</summary>
public class PickerSearchTests
{
    private sealed record Option(string Text, string? Detail = null);

    private static readonly Option[] Projects =
    [
        new("(standalone task)"),
        new("IT / Server refresh", "P-26-0003"),
        new("Facilities / Office move", "P-26-0004"),
        new("IT / Laptop rollout", "P-26-0007"),
        new("IT / Server room cooling", "P-26-0011"),
    ];

    private static IReadOnlyList<string> Find(string? query, int pinned = 0) =>
        PickerSearch.Filter(Projects, query, o => o.Text, o => o.Detail, pinned).Select(o => o.Text).ToList();

    /// <summary>PCK-001: a missing, empty or blank search returns every option, in its order.</summary>
    [Fact]
    public void Blank_search_returns_everything_in_order()
    {
        var everything = Projects.Select(o => o.Text).ToList();
        Assert.Equal(everything, Find(null));
        Assert.Equal(everything, Find(""));
        Assert.Equal(everything, Find("  \t "));
    }

    /// <summary>PCK-002: matching ignores case.</summary>
    [Fact]
    public void Matching_ignores_case()
    {
        Assert.Equal(["IT / Laptop rollout"], Find("LAPTOP"));
        Assert.Equal(["IT / Laptop rollout"], Find("laptop"));
    }

    /// <summary>PCK-003: the detail line matches too - a project or task number, an email, a department.</summary>
    [Fact]
    public void Detail_line_matches()
    {
        Assert.Equal(["Facilities / Office move"], Find("P-26-0004"));
        Assert.Equal(["IT / Server room cooling"], Find("0011"));
    }

    /// <summary>PCK-004: every word must match, in any order, in the text or the detail.</summary>
    [Fact]
    public void Every_word_must_match()
    {
        Assert.Equal(["IT / Server refresh", "IT / Server room cooling"], Find("it serv"));
        Assert.Equal(["IT / Server room cooling"], Find("cooling server"));
        Assert.Equal(["IT / Server refresh"], Find("server 0003"));
        Assert.Empty(Find("server office"));
    }

    /// <summary>PCK-005: options whose text starts with the search come first; each group keeps its order.</summary>
    [Fact]
    public void Prefix_matches_come_first()
    {
        Option[] people = [new("Anna Server"), new("Bob Smith"), new("Sam Brown"), new("Samantha Jones")];
        var found = PickerSearch.Filter(people, "sam", o => o.Text, o => o.Detail).Select(o => o.Text).ToList();
        Assert.Equal(["Sam Brown", "Samantha Jones"], found);

        found = PickerSearch.Filter(people, "s", o => o.Text, o => o.Detail).Select(o => o.Text).ToList();
        Assert.Equal(["Sam Brown", "Samantha Jones", "Anna Server", "Bob Smith"], found);
    }

    /// <summary>PCK-006: nothing matching gives an empty list.</summary>
    [Fact]
    public void No_match_is_empty()
    {
        Assert.Empty(Find("zebra"));
    }

    /// <summary>PCK-007: an option without a detail line, or a list without a detail at all, is fine.</summary>
    [Fact]
    public void Missing_detail_is_safe()
    {
        Assert.Equal(["(standalone task)"], Find("standalone"));
        var noDetail = PickerSearch.Filter(Projects, "office", o => o.Text).Select(o => o.Text).ToList();
        Assert.Equal(["Facilities / Office move"], noDetail);
        Assert.Empty(PickerSearch.Filter(Projects, "0004", o => o.Text));
    }

    /// <summary>PCK-008: a search splits into words on any whitespace.</summary>
    [Fact]
    public void Words_split_on_any_whitespace()
    {
        Assert.Equal(["it", "server", "room"], PickerSearch.Words("  it\tserver \n room "));
        Assert.Empty(PickerSearch.Words(null));
        Assert.Empty(PickerSearch.Words("   "));
    }

    /// <summary>PCK-009: the pinned leading options ("All", "None", "(standalone task)") stay first whatever is typed; the rest are filtered.</summary>
    [Fact]
    public void Pinned_options_stay_first()
    {
        Assert.Equal(["(standalone task)", "IT / Laptop rollout"], Find("laptop", pinned: 1));
        Assert.Equal(["(standalone task)"], Find("zebra", pinned: 1));
        Assert.Equal(Projects.Select(o => o.Text), Find(null, pinned: 1));
        // More pinned than there are options is just all of them.
        Assert.Equal(Projects.Select(o => o.Text), Find("zebra", pinned: 99));
    }

    /// <summary>
    /// PCK-010: a server search matches the typed text literally (§13 item 122). Plain text is wrapped for ILIKE; the
    /// wildcards and the escape character are escaped with the backslash every ILike call names, so a trailing backslash
    /// can't escape the closing %.
    /// </summary>
    [Fact]
    public void Like_patterns_match_the_text_literally()
    {
        Assert.Equal(@"\", SqlLike.EscapeCharacter);
        Assert.Equal("%server%", SqlLike.Contains("server"));
        Assert.Equal("T-26%", SqlLike.StartsWith("T-26"));
        Assert.Equal(@"%100\%%", SqlLike.Contains("100%"));
        Assert.Equal(@"%a\_b%", SqlLike.Contains("a_b"));
        Assert.Equal(@"%\_%", SqlLike.Contains("_"));
        Assert.Equal(@"%dir\\%", SqlLike.Contains(@"dir\"));
        Assert.Equal(@"C:\\temp\%%", SqlLike.StartsWith(@"C:\temp%"));
        Assert.Equal("%%", SqlLike.Contains(""));
    }
}
