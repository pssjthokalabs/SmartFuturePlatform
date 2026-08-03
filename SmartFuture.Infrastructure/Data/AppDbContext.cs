using System.Reflection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Auditing;
using SmartFuture.Domain.AppVersion;
using SmartFuture.Domain.Billing;
using SmartFuture.Domain.Common;
using SmartFuture.Domain.Coverage;
using SmartFuture.Domain.CoverageRequests;
using SmartFuture.Domain.Customers;
using SmartFuture.Domain.Identity;
using SmartFuture.Domain.Installations;
using SmartFuture.Domain.Jobs;
using SmartFuture.Domain.NetworkAccounts;
using SmartFuture.Domain.Notifications;
using SmartFuture.Domain.Auth;
using SmartFuture.Domain.OrderIntents;
using SmartFuture.Domain.Orders;
using SmartFuture.Domain.Privacy;
using SmartFuture.Domain.ServiceChanges;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Domain.SupportTickets;
using SmartFuture.Domain.Webhooks;

namespace SmartFuture.Infrastructure.Data;

public class AppDbContext
    : IdentityDbContext<User, IdentityRole<Guid>, Guid>, IAppDbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PortalAuthHandoffToken> PortalAuthHandoffTokens => Set<PortalAuthHandoffToken>();
    public DbSet<VerificationCode> VerificationCodes => Set<VerificationCode>();
    public DbSet<CustomerProfile> CustomerProfiles => Set<CustomerProfile>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ServicePackage> ServicePackages => Set<ServicePackage>();
    public DbSet<ServicePackageSubType> ServicePackageSubTypes => Set<ServicePackageSubType>();
    public DbSet<ServicePackageVariant> ServicePackageVariants => Set<ServicePackageVariant>();
    public DbSet<CoverageRequest> CoverageRequests => Set<CoverageRequest>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderIntent> OrderIntents => Set<OrderIntent>();
    public DbSet<Installation> Installations => Set<Installation>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLineItem> InvoiceLineItems => Set<InvoiceLineItem>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentInitiation> PaymentInitiations => Set<PaymentInitiation>();
    public DbSet<PaymentRetryAttempt> PaymentRetryAttempts => Set<PaymentRetryAttempt>();
    public DbSet<PaystackWebhookLog> PaystackWebhookLogs => Set<PaystackWebhookLog>();
    public DbSet<DebitOrderMandate> DebitOrderMandates => Set<DebitOrderMandate>();
    public DbSet<CustomerPaymentMandate> CustomerPaymentMandates => Set<CustomerPaymentMandate>();
    public DbSet<ServiceBillingSchedule> ServiceBillingSchedules => Set<ServiceBillingSchedule>();
    public DbSet<BillingRunLog> BillingRunLogs => Set<BillingRunLog>();
    public DbSet<SupportTicket> SupportTickets => Set<SupportTicket>();
    public DbSet<SupportTicketComment> SupportTicketComments => Set<SupportTicketComment>();
    public DbSet<OutboundNotification> OutboundNotifications => Set<OutboundNotification>();
    public DbSet<WebhookInbox> WebhookInboxes => Set<WebhookInbox>();
    public DbSet<PrivacyRequest> PrivacyRequests => Set<PrivacyRequest>();
    public DbSet<NetworkAccount> NetworkAccounts => Set<NetworkAccount>();
    public DbSet<RadiusProfile> RadiusProfiles => Set<RadiusProfile>();
    public DbSet<ProvisioningEvent> ProvisioningEvents => Set<ProvisioningEvent>();
    public DbSet<ServiceChangeRequest> ServiceChangeRequests => Set<ServiceChangeRequest>();
    public DbSet<MobileAppVersionRule> MobileAppVersionRules => Set<MobileAppVersionRule>();
    public DbSet<BillingDayOption> BillingDayOptions => Set<BillingDayOption>();
    public DbSet<CoverageMapRule> CoverageMapRules => Set<CoverageMapRule>();

    // Job Opportunities module.
    public DbSet<JobOpportunity> JobOpportunities => Set<JobOpportunity>();
    public DbSet<JobSource> JobSources => Set<JobSource>();
    public DbSet<JobImportRun> JobImportRuns => Set<JobImportRun>();
    public DbSet<JobSubscriberProfile> JobSubscriberProfiles => Set<JobSubscriberProfile>();
    public DbSet<JobSubscriberDocument> JobSubscriberDocuments => Set<JobSubscriberDocument>();
    public DbSet<JobAlertPreference> JobAlertPreferences => Set<JobAlertPreference>();
    public DbSet<JobAlertDeliveryLog> JobAlertDeliveryLogs => Set<JobAlertDeliveryLog>();
    public DbSet<JobModuleSettings> JobModuleSettings => Set<JobModuleSettings>();

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        => Database.BeginTransactionAsync(cancellationToken);

    public IExecutionStrategy CreateExecutionStrategy()
        => Database.CreateExecutionStrategy();

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
