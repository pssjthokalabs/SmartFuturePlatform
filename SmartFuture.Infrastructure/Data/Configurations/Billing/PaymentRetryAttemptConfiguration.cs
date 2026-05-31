using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Billing;

namespace SmartFuture.Infrastructure.Data.Configurations.Billing;

public class PaymentRetryAttemptConfiguration : IEntityTypeConfiguration<PaymentRetryAttempt>
{
    public void Configure(EntityTypeBuilder<PaymentRetryAttempt> builder)
    {
        builder.ToTable("PaymentRetryAttempts");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.CreatedAtUtc).IsRequired();
        builder.Property(p => p.UpdatedAtUtc);
        builder.Property(p => p.RowVersion).IsRowVersion();

        builder.Property(p => p.Provider).HasConversion<int>().IsRequired();
        builder.Property(p => p.Status).HasConversion<int>().IsRequired();
        builder.Property(p => p.Source).HasConversion<int>().IsRequired();

        builder.Property(p => p.AttemptNumber).IsRequired();
        builder.Property(p => p.Amount).HasPrecision(18, 2).IsRequired();
        builder.Property(p => p.ProviderAmount).HasPrecision(18, 2);

        builder.Property(p => p.FailureReason).HasMaxLength(2000);
        builder.Property(p => p.ProviderReference).HasMaxLength(200);

        builder.Property(p => p.ScheduledForUtc).IsRequired();

        builder.HasOne(p => p.Invoice)
            .WithMany()
            .HasForeignKey(p => p.InvoiceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(p => p.Customer)
            .WithMany()
            .HasForeignKey(p => p.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(p => p.Mandate)
            .WithMany()
            .HasForeignKey(p => p.MandateId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(p => p.Payment)
            .WithMany()
            .HasForeignKey(p => p.PaymentId)
            .OnDelete(DeleteBehavior.SetNull);

        // Composite query the retry worker needs: "find every Pending
        // attempt scheduled at or before now" — covered by a single
        // index on (Status, ScheduledForUtc).
        builder.HasIndex(p => new { p.Status, p.ScheduledForUtc });
        builder.HasIndex(p => p.InvoiceId);
        builder.HasIndex(p => p.CustomerId);
        builder.HasIndex(p => p.MandateId);
        builder.HasIndex(p => p.PaymentId);
        builder.HasIndex(p => p.ProviderReference);
    }
}
