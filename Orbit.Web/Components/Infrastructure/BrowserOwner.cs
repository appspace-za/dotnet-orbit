using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Orbit.Components.Infrastructure;

/// <summary>
/// Whose browser memory it is: the signed-in user's id, or "-" when nobody is. The filter memory (<see cref="BrowserFilterMemory"/>)
/// and the tree view's collapsed parents (<see cref="BrowserTreeMemory"/>) are kept per user, so a colleague signing in on the same
/// browser doesn't inherit them.
/// </summary>
internal static class BrowserOwner
{
    public static async Task<string> IdAsync(AuthenticationStateProvider authentication)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id.ToString("N") : "-";
    }
}
