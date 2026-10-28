using Microsoft.AspNetCore.Authorization;

namespace Orbit.Auth;

/// <summary>
/// The page door for a Blazor page (spec §6.5), the twin of a <c>Policies.Permission(...)</c> convention in Program.cs:
/// <c>@attribute [RequirePermission(Permission.TasksCreate)]</c>. It opens on the permission at any scope; the services
/// behind the page apply the scope.
/// </summary>
public sealed class RequirePermissionAttribute : AuthorizeAttribute
{
    public RequirePermissionAttribute(string permission) => Policy = Policies.Permission(permission);
}
