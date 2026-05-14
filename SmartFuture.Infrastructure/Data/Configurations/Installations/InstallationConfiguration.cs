using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Installations;

namespace SmartFuture.Infrastructure.Data.Configurations.Installations;

public class InstallationConfiguration : IEntityTypeConfiguration<Installation>
{
    public void Configure(EntityTypeBuilder<Installation> builder)
    {
        builder.ToTable("Installations");

        builder.HasKey(i => i.Id);

        builder.Property(i => i.CreatedAtUtc).IsRequired();
        builder.Property(i => i.UpdatedAtUtc);
        builder.Property(i => i.RowVersion).IsRowVersion();

        builder.Property(i => i.InstallationNumber)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(i => i.OrderId).IsRequired();
        builder.Property(i => i.Status).HasConversion<int>().IsRequired();
        builder.Property(i => i.Source).HasConversion<int>().IsRequired();

        builder.Property(i => i.TechnicianName).HasMaxLength(200);
        builder.Property(i => i.TechnicianPhone).HasMaxLength(50);
        builder.Property(i => i.TechnicianEmail).HasMaxLength(256);

        builder.Property(i => i.AddressLine1).IsRequired().HasMaxLength(250);
        builder.Property(i => i.AddressLine2).HasMaxLength(250);
        builder.Property(i => i.Suburb).HasMaxLength(150);
        builder.Property(i => i.City).HasMaxLength(150);
        builder.Property(i => i.Province).HasMaxLength(150);
        builder.Property(i => i.PostalCode).HasMaxLength(30);
        builder.Property(i => i.Country).HasMaxLength(100);

        builder.Property(i => i.Latitude).HasPrecision(9, 6);
        builder.Property(i => i.Longitude).HasPrecision(9, 6);

        builder.Property(i => i.GooglePlaceId).HasMaxLength(200);
        builder.Property(i => i.MapProviderReference).HasMaxLength(300);

        builder.Property(i => i.CustomerNotes).HasMaxLength(2000);
        builder.Property(i => i.AdminNotes).HasMaxLength(3000);
        builder.Property(i => i.TechnicianNotes).HasMaxLength(3000);
        builder.Property(i => i.CompletionNotes).HasMaxLength(3000);
        builder.Property(i => i.FailureReason).HasMaxLength(1000);
        builder.Property(i => i.CancellationReason).HasMaxLength(1000);

        builder.HasOne(i => i.Order)
            .WithMany()
            .HasForeignKey(i => i.OrderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(i => i.TechnicianUser)
            .WithMany()
            .HasForeignKey(i => i.TechnicianUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(i => i.LastStatusChangedByUser)
            .WithMany()
            .HasForeignKey(i => i.LastStatusChangedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(i => i.InstallationNumber).IsUnique();
        builder.HasIndex(i => i.OrderId);
        builder.HasIndex(i => i.Status);
        builder.HasIndex(i => i.Source);
        builder.HasIndex(i => i.ScheduledForUtc);
        builder.HasIndex(i => i.CompletedAtUtc);
        builder.HasIndex(i => i.CancelledAtUtc);
        builder.HasIndex(i => i.FailedAtUtc);
        builder.HasIndex(i => i.TechnicianUserId);
        builder.HasIndex(i => i.LastStatusChangedByUserId);
        builder.HasIndex(i => i.City);
        builder.HasIndex(i => i.Suburb);
        builder.HasIndex(i => i.Province);
        builder.HasIndex(i => i.PostalCode);
        builder.HasIndex(i => i.CreatedAtUtc);
    }
}
