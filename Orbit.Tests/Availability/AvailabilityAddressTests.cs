using Orbit.Components.Pages.Availability;

namespace Orbit.Tests.Availability;

/// <summary>The person page's address after its add form closes (final review #3).</summary>
public class AvailabilityAddressTests
{
    private static readonly Guid Paul = Guid.Parse("01a119f4-5de0-7255-82ce-fb7d9d16a439");

    /// <summary>
    /// The page drops ?Add from its address only while it is still the address in the browser: Back while the form was open has
    /// already left for the grid, and replacing that entry would bounce the person back to the page they left.
    /// </summary>
    [Theory]
    [InlineData("http://localhost:5016/Availability/01a119f4-5de0-7255-82ce-fb7d9d16a439?Add=2026-11-24", true)]
    [InlineData("http://localhost:5016/availability/01A119F4-5DE0-7255-82CE-FB7D9D16A439?Show=past&Add=2026-11-24", true)]
    [InlineData("http://localhost:5016/Availability/01a119f4-5de0-7255-82ce-fb7d9d16a439", false)]
    [InlineData("http://localhost:5016/Availability/01a119f4-5de0-7255-82ce-fb7d9d16a439?Show=past", false)]
    [InlineData("http://localhost:5016/Availability?View=month", false)]
    [InlineData("http://localhost:5016/Availability/01a119f4-af2c-70eb-8161-bb80ceb91b12?Add=2026-11-24", false)]
    public void The_add_form_is_open_only_on_this_persons_address_with_add(string uri, bool open) =>
        Assert.Equal(open, AvailabilityAddress.IsAddOpen(uri, Paul));
}
