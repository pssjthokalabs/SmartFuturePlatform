using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Auditing;

namespace SmartFuture.Infrastructure.Data.Configurations.Auditing;

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.CreatedAtUtc).IsRequired();
        builder.Property(a => a.UpdatedAtUtc);
        builder.Property(a => a.RowVersion).IsRowVersion();

        builder.Property(a => a.ActorType).HasConversion<int>().IsRequired();
        builder.Property(a => a.ActionType).HasConversion<int>().IsRequired();
        builder.Property(a => a.EntityType).HasConversion<int>().IsRequired();

        builder.Property(a => a.EntityName).HasMaxLength(200);
        builder.Property(a => a.Summary).HasMaxLength(1000);

        builder.Property(a => a.MetadataJson).HasColumnType("nvarchar(max)");

        builder.Property(a => a.IpAddress).HasMaxLength(100);
        builder.Property(a => a.UserAgent).HasMaxLength(500);

        builder.Property(a => a.IsSuccess).IsRequired();
        builder.Property(a => a.FailureReason).HasMaxLength(1000);

        builder.HasOne(a => a.ActorUser)
            .WithMany()
            .HasForeignKey(a => a.ActorUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(a => a.ActorUserId);
        builder.HasIndex(a => a.ActorType);
        builder.HasIndex(a => a.ActionType);
        builder.HasIndex(a => a.EntityType);
        builder.HasIndex(a => a.EntityId);
        builder.HasIndex(a => a.IsSuccess);
        builder.HasIndex(a => a.CreatedAtUtc);
    }
}
