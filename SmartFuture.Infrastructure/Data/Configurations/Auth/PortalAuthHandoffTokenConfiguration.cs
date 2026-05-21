using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Auth;

namespace SmartFuture.Infrastructure.Data.Configurations.Auth;

public class PortalAuthHandoffTokenConfiguration : IEntityTypeConfiguration<PortalAuthHandoffToken>
{
    public void Configure(EntityTypeBuilder<PortalAuthHandoffToken> builder)
    {
        builder.ToTable("PortalAuthHandoffTokens");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.CreatedAtUtc).IsRequired();
        builder.Property(t => t.UpdatedAtUtc);
        builder.Property(t => t.RowVersion).IsRowVersion();

        builder.Property(t => t.TokenHash).IsRequired().HasMaxLength(128);
        builder.Property(t => t.UserId).IsRequired();
        builder.Property(t => t.IntentToken).HasMaxLength(128);
        builder.Property(t => t.Purpose).HasConversion<int>().IsRequired();
        builder.Property(t => t.ExpiresAtUtc).IsRequired();

        builder.Property(t => t.CreatedIpAddress).HasMaxLength(64);
        builder.Property(t => t.CreatedUserAgent).HasMaxLength(512);
        builder.Property(t => t.ConsumedIpAddress).HasMaxLength(64);
        builder.Property(t => t.ConsumedUserAgent).HasMaxLength(512);

        builder.HasOne(t => t.User)
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => t.UserId);
        builder.HasIndex(t => t.ExpiresAtUtc);
        builder.HasIndex(t => t.ConsumedAtUtc);
    }
}
