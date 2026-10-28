using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Orbit.Data.Entities;

namespace Orbit.Auth;

/// <summary>
/// Re-checks a Blazor circuit's signed-in user every five minutes (spec §8), as the cookie's security-stamp check does for
/// page requests (Program.cs): a changed security stamp or a deactivated account signs the open tab out. Every operation
/// already re-reads the user's role and grants (<see cref="HttpActorProvider"/>), so this only closes a tab that is
/// left open.
/// <para>
/// A circuit never sees the browser's cookie after it connects, so this can't tell that the tab's browser signed out (which
/// leaves the stamp alone) or signed in as someone else. The browser handles that: every page says who it is signed in as
/// (<c>orbit-session</c> in orbit-interop.js), and a tab opened as anyone else reloads. Still open: a tab left connected past
/// the cookie's expiry while no other page loads.
/// </para>
/// </summary>
public sealed class OrbitRevalidatingAuthStateProvider(
    ILoggerFactory loggerFactory,
    IServiceScopeFactory scopes,
    IOptions<IdentityOptions> options) : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(5);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return await IsStillValidAsync(users, authenticationState.User);
    }

    private async Task<bool> IsStillValidAsync(UserManager<ApplicationUser> users, ClaimsPrincipal principal)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null || !user.IsActive) return false;
        if (!users.SupportsUserSecurityStamp) return true;
        var principalStamp = principal.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType);
        return principalStamp == await users.GetSecurityStampAsync(user);
    }
}
