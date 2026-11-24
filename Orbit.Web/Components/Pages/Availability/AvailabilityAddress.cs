using Microsoft.AspNetCore.WebUtilities;

namespace Orbit.Components.Pages.Availability;

/// <summary>The person page's address (spec §6.22): whether its add form, opened from a team grid cell, is still what the browser shows.</summary>
public static class AvailabilityAddress
{
    /// <summary>
    /// True while the browser is on this person's page with <c>?Add=</c>. Back while the form was open has already left (MudBlazor
    /// closes a dialog when the address changes), and dropping Add then would replace the page left for with this one.
    /// </summary>
    public static bool IsAddOpen(string uri, Guid userId) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u)
        && string.Equals(u.AbsolutePath.TrimEnd('/'), $"/Availability/{userId}", StringComparison.OrdinalIgnoreCase)
        && QueryHelpers.ParseQuery(u.Query).Keys.Any(k => string.Equals(k, "Add", StringComparison.OrdinalIgnoreCase));
}
