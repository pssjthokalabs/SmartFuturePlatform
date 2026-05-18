using System.Reflection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Auditing;
using SmartFuture.Domain.Billing;
using SmartFuture.Domain.Common;
using SmartFuture.Domain.CoverageRequests;
using SmartFuture.Domain.Customers;
using SmartFuture.Domain.Identity;
using SmartFuture.Domain.Installations;
using SmartFuture.Domain.NetworkAccounts;
using SmartFuture.Domain.Notifications;
using SmartFuture.Domain.Orders;
using SmartFuture.Domain.Privacy;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Domain.SupportTickets;
using SmartFuture.Domain.Webhooks;

namespace SmartFuture.Infrastructure.Data;

public class AppDbContext
    : IdentityDbContext<User, IdentityRole<Guid>, Guid>, IAppDbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<VerificationCode> VerificationCodes => Set<VerificationCode>();
    public DbSet<CustomerProfile> CustomerProfiles => Set<CustomerProfile>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ServicePackage> ServicePackages => Set<ServicePackage>();
    public DbSet<CoverageRequest> CoverageRequests => Set<CoverageRequest>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Installation> Installations => Set<Installation>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLineItem> InvoiceLineItems => Set<InvoiceLineItem>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentInitiation> PaymentInitiations => Set<PaymentInitiation>();
    public DbSet<DebitOrderMandate> DebitOrderMandates => Set<DebitOrderMandate>();
    public DbSet<SupportTicket> SupportTickets => Set<SupportTicket>();
    public DbSet<SupportTicketComment> SupportTicketComments => Set<SupportTicketComment>();
    public DbSet<OutboundNotification> OutboundNotifications => Set<OutboundNotification>();
    public DbSet<WebhookInbox> WebhookInboxes => Set<WebhookInbox>();
    public DbSet<PrivacyRequest> PrivacyRequests => Set<PrivacyRequest>();
    public DbSet<NetworkAccount> NetworkAccounts => Set<NetworkAccount>();

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        => Database.BeginTransactionAsync(cancellationToken);

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ApplyAuditTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        ApplyAuditTimestamps();
        return base.SaveChanges();
    }

    private void ApplyAuditTimestamps()
    {
        var now = DateTime.UtcNow;

        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity.CreatedAtUtc == default)
                        entry.Entity.CreatedAtUtc = now;
                    entry.Entity.UpdatedAtUtc = null;
                    break;

                case EntityState.Modified:
                    entry.Property(nameof(BaseEntity.CreatedAtUtc)).IsModified = false;
                    entry.Entity.UpdatedAtUtc = now;
                    break;
            }
        }
    }
}
