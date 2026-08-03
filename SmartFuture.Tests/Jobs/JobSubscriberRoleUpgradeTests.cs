using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auth;
using SmartFuture.Application.Auth.Dtos;
using SmartFuture.Application.Common.Interfaces.Identity;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Common.Storage;
using SmartFuture.Application.Communication.Sms;
using SmartFuture.Application.Communication.Verification;
using SmartFuture.Application.Jobs;
using SmartFuture.Application.Jobs.Dtos;
using SmartFuture.Application.Notifications;
using SmartFuture.Application.Users;
using SmartFuture.Domain.Identity;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Errors;
using SmartFuture.Tests.Infrastructure;

namespace SmartFuture.Tests.Jobs;

// The safety-critical contract of the Job Opportunities module:
//
//   SmartFuture has ONE Users table shared by two product lines. A
//   person can legitimately be a fibre Customer and a JobSubscriber at
//   the same time, and may arrive at either product first. These tests
//   pin the four journeys that must never regress:
//
//     1. JobSubscriber later signs up as a Customer  → role ADDED, not
//        a duplicate-email dead end.
//     2. Customer later enrols for Job Opportunities → role ADDED, both
//        roles retained, no second user row.
//     3. Someone who merely KNOWS an email address cannot attach a role
//        to that account.
//     4. Existing customer signup behaviour is unchanged (a Customer
//        registering again still gets EMAIL_TAKEN).
public class JobSubscriberRoleUpgradeTests
{
    private const string Password = "Str0ngPassw0rd!";
    private const string WrongPassword = "Wr0ngPassw0rd!";

    // ─── Harness ──────────────────────────────────────────────────────

    private static UserRoleUpgradeService BuildRoleUpgradeService(IdentityTestHarness harness)
        => new(harness.UserManager, harness.Fixture.AppDbContext, StubAuditService(), StubCurrentUser(),
            NullLogger<UserRoleUpgradeService>.Instance);

    private static AuthService BuildAuthService(IdentityTestHarness harness)
        => new(harness.UserManager, harness.SignInManager, StubJwtGenerator(), harness.Fixture.AppDbContext, StubAuditService(),
            StubNotificationService(), StubCurrentUser(), Options.Create(new FrontendSettings()), StubHostEnvironment(),
            Mock.Of<IPhoneVerificationService>(), Options.Create(new OtpSettings()), Mock.Of<ISmsProvider>(),
            BuildRoleUpgradeService(harness), NullLogger<AuthService>.Instance);

    private static JobSubscriberService BuildJobSubscriberService(IdentityTestHarness harness)
        => new(harness.UserManager, harness.SignInManager, StubJwtGenerator(), BuildRoleUpgradeService(harness),
            harness.Fixture.AppDbContext, Mock.Of<IFileStorageService>(), StubAuditService(), StubCurrentUser(),
            NullLogger<JobSubscriberService>.Instance);

    private static IAuditService StubAuditService() => Mock.Of<IAuditService>();

    private static INotificationService StubNotificationService() => Mock.Of<INotificationService>();

    private static ICurrentUserService StubCurrentUser() => Mock.Of<ICurrentUserService>();

    private static IHostEnvironment StubHostEnvironment()
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns("Development");
        return env.Object;
    }

    // Token minting is not under test — every path that reaches it has
    // already made the decision we care about.
    private static IJwtTokenGenerator StubJwtGenerator()
    {
        var generator = new Mock<IJwtTokenGenerator>();
        generator.Setup(g => g.GenerateTokenAsync(It.IsAny<User>()))
            .ReturnsAsync(new AuthTokenDto { AccessToken = "test-token", RefreshToken = "test-refresh" });
        return generator.Object;
    }

    private static RegisterRequestDto CustomerRegistration(string email, string password) => new()
    {
        FirstName = "Thabo",
        LastName = "Nkosi",
        Email = email,
        Password = password,
        ConfirmPassword = password
    };

    // ─── 1. JobSubscriber → Customer ──────────────────────────────────

    [Fact]
    public async Task Customer_registration_upgrades_an_existing_job_subscriber_instead_of_rejecting_the_email()
    {
        await using var harness = await IdentityTestHarness.CreateAsync();
        var existing = await harness.CreateUserAsync("seeker@example.com", Password, SystemRoles.JobSubscriber);
        var auth = BuildAuthService(harness);

        var result = await auth.RegisterAsync(CustomerRegistration("seeker@example.com", Password));

        result.IsSuccess.Should().BeTrue("a job subscriber signing up for fibre must not hit a duplicate-email dead end");

        var roles = await harness.GetRolesAsync(existing.Id);
        roles.Should().Contain(SystemRoles.Customer);
        roles.Should().Contain(SystemRoles.JobSubscriber, "adding Customer must never strip the role they already had");

        // Critically: ONE user row, not two.
        var userCount = await harness.DbContext.Users.CountAsync(u => u.Email == "seeker@example.com");
        userCount.Should().Be(1);
    }

    [Fact]
    public async Task Customer_registration_creates_a_customer_profile_for_the_upgraded_job_subscriber()
    {
        await using var harness = await IdentityTestHarness.CreateAsync();
        var existing = await harness.CreateUserAsync("seeker2@example.com", Password, SystemRoles.JobSubscriber);
        var auth = BuildAuthService(harness);

        // A job subscriber has no CustomerProfile — the customer journey
        // (orders, coverage, billing) assumes one exists.
        (await harness.DbContext.CustomerProfiles.AnyAsync(p => p.UserId == existing.Id)).Should().BeFalse();

        var result = await auth.RegisterAsync(CustomerRegistration("seeker2@example.com", Password));

        result.IsSuccess.Should().BeTrue();
        (await harness.DbContext.CustomerProfiles.AnyAsync(p => p.UserId == existing.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task Customer_registration_with_a_wrong_password_does_not_attach_the_customer_role()
    {
        await using var harness = await IdentityTestHarness.CreateAsync();
        var existing = await harness.CreateUserAsync("seeker3@example.com", Password, SystemRoles.JobSubscriber);
        var auth = BuildAuthService(harness);

        var result = await auth.RegisterAsync(CustomerRegistration("seeker3@example.com", WrongPassword));

        result.IsSuccess.Should().BeFalse();
        result.Code.Should().Be(ErrorCodes.ACCOUNT_EXISTS_SIGN_IN_REQUIRED,
            "the client should route to sign-in, not show a dead-end 'email taken' error");

        var roles = await harness.GetRolesAsync(existing.Id);
        roles.Should().NotContain(SystemRoles.Customer,
            "knowing an email address is not proof of ownership — a role must never be granted on that alone");
    }

    // ─── 2. Customer → JobSubscriber ──────────────────────────────────

    [Fact]
    public async Task Signed_in_customer_can_enrol_as_a_job_subscriber_and_keeps_both_roles()
    {
        await using var harness = await IdentityTestHarness.CreateAsync();
        var customer = await harness.CreateUserAsync("customer@example.com", Password, SystemRoles.Customer);
        var jobs = BuildJobSubscriberService(harness);

        var result = await jobs.RegisterAsync(new RegisterJobSubscriberRequestDto { CurrentCity = "Cape Town" }, customer.Id);

        result.IsSuccess.Should().BeTrue();

        var roles = await harness.GetRolesAsync(customer.Id);
        roles.Should().Contain(SystemRoles.Customer, "enrolling for jobs must never remove customer access");
        roles.Should().Contain(SystemRoles.JobSubscriber);

        (await harness.DbContext.Users.CountAsync(u => u.Email == "customer@example.com")).Should().Be(1);
        (await harness.DbContext.JobSubscriberProfiles.AnyAsync(p => p.UserId == customer.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task Anonymous_job_subscriber_registration_upgrades_an_existing_customer_when_the_password_matches()
    {
        await using var harness = await IdentityTestHarness.CreateAsync();
        var customer = await harness.CreateUserAsync("dual@example.com", Password, SystemRoles.Customer);
        var jobs = BuildJobSubscriberService(harness);

        var result = await jobs.RegisterAsync(new RegisterJobSubscriberRequestDto
        {
            FirstName = "Thabo",
            LastName = "Nkosi",
            Email = "dual@example.com",
            Password = Password,
            ConfirmPassword = Password
        }, currentUserId: null);

        result.IsSuccess.Should().BeTrue();

        var roles = await harness.GetRolesAsync(customer.Id);
        roles.Should().Contain(SystemRoles.Customer);
        roles.Should().Contain(SystemRoles.JobSubscriber);
        (await harness.DbContext.Users.CountAsync(u => u.Email == "dual@example.com")).Should().Be(1);
    }

    // ─── 3. Ownership must be proven ──────────────────────────────────

    [Fact]
    public async Task Anonymous_job_subscriber_registration_with_a_wrong_password_is_refused_without_touching_roles()
    {
        await using var harness = await IdentityTestHarness.CreateAsync();
        var customer = await harness.CreateUserAsync("victim@example.com", Password, SystemRoles.Customer);
        var jobs = BuildJobSubscriberService(harness);

        var result = await jobs.RegisterAsync(new RegisterJobSubscriberRequestDto
        {
            FirstName = "Mallory",
            LastName = "Attacker",
            Email = "victim@example.com",
            Password = WrongPassword,
            ConfirmPassword = WrongPassword
        }, currentUserId: null);

        result.IsSuccess.Should().BeFalse();
        result.Code.Should().Be(ErrorCodes.ACCOUNT_EXISTS_SIGN_IN_REQUIRED);

        var roles = await harness.GetRolesAsync(customer.Id);
        roles.Should().NotContain(SystemRoles.JobSubscriber);
        (await harness.DbContext.Users.CountAsync(u => u.Email == "victim@example.com")).Should().Be(1);
    }

    // ─── 4. Existing behaviour is unchanged ───────────────────────────

    [Fact]
    public async Task Customer_registering_twice_still_gets_the_original_email_taken_error()
    {
        await using var harness = await IdentityTestHarness.CreateAsync();
        await harness.CreateUserAsync("existing@example.com", Password, SystemRoles.Customer);
        var auth = BuildAuthService(harness);

        var result = await auth.RegisterAsync(CustomerRegistration("existing@example.com", Password));

        result.IsSuccess.Should().BeFalse();
        result.Code.Should().Be(ErrorCodes.EMAIL_TAKEN,
            "the pre-existing customer signup path must not change behaviour");
    }

    [Fact]
    public async Task Brand_new_customer_registration_still_succeeds_and_creates_one_user()
    {
        await using var harness = await IdentityTestHarness.CreateAsync();
        var auth = BuildAuthService(harness);

        var result = await auth.RegisterAsync(CustomerRegistration("brandnew@example.com", Password));

        result.IsSuccess.Should().BeTrue();

        var user = await harness.DbContext.Users.SingleAsync(u => u.Email == "brandnew@example.com");
        var roles = await harness.GetRolesAsync(user.Id);
        roles.Should().Contain(SystemRoles.Customer);
        roles.Should().NotContain(SystemRoles.JobSubscriber, "a fibre signup must not silently enrol anyone for job alerts");
    }

    [Fact]
    public async Task Brand_new_job_subscriber_registration_does_not_grant_the_customer_role()
    {
        await using var harness = await IdentityTestHarness.CreateAsync();
        var jobs = BuildJobSubscriberService(harness);

        var result = await jobs.RegisterAsync(new RegisterJobSubscriberRequestDto
        {
            FirstName = "Naledi",
            LastName = "Dlamini",
            Email = "newseeker@example.com",
            Password = Password,
            ConfirmPassword = Password
        }, currentUserId: null);

        result.IsSuccess.Should().BeTrue();

        var user = await harness.DbContext.Users.SingleAsync(u => u.Email == "newseeker@example.com");
        var roles = await harness.GetRolesAsync(user.Id);
        roles.Should().Contain(SystemRoles.JobSubscriber);
        roles.Should().NotContain(SystemRoles.Customer, "a job seeker is not an ISP customer until they choose to be");

        // No CustomerProfile either — that row belongs to the customer
        // journey and would pollute customer-facing counts.
        (await harness.DbContext.CustomerProfiles.AnyAsync(p => p.UserId == user.Id)).Should().BeFalse();
    }

    // ─── Idempotence ──────────────────────────────────────────────────

    [Fact]
    public async Task Enrolling_twice_is_idempotent_and_never_duplicates_the_role_or_profile()
    {
        await using var harness = await IdentityTestHarness.CreateAsync();
        var customer = await harness.CreateUserAsync("repeat@example.com", Password, SystemRoles.Customer);
        var jobs = BuildJobSubscriberService(harness);

        (await jobs.RegisterAsync(new RegisterJobSubscriberRequestDto(), customer.Id)).IsSuccess.Should().BeTrue();
        (await jobs.RegisterAsync(new RegisterJobSubscriberRequestDto(), customer.Id)).IsSuccess.Should().BeTrue();

        var roles = await harness.GetRolesAsync(customer.Id);
        roles.Count(r => r == SystemRoles.JobSubscriber).Should().Be(1);
        (await harness.DbContext.JobSubscriberProfiles.CountAsync(p => p.UserId == customer.Id)).Should().Be(1);
        (await harness.DbContext.JobAlertPreferences.CountAsync(p => p.UserId == customer.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Role_upgrade_service_is_additive_only()
    {
        await using var harness = await IdentityTestHarness.CreateAsync();
        var user = await harness.CreateUserAsync("additive@example.com", Password, SystemRoles.Customer, SystemRoles.Support);
        var upgrades = BuildRoleUpgradeService(harness);

        var result = await upgrades.EnsureJobSubscriberRoleAsync(user, "test");

        result.IsSuccess.Should().BeTrue();

        var roles = await harness.GetRolesAsync(user.Id);
        roles.Should().Contain(new[] { SystemRoles.Customer, SystemRoles.Support, SystemRoles.JobSubscriber },
            "granting one role must never disturb the others");
    }

    [Fact]
    public async Task Enrolment_does_not_overwrite_a_profile_the_subscriber_already_curated()
    {
        await using var harness = await IdentityTestHarness.CreateAsync();
        var user = await harness.CreateUserAsync("curated@example.com", Password, SystemRoles.JobSubscriber);
        var jobs = BuildJobSubscriberService(harness);

        await jobs.UpsertProfileAsync(user.Id, new UpsertJobSubscriberProfileRequestDto { CurrentCity = "Durban" });

        // Re-running enrolment with a DIFFERENT city must not clobber it.
        await jobs.RegisterAsync(new RegisterJobSubscriberRequestDto { CurrentCity = "Johannesburg" }, user.Id);

        var profile = await harness.DbContext.JobSubscriberProfiles.SingleAsync(p => p.UserId == user.Id);
        profile.CurrentCity.Should().Be("Durban");
    }
}
