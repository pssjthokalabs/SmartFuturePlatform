using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Openserve;
using SmartFuture.Shared.Enums.Openserve;

namespace SmartFuture.Infrastructure.Data.Configurations.Openserve;

public class OpenserveOrderConfiguration : IEntityTypeConfiguration<OpenserveOrder>
{
    public void Configure(EntityTypeBuilder<OpenserveOrder> builder)
    {
        builder.ToTable("OpenserveOrders");

        builder.HasKey(o => o.Id);

        builder.Property(o => o.CreatedAtUtc).IsRequired();
        builder.Property(o => o.UpdatedAtUtc);
        builder.Property(o => o.RowVersion).IsRowVersion();

        builder.Property(o => o.ExternalReferenceNumber).IsRequired().HasMaxLength(120);
        builder.Property(o => o.LastMessageId).HasMaxLength(64);
        builder.Property(o => o.CorrelationId).HasMaxLength(120);

        builder.Property(o => o.OpenserveOrderId).HasMaxLength(60);
        builder.Property(o => o.OpenserveOrderName).HasMaxLength(60);
        builder.Property(o => o.SubscriberReferenceNumber).HasMaxLength(120);

        builder.Property(o => o.OrderType).IsRequired().HasMaxLength(60);
        builder.Property(o => o.Reason).HasMaxLength(60);
        builder.Property(o => o.RawState).HasMaxLength(60);
        builder.Property(o => o.NormalizedStatus).HasConversion<int>().IsRequired();

        builder.Property(o => o.LastFailureCode).HasMaxLength(120);
        builder.Property(o => o.LastFailureMessage).HasMaxLength(2000);
        builder.Property(o => o.LastFailureClass).HasConversion<int>().IsRequired().HasDefaultValue(OpenserveSubmissionFailureClass.None);
        builder.Property(o => o.LastSubmissionTrigger).HasConversion<int?>();
        builder.Property(o => o.AutomaticRetryCount).IsRequired().HasDefaultValue(0);

        builder.HasOne(o => o.Order)
            .WithMany()
            .HasForeignKey(o => o.OrderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(o => o.PackageOpenserveMapping)
            .WithMany()
            .HasForeignKey(o => o.PackageOpenserveMappingId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(o => o.ExternalReferenceNumber).IsUnique();
        builder.HasIndex(o => o.OrderId);
        builder.HasIndex(o => o.OpenserveOrderId);
        builder.HasIndex(o => o.NormalizedStatus);
        builder.HasIndex(o => o.IsTerminal);
        builder.HasIndex(o => o.NextAutomaticRetryAtUtc);
    }
}
