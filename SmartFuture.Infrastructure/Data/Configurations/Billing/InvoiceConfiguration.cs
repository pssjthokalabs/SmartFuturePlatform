using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Billing;

namespace SmartFuture.Infrastructure.Data.Configurations.Billing;

public class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("Invoices");

        builder.HasKey(i => i.Id);

        builder.Property(i => i.CreatedAtUtc).IsRequired();
        builder.Property(i => i.UpdatedAtUtc);
        builder.Property(i => i.RowVersion).IsRowVersion();

        builder.Property(i => i.InvoiceNumber)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(i => i.OrderId).IsRequired();
        builder.Property(i => i.Status).HasConversion<int>().IsRequired();

        builder.Property(i => i.SubtotalAmount).HasPrecision(18, 2);
        builder.Property(i => i.TaxAmount).HasPrecision(18, 2);
        builder.Property(i => i.TotalAmount).HasPrecision(18, 2);
        builder.Property(i => i.AmountPaid).HasPrecision(18, 2);
        builder.Property(i => i.BalanceDue).HasPrecision(18, 2);

        builder.Property(i => i.CurrencyCode).IsRequired().HasMaxLength(3);

        builder.Property(i => i.Notes).HasMaxLength(2000);
        builder.Property(i => i.AdminNotes).HasMaxLength(3000);
        builder.Property(i => i.ExternalReference).HasMaxLength(100);

        builder.HasOne(i => i.Order)
            .WithMany()
            .HasForeignKey(i => i.OrderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(i => i.LastStatusChangedByUser)
            .WithMany()
            .HasForeignKey(i => i.LastStatusChangedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // ─── Phase 0A — recurring-billing period linkage (additive) ────────
        //
        // Nullable FK to the per-service schedule + the billing period this
        // invoice covers. Nothing writes these in Phase 0A. The unique
        // (ServiceBillingScheduleId, PeriodStartUtc) duplicate-invoice guard
        // is intentionally DEFERRED to Phase 0B, when the generator actually
        // populates these columns.
        builder.HasOne(i => i.ServiceBillingSchedule)
            .WithMany()
            .HasForeignKey(i => i.ServiceBillingScheduleId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(i => i.InvoiceNumber).IsUnique();
        builder.HasIndex(i => i.OrderId);
        builder.HasIndex(i => i.Status);
        builder.HasIndex(i => i.IssuedAtUtc);
        builder.HasIndex(i => i.DueAtUtc);
        builder.HasIndex(i => i.PaidAtUtc);
        builder.HasIndex(i => i.CreatedAtUtc);
        builder.HasIndex(i => i.ServiceBillingScheduleId);

        // Phase 0B — duplicate-invoice guard for the recurring generator:
        // at most one invoice per (schedule, billing-period start). Filtered
        // so it never constrains the many existing invoices that have NULL
        // schedule/period (mirrors the ExternalReference filtered-unique
        // pattern above). App-level guard in RecurringInvoiceGenerator backs
        // this up.
        builder.HasIndex(i => new { i.ServiceBillingScheduleId, i.PeriodStartUtc })
            .IsUnique()
            .HasFilter("[ServiceBillingScheduleId] IS NOT NULL AND [PeriodStartUtc] IS NOT NULL");

        builder.HasIndex(i => i.ExternalReference)
            .IsUnique()
            .HasFilter("[ExternalReference] IS NOT NULL");
    }
}
