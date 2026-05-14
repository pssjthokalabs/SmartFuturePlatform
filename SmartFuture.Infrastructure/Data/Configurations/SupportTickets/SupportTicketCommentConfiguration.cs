using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.SupportTickets;

namespace SmartFuture.Infrastructure.Data.Configurations.SupportTickets;

public class SupportTicketCommentConfiguration : IEntityTypeConfiguration<SupportTicketComment>
{
    public void Configure(EntityTypeBuilder<SupportTicketComment> builder)
    {
        builder.ToTable("SupportTicketComments");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.CreatedAtUtc).IsRequired();
        builder.Property(c => c.UpdatedAtUtc);
        builder.Property(c => c.RowVersion).IsRowVersion();

        builder.Property(c => c.SupportTicketId).IsRequired();
        builder.Property(c => c.AuthorUserId).IsRequired();

        builder.Property(c => c.Body).IsRequired().HasMaxLength(4000);
        builder.Property(c => c.IsInternal).IsRequired();
        builder.Property(c => c.IsSystemGenerated).IsRequired();

        builder.HasOne(c => c.SupportTicket)
            .WithMany()
            .HasForeignKey(c => c.SupportTicketId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(c => c.AuthorUser)
            .WithMany()
            .HasForeignKey(c => c.AuthorUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(c => c.SupportTicketId);
        builder.HasIndex(c => c.AuthorUserId);
        builder.HasIndex(c => c.IsInternal);
        builder.HasIndex(c => c.IsSystemGenerated);
        builder.HasIndex(c => c.CreatedAtUtc);
    }
}
