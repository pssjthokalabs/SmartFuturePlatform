using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Billing;

namespace SmartFuture.Infrastructure.Data.Configurations.Billing;

public class PaystackWebhookLogConfiguration : IEntityTypeConfiguration<PaystackWebhookLog>
{
    public void Configure(EntityTypeBuilder<PaystackWebhookLog> builder)
    {
        builder.ToTable("PaystackWebhookLogs");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.CreatedAtUtc).IsRequired();
        builder.Property(p => p.UpdatedAtUtc);
        builder.Property(p => p.RowVersion).IsRowVersion();

        builder.Property(p => p.Provider).HasConversion<int>().IsRequired();
        builder.Property(p => p.ReceivedAtUtc).IsRequired();

        builder.Property(p => p.RawBodyLength).IsRequired();
        builder.Property(p => p.SignaturePresent).IsRequired();
        builder.Property(p => p.SignatureValid).IsRequired();

        builder.Property(p => p.Event).HasMaxLength(80);
        builder.Property(p => p.Reference).HasMaxLength(200);
        builder.Property(p => p.AmountSubunits);
        builder.Property(p => p.Currency).HasMaxLength(3);
        builder.Property(p => p.Status).HasMaxLength(40);

        builder.Property(p => p.PaymentInitiationId);
        builder.Property(p => p.PaymentId);
        builder.Property(p => p.InvoiceId);

        builder.Property(p => p.Accepted).IsRequired();
        builder.Property(p => p.OutcomeMessage).HasMaxLength(500);
        builder.Property(p => p.RejectionReason).HasMaxLength(200);

        builder.Property(p => p.ApplyAttempted).IsRequired();
        builder.Property(p => p.ApplySucceeded).IsRequired();
        builder.Property(p => p.ApplyErrorCode).HasMaxLength(80);
        builder.Property(p => p.ApplyErrorMessage).HasMaxLength(500);

        builder.Property(p => p.HttpStatusReturned).IsRequired();
        builder.Property(p => p.EnvironmentName).HasMaxLength(50);

        builder.HasIndex(p => p.Reference);
        builder.HasIndex(p => p.ReceivedAtUtc);
    }
}
