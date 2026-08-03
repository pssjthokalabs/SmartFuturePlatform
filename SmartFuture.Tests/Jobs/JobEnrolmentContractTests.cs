using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Common.Interfaces.Identity;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Common.Storage;
using SmartFuture.Application.Auth.Dtos;
using SmartFuture.Application.Jobs;
using SmartFuture.Application.Jobs.Dtos;
using SmartFuture.Application.Users;
using SmartFuture.Domain.Identity;
using SmartFuture.Shared.Constants;
using SmartFuture.Tests.Infrastructure;

namespace SmartFuture.Tests.Jobs;

// Pins the enrolment WIRE contract the mobile app and public website
// build against. Each test here answers a question the client team asked
// during integration, so a future refactor that changes the answer fails
// loudly instead of silently breaking a shipped build.
public class JobEnrolmentContractTests
{
    private const string Password = "Str0ngPassw0rd!";

    private static JobSubscriberService BuildService(IdentityTestHarness harness)
    {
        var roleUpgrades = new UserRoleUpgradeService(harness.UserManager, harness.Fixture.AppDbContext,
            Mock.Of<IAuditService>(), Mock.Of<ICurrentUserService>(), NullLogger<UserRoleUpgradeService>.Instance);

        var jwt = new Mock<IJwtTokenGenerator>();
        jwt.Setup(g => g.GenerateTokenAsync(It.IsAny<User>()))
            .ReturnsAsync(new AuthTokenDto { AccessToken = "t", RefreshToken = "r" });

        return new JobSubscriberService(harness.UserManager, harness.SignInManager, jwt.Object, roleUpgrades,
            harness.Fixture.AppDbContext, Mock.Of<IFileStorageService>(), Mock.Of<IAuditService>(),
            Mock.Of<ICurrentUserService>(), NullLogger<JobSubscriberService>.Instance);
    }

    // ─── Q1: /enrol for an already-authenticated user ─────────────────

    [Fact]
    public async Task Authenticated_enrol_succeeds_with_no_password_supplied()
    {
        await using var harness = await IdentityTestHarness.CreateAsync();
        var customer = await harness.CreateUserAsync("enrol-nopwd@example.com", Password, SystemRoles.Customer);
        var service = BuildService(harness);

        // Exactly the slim body the client sends: no password, no
        // confirmPassword. The signed-in path must never reach the
        // anonymous password checks.
        var result = await service.RegisterAsync(new RegisterJobSubscriberRequestDto
        {
            FirstName = "Thabo",
            LastName = "Nkosi",
            Email = "enrol-nopwd@example.com",
            PhoneNumber = "0737942244",
            CurrentCity = "Cape Town",
            CurrentProvince = "Western Cape",
            PreferredCategories = new List<string> { "Information Technology" },
            PreferredLocations = new List<string> { "Cape Town" },
            SubscribeToAlerts = true,
        }, currentUserId: customer.Id);

        result.IsSuccess.Should().BeTrue("an authenticated enrol must not require a password");

        var roles = await harness.GetRolesAsync(customer.Id);
        roles.Should().Contain(SystemRoles.JobSubscriber);
        roles.Should().Contain(SystemRoles.Customer);
    }

    [Fact]
    public async Task Authenticated_enrol_succeeds_with_a_completely_empty_body()
    {
        await using var harness = await IdentityTestHarness.CreateAsync();
        var customer = await harness.CreateUserAsync("enrol-empty@example.com", Password, SystemRoles.Customer);
        var service = BuildService(harness);

        var result = await service.RegisterAsync(new RegisterJobSubscriberRequestDto(), currentUserId: customer.Id);

        result.IsSuccess.Should().BeTrue();
        (await harness.GetRolesAsync(customer.Id)).Should().Contain(SystemRoles.JobSubscriber);
    }

    [Fact]
    public async Task Authenticated_enrol_ignores_identity_fields_in_the_body()
    {
        // The signed-in path derives identity from the TOKEN, never the
        // payload — otherwise a caller could rename another account by
        // posting a different name to /enrol.
        await using var harness = await IdentityTestHarness.CreateAsync();
        var customer = await harness.CreateUserAsync("enrol-identity@example.com", Password, SystemRoles.Customer);
        var service = BuildService(harness);

        await service.RegisterAsync(new RegisterJobSubscriberRequestDto
        {
            FirstName = "Mallory",
            LastName = "Overwrite",
            Email = "someone-else@example.com",
            PhoneNumber = "0000000000",
        }, currentUserId: customer.Id);

        var reloaded = await harness.DbContext.Users.AsNoTracking().SingleAsync(u => u.Id == customer.Id);
        reloaded.FirstName.Should().Be("Test");
        reloaded.LastName.Should().Be("User");
        reloaded.Email.Should().Be("enrol-identity@example.com");
    }

    // ─── Q2: what /register actually persists ─────────────────────────

    [Fact]
    public async Task Anonymous_register_persists_only_the_enrolment_subset_of_profile_fields()
    {
        await using var harness = await IdentityTestHarness.CreateAsync();
        var service = BuildService(harness);

        var result = await service.RegisterAsync(new RegisterJobSubscriberRequestDto
        {
            FirstName = "Naledi",
            LastName = "Dlamini",
            Email = "register-full@example.com",
            PhoneNumber = "0737942245",
            Password = Password,
            ConfirmPassword = Password,
            CurrentCity = "Durban",
            CurrentProvince = "KwaZulu-Natal",
            PreferredCategories = new List<string> { "Healthcare" },
            PreferredLocations = new List<string> { "Durban" },
            SubscribeToAlerts = true,
        }, currentUserId: null);

        result.IsSuccess.Should().BeTrue();

        var user = await harness.DbContext.Users.SingleAsync(u => u.Email == "register-full@example.com");
        var profile = await harness.DbContext.JobSubscriberProfiles.AsNoTracking().SingleAsync(p => p.UserId == user.Id);

        // Stored by /register:
        profile.CurrentCity.Should().Be("Durban");
        profile.CurrentProvince.Should().Be("KwaZulu-Natal");
        profile.PreferredCategoriesJson.Should().NotBeNull();
        profile.PreferredLocationsJson.Should().NotBeNull();

        // NOT stored by /register — the DTO has no surface for these, so
        // a client that posts them gets them silently dropped and MUST
        // follow up with PUT /api/job-subscribers/me/profile.
        profile.PreferredFirstName.Should().BeNull();
        profile.ContactPhone.Should().BeNull();
        profile.LinkedInProfileUrl.Should().BeNull();
        profile.WebsiteUrl.Should().BeNull();
        profile.SalaryExpectations.Should().BeNull();
        profile.HighestQualification.Should().BeNull();
        profile.YearsOfExperience.Should().BeNull();
    }

    [Fact]
    public async Task Profile_upsert_after_register_stores_the_full_profile()
    {
        // The prescribed two-call flow: register → PUT me/profile.
        await using var harness = await IdentityTestHarness.CreateAsync();
        var service = BuildService(harness);

        await service.RegisterAsync(new RegisterJobSubscriberRequestDto
        {
            FirstName = "Naledi", LastName = "Dlamini",
            Email = "register-then-profile@example.com",
            Password = Password, ConfirmPassword = Password,
        }, currentUserId: null);

        var user = await harness.DbContext.Users.SingleAsync(u => u.Email == "register-then-profile@example.com");

        var upsert = await service.UpsertProfileAsync(user.Id, new UpsertJobSubscriberProfileRequestDto
        {
            PreferredFirstName = "Nali",
            ContactPhone = "0737942246",
            CurrentCity = "Durban",
            CurrentProvince = "KwaZulu-Natal",
            CurrentCountry = "South Africa",
            LinkedInProfileUrl = "https://www.linkedin.com/in/naledi",
            WebsiteUrl = "https://naledi.co.za",
            SalaryExpectations = "R25 000 per month",
            HighestQualification = "BSc Nursing",
            YearsOfExperience = 6,
        });

        upsert.IsSuccess.Should().BeTrue();

        var profile = await harness.DbContext.JobSubscriberProfiles.AsNoTracking().SingleAsync(p => p.UserId == user.Id);
        profile.PreferredFirstName.Should().Be("Nali");
        profile.LinkedInProfileUrl.Should().Be("https://www.linkedin.com/in/naledi");
        profile.SalaryExpectations.Should().Be("R25 000 per month");
        profile.HighestQualification.Should().Be("BSc Nursing");
        profile.YearsOfExperience.Should().Be(6);
    }

    // ─── Q3: what "complete" actually requires ────────────────────────

    [Fact]
    public async Task Optional_profile_fields_left_null_do_not_block_completion()
    {
        await using var harness = await IdentityTestHarness.CreateAsync();
        var user = await harness.CreateUserAsync("completion@example.com", Password, SystemRoles.JobSubscriber);
        var service = BuildService(harness);

        // Everything the client might not have collected is left null.
        await service.UpsertProfileAsync(user.Id, new UpsertJobSubscriberProfileRequestDto
        {
            CurrentCity = "Polokwane",
            HighestQualification = null,
            YearsOfExperience = null,
            PreferredCategories = null,
            PreferredLocations = null,
        });

        // Location satisfied, CV still outstanding.
        var beforeCv = await service.GetMeAsync(user.Id);
        beforeCv.Data!.MissingFields.Should().BeEquivalentTo(new[] { "cv" });
        beforeCv.Data.IsProfileComplete.Should().BeFalse();

        // Simulate the CV landing (the storage call itself is mocked out
        // in these tests, so stamp the profile the way the upload does).
        var profile = await harness.DbContext.JobSubscriberProfiles.SingleAsync(p => p.UserId == user.Id);
        profile.CvObjectKey = "job-documents/x/cv/abc.pdf";
        profile.CvFileName = "cv.pdf";
        profile.CompletedAtUtc = DateTime.UtcNow;
        await harness.DbContext.SaveChangesAsync();

        var afterCv = await service.GetMeAsync(user.Id);
        afterCv.Data!.MissingFields.Should().BeEmpty("only a CV plus a city or province are required");
        afterCv.Data.IsProfileComplete.Should().BeTrue();
    }

    [Fact]
    public async Task Province_alone_satisfies_the_location_requirement()
    {
        await using var harness = await IdentityTestHarness.CreateAsync();
        var user = await harness.CreateUserAsync("province-only@example.com", Password, SystemRoles.JobSubscriber);
        var service = BuildService(harness);

        await service.UpsertProfileAsync(user.Id, new UpsertJobSubscriberProfileRequestDto { CurrentProvince = "Limpopo" });

        var me = await service.GetMeAsync(user.Id);
        me.Data!.MissingFields.Should().NotContain("currentLocation");
    }
}
