using System.Security.Claims;

namespace Orbit.Auth;

/// <summary>
/// Who the current DI scope acts for when it isn't an HTTP request (spec §6.5). In Blazor a DI scope lasts as long as the
/// browser tab's circuit, so each operation runs in a short-lived scope of its own
/// (<see cref="Orbit.Components.Infrastructure.OrbitRunner"/>), which sets <see cref="User"/> from the circuit's authentication
/// state. An HTTP request leaves <see cref="User"/> null, and <see cref="HttpActorProvider"/> reads <c>HttpContext.User</c> as before.
/// </summary>
public sealed class ActorPrincipal
{
    public ClaimsPrincipal? User { get; set; }

    /// <summary>
    /// Set on a circuit's own long-lived scope (<see cref="CircuitScopeMarker"/>). Services must not run there: its one
    /// DbContext would outlive every operation, so a refused change's queued audit row would be saved by the next one. The
    /// context refuses to be created in such a scope (<see cref="CircuitScopeGuard"/>), and the permission handler reads the
    /// actor in a scope of its own there (<see cref="PermissionAuthorizationHandler"/>).
    /// </summary>
    public bool IsCircuit { get; set; }
}
