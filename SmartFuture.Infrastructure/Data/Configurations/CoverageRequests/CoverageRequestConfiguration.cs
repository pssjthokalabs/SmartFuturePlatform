using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.CoverageRequests;

namespace SmartFuture.Infrastructure.Data.Configurations.CoverageRequests;

public class CoverageRequestConfiguration : IEntityTypeConfiguration<CoverageRequest>
{
    public void Configure(EntityTypeBuilder<CoverageRequest> builder)
    {
        builder.ToTable("CoverageRequests");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.CreatedAtUtc).IsRequired();
        builder.Property(c => c.UpdatedAtUtc);
        builder.Property(c => c.RowVersion).IsRowVersion();

        builder.Property(c => c.UserId).IsRequired();

        builder.Property(c => c.Status).HasConversion<int>().IsRequired();
        builder.Property(c => c.Source).HasConversion<int>().IsRequired();
        builder.Property(c => c.RequestedServiceType).HasConversion<int?>();

        builder.Property(c => c.FullName).HasMaxLength(200);
        builder.Property(c => c.Email).HasMaxLength(256);
        builder.Property(c => c.PhoneNumber).HasMaxLength(50);

        builder.Property(c => c.AddressLine1).IsRequired().HasMaxLength(250);
        builder.Property(c => c.AddressLine2).HasMaxLength(250);
        builder.Property(c => c.Suburb).HasMaxLength(150);
        builder.Property(c => c.City).HasMaxLength(150);
        builder.Property(c => c.Province).HasMaxLength(150);
        builder.Property(c => c.PostalCode).HasMaxLength(30);
        builder.Property(c => c.Country).HasMaxLength(100);

        builder.Property(c => c.Latitude).HasPrecision(9, 6);
        builder.Property(c => c.Longitude).HasPrecision(9, 6);

        builder.Property(c => c.GooglePlaceId).HasMaxLength(200);
        builder.Property(c => c.MapProviderReference).HasMaxLength(300);

        builder.Property(c => c.CustomerNotes).HasMaxLength(2000);
        builder.Property(c => c.AdminNotes).HasMaxLength(3000);
        builder.Property(c => c.CoverageResultSummary).HasMaxLength(1000);

        builder.HasOne(c => c.User)
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(c => c.CustomerProfile)
            .WithMany()
            .HasForeignKey(c => c.CustomerProfileId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(c => c.ServicePackage)
            .WithMany()
            .HasForeignKey(c => c.ServicePackageId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(c => c.ReviewedByUser)
            .WithMany()
            .HasForeignKey(c => c.ReviewedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(c => c.UserId);
        builder.HasIndex(c => c.CustomerProfileId);
        builder.HasIndex(c => c.ServicePackageId);
        builder.HasIndex(c => c.RequestedServiceType);
        builder.HasIndex(c => c.Status);
        builder.HasIndex(c => c.Source);
        builder.HasIndex(c => c.City);
        builder.HasIndex(c => c.Suburb);
        builder.HasIndex(c => c.Province);
        builder.HasIndex(c => c.PostalCode);
        builder.HasIndex(c => c.CreatedAtUtc);
        builder.HasIndex(c => c.ReviewedAtUtc);
        builder.HasIndex(c => c.ReviewedByUserId);
    }
}
