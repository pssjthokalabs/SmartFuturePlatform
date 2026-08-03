using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Jobs;

namespace SmartFuture.Infrastructure.Data.Configurations.Jobs;

public class JobOpportunityConfiguration : IEntityTypeConfiguration<JobOpportunity>
{
    public void Configure(EntityTypeBuilder<JobOpportunity> builder)
    {
        builder.ToTable("JobOpportunities");

        builder.HasKey(j => j.Id);

        builder.Property(j => j.CreatedAtUtc).IsRequired();
        builder.Property(j => j.UpdatedAtUtc);
        builder.Property(j => j.RowVersion).IsRowVersion();

        builder.Property(j => j.Title).IsRequired().HasMaxLength(300);
        builder.Property(j => j.Slug).IsRequired().HasMaxLength(320);
        builder.Property(j => j.CompanyName).HasMaxLength(200);

        builder.Property(j => j.Location).HasMaxLength(300);
        builder.Property(j => j.Country).HasMaxLength(100);
        builder.Property(j => j.Province).HasMaxLength(100);
        builder.Property(j => j.City).HasMaxLength(100);

        builder.Property(j => j.Category).HasMaxLength(100);
        builder.Property(j => j.WorkplaceType).HasConversion<int>().IsRequired();
        builder.Property(j => j.EmploymentType).HasMaxLength(100);
        builder.Property(j => j.SalaryText).HasMaxLength(300);

        builder.Property(j => j.Summary).HasMaxLength(1000);
        // Crawled descriptions are genuinely long-form; MAX is the honest
        // column type here. The SQLite test fixture strips this
        // annotation (see SqliteTestDbFixture) so tests still bind.
        builder.Property(j => j.DescriptionHtml).HasColumnType("nvarchar(max)");
        builder.Property(j => j.DescriptionText).HasColumnType("nvarchar(max)");
        builder.Property(j => j.RequirementsText).HasColumnType("nvarchar(max)");
        builder.Property(j => j.ApplicationInstructions).HasMaxLength(2000);
        builder.Property(j => j.RawContentSnapshot).HasColumnType("nvarchar(max)");

        builder.Property(j => j.ApplyUrl).HasMaxLength(1000);
        builder.Property(j => j.ApplyEmail).HasMaxLength(256);
        builder.Property(j => j.SourceUrl).HasMaxLength(1000);
        builder.Property(j => j.SourceName).IsRequired().HasMaxLength(150);
        builder.Property(j => j.ExternalId).HasMaxLength(300);
        // SHA-256 hex digest — fixed 64 chars.
        builder.Property(j => j.Fingerprint).IsRequired().HasMaxLength(64);

        builder.Property(j => j.LogoUrl).HasMaxLength(1000);
        builder.Property(j => j.TagsJson).HasMaxLength(2000);

        builder.Property(j => j.Status).HasConversion<int>().IsRequired();

        builder.HasOne(j => j.Source)
            .WithMany(s => s.Jobs)
            .HasForeignKey(j => j.SourceId)
            // Deleting a source must never delete the jobs it produced —
            // they stay readable with SourceName intact.
            .OnDelete(DeleteBehavior.SetNull);

        // The de-duplication contract. UNIQUE so a re-crawl of the same
        // listing can only ever update the existing row.
        builder.HasIndex(j => j.Fingerprint).IsUnique();
        builder.HasIndex(j => j.Slug).IsUnique();

        builder.HasIndex(j => j.Status);
        builder.HasIndex(j => j.SourceId);
        builder.HasIndex(j => j.Category);
        builder.HasIndex(j => j.City);
        builder.HasIndex(j => j.Province);
        builder.HasIndex(j => j.WorkplaceType);
        builder.HasIndex(j => j.ClosingDateUtc);
        builder.HasIndex(j => j.PostedDateUtc);
        builder.HasIndex(j => j.IsFeatured);
        builder.HasIndex(j => j.CreatedAtUtc);
        // Covers the default public query: Active + not-yet-closed,
        // newest first.
        builder.HasIndex(j => new { j.Status, j.ClosingDateUtc, j.PostedDateUtc });
    }
}
