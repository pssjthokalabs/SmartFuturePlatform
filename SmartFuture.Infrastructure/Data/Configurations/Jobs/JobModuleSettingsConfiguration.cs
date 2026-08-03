using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Jobs;

namespace SmartFuture.Infrastructure.Data.Configurations.Jobs;

public class JobModuleSettingsConfiguration : IEntityTypeConfiguration<JobModuleSettings>
{
    public void Configure(EntityTypeBuilder<JobModuleSettings> builder)
    {
        builder.ToTable("JobModuleSettings");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.CreatedAtUtc).IsRequired();
        builder.Property(s => s.UpdatedAtUtc);
        builder.Property(s => s.RowVersion).IsRowVersion();

        builder.Property(s => s.PublicDisclaimer).HasMaxLength(1000);
    }
}
