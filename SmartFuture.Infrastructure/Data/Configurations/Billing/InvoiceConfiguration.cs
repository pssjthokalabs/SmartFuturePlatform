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

        builder.HasIndex(i => i.InvoiceNumber).IsUnique();
        builder.HasIndex(i => i.OrderId);
        builder.HasIndex(i => i.Status);
        builder.HasIndex(i => i.IssuedAtUtc);
        builder.HasIndex(i => i.DueAtUtc);
        builder.HasIndex(i => i.PaidAtUtc);
        builder.HasIndex(i => i.CreatedAtUtc);

        builder.HasIndex(i => i.ExternalReference)
            .IsUnique()
            .HasFilter("[ExternalReference] IS NOT NULL");
    }
}
