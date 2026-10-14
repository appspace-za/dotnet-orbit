using System.Text.Encodings.Web;
using Orbit.Application;
using Orbit.Helpers;

namespace Orbit.Tests.Tasks;

/// <summary>Web addresses in descriptions and comments shown as links (spec §6.2).</summary>
public class LinkRulesTests
{
    /// <summary>The addresses found in <paramref name="text"/>, after checking the runs join back into exactly what was typed.</summary>
    private static string[] Links(string text)
    {
        var segments = LinkRules.Split(text);
        Assert.Equal(text, string.Concat(segments.Select(s => s.Text)));
        return segments.Where(s => s.IsLink).Select(s => s.Text).ToArray();
    }

    /// <summary>LNK-001: an address in a sentence is a link, and the text either side of it is kept as it was.</summary>
    [Fact]
    public void An_address_in_a_sentence_is_a_link()
    {
        var segments = LinkRules.Split("See https://example.com/a?b=1&c=2 for details");

        Assert.Equal(
            [new TextSegment("See ", false), new TextSegment("https://example.com/a?b=1&c=2", true), new TextSegment(" for details", false)],
            segments);
    }

    /// <summary>LNK-002: trailing punctuation and a closing bracket the address didn't open are left out; one it opened is kept.</summary>
    [Fact]
    public void Trailing_punctuation_and_unmatched_brackets_are_left_out()
    {
        Assert.Equal(["https://example.com/a"], Links("See https://example.com/a."));
        Assert.Equal(["https://example.com/a"], Links("(see https://example.com/a)"));
        Assert.Equal(["https://example.com/?q=1"], Links("Is it https://example.com/?q=1?"));
        Assert.Equal(["https://example.com"], Links("'https://example.com',"));
        Assert.Equal(["https://example.com"], Links("**https://example.com**"));
        Assert.Equal(["https://example.com/docs"], Links("[the docs](https://example.com/docs)"));
        Assert.Equal(["https://en.wikipedia.org/wiki/Foo_(bar)"], Links("https://en.wikipedia.org/wiki/Foo_(bar)"));
        Assert.Equal(["https://en.wikipedia.org/wiki/Foo_(bar)"], Links("(https://en.wikipedia.org/wiki/Foo_(bar))."));
    }

    /// <summary>LNK-003: only http and https addresses are links - not www. on its own, another scheme, or a scheme with no host.</summary>
    [Fact]
    public void Only_http_and_https_addresses_are_links()
    {
        Assert.Equal(["http://example.com"], Links("http://example.com"));
        Assert.Equal(["HTTPS://EXAMPLE.COM/A"], Links("HTTPS://EXAMPLE.COM/A"));
        Assert.Empty(Links("www.example.com"));
        Assert.Empty(Links("ftp://example.com/file"));
        Assert.Empty(Links("javascript:alert(1)"));
        Assert.Empty(Links("mailto:someone@example.com"));
        Assert.Empty(Links("https://"));
        Assert.Empty(Links("https:// example.com"));
        Assert.Empty(Links("xhttps://example.com"));
    }

    /// <summary>LNK-004: several addresses are each a link, at the start and end of the text and straight after a line break.</summary>
    [Fact]
    public void Finds_every_address()
    {
        Assert.Equal(
            ["https://a.example", "http://b.example/x", "https://c.example"],
            Links("https://a.example and http://b.example/x\nhttps://c.example"));
        Assert.Equal(["https://a.example/?next=https://b.example"], Links("https://a.example/?next=https://b.example"));
    }

    /// <summary>LNK-005: text with no address is one plain run, and empty or missing text is none.</summary>
    [Fact]
    public void Text_without_addresses_is_left_plain()
    {
        Assert.Equal([new TextSegment("No links here.\nNone at all.", false)], LinkRules.Split("No links here.\nNone at all."));
        Assert.Empty(LinkRules.Split(""));
        Assert.Empty(LinkRules.Split(null));
    }

    /// <summary>LNK-006: angle brackets and double quotes end an address, as when one is wrapped in them or in markup.</summary>
    [Fact]
    public void Angle_brackets_and_quotes_end_an_address()
    {
        Assert.Equal(["https://example.com"], Links("<https://example.com>"));
        Assert.Equal(["https://example.com"], Links("\"https://example.com\""));
        Assert.Equal(["https://example.com"], Links("<a href=\"https://example.com\">x</a>"));
        Assert.Equal(["https://example.com"], Links("https://example.com<script>"));
    }

    /// <summary>LNK-007: the page gets the link opening in a new tab, with the address and the text around it encoded.</summary>
    [Fact]
    public void Links_open_in_a_new_tab_and_everything_is_encoded()
    {
        using var html = new StringWriter();
        Ui.Linkify("<b>See</b> https://example.com/a?b=1&c=2.").WriteTo(html, HtmlEncoder.Default);

        Assert.Equal(
            "&lt;b&gt;See&lt;/b&gt; <a href=\"https://example.com/a?b=1&amp;c=2\" rel=\"noopener noreferrer\" target=\"_blank\">https://example.com/a?b=1&amp;c=2</a>.",
            html.ToString());
    }
}
