using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.SupportTickets;

namespace SmartFuture.Infrastructure.Data.Configurations.SupportTickets;

public class SupportTicketConfiguration : IEntityTypeConfiguration<SupportTicket>
{
    public void Configure(EntityTypeBuilder<SupportTicket> builder)
    {
        builder.ToTable("SupportTickets");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.CreatedAtUtc).IsRequired();
        builder.Property(t => t.UpdatedAtUtc);
        builder.Property(t => t.RowVersion).IsRowVersion();

        builder.Property(t => t.TicketNumber)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(t => t.UserId).IsRequired();

        builder.Property(t => t.Status).HasConversion<int>().IsRequired();
        builder.Property(t => t.Category).HasConversion<int>().IsRequired();
        builder.Property(t => t.Priority).HasConversion<int>().IsRequired();
        builder.Property(t => t.Source).HasConversion<int>().IsRequired();

        builder.Property(t => t.Subject).IsRequired().HasMaxLength(200);
        builder.Property(t => t.Description).IsRequired().HasMaxLength(4000);

        builder.Property(t => t.InternalSummary).HasMaxLength(2000);
        builder.Property(t => t.ResolutionSummary).HasMaxLength(3000);

        builder.HasOne(t => t.User)
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(t => t.Order)
            .WithMany()
            .HasForeignKey(t => t.OrderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(t => t.CoverageRequest)
            .WithMany()
            .HasForeignKey(t => t.CoverageRequestId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(t => t.Installation)
            .WithMany()
            .HasForeignKey(t => t.InstallationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(t => t.Invoice)
            .WithMany()
            .HasForeignKey(t => t.InvoiceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(t => t.Payment)
            .WithMany()
            .HasForeignKey(t => t.PaymentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(t => t.DebitOrderMandate)
            .WithMany()
            .HasForeignKey(t => t.DebitOrderMandateId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(t => t.AssignedToUser)
            .WithMany()
            .HasForeignKey(t => t.AssignedToUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(t => t.LastStatusChangedByUser)
            .WithMany()
            .HasForeignKey(t => t.LastStatusChangedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(t => t.TicketNumber).IsUnique();
        builder.HasIndex(t => t.UserId);
        builder.HasIndex(t => t.OrderId);
        builder.HasIndex(t => t.CoverageRequestId);
        builder.HasIndex(t => t.InstallationId);
        builder.HasIndex(t => t.InvoiceId);
        builder.HasIndex(t => t.PaymentId);
        builder.HasIndex(t => t.DebitOrderMandateId);
        builder.HasIndex(t => t.Status);
        builder.HasIndex(t => t.Category);
        builder.HasIndex(t => t.Priority);
        builder.HasIndex(t => t.Source);
        builder.HasIndex(t => t.AssignedToUserId);
        builder.HasIndex(t => t.CreatedAtUtc);
        builder.HasIndex(t => t.FirstRespondedAtUtc);
        builder.HasIndex(t => t.ResolvedAtUtc);
        builder.HasIndex(t => t.ClosedAtUtc);
        builder.HasIndex(t => t.LastStatusChangedByUserId);
    }
}
