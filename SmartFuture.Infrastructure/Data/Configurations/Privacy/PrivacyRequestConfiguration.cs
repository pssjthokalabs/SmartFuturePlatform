using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Privacy;

namespace SmartFuture.Infrastructure.Data.Configurations.Privacy;

public class PrivacyRequestConfiguration : IEntityTypeConfiguration<PrivacyRequest>
{
    public void Configure(EntityTypeBuilder<PrivacyRequest> builder)
    {
        builder.ToTable("PrivacyRequests");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.CreatedAtUtc).IsRequired();
        builder.Property(p => p.UpdatedAtUtc);
        builder.Property(p => p.RowVersion).IsRowVersion();

        builder.Property(p => p.UserId).IsRequired();

        builder.Property(p => p.Type).HasConversion<int>().IsRequired();
        builder.Property(p => p.Status).HasConversion<int>().IsRequired();

        builder.Property(p => p.RequestReason).HasMaxLength(2000);
        builder.Property(p => p.AdminNotes).HasMaxLength(3000);
        builder.Property(p => p.RejectionReason).HasMaxLength(2000);

        builder.HasOne(p => p.User)
            .WithMany()
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(p => p.ReviewedByUser)
            .WithMany()
            .HasForeignKey(p => p.ReviewedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(p => p.CompletedByUser)
            .WithMany()
            .HasForeignKey(p => p.CompletedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => p.UserId);
        builder.HasIndex(p => p.Type);
        builder.HasIndex(p => p.Status);
        builder.HasIndex(p => p.ReviewedAtUtc);
        builder.HasIndex(p => p.CompletedAtUtc);
        builder.HasIndex(p => p.CreatedAtUtc);
        builder.HasIndex(p => p.ReviewedByUserId);
        builder.HasIndex(p => p.CompletedByUserId);
    }
}
