using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Openserve;

namespace SmartFuture.Infrastructure.Data.Configurations.Openserve;

public class OpenserveIntegrationConfigConfiguration : IEntityTypeConfiguration<OpenserveIntegrationConfig>
{
    public void Configure(EntityTypeBuilder<OpenserveIntegrationConfig> builder)
    {
        builder.ToTable("OpenserveIntegrationConfigs");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.CreatedAtUtc).IsRequired();
        builder.Property(c => c.UpdatedAtUtc);
        builder.Property(c => c.RowVersion).IsRowVersion();

        builder.Property(c => c.BaseUrl).HasMaxLength(300);
        builder.Property(c => c.WsIspCode).HasMaxLength(60);
        builder.Property(c => c.IspIdentifier).HasMaxLength(60);
        builder.Property(c => c.SenderId).HasMaxLength(60);
        builder.Property(c => c.ReplyToAddress).HasMaxLength(300);
        builder.Property(c => c.EventNotificationUrl).HasMaxLength(300);
        builder.Property(c => c.AllowedIpRangesCsv).HasMaxLength(1000);

        // Ciphertext, not plaintext — DataProtection blobs run longer
        // than the source secret; nvarchar(max) avoids a silent
        // truncation failure mode.
        builder.Property(c => c.ApiKeyProtected).HasColumnType("nvarchar(max)");
        builder.Property(c => c.SharedSecretProtected).HasColumnType("nvarchar(max)");

        builder.HasOne(c => c.UpdatedByUser)
            .WithMany()
            .HasForeignKey(c => c.UpdatedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
