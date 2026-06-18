using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.AppVersion;
using SmartFuture.Shared.Enums.AppVersion;

namespace SmartFuture.Infrastructure.Data.Configurations.AppVersion;

public class MobileAppVersionRuleConfiguration : IEntityTypeConfiguration<MobileAppVersionRule>
{
    // Deterministic seed identifiers + timestamp — HasData requires
    // constant values (no DateTime.UtcNow), and the seed travels inside
    // the migration so it is inserted only when the migration is applied
    // on the host (UAT/Live), never from a local build/run.
    private static readonly DateTime SeedUtc = new(2026, 6, 14, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid AndroidGoogleId = new("a1111111-1111-1111-1111-111111111111");
    private static readonly Guid AndroidHuaweiId = new("a2222222-2222-2222-2222-222222222222");
    private static readonly Guid IosAppleId      = new("a3333333-3333-3333-3333-333333333333");

    private const string DefaultMessage = "A new version of SmartFuture is available with improvements and fixes.";

    public void Configure(EntityTypeBuilder<MobileAppVersionRule> builder)
    {
        builder.ToTable("MobileAppVersionRules");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.CreatedAtUtc).IsRequired();
        builder.Property(r => r.UpdatedAtUtc);
        builder.Property(r => r.RowVersion).IsRowVersion();

        builder.Property(r => r.Platform).HasConversion<int>().IsRequired();
        builder.Property(r => r.Channel).HasConversion<int>().IsRequired();

        builder.Property(r => r.LatestVersion).IsRequired().HasMaxLength(32);
        builder.Property(r => r.MinimumSupportedVersion).IsRequired().HasMaxLength(32);
        builder.Property(r => r.LatestBuildNumber).IsRequired();
        builder.Property(r => r.MinimumSupportedBuildNumber).IsRequired();

        builder.Property(r => r.UpdateRequired).IsRequired();
        builder.Property(r => r.UpdateAvailable).IsRequired();
        builder.Property(r => r.IsEnabled).IsRequired();

        builder.Property(r => r.Title).IsRequired().HasMaxLength(120);
        builder.Property(r => r.Message).IsRequired().HasMaxLength(600);
        builder.Property(r => r.PrimaryButtonText).IsRequired().HasMaxLength(40);
        builder.Property(r => r.SecondaryButtonText).HasMaxLength(40);
        builder.Property(r => r.StoreUrl).HasMaxLength(500);
        builder.Property(r => r.ReleaseNotes).HasMaxLength(2000);

        // One rule per platform+channel — the resolver relies on this.
        builder.HasIndex(r => new { r.Platform, r.Channel }).IsUnique();

        // ─── Seed the three launch rules (idempotent via fixed keys) ──
        builder.HasData(
            new MobileAppVersionRule
            {
                Id = AndroidGoogleId,
                Platform = MobileAppPlatform.Android,
                Channel = MobileAppChannel.Google,
                LatestVersion = "1.0.1",
                LatestBuildNumber = 0,
                MinimumSupportedVersion = "1.0.0",
                MinimumSupportedBuildNumber = 0,
                UpdateRequired = false,
                UpdateAvailable = false,
                IsEnabled = true,
                Title = "Update available",
                Message = DefaultMessage,
                PrimaryButtonText = "Update app",
                SecondaryButtonText = "Later",
                StoreUrl = "https://play.google.com/store/apps/details?id=com.smartfuture.app",
                ReleaseNotes = null,
                CreatedAtUtc = SeedUtc
            },
            new MobileAppVersionRule
            {
                Id = AndroidHuaweiId,
                Platform = MobileAppPlatform.Android,
                Channel = MobileAppChannel.Huawei,
                LatestVersion = "1.0.1",
                LatestBuildNumber = 0,
                MinimumSupportedVersion = "1.0.0",
                MinimumSupportedBuildNumber = 0,
                UpdateRequired = false,
                UpdateAvailable = false,
                IsEnabled = true,
                Title = "Update available",
                Message = DefaultMessage,
                PrimaryButtonText = "Update app",
                SecondaryButtonText = "Later",
                StoreUrl = "https://appgallery.huawei.com/app/C118015685",
                ReleaseNotes = null,
                CreatedAtUtc = SeedUtc
            },
            new MobileAppVersionRule
            {
                Id = IosAppleId,
                Platform = MobileAppPlatform.iOS,
                Channel = MobileAppChannel.Apple,
                LatestVersion = "1.0.1",
                LatestBuildNumber = 0,
                MinimumSupportedVersion = "1.0.0",
                MinimumSupportedBuildNumber = 0,
                UpdateRequired = false,
                UpdateAvailable = false,
                IsEnabled = true,
                Title = "Update available",
                Message = DefaultMessage,
                PrimaryButtonText = "Update app",
                SecondaryButtonText = "Later",
                StoreUrl = string.Empty,
                ReleaseNotes = null,
                CreatedAtUtc = SeedUtc
            });
    }
}
