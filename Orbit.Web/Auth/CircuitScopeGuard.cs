using Microsoft.EntityFrameworkCore;
using Orbit.Data;

namespace Orbit.Auth;

/// <summary>
/// Registers Orbit's <see cref="ApplicationDbContext"/> so that it can't be created in a Blazor circuit's own scope (spec §6,
/// decision 112). That scope lasts as long as the browser tab, so a context made there would be shared by every change the tab
/// makes, and a refused change's queued audit row would be saved by the next one. Every service reaches the database through
/// the context, so guarding its creation stops a directly injected service, whatever it does, rather than only one that reads
/// the actor. Components call services through <see cref="Orbit.Components.Infrastructure.OrbitRunner"/> instead.
/// </summary>
public static class CircuitScopeGuard
{
    public const string Refusal =
        "The database was asked for in a Blazor circuit's own scope. Call services through OrbitRunner, which gives each operation its own scope.";

    public static IServiceCollection AddOrbitDbContext(this IServiceCollection services, string connectionString) =>
        // The options are scoped (AddDbContext's default), so this runs in each scope that creates a context.
        services.AddDbContext<ApplicationDbContext>((provider, options) =>
        {
            if (provider.GetRequiredService<ActorPrincipal>().IsCircuit) throw new InvalidOperationException(Refusal);
            options.UseNpgsql(connectionString);
        });
}
