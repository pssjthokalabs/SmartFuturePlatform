using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Jobs;

namespace SmartFuture.Infrastructure.Data.Configurations.Jobs;

public class JobAlertPreferenceConfiguration : IEntityTypeConfiguration<JobAlertPreference>
{
    public void Configure(EntityTypeBuilder<JobAlertPreference> builder)
    {
        builder.ToTable("JobAlertPreferences");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.CreatedAtUtc).IsRequired();
        builder.Property(p => p.UpdatedAtUtc);
        builder.Property(p => p.RowVersion).IsRowVersion();

        builder.Property(p => p.Frequency).HasConversion<int>().IsRequired();
        builder.Property(p => p.CategoriesJson).HasMaxLength(2000);
        builder.Property(p => p.LocationsJson).HasMaxLength(2000);
        builder.Property(p => p.KeywordsJson).HasMaxLength(2000);
        builder.Property(p => p.UnsubscribeToken).HasMaxLength(128);

        builder.HasOne(p => p.User)
            .WithMany()
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(p => p.UserId).IsUnique();
        builder.HasIndex(p => new { p.IsSubscribed, p.Frequency });
        builder.HasIndex(p => p.UnsubscribeToken)
            .IsUnique()
            .HasFilter("[UnsubscribeToken] IS NOT NULL");
    }
}
