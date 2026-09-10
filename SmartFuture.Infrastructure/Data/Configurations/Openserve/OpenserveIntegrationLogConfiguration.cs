using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Openserve;

namespace SmartFuture.Infrastructure.Data.Configurations.Openserve;

public class OpenserveIntegrationLogConfiguration : IEntityTypeConfiguration<OpenserveIntegrationLog>
{
    public void Configure(EntityTypeBuilder<OpenserveIntegrationLog> builder)
    {
        builder.ToTable("OpenserveIntegrationLogs");

        builder.HasKey(l => l.Id);

        builder.Property(l => l.CreatedAtUtc).IsRequired();
        builder.Property(l => l.UpdatedAtUtc);
        builder.Property(l => l.RowVersion).IsRowVersion();

        builder.Property(l => l.Direction).HasConversion<int>().IsRequired();
        builder.Property(l => l.OperationType).HasConversion<int>().IsRequired();

        builder.Property(l => l.MessageId).HasMaxLength(64);
        builder.Property(l => l.CorrelationId).HasMaxLength(120);
        builder.Property(l => l.HttpMethod).HasMaxLength(10);
        builder.Property(l => l.Endpoint).HasMaxLength(500);

        builder.Property(l => l.RequestHeadersJson).HasMaxLength(4000);
        builder.Property(l => l.RequestBodyJson).HasColumnType("nvarchar(max)");
        builder.Property(l => l.ResponseBodyJson).HasColumnType("nvarchar(max)");
        builder.Property(l => l.ErrorSummary).HasMaxLength(2000);

        builder.Property(l => l.OccurredAtUtc).IsRequired();

        builder.HasOne(l => l.OpenserveOrder)
            .WithMany()
            .HasForeignKey(l => l.OpenserveOrderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(l => l.OpenserveOrderId);
        builder.HasIndex(l => l.OccurredAtUtc);
        builder.HasIndex(l => l.OperationType);
        builder.HasIndex(l => l.MessageId);
    }
}
