using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.ServiceChanges;

namespace SmartFuture.Infrastructure.Data.Configurations.ServiceChanges;

public class ServiceChangeRequestConfiguration : IEntityTypeConfiguration<ServiceChangeRequest>
{
    public void Configure(EntityTypeBuilder<ServiceChangeRequest> builder)
    {
        builder.ToTable("ServiceChangeRequests");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.CreatedAtUtc).IsRequired();
        builder.Property(r => r.UpdatedAtUtc);
        builder.Property(r => r.RowVersion).IsRowVersion();

        builder.Property(r => r.RequestNumber).IsRequired().HasMaxLength(50);

        builder.Property(r => r.UserId).IsRequired();
        builder.Property(r => r.NetworkAccountId).IsRequired();

        builder.Property(r => r.CurrentPackageName).IsRequired().HasMaxLength(150);
        builder.Property(r => r.CurrentMonthlyPrice).HasPrecision(18, 2);
        builder.Property(r => r.CurrentBillingCycle).HasConversion<int>().IsRequired();

        builder.Property(r => r.RequestedPackageId).IsRequired();
        builder.Property(r => r.RequestedPackageName).IsRequired().HasMaxLength(150);
        builder.Property(r => r.RequestedMonthlyPrice).HasPrecision(18, 2);
        builder.Property(r => r.RequestedBillingCycle).HasConversion<int>().IsRequired();

        builder.Property(r => r.ChangeType).HasConversion<int>().IsRequired();
        builder.Property(r => r.EffectiveMode).HasConversion<int>().IsRequired();
        builder.Property(r => r.Status).HasConversion<int>().IsRequired();
        builder.Property(r => r.Source).HasConversion<int>().IsRequired();

        builder.Property(r => r.ProRataAmount).HasPrecision(18, 2);

        builder.Property(r => r.CustomerNotes).HasMaxLength(2000);
        builder.Property(r => r.AdminNotes).HasMaxLength(3000);
        builder.Property(r => r.CancellationReason).HasMaxLength(1000);
        builder.Property(r => r.RejectionReason).HasMaxLength(1000);
        builder.Property(r => r.FailureReason).HasMaxLength(1000);

        builder.HasOne(r => r.User)
            .WithMany()
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.NetworkAccount)
            .WithMany()
            .HasForeignKey(r => r.NetworkAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.Order)
            .WithMany()
            .HasForeignKey(r => r.OrderId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(r => r.RequestedPackage)
            .WithMany()
            .HasForeignKey(r => r.RequestedPackageId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.Invoice)
            .WithMany()
            .HasForeignKey(r => r.InvoiceId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(r => r.Payment)
            .WithMany()
            .HasForeignKey(r => r.PaymentId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(r => r.LastStatusChangedByUser)
            .WithMany()
            .HasForeignKey(r => r.LastStatusChangedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => r.RequestNumber).IsUnique();
        builder.HasIndex(r => r.UserId);
        builder.HasIndex(r => r.NetworkAccountId);
        builder.HasIndex(r => r.OrderId);
        builder.HasIndex(r => r.RequestedPackageId);
        builder.HasIndex(r => r.InvoiceId);
        builder.HasIndex(r => r.Status);
        builder.HasIndex(r => r.ChangeType);
        builder.HasIndex(r => r.EffectiveMode);
        builder.HasIndex(r => r.Source);
        builder.HasIndex(r => r.EffectiveDateUtc);
        builder.HasIndex(r => r.CreatedAtUtc);
    }
}
