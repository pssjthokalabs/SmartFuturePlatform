using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Notifications;

namespace SmartFuture.Infrastructure.Data.Configurations.Notifications;

public class OutboundNotificationConfiguration : IEntityTypeConfiguration<OutboundNotification>
{
    public void Configure(EntityTypeBuilder<OutboundNotification> builder)
    {
        builder.ToTable("OutboundNotifications");

        builder.HasKey(n => n.Id);

        builder.Property(n => n.CreatedAtUtc).IsRequired();
        builder.Property(n => n.UpdatedAtUtc);
        builder.Property(n => n.RowVersion).IsRowVersion();

        builder.Property(n => n.Channel).HasConversion<int>().IsRequired();
        builder.Property(n => n.Type).HasConversion<int>().IsRequired();
        builder.Property(n => n.Status).HasConversion<int>().IsRequired();

        builder.Property(n => n.RecipientEmail).HasMaxLength(256);
        builder.Property(n => n.RecipientPhone).HasMaxLength(50);

        builder.Property(n => n.Subject).HasMaxLength(300);
        builder.Property(n => n.Body).IsRequired().HasMaxLength(8000);

        builder.Property(n => n.ProviderName).HasMaxLength(100);
        builder.Property(n => n.ProviderMessageId).HasMaxLength(200);
        builder.Property(n => n.FailureReason).HasMaxLength(2000);

        builder.Property(n => n.AttemptCount).IsRequired();
        builder.Property(n => n.LastAttemptAtUtc);
        builder.Property(n => n.SentAtUtc);

        builder.Property(n => n.RelatedEntityType).HasMaxLength(100);
        builder.Property(n => n.MetadataJson).HasColumnType("nvarchar(max)");

        builder.HasOne(n => n.User)
            .WithMany()
            .HasForeignKey(n => n.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(n => n.UserId);
        builder.HasIndex(n => n.Channel);
        builder.HasIndex(n => n.Type);
        builder.HasIndex(n => n.Status);
        builder.HasIndex(n => n.RecipientEmail);
        builder.HasIndex(n => n.RecipientPhone);
        builder.HasIndex(n => new { n.RelatedEntityType, n.RelatedEntityId });
        builder.HasIndex(n => n.SentAtUtc);
        builder.HasIndex(n => n.CreatedAtUtc);
    }
}
