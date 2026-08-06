using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Users;
using SmartFuture.Domain.Identity;
using SmartFuture.Shared.Constants;
using SmartFuture.Tests.Infrastructure;

namespace SmartFuture.Tests.Jobs;

// The Live enrolment failure, reproduced.
//
// WHAT HAPPENED ON LIVE
//
// Live received the Jobs schema from the idempotent SQL script, not from
// the application. Role rows are NOT created by any migration — they come
// from DbInitializer.SeedRolesAsync, which runs only via
// ApplyDatabaseMigrationsAsync and only after its
// `if (!enabled) return;` guard on Database:ApplyMigrationsOnStartup.
// Live has that flag off, so the tables arrived and the JobSubscriber
// role row never did.
//
// UserManager.AddToRoleAsync THROWS InvalidOperationException when the
// role row is absent. That exception was caught and reported as
// "An unexpected error occurred while enabling job seeker access" —
// verbatim the message Live showed for a cause that was entirely
// predictable.
//
// These tests delete the role row to recreate that exact environment.
public class JobSubscriberMissingRoleTests
{
    private static UserRoleUpgradeService BuildService(IdentityTestHarness harness)
        => new(harness.UserManager, harness.RoleManager, harness.Fixture.AppDbContext,
            Mock.Of<IAuditService>(), Mock.Of<ICurrentUserService>(),
            NullLogger<UserRoleUpgradeService>.Instance);

    private static async Task<User> NewUserAsync(IdentityTestHarness harness, string email)
    {
        var user = new User
        {
            UserName = email,
            Email = email,
            FirstName = "Test",
            LastName = "User",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        (await harness.UserManager.CreateAsync(user, "Str0ngPassw0rd!")).Succeeded.Should().BeTrue();
        return user;
    }

    /// Reproduces Live: the role row simply is not there.
    private static async Task DeleteJobSubscriberRoleAsync(IdentityTestHarness harness)
    {
        var role = await harness.RoleManager.FindByNameAsync(SystemRoles.JobSubscriber);
        role.Should().NotBeNull();
        (await harness.RoleManager.DeleteAsync(role!)).Succeeded.Should().BeTrue();
        (await harness.RoleManager.RoleExistsAsync(SystemRoles.JobSubscriber)).Should().BeFalse();
    }

    [Fact]
    public async Task Enrolment_succeeds_even_when_the_JobSubscriber_role_row_is_missing()
    {
        await using var harness = await IdentityTestHarness.CreateAsync();
        await DeleteJobSubscriberRoleAsync(harness);

        var user = await NewUserAsync(harness, "live.repro@example.com");
        var result = await BuildService(harness).EnsureJobSubscriberRoleAsync(user, "test");

        result.IsSuccess.Should().BeTrue("a missing system role must self-heal, not fail the enrolment");
        (await harness.UserManager.IsInRoleAsync(user, SystemRoles.JobSubscriber)).Should().BeTrue();
        (await harness.RoleManager.RoleExistsAsync(SystemRoles.JobSubscriber)).Should().BeTrue();
    }

    [Fact]
    public async Task An_existing_Customer_keeps_that_role_when_job_access_is_enabled()
    {
        // The same-account rule: one UserId holding both roles. A second
        // account, or a lost Customer role, is the failure mode here.
        await using var harness = await IdentityTestHarness.CreateAsync();
        await DeleteJobSubscriberRoleAsync(harness);

        var user = await NewUserAsync(harness, "existing.client@example.com");
        (await harness.UserManager.AddToRoleAsync(user, SystemRoles.Customer)).Succeeded.Should().BeTrue();

        var result = await BuildService(harness).EnsureJobSubscriberRoleAsync(user, "existing client enrols");

        result.IsSuccess.Should().BeTrue();

        var roles = await harness.UserManager.GetRolesAsync(user);
        roles.Should().Contain(SystemRoles.Customer, "the ISP relationship must survive");
        roles.Should().Contain(SystemRoles.JobSubscriber);

        harness.Fixture.AppDbContext.Users.Count().Should().Be(1, "no duplicate account may be created");
    }

    [Fact]
    public async Task Enrolling_twice_is_idempotent()
    {
        await using var harness = await IdentityTestHarness.CreateAsync();
        var user = await NewUserAsync(harness, "twice@example.com");
        var service = BuildService(harness);

        (await service.EnsureJobSubscriberRoleAsync(user, "first")).IsSuccess.Should().BeTrue();
        (await service.EnsureJobSubscriberRoleAsync(user, "second")).IsSuccess.Should().BeTrue();

        (await harness.UserManager.GetRolesAsync(user))
            .Count(r => r == SystemRoles.JobSubscriber).Should().Be(1);
    }

    [Fact]
    public async Task The_Customer_role_also_self_heals()
    {
        // Same defect class: a job seeker who later becomes an ISP
        // customer would hit it from the other direction.
        await using var harness = await IdentityTestHarness.CreateAsync();
        var role = await harness.RoleManager.FindByNameAsync(SystemRoles.Customer);
        (await harness.RoleManager.DeleteAsync(role!)).Succeeded.Should().BeTrue();

        var user = await NewUserAsync(harness, "seeker.to.client@example.com");
        var result = await BuildService(harness).EnsureCustomerRoleAsync(user, "test");

        result.IsSuccess.Should().BeTrue();
        (await harness.UserManager.IsInRoleAsync(user, SystemRoles.Customer)).Should().BeTrue();
    }
}
