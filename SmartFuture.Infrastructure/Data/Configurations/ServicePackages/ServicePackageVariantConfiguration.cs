using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.ServicePackages;

namespace SmartFuture.Infrastructure.Data.Configurations.ServicePackages;

public class ServicePackageVariantConfiguration : IEntityTypeConfiguration<ServicePackageVariant>
{
    public void Configure(EntityTypeBuilder<ServicePackageVariant> builder)
    {
        builder.ToTable("ServicePackageVariants");

        builder.HasKey(v => v.Id);

        builder.Property(v => v.CreatedAtUtc).IsRequired();
        builder.Property(v => v.UpdatedAtUtc);
        builder.Property(v => v.RowVersion).IsRowVersion();

        builder.Property(v => v.ServicePackageId).IsRequired();

        builder.Property(v => v.Name).IsRequired().HasMaxLength(100);
        builder.Property(v => v.Price).HasPrecision(18, 2);
        builder.Property(v => v.InstallationFee).HasPrecision(18, 2);
        builder.Property(v => v.IsActive).IsRequired();
        builder.Property(v => v.DisplayOrder).IsRequired();

        // Delete the variants when their package is hard-deleted. Packages
        // are archived (soft) in practice, so this is a safety net.
        builder.HasOne(v => v.ServicePackage)
            .WithMany(p => p.Variants)
            .HasForeignKey(v => v.ServicePackageId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(v => v.ServicePackageId);
        builder.HasIndex(v => v.IsActive);
        builder.HasIndex(v => v.DisplayOrder);
    }
}
