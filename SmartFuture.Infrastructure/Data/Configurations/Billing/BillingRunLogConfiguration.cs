using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Billing;

namespace SmartFuture.Infrastructure.Data.Configurations.Billing;

public class BillingRunLogConfiguration : IEntityTypeConfiguration<BillingRunLog>
{
    public void Configure(EntityTypeBuilder<BillingRunLog> builder)
    {
        builder.ToTable("BillingRunLogs");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.CreatedAtUtc).IsRequired();
        builder.Property(r => r.UpdatedAtUtc);
        builder.Property(r => r.RowVersion).IsRowVersion();

        builder.Property(r => r.Status).HasConversion<int>().IsRequired();
        builder.Property(r => r.TriggeredBy).HasConversion<int>().IsRequired();
        builder.Property(r => r.DryRun).IsRequired();

        builder.Property(r => r.StartedAtUtc).IsRequired();

        builder.Property(r => r.MachineName).HasMaxLength(200);
        builder.Property(r => r.InstanceId).IsRequired().HasMaxLength(64);

        builder.Property(r => r.SummaryJson).HasMaxLength(4000);
        builder.Property(r => r.ErrorText).HasMaxLength(4000);

        // Lock check ("any non-stale Running row?") + recent-runs listing.
        builder.HasIndex(r => new { r.Status, r.StartedAtUtc });
        builder.HasIndex(r => r.StartedAtUtc);
    }
}
