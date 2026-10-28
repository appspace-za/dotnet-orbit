using MudBlazor;
using Orbit.Application;
using Orbit.Data.Entities;

namespace Orbit.Components.Shared;

/// <summary>
/// How a role reads (spec §6.5): the built-in role red, a role with any grant at All departments amber (company-wide), any
/// other grey.
/// </summary>
public static class RoleLook
{
    public static Color Chip(RoleRef role) =>
        role.IsBuiltIn ? Color.Error : role.ReachesEverywhere ? Color.Warning : Color.Secondary;

    /// <summary>An API key's role, loaded with its permission rows.</summary>
    public static Color Chip(ApplicationRole role) =>
        role.IsBuiltIn ? Color.Error
        : role.Permissions.Any(p => p.Scope == PermissionScope.All) ? Color.Warning
        : Color.Secondary;

    /// <summary>What kind of role it is: "Built-in", "Company-wide" (a grant at All departments) or "Department".</summary>
    public static string Kind(RoleRef role) =>
        role.IsBuiltIn ? "Built-in" : role.ReachesEverywhere ? "Company-wide" : "Department";
}
