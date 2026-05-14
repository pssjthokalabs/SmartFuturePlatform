using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Billing;

namespace SmartFuture.Infrastructure.Data.Configurations.Billing;

public class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("Payments");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.CreatedAtUtc).IsRequired();
        builder.Property(p => p.UpdatedAtUtc);
        builder.Property(p => p.RowVersion).IsRowVersion();

        builder.Property(p => p.PaymentNumber)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(p => p.InvoiceId).IsRequired();
        builder.Property(p => p.Status).HasConversion<int>().IsRequired();
        builder.Property(p => p.Method).HasConversion<int>().IsRequired();

        builder.Property(p => p.Amount).HasPrecision(18, 2);
        builder.Property(p => p.CurrencyCode).IsRequired().HasMaxLength(3);

        builder.Property(p => p.GatewayName).HasMaxLength(100);
        builder.Property(p => p.GatewayReference).HasMaxLength(200);
        builder.Property(p => p.GatewayTransactionId).HasMaxLength(200);
        builder.Property(p => p.ExternalReference).HasMaxLength(100);
        builder.Property(p => p.FailureReason).HasMaxLength(1000);
        builder.Property(p => p.Notes).HasMaxLength(2000);
        builder.Property(p => p.AdminNotes).HasMaxLength(3000);

        builder.HasOne(p => p.Invoice)
            .WithMany()
            .HasForeignKey(p => p.InvoiceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(p => p.LastStatusChangedByUser)
            .WithMany()
            .HasForeignKey(p => p.LastStatusChangedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => p.PaymentNumber).IsUnique();
        builder.HasIndex(p => p.InvoiceId);
        builder.HasIndex(p => p.Status);
        builder.HasIndex(p => p.Method);
        builder.HasIndex(p => p.PaidAtUtc);
        builder.HasIndex(p => p.FailedAtUtc);
        builder.HasIndex(p => p.RefundedAtUtc);
        builder.HasIndex(p => p.CreatedAtUtc);
        builder.HasIndex(p => p.GatewayReference);
        builder.HasIndex(p => p.GatewayTransactionId);

        builder.HasIndex(p => p.ExternalReference)
            .IsUnique()
            .HasFilter("[ExternalReference] IS NOT NULL");
    }
}
