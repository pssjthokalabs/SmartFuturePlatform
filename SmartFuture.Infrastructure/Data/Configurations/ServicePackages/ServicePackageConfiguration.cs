using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.ServicePackages;

namespace SmartFuture.Infrastructure.Data.Configurations.ServicePackages;

public class ServicePackageConfiguration : IEntityTypeConfiguration<ServicePackage>
{
    public void Configure(EntityTypeBuilder<ServicePackage> builder)
    {
        builder.ToTable("ServicePackages");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.CreatedAtUtc).IsRequired();
        builder.Property(p => p.UpdatedAtUtc);
        builder.Property(p => p.RowVersion).IsRowVersion();

        builder.Property(p => p.Type).HasConversion<int>().IsRequired();
        builder.Property(p => p.Status).HasConversion<int>().IsRequired();
        builder.Property(p => p.BillingCycle).HasConversion<int>().IsRequired();
        builder.Property(p => p.ProvisioningType).HasConversion<int?>();
        builder.Property(p => p.RequiresProvisioning).IsRequired();

        builder.Property(p => p.Name)
            .IsRequired()
            .HasMaxLength(150);

        builder.Property(p => p.Description).HasMaxLength(2000);
        builder.Property(p => p.ShortDescription).HasMaxLength(500);

        builder.Property(p => p.SpeedLabel).HasMaxLength(100);
        builder.Property(p => p.DataAllowanceLabel).HasMaxLength(100);

        builder.Property(p => p.Price).HasPrecision(18, 2);
        builder.Property(p => p.InstallationFee).HasPrecision(18, 2);

        builder.Property(p => p.RouterDescription).HasMaxLength(500);
        builder.Property(p => p.TermsSummary).HasMaxLength(1000);
        builder.Property(p => p.CoverageNotes).HasMaxLength(1000);
        builder.Property(p => p.ExternalReference).HasMaxLength(100);

        builder.Property(p => p.ImageUrl).HasMaxLength(500);
        builder.Property(p => p.ImageStorageKey).HasMaxLength(500);

        // JSON array of feature bullets. 4000 chars is ample for a short
        // marketing list and keeps the column off nvarchar(max).
        builder.Property(p => p.FeaturesJson).HasMaxLength(4000);

        builder.HasOne(p => p.RadiusProfile)
            .WithMany()
            .HasForeignKey(p => p.RadiusProfileId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => p.Type);
        builder.HasIndex(p => p.Status);
        builder.HasIndex(p => p.RequiresProvisioning);
        builder.HasIndex(p => p.IsFeatured);
        builder.HasIndex(p => p.DisplayOrder);
        builder.HasIndex(p => p.Price);
        builder.HasIndex(p => p.CreatedAtUtc);
        builder.HasIndex(p => p.Name);

        builder.HasIndex(p => p.ExternalReference)
            .IsUnique()
            .HasFilter("[ExternalReference] IS NOT NULL");
    }
}
