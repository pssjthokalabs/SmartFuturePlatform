using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Jobs;

namespace SmartFuture.Infrastructure.Data.Configurations.Jobs;

public class JobSubscriberDocumentConfiguration : IEntityTypeConfiguration<JobSubscriberDocument>
{
    public void Configure(EntityTypeBuilder<JobSubscriberDocument> builder)
    {
        builder.ToTable("JobSubscriberDocuments");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.CreatedAtUtc).IsRequired();
        builder.Property(d => d.UpdatedAtUtc);
        builder.Property(d => d.RowVersion).IsRowVersion();

        builder.Property(d => d.DocumentType).HasConversion<int>().IsRequired();
        builder.Property(d => d.ObjectKey).IsRequired().HasMaxLength(500);
        builder.Property(d => d.FileUrl).HasMaxLength(1000);
        builder.Property(d => d.FileName).IsRequired().HasMaxLength(300);
        builder.Property(d => d.ContentType).IsRequired().HasMaxLength(150);

        builder.HasOne(d => d.User)
            .WithMany()
            .HasForeignKey(d => d.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(d => new { d.UserId, d.DocumentType, d.IsCurrent });
        builder.HasIndex(d => d.UploadedAtUtc);
    }
}
