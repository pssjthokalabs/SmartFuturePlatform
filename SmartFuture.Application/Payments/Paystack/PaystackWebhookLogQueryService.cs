using Microsoft.EntityFrameworkCore;
using SmartFuture.Application.Persistence;

namespace SmartFuture.Application.Payments.Paystack;

/// <summary>
/// Read-only queries over <c>PaystackWebhookLogs</c>. Powers the
/// admin "why didn't this invoice flip?" debugging surface. Returns a
/// flat DTO so callers don't have to know about the entity shape.
/// </summary>
public interface IPaystackWebhookLogQueryService
{
    Task<IReadOnlyList<PaystackWebhookLogDto>> ByReferenceAsync(
        string reference, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PaystackWebhookLogDto>> RecentAsync(
        int page, int pageSize, CancellationToken cancellationToken = default);
}

public class PaystackWebhookLogQueryService : IPaystackWebhookLogQueryService
{
    private readonly IAppDbContext _dbContext;

    public PaystackWebhookLogQueryService(IAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<PaystackWebhookLogDto>> ByReferenceAsync(
        string reference, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reference)) return Array.Empty<PaystackWebhookLogDto>();
        reference = reference.Trim();
        return await _dbContext.PaystackWebhookLogs
            .AsNoTracking()
            .Where(l => l.Reference == reference)
            .OrderByDescending(l => l.ReceivedAtUtc)
            .Select(l => Project(l))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PaystackWebhookLogDto>> RecentAsync(
        int page, int pageSize, CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;
        return await _dbContext.PaystackWebhookLogs
            .AsNoTracking()
            .OrderByDescending(l => l.ReceivedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => Project(l))
            .ToListAsync(cancellationToken);
    }

    // EF will translate this into the projection. Defined here so both
    // queries return the same flat shape.
    private static PaystackWebhookLogDto Project(Domain.Billing.PaystackWebhookLog l)
        => new()
        {
            Id                  = l.Id,
            ReceivedAtUtc       = l.ReceivedAtUtc,
            RawBodyLength       = l.RawBodyLength,
            SignaturePresent    = l.SignaturePresent,
            SignatureValid      = l.SignatureValid,
            Event               = l.Event,
            Reference           = l.Reference,
            AmountSubunits      = l.AmountSubunits,
            Currency            = l.Currency,
            Status              = l.Status,
            PaymentInitiationId = l.PaymentInitiationId,
            PaymentId           = l.PaymentId,
            InvoiceId           = l.InvoiceId,
            Accepted            = l.Accepted,
            OutcomeMessage      = l.OutcomeMessage,
            RejectionReason     = l.RejectionReason,
            ApplyAttempted      = l.ApplyAttempted,
            ApplySucceeded      = l.ApplySucceeded,
            ApplyErrorCode      = l.ApplyErrorCode,
            ApplyErrorMessage   = l.ApplyErrorMessage,
            HttpStatusReturned  = l.HttpStatusReturned,
            EnvironmentName     = l.EnvironmentName,
        };
}

public class PaystackWebhookLogDto
{
    public Guid     Id                  { get; set; }
    public DateTime ReceivedAtUtc       { get; set; }
    public int      RawBodyLength       { get; set; }
    public bool     SignaturePresent    { get; set; }
    public bool     SignatureValid      { get; set; }
    public string?  Event               { get; set; }
    public string?  Reference           { get; set; }
    public long?    AmountSubunits      { get; set; }
    public string?  Currency            { get; set; }
    public string?  Status              { get; set; }
    public Guid?    PaymentInitiationId { get; set; }
    public Guid?    PaymentId           { get; set; }
    public Guid?    InvoiceId           { get; set; }
    public bool     Accepted            { get; set; }
    public string?  OutcomeMessage      { get; set; }
    public string?  RejectionReason     { get; set; }
    public bool     ApplyAttempted      { get; set; }
    public bool     ApplySucceeded      { get; set; }
    public string?  ApplyErrorCode      { get; set; }
    public string?  ApplyErrorMessage   { get; set; }
    public int      HttpStatusReturned  { get; set; }
    public string?  EnvironmentName     { get; set; }
}
