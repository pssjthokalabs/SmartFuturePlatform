using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Jobs;

namespace SmartFuture.Infrastructure.Data.Configurations.Jobs;

public class JobSubscriberProfileConfiguration : IEntityTypeConfiguration<JobSubscriberProfile>
{
    public void Configure(EntityTypeBuilder<JobSubscriberProfile> builder)
    {
        builder.ToTable("JobSubscriberProfiles");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.CreatedAtUtc).IsRequired();
        builder.Property(p => p.UpdatedAtUtc);
        builder.Property(p => p.RowVersion).IsRowVersion();

        builder.Property(p => p.PreferredFirstName).HasMaxLength(100);
        builder.Property(p => p.ContactPhone).HasMaxLength(30);
        builder.Property(p => p.ContactPhoneNormalized).HasMaxLength(30);

        builder.Property(p => p.CurrentCity).HasMaxLength(100);
        builder.Property(p => p.CurrentProvince).HasMaxLength(100);
        builder.Property(p => p.CurrentCountry).HasMaxLength(100);

        builder.Property(p => p.LinkedInProfileUrl).HasMaxLength(500);
        builder.Property(p => p.WebsiteUrl).HasMaxLength(500);
        builder.Property(p => p.SalaryExpectations).HasMaxLength(200);
        builder.Property(p => p.HighestQualification).HasMaxLength(200);

        builder.Property(p => p.PreferredCategoriesJson).HasMaxLength(2000);
        builder.Property(p => p.PreferredLocationsJson).HasMaxLength(2000);

        builder.Property(p => p.CvObjectKey).HasMaxLength(500);
        builder.Property(p => p.CvFileUrl).HasMaxLength(1000);
        builder.Property(p => p.CvFileName).HasMaxLength(300);
        builder.Property(p => p.CvContentType).HasMaxLength(150);

        builder.Property(p => p.CoverLetterObjectKey).HasMaxLength(500);
        builder.Property(p => p.CoverLetterFileUrl).HasMaxLength(1000);
        builder.Property(p => p.CoverLetterFileName).HasMaxLength(300);
        builder.Property(p => p.CoverLetterContentType).HasMaxLength(150);

        builder.HasOne(p => p.User)
            .WithMany()
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Exactly one job profile per user.
        builder.HasIndex(p => p.UserId).IsUnique();
        builder.HasIndex(p => p.CompletedAtUtc);
    }
}
