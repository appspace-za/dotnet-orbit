using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Orbit.Application;

namespace Orbit.Auth;

/// <summary>A page or folder needs this permission at any scope (spec §6.5); the service applies the scope.</summary>
public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

/// <summary>
/// Answers <see cref="PermissionRequirement"/> from the current actor's grants - read from the database per request by
/// <see cref="IActorProvider"/>, so a role edit or a deactivation bites on the very next page load, not when the cookie refreshes.
/// <para>
/// The scope says where the check runs (<see cref="ActorPrincipal.IsCircuit"/>), whatever its resource is. A Blazor route check
/// (AuthorizeRouteView) runs in the circuit's long-lived scope, where no database context may be made
/// (<see cref="CircuitScopeGuard"/>), so it reads the actor in a scope of its own, on every navigation. Anywhere else (a request,
/// or an <see cref="Orbit.Components.Infrastructure.OrbitRunner"/> operation) the check shares that scope's actor. The actor
/// provider is resolved only then, never in the constructor, since building it needs the context.
/// </para>
/// </summary>
public sealed class PermissionAuthorizationHandler(ActorPrincipal current, IServiceProvider services, IServiceScopeFactory scopes)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true) return;
        Actor actor;
        try
        {
            actor = current.IsCircuit
                ? await InOwnScopeAsync(context.User)
                : await services.GetRequiredService<IActorProvider>().GetAsync();
        }
        catch (ForbiddenException)
        {
            return; // deactivated or unknown: fail closed
        }
        if (actor.Has(requirement.Permission)) context.Succeed(requirement);
    }

    private async Task<Actor> InOwnScopeAsync(ClaimsPrincipal user)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ActorPrincipal>().User = user;
        return await scope.ServiceProvider.GetRequiredService<IActorProvider>().GetAsync();
    }
}
