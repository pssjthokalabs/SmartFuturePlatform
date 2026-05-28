using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.NetworkAccounts;

namespace SmartFuture.Infrastructure.Data.Configurations.NetworkAccounts;

public class RadiusProfileConfiguration : IEntityTypeConfiguration<RadiusProfile>
{
    public void Configure(EntityTypeBuilder<RadiusProfile> builder)
    {
        builder.ToTable("RadiusProfiles");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.CreatedAtUtc).IsRequired();
        builder.Property(r => r.UpdatedAtUtc);
        builder.Property(r => r.RowVersion).IsRowVersion();

        builder.Property(r => r.Name).IsRequired().HasMaxLength(100);
        builder.Property(r => r.DownloadMbps).IsRequired();
        builder.Property(r => r.UploadMbps).IsRequired();
        builder.Property(r => r.Priority).IsRequired();
        builder.Property(r => r.IsActive).IsRequired();
        builder.Property(r => r.Notes).HasMaxLength(2000);

        builder.HasIndex(r => r.Name).IsUnique();
        builder.HasIndex(r => r.IsActive);
    }
}
