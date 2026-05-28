using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.NetworkAccounts;

namespace SmartFuture.Infrastructure.Data.Configurations.NetworkAccounts;

public class ProvisioningEventConfiguration : IEntityTypeConfiguration<ProvisioningEvent>
{
    public void Configure(EntityTypeBuilder<ProvisioningEvent> builder)
    {
        builder.ToTable("ProvisioningEvents");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.CreatedAtUtc).IsRequired();
        builder.Property(e => e.UpdatedAtUtc);
        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.Property(e => e.NetworkAccountId).IsRequired();
        builder.Property(e => e.EventType).HasConversion<int>().IsRequired();
        builder.Property(e => e.ProviderName).IsRequired().HasMaxLength(60);
        builder.Property(e => e.ProviderReference).HasMaxLength(200);
        builder.Property(e => e.IsSuccess).IsRequired();
        builder.Property(e => e.FailureReason).HasMaxLength(2000);
        builder.Property(e => e.Summary).HasMaxLength(500);
        builder.Property(e => e.MetadataJson).HasMaxLength(4000);

        builder.HasOne(e => e.NetworkAccount)
            .WithMany()
            .HasForeignKey(e => e.NetworkAccountId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.TriggeredByUser)
            .WithMany()
            .HasForeignKey(e => e.TriggeredByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => e.NetworkAccountId);
        builder.HasIndex(e => e.EventType);
        builder.HasIndex(e => e.IsSuccess);
        builder.HasIndex(e => e.CreatedAtUtc);
    }
}
