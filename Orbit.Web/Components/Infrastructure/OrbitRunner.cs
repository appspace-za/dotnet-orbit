using Microsoft.AspNetCore.Components.Authorization;
using Orbit.Auth;

namespace Orbit.Components.Infrastructure;

/// <summary>
/// Runs one UI operation in a DI scope of its own (spec §6, §6.5). In Blazor a scope otherwise lasts as long as the browser tab,
/// and every service shares that scope's one DbContext. A long-lived context would let a refused change's queued audit row and
/// half-made edits be saved by the next change, and two overlapping events would collide. So each load or change gets a fresh
/// scope, which is the same one-request-one-context behaviour that Razor Pages have. The scope acts for the circuit's signed-in
/// user, read afresh for each operation, so the role and grants are re-read from the database every time.
/// </summary>
public sealed class OrbitRunner(IServiceScopeFactory scopes, AuthenticationStateProvider authentication)
{
    public async Task<T> RunAsync<T>(Func<IServiceProvider, Task<T>> operation)
    {
        var state = await authentication.GetAuthenticationStateAsync();
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ActorPrincipal>().User = state.User;
        return await operation(scope.ServiceProvider);
    }

    public Task RunAsync(Func<IServiceProvider, Task> operation) =>
        RunAsync(async services =>
        {
            await operation(services);
            return true;
        });
}

public static class ServiceProviderExtensions
{
    /// <summary>Shorthand for GetRequiredService inside an <see cref="OrbitRunner"/> operation.</summary>
    public static T Get<T>(this IServiceProvider services) where T : notnull => services.GetRequiredService<T>();
}
