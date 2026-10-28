using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Orbit.Auth;

/// <summary>Marks a Blazor circuit's own DI scope as soon as the circuit opens (<see cref="ActorPrincipal.IsCircuit"/>).</summary>
public sealed class CircuitScopeMarker(ActorPrincipal principal) : CircuitHandler
{
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        principal.IsCircuit = true;
        return Task.CompletedTask;
    }
}
