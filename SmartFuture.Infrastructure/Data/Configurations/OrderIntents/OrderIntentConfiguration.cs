using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.OrderIntents;

namespace SmartFuture.Infrastructure.Data.Configurations.OrderIntents;

public class OrderIntentConfiguration : IEntityTypeConfiguration<OrderIntent>
{
    public void Configure(EntityTypeBuilder<OrderIntent> builder)
    {
        builder.ToTable("OrderIntents");

        builder.HasKey(o => o.Id);

        builder.Property(o => o.CreatedAtUtc).IsRequired();
        builder.Property(o => o.UpdatedAtUtc);
        builder.Property(o => o.RowVersion).IsRowVersion();

        builder.Property(o => o.IntentToken).IsRequired().HasMaxLength(128);

        builder.Property(o => o.ServicePackageId).IsRequired();

        builder.Property(o => o.Status).HasConversion<int>().IsRequired();

        builder.Property(o => o.FullName).HasMaxLength(200);
        builder.Property(o => o.Email).HasMaxLength(256);
        builder.Property(o => o.PhoneNumber).HasMaxLength(50);

        builder.Property(o => o.AddressLine1).HasMaxLength(250);
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

        builder.Property(o => o.CustomerNotes).HasMaxLength(2000);
        builder.Property(o => o.Source).HasMaxLength(50);

        // Phase 9 — legal consent. Short version strings (YYYY-MM)
        // capped tightly so a typo in the website never balloons the
        // column. Timestamps are plain UTC datetimes.
        builder.Property(o => o.TermsVersion).HasMaxLength(20);
        builder.Property(o => o.PrivacyVersion).HasMaxLength(20);

        builder.Property(o => o.ExpiresAtUtc).IsRequired();

        builder.HasOne(o => o.ServicePackage)
            .WithMany()
            .HasForeignKey(o => o.ServicePackageId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(o => o.ClaimedByUser)
            .WithMany()
            .HasForeignKey(o => o.ClaimedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(o => o.ConvertedOrder)
            .WithMany()
            .HasForeignKey(o => o.ConvertedOrderId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(o => o.IntentToken).IsUnique();
        builder.HasIndex(o => o.ServicePackageId);
        builder.HasIndex(o => o.Email);
        builder.HasIndex(o => o.Status);
        builder.HasIndex(o => o.ExpiresAtUtc);
        builder.HasIndex(o => o.ClaimedByUserId);
        builder.HasIndex(o => o.ConvertedOrderId);
    }
}
