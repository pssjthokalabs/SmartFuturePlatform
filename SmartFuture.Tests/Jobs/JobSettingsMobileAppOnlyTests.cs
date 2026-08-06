using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFuture.Application.Auditing;
using SmartFuture.Application.Auditing.Dtos;
using SmartFuture.Application.Common.Interfaces.Shared;
using SmartFuture.Application.Jobs;
using SmartFuture.Application.Jobs.Dtos;
using SmartFuture.Domain.Jobs;
using SmartFuture.Tests.Infrastructure;

namespace SmartFuture.Tests.Jobs;

// "Job details only in mobile app" — a WEBSITE-only switch.
//
// The API deliberately does not enforce it: it cannot reliably tell a
// website request from an app request, and gating the payload would
// break the app. So the contract the backend owes is narrow but exact —
// store the flag, return it on both the admin and public payloads, and
// never let it change what the API itself withholds.
public class JobSettingsMobileAppOnlyTests
{
    private static JobSettingsService BuildService(SqliteTestDbFixture fixture)
        => new(fixture.AppDbContext, Mock.Of<IAuditService>(), Mock.Of<ICurrentUserService>(),
            NullLogger<JobSettingsService>.Instance);

    [Fact]
    public async Task Settings_dto_exposes_the_mobile_app_only_flag_and_store_links()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var service = BuildService(fixture);

        var result = await service.GetAsync();

        result.IsSuccess.Should().BeTrue();
        result.Data!.JobDetailsMobileAppOnly.Should().BeFalse("off by default — the website keeps working as before");
        result.Data.GooglePlayUrl.Should().BeNull();
        result.Data.HuaweiAppGalleryUrl.Should().BeNull();
        result.Data.AppleAppStoreUrl.Should().BeNull();
    }

    [Fact]
    public async Task Admin_can_turn_the_flag_on_and_store_the_app_links()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var service = BuildService(fixture);

        var updated = await service.UpdateAsync(new UpdateJobSettingsRequestDto
        {
            JobDetailsMobileAppOnly = true,
            GooglePlayUrl = "https://play.google.com/store/apps/details?id=com.smartfuture.app",
            HuaweiAppGalleryUrl = "https://appgallery.huawei.com/app/C123",
            AppleAppStoreUrl = null
        });

        updated.IsSuccess.Should().BeTrue();
        updated.Data!.JobDetailsMobileAppOnly.Should().BeTrue();
        updated.Data.GooglePlayUrl.Should().Be("https://play.google.com/store/apps/details?id=com.smartfuture.app");
        updated.Data.HuaweiAppGalleryUrl.Should().Be("https://appgallery.huawei.com/app/C123");
        // A null in the request means "leave this one alone" (JobSettingsService:125),
        // so an unset Apple link stays unset rather than being blanked or defaulted.
        updated.Data.AppleAppStoreUrl.Should().BeNull("a null request value must not overwrite the stored link");

        var reread = await service.GetAsync();
        reread.Data!.JobDetailsMobileAppOnly.Should().BeTrue();
    }

    [Fact]
    public async Task Omitted_fields_leave_the_flag_untouched()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var service = BuildService(fixture);

        await service.UpdateAsync(new UpdateJobSettingsRequestDto { JobDetailsMobileAppOnly = true });
        // A form that only toggles alerts must not silently clear it.
        await service.UpdateAsync(new UpdateJobSettingsRequestDto { JobAlertsEnabled = true });

        (await service.GetAsync()).Data!.JobDetailsMobileAppOnly.Should().BeTrue();
    }

    [Fact]
    public async Task Public_settings_payload_carries_the_flag_and_links_for_the_website()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var service = BuildService(fixture);

        await service.UpdateAsync(new UpdateJobSettingsRequestDto
        {
            JobDetailsMobileAppOnly = true,
            JobDetailsSubscribersOnly = true,
            GooglePlayUrl = "https://play.google.com/store/apps/details?id=com.smartfuture.app"
        });

        var pub = await service.GetPublicAsync();

        pub.IsSuccess.Should().BeTrue();
        pub.Data!.JobDetailsMobileAppOnly.Should().BeTrue();
        pub.Data.GooglePlayUrl.Should().Be("https://play.google.com/store/apps/details?id=com.smartfuture.app");

        // Both flags travel independently. The website decides that the
        // app prompt wins; the app keeps using the subscriber gate.
        pub.Data.JobDetailsSubscribersOnly.Should().BeTrue();
    }

    [Fact]
    public async Task Turning_the_flag_on_does_not_change_what_the_api_withholds()
    {
        // The mobile app reads the same endpoints. If enabling a
        // website-only switch altered server-side gating, the app would
        // start hiding details too — the one outcome this must not have.
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var service = BuildService(fixture);

        var before = (await service.GetPublicAsync()).Data!;
        await service.UpdateAsync(new UpdateJobSettingsRequestDto { JobDetailsMobileAppOnly = true });
        var after = (await service.GetPublicAsync()).Data!;

        after.JobDetailsSubscribersOnly.Should().Be(before.JobDetailsSubscribersOnly);
        after.JobsModuleEnabled.Should().Be(before.JobsModuleEnabled);

        var settings = await fixture.DbContext.JobModuleSettings.FindAsync(JobModuleSettings.SingletonId);
        settings!.JobDetailsSubscribersOnly.Should().Be(before.JobDetailsSubscribersOnly,
            "the server-side subscriber gate is untouched by the website switch");
    }

    [Fact]
    public async Task Blank_store_links_are_stored_as_null_not_empty_strings()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var service = BuildService(fixture);

        var result = await service.UpdateAsync(new UpdateJobSettingsRequestDto
        {
            GooglePlayUrl = "   ",
            HuaweiAppGalleryUrl = ""
        });

        result.Data!.GooglePlayUrl.Should().BeNull();
        result.Data.HuaweiAppGalleryUrl.Should().BeNull();
    }
}
