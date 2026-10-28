using Orbit.Components.Infrastructure;
using Orbit.Helpers;

namespace Orbit.Tests.Tasks;

/// <summary>
/// A list page's filters are read from the address as text and parsed leniently (spec §13 items 100 and 108): a bad value in a
/// bookmark or a hand-typed address is no filter, never an error in the page.
/// </summary>
public class FilterQueryTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData(" true ", true)]
    [InlineData("false", false)]
    [InlineData("1", false)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_flag_is_on_only_for_true(string? value, bool expected) =>
        Assert.Equal(expected, FilterQuery.Flag(value));

    [Theory]
    [InlineData("abc")]
    [InlineData("none")]
    [InlineData("")]
    [InlineData(null)]
    public void An_id_that_isnt_one_is_no_filter(string? value) =>
        Assert.Null(FilterChoice.Id(value));

    [Fact]
    public void An_id_is_read()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id, FilterChoice.Id(id.ToString()));
    }

    private const string Address = "https://orbit.test/Tasks?PageNumber=3&Status=Todo&Search=printer&ParentTaskId=abc&Other=x";
    private static readonly string[] Keys = ["Search", "Status", "Priority"];

    /// <summary>A list page's next address (decision 112): its filters from the address, in their order, with one changed.</summary>
    [Fact]
    public void The_filters_come_from_the_address_in_order()
    {
        Assert.Equal("Search=printer&Status=Todo", FilterQuery.FromAddress(Address, Keys).ToString());
        Assert.Equal("Search=printer&Status=Done&Priority=High",
            FilterQuery.FromAddress(Address, Keys, "Status", "Done").Add("Priority", "High").ToString());
    }

    [Fact]
    public void Clearing_a_filter_drops_it_and_the_page_number_never_comes_along()
    {
        Assert.Equal("Status=Todo", FilterQuery.FromAddress(Address, Keys, "Search", "").ToString());
        Assert.DoesNotContain("PageNumber", FilterQuery.FromAddress(Address, Keys).ToString());
    }

    [Fact]
    public void A_drill_down_is_carried_along()
    {
        Assert.Equal("Status=Done&ParentTaskId=abc",
            FilterQuery.FromAddress(Address, ["Status"], "Status", "Done").Keep(Address, ["ParentTaskId"]).ToString());
        Assert.Equal("", FilterQuery.FromAddress("https://orbit.test/Tasks", Keys).Keep("https://orbit.test/Tasks", ["ParentTaskId"]).ToString());
    }

    [Fact]
    public void An_address_has_a_query_only_when_there_are_filters()
    {
        Assert.Equal("/Tasks", new FilterQuery().At("/Tasks"));
        Assert.Equal("/Tasks?Status=Todo&OpenOnly=true", new FilterQuery().Add("Status", "Todo").Add("OpenOnly", true).At("/Tasks"));
        Assert.Equal("/Tasks?Search=a%20%26%20b", new FilterQuery().Add("Search", "a & b").At("/Tasks"));
    }

    [Fact]
    public void A_picker_date_goes_both_ways()
    {
        var picked = PickerDate.Parse("2026-10-28");
        Assert.Equal(new DateTime(2026, 10, 28), picked);
        Assert.Equal("2026-10-28", PickerDate.Write(picked));
        Assert.Equal(new DateOnly(2026, 10, 28), PickerDate.Day(picked));
        Assert.Equal(picked, PickerDate.Of(new DateOnly(2026, 10, 28)));
    }

    [Theory]
    [InlineData("28/10/2026")]
    [InlineData("2026-13-01")]
    [InlineData("")]
    [InlineData(null)]
    public void A_date_that_isnt_one_is_no_date(string? text)
    {
        Assert.Null(PickerDate.Parse(text));
        Assert.Null(PickerDate.Write(null));
        Assert.Null(PickerDate.Of(null));
    }
}
