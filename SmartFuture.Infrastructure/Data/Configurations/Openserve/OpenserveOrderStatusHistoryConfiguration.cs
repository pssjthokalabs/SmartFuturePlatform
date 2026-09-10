using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Openserve;

namespace SmartFuture.Infrastructure.Data.Configurations.Openserve;

public class OpenserveOrderStatusHistoryConfiguration : IEntityTypeConfiguration<OpenserveOrderStatusHistory>
{
    public void Configure(EntityTypeBuilder<OpenserveOrderStatusHistory> builder)
    {
        builder.ToTable("OpenserveOrderStatusHistories");

        builder.HasKey(h => h.Id);

        builder.Property(h => h.CreatedAtUtc).IsRequired();
        builder.Property(h => h.UpdatedAtUtc);
        builder.Property(h => h.RowVersion).IsRowVersion();

        builder.Property(h => h.OpenserveEventId).HasMaxLength(60);
        builder.Property(h => h.CorrelationId).HasMaxLength(120);
        builder.Property(h => h.EventType).HasConversion<int>().IsRequired();

        builder.Property(h => h.PreviousRawState).HasMaxLength(60);
        builder.Property(h => h.NewRawState).HasMaxLength(60);
        builder.Property(h => h.NormalizedStatus).HasConversion<int>().IsRequired();

        builder.Property(h => h.Description).HasMaxLength(2000);
        builder.Property(h => h.InstallationStatus).HasMaxLength(200);
        builder.Property(h => h.ProcessingResult).HasMaxLength(60);
        builder.Property(h => h.ReceivedAtUtc).IsRequired();

        builder.HasOne(h => h.OpenserveOrder)
            .WithMany()
            .HasForeignKey(h => h.OpenserveOrderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(h => h.IntegrationLog)
            .WithMany()
            .HasForeignKey(h => h.IntegrationLogId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(h => h.OpenserveOrderId);
        builder.HasIndex(h => h.OrderId);
        builder.HasIndex(h => h.ReceivedAtUtc);
        builder.HasIndex(h => h.OpenserveEventId);
    }
}
