using System.ComponentModel.DataAnnotations;
using Orbit.Application.Models;
using Orbit.Data.Entities;

namespace Orbit.Components.Pages.Admin.Users;

/// <summary>A user as the Create and Edit pages edit it (spec §6.5, §6.13).</summary>
public sealed class UserForm
{
    [Required, EmailAddress] public string Email { get; set; } = string.Empty;
    [Required, StringLength(200)] public string DisplayName { get; set; } = string.Empty;
    [Required] public Guid RoleId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? ManagerId { get; set; }
    public AuthSource AuthSource { get; set; } = AuthSource.Local;
    [DataType(DataType.Password)] public string? Password { get; set; }

    public UserInput ToInput() => new() { Email = Email, DisplayName = DisplayName, RoleId = RoleId, DepartmentId = DepartmentId, ManagerId = ManagerId, AuthSource = AuthSource, Password = Password };

    public static UserForm From(UserSummary u) => new() { Email = u.Email, DisplayName = u.DisplayName, RoleId = u.Role.Id, DepartmentId = u.DepartmentId, ManagerId = u.ManagerId, AuthSource = u.AuthSource };

    /// <summary>The default role for a new user or key: the shipped Member role when it still exists, else the first editable role.</summary>
    public static Guid DefaultRole(IReadOnlyList<RolePickerItem> roles) =>
        (roles.FirstOrDefault(r => r.Name == Orbit.Application.DefaultRoles.Member) ?? roles.FirstOrDefault(r => !r.IsBuiltIn) ?? roles.FirstOrDefault())?.Id ?? Guid.Empty;
}
