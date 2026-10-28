using Microsoft.Extensions.DependencyInjection;
using Orbit.Auth;
using Orbit.Data;

namespace Orbit.Tests.Access;

/// <summary>
/// The database context can't be made in a Blazor circuit's own long-lived scope, so a service injected straight into a component
/// fails at once, whatever it does (spec §13 item 112). Making a context doesn't connect, so no database is involved.
/// </summary>
public class CircuitScopeGuardTests
{
    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddScoped<ActorPrincipal>();
        services.AddOrbitDbContext("Host=unused;Database=unused");
        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public void A_circuit_scope_gets_no_context()
    {
        using var provider = Services();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ActorPrincipal>().IsCircuit = true;

        var refused = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
        Assert.Equal(CircuitScopeGuard.Refusal, refused.Message);
    }

    [Fact]
    public void Any_other_scope_gets_its_own_context()
    {
        using var provider = Services();
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        var context = first.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Same(context, first.ServiceProvider.GetRequiredService<ApplicationDbContext>());
        Assert.NotSame(context, second.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }
}
