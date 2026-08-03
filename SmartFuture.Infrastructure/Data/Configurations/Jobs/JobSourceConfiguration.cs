using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Jobs;

namespace SmartFuture.Infrastructure.Data.Configurations.Jobs;

public class JobSourceConfiguration : IEntityTypeConfiguration<JobSource>
{
    public void Configure(EntityTypeBuilder<JobSource> builder)
    {
        builder.ToTable("JobSources");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.CreatedAtUtc).IsRequired();
        builder.Property(s => s.UpdatedAtUtc);
        builder.Property(s => s.RowVersion).IsRowVersion();

        builder.Property(s => s.SourceName).IsRequired().HasMaxLength(150);
        builder.Property(s => s.SourceUrl).IsRequired().HasMaxLength(1000);
        builder.Property(s => s.SourceType).HasConversion<int>().IsRequired();

        builder.Property(s => s.Notes).HasMaxLength(2000);
        builder.Property(s => s.DefaultCategory).HasMaxLength(100);
        builder.Property(s => s.DefaultLocation).HasMaxLength(200);
        builder.Property(s => s.LastFailureMessage).HasMaxLength(2000);

        // Same source URL must not be configured twice — a duplicate
        // would double-crawl and churn the same fingerprints.
        builder.HasIndex(s => s.SourceUrl).IsUnique();
        builder.HasIndex(s => s.IsActive);
        builder.HasIndex(s => s.SourceType);
        builder.HasIndex(s => s.LastCheckedAtUtc);
    }
}
