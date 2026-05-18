using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Identity;

namespace SmartFuture.Infrastructure.Data.Configurations.Identity;

public class VerificationCodeConfiguration : IEntityTypeConfiguration<VerificationCode>
{
    public void Configure(EntityTypeBuilder<VerificationCode> builder)
    {
        builder.ToTable("VerificationCodes");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.UserId).IsRequired();
        builder.Property(c => c.Purpose).IsRequired();
        builder.Property(c => c.Channel).IsRequired();

        builder.Property(c => c.CodeHash)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(c => c.ExpiresAtUtc).IsRequired();
        builder.Property(c => c.ConsumedAtUtc);
        builder.Property(c => c.AttemptCount).IsRequired();
        builder.Property(c => c.MaxAttempts).IsRequired();
        builder.Property(c => c.LastAttemptAtUtc);

        builder.Property(c => c.CreatedAtUtc).IsRequired();
        builder.Property(c => c.UpdatedAtUtc);
        builder.Property(c => c.RowVersion).IsRowVersion();

        builder.HasOne(c => c.User)
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Look up the latest active code for (user, purpose) when a
        // confirm-attempt comes in — composite index supports that path.
        builder.HasIndex(c => new { c.UserId, c.Purpose, c.ExpiresAtUtc });
    }
}
