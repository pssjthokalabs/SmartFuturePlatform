using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Openserve;

namespace SmartFuture.Infrastructure.Data.Configurations.Openserve;

public class PackageOpenserveMappingConfiguration : IEntityTypeConfiguration<PackageOpenserveMapping>
{
    public void Configure(EntityTypeBuilder<PackageOpenserveMapping> builder)
    {
        builder.ToTable("PackageOpenserveMappings");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.CreatedAtUtc).IsRequired();
        builder.Property(m => m.UpdatedAtUtc);
        builder.Property(m => m.RowVersion).IsRowVersion();

        builder.Property(m => m.OpenserveProductName).IsRequired().HasMaxLength(200);
        builder.Property(m => m.Sku).IsRequired().HasMaxLength(20);
        builder.Property(m => m.Capacity).IsRequired().HasMaxLength(20);
        builder.Property(m => m.CapacityUom).IsRequired().HasMaxLength(20);

        builder.Property(m => m.OpenserveProductOfferingId).HasMaxLength(60);
        builder.Property(m => m.OpenserveProductSpecificationId).HasMaxLength(60);
        builder.Property(m => m.Notes).HasMaxLength(2000);

        builder.HasOne(m => m.ServicePackage)
            .WithMany()
            .HasForeignKey(m => m.ServicePackageId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(m => m.ServicePackageId).IsUnique();
        builder.HasIndex(m => m.IsEnabled);
    }
}
