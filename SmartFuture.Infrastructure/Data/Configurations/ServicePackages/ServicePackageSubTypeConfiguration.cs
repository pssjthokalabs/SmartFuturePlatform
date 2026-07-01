using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Shared.Enums.ServicePackages;

namespace SmartFuture.Infrastructure.Data.Configurations.ServicePackages;

public class ServicePackageSubTypeConfiguration : IEntityTypeConfiguration<ServicePackageSubType>
{
    // Deterministic seed identifiers + timestamp — HasData requires
    // constant values (no DateTime.UtcNow). The seed travels inside the
    // migration so CCTV/Intercom are inserted only when the migration is
    // applied on the host (UAT/Live), never from a local build/run.
    private static readonly DateTime SeedUtc = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid CctvId     = new("5b1c0001-0000-4000-8000-0000000000c1");
    private static readonly Guid IntercomId = new("5b1c0002-0000-4000-8000-0000000000c2");

    public void Configure(EntityTypeBuilder<ServicePackageSubType> builder)
    {
        builder.ToTable("ServicePackageSubTypes");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.CreatedAtUtc).IsRequired();
        builder.Property(s => s.UpdatedAtUtc);
        builder.Property(s => s.RowVersion).IsRowVersion();

        builder.Property(s => s.PackageType).HasConversion<int>().IsRequired();

        builder.Property(s => s.Name).IsRequired().HasMaxLength(100);
        builder.Property(s => s.Slug).IsRequired().HasMaxLength(100);
        builder.Property(s => s.IsActive).IsRequired();
        builder.Property(s => s.DisplayOrder).IsRequired();

        builder.HasIndex(s => s.PackageType);
        builder.HasIndex(s => s.IsActive);
        builder.HasIndex(s => s.DisplayOrder);
        // Slug unique per package line so the public site can key on it.
        builder.HasIndex(s => new { s.PackageType, s.Slug }).IsUnique();

        // ─── Seed the two launch Security subtypes (idempotent keys) ──
        builder.HasData(
            new ServicePackageSubType
            {
                Id = CctvId,
                PackageType = ServicePackageType.Security,
                Name = "CCTV",
                Slug = "cctv",
                IsActive = true,
                DisplayOrder = 10,
                CreatedAtUtc = SeedUtc
            },
            new ServicePackageSubType
            {
                Id = IntercomId,
                PackageType = ServicePackageType.Security,
                Name = "Intercom",
                Slug = "intercom",
                IsActive = true,
                DisplayOrder = 20,
                CreatedAtUtc = SeedUtc
            });
    }
}
