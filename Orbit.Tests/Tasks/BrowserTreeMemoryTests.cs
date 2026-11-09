using Orbit.Components.Infrastructure;

namespace Orbit.Tests.Tasks;

/// <summary>
/// The collapsed parents the tree view remembers in the browser (spec §6.15, decision 145): each id once, the newest kept up to
/// the limit, and expanding removes only what it names.
/// </summary>
public class BrowserTreeMemoryTests
{
    private static Guid G() => Guid.NewGuid();

    /// <summary>TREE-020: collapsing adds each id once, the most recent last.</summary>
    [Fact]
    public void Collapsing_adds_each_id_once_most_recent_last()
    {
        Guid a = G(), b = G(), c = G();
        Assert.Equal([b, a], BrowserTreeMemory.Next([a, b], [a], collapsed: true));
        Assert.Equal([a, b, c], BrowserTreeMemory.Next([a, b], [c], collapsed: true));
    }

    /// <summary>TREE-020: expanding removes only the ids given, so Expand all on one page leaves another page's parents collapsed.</summary>
    [Fact]
    public void Expanding_removes_only_the_ids_given()
    {
        Guid a = G(), b = G(), c = G();
        Assert.Equal([a, c], BrowserTreeMemory.Next([a, b, c], [b], collapsed: false));
    }

    /// <summary>TREE-020: the list keeps the newest ids up to the limit.</summary>
    [Fact]
    public void The_list_keeps_the_newest_ids_up_to_the_limit()
    {
        var ids = Enumerable.Range(0, 5).Select(_ => G()).ToArray();
        Assert.Equal(ids[2..], BrowserTreeMemory.Next(ids[..4], [ids[4]], collapsed: true, limit: 3));
    }
}
