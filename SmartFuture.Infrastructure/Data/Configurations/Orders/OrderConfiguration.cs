using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Orders;

namespace SmartFuture.Infrastructure.Data.Configurations.Orders;

public class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders");

        builder.HasKey(o => o.Id);

        builder.Property(o => o.CreatedAtUtc).IsRequired();
        builder.Property(o => o.UpdatedAtUtc);
        builder.Property(o => o.RowVersion).IsRowVersion();

        builder.Property(o => o.OrderNumber)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(o => o.UserId).IsRequired();

        builder.Property(o => o.Status).HasConversion<int>().IsRequired();
        builder.Property(o => o.Source).HasConversion<int>().IsRequired();

        builder.Property(o => o.PackageName)
            .IsRequired()
            .HasMaxLength(150);

        // Selected variant display snapshot ("4 IP"). Nullable — most
        // orders have no variant.
        builder.Property(o => o.PackageVariantName).HasMaxLength(100);

        builder.Property(o => o.PackageType).HasConversion<int>().IsRequired();
        builder.Property(o => o.PackageSpeedLabel).HasMaxLength(100);
        builder.Property(o => o.PackageDataAllowanceLabel).HasMaxLength(100);

        builder.Property(o => o.PackagePrice).HasPrecision(18, 2);
        builder.Property(o => o.PackageBillingCycle).HasConversion<int>().IsRequired();
        builder.Property(o => o.PackageInstallationFee).HasPrecision(18, 2);

        builder.Property(o => o.FullName).HasMaxLength(200);
        builder.Property(o => o.Email).HasMaxLength(256);
        builder.Property(o => o.PhoneNumber).HasMaxLength(50);

        builder.Property(o => o.AddressLine1).IsRequired().HasMaxLength(250);
        builder.Property(o => o.AddressLine2).HasMaxLength(250);
        builder.Property(o => o.Suburb).HasMaxLength(150);
        builder.Property(o => o.City).HasMaxLength(150);
        builder.Property(o => o.Province).HasMaxLength(150);
        builder.Property(o => o.PostalCode).HasMaxLength(30);
        builder.Property(o => o.Country).HasMaxLength(100);

        builder.Property(o => o.Latitude).HasPrecision(9, 6);
        builder.Property(o => o.Longitude).HasPrecision(9, 6);

        builder.Property(o => o.GooglePlaceId).HasMaxLength(200);
        builder.Property(o => o.MapProviderReference).HasMaxLength(300);
        builder.Property(o => o.BuildingComplexName).HasMaxLength(200);
        builder.Property(o => o.UnitNumber).HasMaxLength(50);
        builder.Property(o => o.OpenserveAmId).HasMaxLength(60);
        builder.Property(o => o.OpenserveBuildingNumId).HasMaxLength(60);
        builder.Property(o => o.OpenserveBuildingName).HasMaxLength(200);
        builder.Property(o => o.OpenserveFloor).HasMaxLength(100);
        builder.Property(o => o.OpenserveUnit).HasMaxLength(60);
        builder.Property(o => o.OpenserveBuildingCandidatesJson).HasColumnType("nvarchar(max)");
        builder.Property(o => o.OpenserveQualificationFailureReason).HasMaxLength(500);
        builder.HasOne<SmartFuture.Domain.Openserve.OpenserveQualificationResult>()
            .WithMany()
            .HasForeignKey(o => o.OpenserveQualificationResultId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(o => o.OpenserveQualificationResultId);
        builder.Property(o => o.OpenserveAutomationPaused).IsRequired().HasDefaultValue(false);
        builder.Property(o => o.OpenserveAutomationPauseReason).HasMaxLength(500);

        builder.Property(o => o.CustomerNotes).HasMaxLength(2000);
        builder.Property(o => o.AdminNotes).HasMaxLength(3000);
        builder.Property(o => o.CancellationReason).HasMaxLength(1000);
        builder.Property(o => o.FailureReason).HasMaxLength(1000);
        builder.Property(o => o.RejectionReason).HasMaxLength(1000);

        builder.Property(o => o.OpenserveActivationReference).HasMaxLength(200);
        builder.Property(o => o.ActivationNotes).HasMaxLength(2000);

        // Customer-selectable billing day. Migration backfills every
        // existing Order to 30 (the launch default) so nullability isn't
        // needed on the column.
        builder.Property(o => o.PreferredBillingDay).IsRequired();
        builder.Property(o => o.FirstProRataInvoiceGeneratedAtUtc);

        builder.HasOne(o => o.ActivatedByUser)
            .WithMany()
            .HasForeignKey(o => o.ActivatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(o => o.User)
            .WithMany()
            .HasForeignKey(o => o.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(o => o.CustomerProfile)
            .WithMany()
            .HasForeignKey(o => o.CustomerProfileId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(o => o.ServicePackage)
            .WithMany()
            .HasForeignKey(o => o.ServicePackageId)
            .OnDelete(DeleteBehavior.SetNull);

        // Selected variant reference. NoAction (not SetNull) — orders are
        // historical/payment records that must never be auto-mutated when a
        // variant changes, and SetNull here would introduce a second cascade
        // path to Orders (via ServicePackages → ServicePackageVariants →
        // Orders) that SQL Server rejects. Variants are disabled, not
        // hard-deleted, once referenced; the PackagePrice / PackageVariantName
        // snapshots preserve what the customer bought regardless.
        builder.HasOne(o => o.ServicePackageVariant)
            .WithMany()
            .HasForeignKey(o => o.ServicePackageVariantId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(o => o.CoverageRequest)
            .WithMany()
            .HasForeignKey(o => o.CoverageRequestId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(o => o.LastStatusChangedByUser)
            .WithMany()
            .HasForeignKey(o => o.LastStatusChangedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(o => o.OrderNumber).IsUnique();
        builder.HasIndex(o => o.UserId);
        builder.HasIndex(o => o.CustomerProfileId);
        builder.HasIndex(o => o.ServicePackageId);
        builder.HasIndex(o => o.ServicePackageVariantId);
        builder.HasIndex(o => o.CoverageRequestId);
        builder.HasIndex(o => o.Status);
        builder.HasIndex(o => o.Source);
        builder.HasIndex(o => o.PackageType);
        builder.HasIndex(o => o.City);
        builder.HasIndex(o => o.Suburb);
        builder.HasIndex(o => o.Province);
        builder.HasIndex(o => o.PostalCode);
        builder.HasIndex(o => o.CreatedAtUtc);
        builder.HasIndex(o => o.SubmittedAtUtc);
        builder.HasIndex(o => o.ConfirmedAtUtc);
        builder.HasIndex(o => o.ExpectedInstallationDateUtc);
        builder.HasIndex(o => o.LastStatusChangedByUserId);
        builder.HasIndex(o => o.NextPayDateUtc);
        builder.HasIndex(o => o.ActivatedByUserId);
    }
}
