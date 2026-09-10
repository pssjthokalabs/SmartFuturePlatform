using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SmartFuture.Domain.Auditing;
using SmartFuture.Domain.AppVersion;
using SmartFuture.Domain.Billing;
using SmartFuture.Domain.Coverage;
using SmartFuture.Domain.CoverageRequests;
using SmartFuture.Domain.Customers;
using SmartFuture.Domain.Identity;
using SmartFuture.Domain.Installations;
using SmartFuture.Domain.Jobs;
using SmartFuture.Domain.NetworkAccounts;
using SmartFuture.Domain.Notifications;
using SmartFuture.Domain.Auth;
using SmartFuture.Domain.Openserve;
using SmartFuture.Domain.OrderIntents;
using SmartFuture.Domain.Orders;
using SmartFuture.Domain.Privacy;
using SmartFuture.Domain.ServiceChanges;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Domain.SupportTickets;
using SmartFuture.Domain.Webhooks;

namespace SmartFuture.Application.Persistence;

public interface IAppDbContext
{
    DbSet<User> Users { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<PortalAuthHandoffToken> PortalAuthHandoffTokens { get; }
    DbSet<VerificationCode> VerificationCodes { get; }
    DbSet<CustomerProfile> CustomerProfiles { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<ServicePackage> ServicePackages { get; }
    DbSet<ServicePackageSubType> ServicePackageSubTypes { get; }
    DbSet<ServicePackageVariant> ServicePackageVariants { get; }
    DbSet<CoverageRequest> CoverageRequests { get; }
    DbSet<Order> Orders { get; }
    DbSet<OrderIntent> OrderIntents { get; }
    DbSet<Installation> Installations { get; }
    DbSet<Invoice> Invoices { get; }
    DbSet<InvoiceLineItem> InvoiceLineItems { get; }
    DbSet<Payment> Payments { get; }
    DbSet<PaymentInitiation> PaymentInitiations { get; }
    DbSet<PaymentRetryAttempt> PaymentRetryAttempts { get; }
    DbSet<PaystackWebhookLog> PaystackWebhookLogs { get; }
    DbSet<DebitOrderMandate> DebitOrderMandates { get; }
    DbSet<CustomerPaymentMandate> CustomerPaymentMandates { get; }
    DbSet<ServiceBillingSchedule> ServiceBillingSchedules { get; }
    DbSet<BillingRunLog> BillingRunLogs { get; }
    DbSet<SupportTicket> SupportTickets { get; }
    DbSet<SupportTicketComment> SupportTicketComments { get; }
    DbSet<OutboundNotification> OutboundNotifications { get; }
    DbSet<WebhookInbox> WebhookInboxes { get; }
    DbSet<PrivacyRequest> PrivacyRequests { get; }
    DbSet<NetworkAccount> NetworkAccounts { get; }
    DbSet<RadiusProfile> RadiusProfiles { get; }
    DbSet<ProvisioningEvent> ProvisioningEvents { get; }
    DbSet<ServiceChangeRequest> ServiceChangeRequests { get; }
    DbSet<MobileAppVersionRule> MobileAppVersionRules { get; }
    DbSet<BillingDayOption> BillingDayOptions { get; }
    DbSet<CoverageMapRule> CoverageMapRules { get; }

    // Openserve fulfilment integration.
    DbSet<OpenserveOrder> OpenserveOrders { get; }
    DbSet<OpenserveOrderStatusHistory> OpenserveOrderStatusHistories { get; }
    DbSet<OpenserveIntegrationLog> OpenserveIntegrationLogs { get; }
    DbSet<PackageOpenserveMapping> PackageOpenserveMappings { get; }
    DbSet<OpenserveIntegrationConfig> OpenserveIntegrationConfigs { get; }

    // Job Opportunities module.
    DbSet<JobOpportunity> JobOpportunities { get; }
    DbSet<JobSource> JobSources { get; }
    DbSet<JobImportRun> JobImportRuns { get; }
    DbSet<JobSubscriberProfile> JobSubscriberProfiles { get; }
    DbSet<JobSubscriberDocument> JobSubscriberDocuments { get; }
    DbSet<JobAlertPreference> JobAlertPreferences { get; }
    DbSet<JobAlertDeliveryLog> JobAlertDeliveryLogs { get; }
    DbSet<JobModuleSettings> JobModuleSettings { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);

    // SQL Server retrying execution strategy is enabled in DI (so
    // transient connection failures retry automatically). When code
    // needs a *user-initiated transaction* it must run inside this
    // strategy, otherwise EF throws
    // "The configured execution strategy 'SqlServerRetryingExecutionStrategy'
    //  does not support user-initiated transactions."
    // Use:
    //     var strategy = _dbContext.CreateExecutionStrategy();
    //     await strategy.ExecuteAsync(async () => {
    //         await using var tx = await _dbContext.BeginTransactionAsync(ct);
    //         // …writes…
    //         await tx.CommitAsync(ct);
    //     });
    IExecutionStrategy CreateExecutionStrategy();
}
