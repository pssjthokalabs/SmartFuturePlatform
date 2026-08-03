using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Jobs;

namespace SmartFuture.Infrastructure.Data.Configurations.Jobs;

public class JobImportRunConfiguration : IEntityTypeConfiguration<JobImportRun>
{
    public void Configure(EntityTypeBuilder<JobImportRun> builder)
    {
        builder.ToTable("JobImportRuns");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.CreatedAtUtc).IsRequired();
        builder.Property(r => r.UpdatedAtUtc);
        builder.Property(r => r.RowVersion).IsRowVersion();

        builder.Property(r => r.SourceName).IsRequired().HasMaxLength(150);
        builder.Property(r => r.Status).HasConversion<int>().IsRequired();
        builder.Property(r => r.Trigger).HasConversion<int>().IsRequired();
        builder.Property(r => r.FailureMessage).HasMaxLength(2000);
        builder.Property(r => r.Notes).HasMaxLength(4000);

        builder.HasOne(r => r.Source)
            .WithMany()
            .HasForeignKey(r => r.SourceId)
            // Keep the history when a source is removed.
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(r => r.SourceId);
        builder.HasIndex(r => r.Status);
        builder.HasIndex(r => r.StartedAtUtc);
    }
}
