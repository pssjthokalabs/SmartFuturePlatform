using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Billing;

namespace SmartFuture.Infrastructure.Data.Configurations.Billing;

public class InvoiceLineItemConfiguration : IEntityTypeConfiguration<InvoiceLineItem>
{
    public void Configure(EntityTypeBuilder<InvoiceLineItem> builder)
    {
        builder.ToTable("InvoiceLineItems");

        builder.HasKey(li => li.Id);

        builder.Property(li => li.InvoiceId).IsRequired();
        builder.Property(li => li.LineType).IsRequired();

        builder.Property(li => li.Description).IsRequired().HasMaxLength(300);

        builder.Property(li => li.Quantity).IsRequired();
        builder.Property(li => li.UnitAmount).HasPrecision(18, 2);
        builder.Property(li => li.TotalAmount).HasPrecision(18, 2);

        builder.Property(li => li.SortOrder).IsRequired();

        builder.Property(li => li.CreatedAtUtc).IsRequired();
        builder.Property(li => li.UpdatedAtUtc);
        builder.Property(li => li.RowVersion).IsRowVersion();

        builder.HasOne(li => li.Invoice)
            .WithMany(i => i.LineItems)
            .HasForeignKey(li => li.InvoiceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(li => new { li.InvoiceId, li.SortOrder });
    }
}
