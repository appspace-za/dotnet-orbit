using Microsoft.AspNetCore.Components;
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

    private static readonly string[] PlanningFilters = ["Search", "DepartmentId", "AssigneeId"];

    /// <summary>
    /// Decision 141: a list page opened with none of its filters goes to its remembered filter, keeping the keys it carries without
    /// remembering them (the Planning page's sprint, from a sprint page's link). It stays when nothing is remembered.
    /// </summary>
    [Fact]
    public void The_remembered_filter_keeps_the_carried_keys()
    {
        var fromSprint = new Uri("https://orbit.test/Sprints/Planning?Sprint=abc");
        Assert.Equal("/Sprints/Planning?Search=printer&DepartmentId=it&Sprint=abc",
            BrowserFilterMemory.RememberedAddress(fromSprint, "Search=printer&DepartmentId=it", PlanningFilters, ["Sprint"]));
        Assert.Equal("/Sprints/Planning?Search=printer",
            BrowserFilterMemory.RememberedAddress(new Uri("https://orbit.test/Sprints/Planning"), "Search=printer", PlanningFilters, ["Sprint"]));
        Assert.Null(BrowserFilterMemory.RememberedAddress(fromSprint, null, PlanningFilters, ["Sprint"]));
    }

    /// <summary>
    /// Decision 141: only the page's filters are taken from the memory. The Planning page remembered its sprint before it stopped
    /// being a filter; going to that address, which names no filter, would come back here again and again, adding the sprint each
    /// time. A memory with none of the page's filters left keeps the page where it is.
    /// </summary>
    [Fact]
    public void A_key_that_is_no_longer_a_filter_is_left_behind()
    {
        var plain = new Uri("https://orbit.test/Sprints/Planning");
        Assert.Null(BrowserFilterMemory.RememberedAddress(plain, "Sprint=abc", PlanningFilters, ["Sprint"]));
        Assert.Equal("/Sprints/Planning?Search=printer",
            BrowserFilterMemory.RememberedAddress(plain, "Sprint=abc&Search=printer", PlanningFilters, ["Sprint"]));
        Assert.Equal("/Sprints/Planning?Search=printer&Sprint=new",
            BrowserFilterMemory.RememberedAddress(new Uri("https://orbit.test/Sprints/Planning?Sprint=new"), "Sprint=old&Search=printer",
                PlanningFilters, ["Sprint"]));
    }

    /// <summary>
    /// Decision 141: the router hands a page a new route, checking its doors again, only when the page or its route values change;
    /// a change of the query string alone (a list page's filters) is the same page.
    /// </summary>
    [Fact]
    public void Only_a_new_page_or_route_value_counts_as_another_page()
    {
        var id = Guid.NewGuid();
        RouteData Route(Type page, params (string Key, object? Value)[] values) => new(page, values.ToDictionary(v => v.Key, v => v.Value));

        Assert.True(OrbitRouteView.SamePage(Route(typeof(Orbit.Components.Pages.Tasks.TasksList)), Route(typeof(Orbit.Components.Pages.Tasks.TasksList))));
        Assert.True(OrbitRouteView.SamePage(Route(typeof(Orbit.Components.Pages.Tasks.TaskDetails), ("Id", id)),
            Route(typeof(Orbit.Components.Pages.Tasks.TaskDetails), ("Id", id))));
        Assert.False(OrbitRouteView.SamePage(Route(typeof(Orbit.Components.Pages.Tasks.TaskDetails), ("Id", id)),
            Route(typeof(Orbit.Components.Pages.Tasks.TaskDetails), ("Id", Guid.NewGuid()))));
        Assert.False(OrbitRouteView.SamePage(Route(typeof(Orbit.Components.Pages.Tasks.TasksList)), Route(typeof(Orbit.Components.Pages.Tasks.TaskDetails), ("Id", id))));
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

    private static readonly DateOnly Today = new(2026, 11, 1);

    /// <summary>
    /// Decision 143: My Time's period, read from the address before the page loads. With no dates it is the last seven days, To
    /// defaults to today and From counts back from To.
    /// </summary>
    [Fact]
    public void A_period_defaults_to_the_days_before_to()
    {
        Assert.Equal((new DateOnly(2026, 10, 26), Today), FilterQuery.Period(null, null, Today, 6));
        Assert.Equal((new DateOnly(2026, 10, 4), new DateOnly(2026, 10, 10)), FilterQuery.Period(null, new DateOnly(2026, 10, 10), Today, 6));
        Assert.Equal((new DateOnly(2026, 10, 1), Today), FilterQuery.Period(new DateOnly(2026, 10, 1), null, Today, 6));
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)),
            FilterQuery.Period(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), Today, 6));
    }

    /// <summary>Decision 143: a From after To is put in order, as the service and the reports always did.</summary>
    [Fact]
    public void A_period_the_wrong_way_round_is_swapped()
    {
        Assert.Equal((new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 20)),
            FilterQuery.Period(new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 10), Today, 6));
        Assert.Equal((Today, new DateOnly(2026, 11, 5)), FilterQuery.Period(new DateOnly(2026, 11, 5), null, Today, 6));
    }

    /// <summary>Decision 143: a report's period is the last 31 days by default, and the page and its PDF read it the same way.</summary>
    [Fact]
    public void A_report_period_defaults_to_the_last_31_days()
    {
        var address = Orbit.Components.Pages.Reports.ReportAddress.Parse(null, null, null, null, null, null, null);
        Assert.Equal((new DateOnly(2026, 10, 2), Today), address.Period(Today));
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)),
            Orbit.Components.Pages.Reports.ReportAddress.Parse(null, "2026-10-31", "2026-10-01", null, null, null, null).Period(Today));
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
