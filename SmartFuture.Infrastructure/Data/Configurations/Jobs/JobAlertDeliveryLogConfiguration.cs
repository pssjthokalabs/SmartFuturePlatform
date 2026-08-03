using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Jobs;

namespace SmartFuture.Infrastructure.Data.Configurations.Jobs;

public class JobAlertDeliveryLogConfiguration : IEntityTypeConfiguration<JobAlertDeliveryLog>
{
    public void Configure(EntityTypeBuilder<JobAlertDeliveryLog> builder)
    {
        builder.ToTable("JobAlertDeliveryLogs");

        builder.HasKey(l => l.Id);

        builder.Property(l => l.CreatedAtUtc).IsRequired();
        builder.Property(l => l.UpdatedAtUtc);
        builder.Property(l => l.RowVersion).IsRowVersion();

        builder.Property(l => l.Status).HasConversion<int>().IsRequired();
        builder.Property(l => l.Frequency).HasConversion<int>().IsRequired();
        builder.Property(l => l.RecipientEmail).HasMaxLength(256);
        builder.Property(l => l.Subject).HasMaxLength(300);
        builder.Property(l => l.FailureMessage).HasMaxLength(2000);

        builder.HasOne(l => l.User)
            .WithMany()
            .HasForeignKey(l => l.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(l => new { l.UserId, l.WindowStartUtc });
        builder.HasIndex(l => l.Status);
        builder.HasIndex(l => l.CreatedAtUtc);
    }
}
