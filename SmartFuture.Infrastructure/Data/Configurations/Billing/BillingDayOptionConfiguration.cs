using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Billing;

namespace SmartFuture.Infrastructure.Data.Configurations.Billing;

public class BillingDayOptionConfiguration : IEntityTypeConfiguration<BillingDayOption>
{
    public void Configure(EntityTypeBuilder<BillingDayOption> builder)
    {
        builder.ToTable("BillingDayOptions");

        builder.HasKey(o => o.Id);

        builder.Property(o => o.CreatedAtUtc).IsRequired();
        builder.Property(o => o.UpdatedAtUtc);
        builder.Property(o => o.RowVersion).IsRowVersion();

        builder.Property(o => o.Day).IsRequired();
        builder.Property(o => o.Label)
            .IsRequired()
            .HasMaxLength(100);
        builder.Property(o => o.IsEnabled).IsRequired();
        builder.Property(o => o.IsDefault).IsRequired();
        builder.Property(o => o.DisplayOrder).IsRequired();

        // Day must be unique — only ONE row per day-of-month value.
        // Migration seeds 15/25/30 and any admin-added values respect
        // this constraint at the DB layer.
        builder.HasIndex(o => o.Day).IsUnique();
        builder.HasIndex(o => o.IsEnabled);
        builder.HasIndex(o => o.DisplayOrder);
    }
}
