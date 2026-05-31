using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Billing;

namespace SmartFuture.Infrastructure.Data.Configurations.Billing;

public class PaymentInitiationConfiguration : IEntityTypeConfiguration<PaymentInitiation>
{
    public void Configure(EntityTypeBuilder<PaymentInitiation> builder)
    {
        builder.ToTable("PaymentInitiations");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.CreatedAtUtc).IsRequired();
        builder.Property(p => p.UpdatedAtUtc);
        builder.Property(p => p.RowVersion).IsRowVersion();

        builder.Property(p => p.Provider).HasConversion<int>().IsRequired();
        builder.Property(p => p.Status).HasConversion<int>().IsRequired();
        builder.Property(p => p.WebhookApplyMode).HasConversion<int>().IsRequired();
        builder.Property(p => p.WebhookLastReceivedAtUtc);
        builder.Property(p => p.IsTestAmountOverrideApplied).IsRequired();
        builder.Property(p => p.ActualProviderAmount).HasPrecision(18, 2);
        builder.Property(p => p.InvoiceAmountAtTime).HasPrecision(18, 2);

        builder.Property(p => p.Amount).HasPrecision(18, 2);
        builder.Property(p => p.CurrencyCode).IsRequired().HasMaxLength(3);

        builder.Property(p => p.ProviderReference).HasMaxLength(200);
        builder.Property(p => p.ProviderCheckoutId).HasMaxLength(200);
        builder.Property(p => p.RedirectUrl).HasMaxLength(1000);
        builder.Property(p => p.SuccessUrl).HasMaxLength(1000);
        builder.Property(p => p.CancelUrl).HasMaxLength(1000);
        builder.Property(p => p.FailureUrl).HasMaxLength(1000);

        builder.Property(p => p.FailureReason).HasMaxLength(2000);
        builder.Property(p => p.MetadataJson).HasColumnType("nvarchar(max)");

        builder.HasOne(p => p.Invoice)
            .WithMany()
            .HasForeignKey(p => p.InvoiceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(p => p.Payment)
            .WithMany()
            .HasForeignKey(p => p.PaymentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => p.InvoiceId);
        builder.HasIndex(p => p.PaymentId);
        builder.HasIndex(p => p.Provider);
        builder.HasIndex(p => p.Status);
        builder.HasIndex(p => p.ProviderReference);
        builder.HasIndex(p => p.ProviderCheckoutId);
        builder.HasIndex(p => p.CreatedAtUtc);
        builder.HasIndex(p => p.ExpiresAtUtc);
    }
}
