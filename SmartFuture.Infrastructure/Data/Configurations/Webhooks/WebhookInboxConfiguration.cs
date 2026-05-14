using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Webhooks;

namespace SmartFuture.Infrastructure.Data.Configurations.Webhooks;

public class WebhookInboxConfiguration : IEntityTypeConfiguration<WebhookInbox>
{
    public void Configure(EntityTypeBuilder<WebhookInbox> builder)
    {
        builder.ToTable("WebhookInboxes");

        builder.HasKey(w => w.Id);

        builder.Property(w => w.CreatedAtUtc).IsRequired();
        builder.Property(w => w.UpdatedAtUtc);
        builder.Property(w => w.RowVersion).IsRowVersion();

        builder.Property(w => w.Provider).HasConversion<int>().IsRequired();
        builder.Property(w => w.EventType).HasConversion<int>().IsRequired();
        builder.Property(w => w.Status).HasConversion<int>().IsRequired();

        builder.Property(w => w.ProviderName).IsRequired().HasMaxLength(100);
        builder.Property(w => w.ProviderEventId).HasMaxLength(200);
        builder.Property(w => w.RawPayloadHash).HasMaxLength(128);
        builder.Property(w => w.SignatureHeader).HasMaxLength(500);
        builder.Property(w => w.IdempotencyKey).HasMaxLength(300);

        builder.Property(w => w.PaymentReference).HasMaxLength(200);
        builder.Property(w => w.InvoiceReference).HasMaxLength(200);
        builder.Property(w => w.OrderReference).HasMaxLength(200);
        builder.Property(w => w.GatewayTransactionId).HasMaxLength(200);

        builder.Property(w => w.Amount).HasPrecision(18, 2);
        builder.Property(w => w.CurrencyCode).HasMaxLength(3);

        builder.Property(w => w.FailureReason).HasMaxLength(2000);
        builder.Property(w => w.ParsedSummaryJson).HasColumnType("nvarchar(max)");

        builder.HasOne(w => w.Payment)
            .WithMany()
            .HasForeignKey(w => w.PaymentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(w => w.Invoice)
            .WithMany()
            .HasForeignKey(w => w.InvoiceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(w => w.Provider);
        builder.HasIndex(w => w.ProviderName);
        builder.HasIndex(w => w.ProviderEventId);
        builder.HasIndex(w => w.Status);
        builder.HasIndex(w => w.EventType);
        builder.HasIndex(w => w.RawPayloadHash);
        builder.HasIndex(w => w.IdempotencyKey);
        builder.HasIndex(w => w.PaymentReference);
        builder.HasIndex(w => w.InvoiceReference);
        builder.HasIndex(w => w.OrderReference);
        builder.HasIndex(w => w.GatewayTransactionId);
        builder.HasIndex(w => w.PaymentId);
        builder.HasIndex(w => w.InvoiceId);
        builder.HasIndex(w => w.ProcessedAtUtc);
        builder.HasIndex(w => w.CreatedAtUtc);

        builder.HasIndex(w => new { w.ProviderName, w.ProviderEventId })
            .IsUnique()
            .HasFilter("[ProviderEventId] IS NOT NULL");

        builder.HasIndex(w => new { w.ProviderName, w.IdempotencyKey })
            .IsUnique()
            .HasFilter("[IdempotencyKey] IS NOT NULL");
    }
}
