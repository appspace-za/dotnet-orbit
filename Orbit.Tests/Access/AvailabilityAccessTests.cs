using Orbit.Application;
using Orbit.Data.Entities;

namespace Orbit.Tests.Access;

/// <summary>Availability access (spec §6.22): who records unavailable time and who changes a work pattern.</summary>
public class AvailabilityAccessTests
{
    /// <summary>AVL-008: Own reaches yourself, Department your department's people and yourself, All everyone; Own never changes a pattern.</summary>
    [Fact]
    public void Managing_availability_follows_the_scope_and_patterns_need_department()
    {
        var it = Guid.NewGuid();
        var ops = Guid.NewGuid();
        var member = TestActors.Member(it);
        var admin = TestActors.DepartmentAdmin(it);
        var sys = TestActors.SystemAdmin();
        var colleague = Guid.NewGuid();
        var outsider = Guid.NewGuid();

        Assert.True(AccessPolicy.CanManageAvailability(member, member.UserId!.Value, it));
        Assert.False(AccessPolicy.CanManageAvailability(member, colleague, it));
        Assert.False(AccessPolicy.CanChangeWorkPattern(member, member.UserId!.Value, it));

        Assert.True(AccessPolicy.CanManageAvailability(admin, colleague, it));
        Assert.True(AccessPolicy.CanChangeWorkPattern(admin, colleague, it));
        Assert.True(AccessPolicy.CanChangeWorkPattern(admin, admin.UserId!.Value, it));
        Assert.False(AccessPolicy.CanManageAvailability(admin, outsider, ops));
        Assert.False(AccessPolicy.CanManageAvailability(admin, outsider, null));

        Assert.True(AccessPolicy.CanChangeWorkPattern(sys, outsider, null));
        Assert.False(AccessPolicy.CanManageAvailability(TestActors.Nobody(it), member.UserId!.Value, it));
    }

    /// <summary>
    /// AVL-008: the catalogue entry allows Own, Department and All; Member gets Own and Department Admin Department. The role editor
    /// shows it in the Time tracking group, after Log time (decision 152).
    /// </summary>
    [Fact]
    public void The_permission_is_in_the_catalogue_with_its_default_grants()
    {
        var p = PermissionCatalog.ByKey[Permission.AvailabilityManage];
        Assert.Equal([PermissionScope.Own, PermissionScope.Department, PermissionScope.All], p.AllowedScopes);
        Assert.Equal(PermissionCatalog.TimeGroup, p.Group);
        Assert.Equal([Permission.TimeLog, Permission.AvailabilityManage],
            PermissionCatalog.Groups.Single(g => g.Key == PermissionCatalog.TimeGroup).Select(x => x.Key));
        Assert.False(p.SystemAdministratorOnly);
        Assert.Equal(PermissionScope.Own, DefaultRoles.MemberGrants[Permission.AvailabilityManage]);
        Assert.Equal(PermissionScope.Department, DefaultRoles.DepartmentAdminGrants[Permission.AvailabilityManage]);
    }

    /// <summary>AVL-008: the people a Department-scoped manager may pick are themselves and their department's active people.</summary>
    [Fact]
    public void Scoping_people_by_availability_reaches_the_department()
    {
        var it = Guid.NewGuid();
        var admin = TestActors.DepartmentAdmin(it);
        ApplicationUser U(Guid? d, bool active = true, bool system = false) =>
            new() { Id = Guid.NewGuid(), DepartmentId = d, IsActive = active, IsSystemAccount = system };
        var self = new ApplicationUser { Id = admin.UserId!.Value, DepartmentId = it, IsActive = true };
        var mate = U(it);
        var other = U(Guid.NewGuid());
        var gone = U(it, active: false);
        var claude = U(it, system: true);

        var picked = Scoping.People(new[] { self, mate, other, gone, claude }.AsQueryable(), admin, Permission.AvailabilityManage).ToList();

        Assert.Equal([self.Id, mate.Id], picked.Select(u => u.Id));
    }
}
