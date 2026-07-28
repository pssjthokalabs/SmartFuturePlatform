using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartFuture.Application.OrderIntents.Dtos;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Application.Payments.Mandates;
using SmartFuture.Application.Payments.PayFast;
using SmartFuture.Application.Payments.Paystack;
using SmartFuture.Domain.Billing;
using SmartFuture.Domain.OrderIntents;
using SmartFuture.Domain.Orders;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.OrderIntents;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.Payments;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.OrderIntents;

// Phase 53 — "Order and Pay" client checkout.
//
// Two new methods on OrderIntentService:
//
//   InitiateClientPaymentAsync  — called when the customer clicks
//                                 "Order and Pay" on /client/orders/new.
//                                 Creates an OrderIntent (NOT a real
//                                 Order) + initiates a Paystack
//                                 transaction tied to the intent.
//
//   ConvertIntentPaymentToPaidOrderAsync — called from
//                                 PaystackNotifyHandler /
//                                 PaystackReconciliationService /
//                                 PaystackVerifyAndApply when the
//                                 inbound reference is SF-INTENT-….
//                                 Atomically creates Order +
//                                 Invoice (Paid) + Payment (Completed) +
//                                 Pending NetworkAccount + marks the
//                                 OrderIntent ConvertedToOrder.
//
// The existing invoice-bound Paystack flow (monthly invoices, retries,
// admin manual invoices) is untouched. Reference prefix routes which
// path runs.

public partial class OrderIntentService
{
    private const string IntentReferencePrefix = "SF-INTENT-";

    // ── HOTFIX (pre-release): installation/activation fee must never be R0 ──
    // Until the admin-editable fee ships, force R100 whenever no positive fee
    // is configured on the package (covers null, 0, and free-installation
    // packages). A real configured fee (> 0) is respected. This is the single
    // source of truth used by BOTH the payment-initiation amount and the
    // invoice/payment rows created on settlement, so the UI, the charged
    // amount, and the invoice can never disagree.
    //
    // The R100 launch floor and the checkout-breakdown maths live in
    // SmartFuture.Application.Billing.ProRata.CheckoutBreakdownCalculator
    // so they are unit-testable without a full OrderIntentService instance.
    // The helpers below stay for callsites that only need the individual
    // piece; the breakdown itself delegates directly.

    private static decimal ResolveInstallationFee(SmartFuture.Domain.ServicePackages.ServicePackage pkg)
        => SmartFuture.Application.Billing.ProRata.CheckoutBreakdownCalculator.ResolveActivationFee(pkg);

    private static decimal ResolveActivationFeeRespectingFree(
        SmartFuture.Domain.ServicePackages.ServicePackage pkg,
        SmartFuture.Domain.ServicePackages.ServicePackageVariant? variant = null)
        => SmartFuture.Application.Billing.ProRata.CheckoutBreakdownCalculator.ResolveActivationFee(pkg, variant);

    private static decimal ResolveMonthlyPrice(
        SmartFuture.Domain.ServicePackages.ServicePackage pkg,
        SmartFuture.Domain.ServicePackages.ServicePackageVariant? variant = null)
        => SmartFuture.Application.Billing.ProRata.CheckoutBreakdownCalculator.ResolveMonthlyPrice(pkg, variant);

    // Kept as a nested type so the rest of the file (line-item
    // construction, invoice-period stamp, etc.) reads unchanged. All the
    // maths now come from CheckoutBreakdownCalculator so production and
    // the phase-2 unit tests can never drift.
    private record CheckoutBreakdown(
        decimal ActivationFee,
        decimal ProRataAmount,
        int ProRataDays,
        DateTime? ProRataPeriodStartUtc,
        DateTime? ProRataPeriodEndUtc)
    {
        public decimal TotalDueNow => ActivationFee + ProRataAmount;
    }

    private CheckoutBreakdown ComputeCheckoutBreakdown(
        SmartFuture.Domain.ServicePackages.ServicePackage pkg,
        int billingDay,
        DateTime now,
        SmartFuture.Domain.ServicePackages.ServicePackageVariant? variant = null)
    {
        var b = SmartFuture.Application.Billing.ProRata.CheckoutBreakdownCalculator.Compute(
            pkg, billingDay, now, _billingSettings, variant);
        return new CheckoutBreakdown(
            b.ActivationFee, b.ProRataAmount, b.ProRataDays,
            b.ProRataPeriodStartUtc, b.ProRataPeriodEndUtc);
    }

    // Resolve + validate the customer's requested billing day. Returns
    // an int when accepted, or a Result<...> failure the caller should
    // short-circuit on (pattern-matched at the call site with `is`).
    private async Task<object> ResolvePreferredBillingDayAsync(int? requested, CancellationToken ct)
    {
        if (requested.HasValue)
        {
            var check = await _billingDayOptions.ValidateForCheckoutAsync(requested.Value, ct);
            if (!check.IsSuccess)
                return Result<InitiateOrderIntentPaymentResponseDto>.Failure(
                    check.Code ?? Shared.Errors.ErrorCodes.VALIDATION_ERROR,
                    check.Message ?? "Billing day is not accepted.");
            return check.Data!.Day;
        }
        return await _billingDayOptions.ResolveDefaultBillingDayAsync(ct);
    }

    public static bool IsIntentReference(string? reference)
        => !string.IsNullOrWhiteSpace(reference)
        && reference!.StartsWith(IntentReferencePrefix, StringComparison.OrdinalIgnoreCase);

    public async Task<Result<InitiateOrderIntentPaymentResponseDto>> InitiateClientPaymentAsync(
        InitiateOrderIntentPaymentRequestDto request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation(
                "[OrderAndPayApiDebug] enter userId={UserId} packageId={PackageId} addressLine1Present={AddrPresent} lat={Lat} lng={Lng} email={Email}",
                _currentUser.UserId, request?.ServicePackageId, !string.IsNullOrWhiteSpace(request?.AddressLine1),
                request?.Latitude, request?.Longitude, request?.Email);

            if (request is null)
                return Result<InitiateOrderIntentPaymentResponseDto>.Failure(ErrorCodes.BAD_REQUEST, "Request body is required.");

            var currentUserId = _currentUser.UserId;
            if (currentUserId is null || currentUserId == Guid.Empty)
                return Result<InitiateOrderIntentPaymentResponseDto>.Failure(ErrorCodes.UNAUTHORIZED, "User is not authenticated.");

            if (request.ServicePackageId == Guid.Empty)
                return Result<InitiateOrderIntentPaymentResponseDto>.Failure(ErrorCodes.VALIDATION_ERROR, "ServicePackageId is required.");

            // Load the package FIRST so the coverage gate below can branch
            // on package.Type — Security/CCTV packages don't have an
            // upstream coverage grid and must NOT require lat/lng.
            var package = await _dbContext.ServicePackages
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == request.ServicePackageId, cancellationToken);
            if (package is null)
            {
                _logger.LogWarning(
                    "[OrderAndPayApiDebug] package-not-found packageId={PackageId} userId={UserId}",
                    request.ServicePackageId, currentUserId);
                // Include the missing id so the portal/dev can see exactly
                // which package was requested but no longer exists in the
                // catalogue. Not sensitive — it's the id the client just sent.
                return Result<InitiateOrderIntentPaymentResponseDto>.Failure(
                    ErrorCodes.NOT_FOUND,
                    $"Service package not found for id {request.ServicePackageId}. The catalogue may have changed — refresh and try again.");
            }
            if (package.Status != ServicePackageStatus.Active)
                return Result<InitiateOrderIntentPaymentResponseDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR,
                    $"Service package '{package.Name}' is not active and cannot be ordered (status={package.Status}).");

            // Optional selected variant. Must belong to this package and be
            // active; its price/fee/free drive the breakdown below and are
            // persisted on the intent so the convert path re-resolves them.
            SmartFuture.Domain.ServicePackages.ServicePackageVariant? variant = null;
            if (request.ServicePackageVariantId is Guid variantId && variantId != Guid.Empty)
            {
                variant = await _dbContext.ServicePackageVariants
                    .AsNoTracking()
                    .FirstOrDefaultAsync(v => v.Id == variantId, cancellationToken);
                if (variant is null || variant.ServicePackageId != package.Id)
                    return Result<InitiateOrderIntentPaymentResponseDto>.Failure(
                        ErrorCodes.VALIDATION_ERROR, "Selected option is not valid for this package.");
                if (!variant.IsActive)
                    return Result<InitiateOrderIntentPaymentResponseDto>.Failure(
                        ErrorCodes.VALIDATION_ERROR, "Selected option is no longer available.");
            }

            var isSecurity = package.Type == ServicePackageType.Security;

            // Issue 5 (go-live) — dispatch-safe address contract. Frontend
            // gates on these too, but enforce server-side so a stale
            // bundle / mobile client / website handoff can't bypass it.
            // Suburb stays optional (many SA addresses don't have one);
            // City, Province and PostalCode are required for the
            // technician dispatch list to make sense.
            if (string.IsNullOrWhiteSpace(request.AddressLine1))
                return Result<InitiateOrderIntentPaymentResponseDto>.Failure(ErrorCodes.VALIDATION_ERROR, "AddressLine1 is required.");
            if (string.IsNullOrWhiteSpace(request.City))
                return Result<InitiateOrderIntentPaymentResponseDto>.Failure(ErrorCodes.VALIDATION_ERROR, "City is required for dispatch.");
            if (string.IsNullOrWhiteSpace(request.Province))
                return Result<InitiateOrderIntentPaymentResponseDto>.Failure(ErrorCodes.VALIDATION_ERROR, "Province is required for dispatch.");
            if (string.IsNullOrWhiteSpace(request.PostalCode))
                return Result<InitiateOrderIntentPaymentResponseDto>.Failure(ErrorCodes.VALIDATION_ERROR, "PostalCode is required for dispatch.");
            // Coverage gate — Fibre (and every non-Security line) MUST
            // submit coordinates from a Google Places pick + an Openserve
            // coverage check. Security/CCTV is exempt: there's no upstream
            // coverage grid, fulfilment is a manual on-site install.
            if (!isSecurity && (!request.Latitude.HasValue || !request.Longitude.HasValue))
                return Result<InitiateOrderIntentPaymentResponseDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR,
                    "Please confirm coverage for your installation address before placing an order.");

            // Category-aware one-active-order rule. An open Fibre order
            // does not block a new Security order and vice versa, but two
            // open orders inside the same category are rejected. Mirrors
            // OrderService.CreateMineAsync so the rule is consistent
            // across the create and intent flows.
            var eligibility = await _orderService.GetMyEligibilityAsync(package.Type, cancellationToken);
            if (eligibility.IsSuccess && eligibility.Data is not null && !eligibility.Data.CanCreateOrder)
            {
                return Result<InitiateOrderIntentPaymentResponseDto>.Failure(
                    ErrorCodes.ORDER_ALREADY_IN_PROGRESS,
                    eligibility.Data.Message ?? "You already have an order in progress.");
            }

            // Billing day resolution. The website + portal always send a
            // value; legacy/mobile callers omit it and fall back to the
            // seeded default (30). Validated against the enabled options
            // catalogue so a disabled day is rejected here.
            var billingDay = await ResolvePreferredBillingDayAsync(
                request.PreferredBillingDay, cancellationToken);
            if (billingDay is Result<InitiateOrderIntentPaymentResponseDto> billingDayFailure)
                return billingDayFailure;
            var resolvedBillingDay = ((int)billingDay);

            var breakdown = ComputeCheckoutBreakdown(package, resolvedBillingDay, now: DateTime.UtcNow, variant: variant);
            var totalDue = breakdown.TotalDueNow;

            // Free-activation: only reject to the free-order endpoint when
            // there's ACTUALLY nothing to charge. A Security package with
            // free activation but a mid-month join still owes pro-rata, so
            // it must go through the paid gateway.
            if (totalDue <= 0m)
                return Result<InitiateOrderIntentPaymentResponseDto>.Failure(
                    ErrorCodes.CONFLICT,
                    "Nothing to charge for this checkout — place the order via the free-order endpoint.");

            // Amount actually sent to the gateway. The variable name
            // stays `installationFee` locally so the diagnostic log +
            // downstream Paystack/PayFast wiring below stay unchanged;
            // the real breakdown is captured in `breakdown`.
            var installationFee = totalDue;

            // Resolve a customer email for Paystack — prefer the
            // request, fall back to the signed-in identity.
            var customerEmail = !string.IsNullOrWhiteSpace(request.Email)
                ? request.Email!.Trim()
                : await _dbContext.Users
                    .AsNoTracking()
                    .Where(u => u.Id == currentUserId.Value)
                    .Select(u => u.Email)
                    .FirstOrDefaultAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(customerEmail))
                return Result<InitiateOrderIntentPaymentResponseDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR,
                    "Cannot start a Paystack payment without a customer email.");

            _logger.LogInformation(
                "[OrderAndPayApiDebug] package-ok packageId={PackageId} packageName={PackageName} fee={Fee} email={Email}",
                package.Id, package.Name, installationFee, customerEmail);

            // Resolve which gateway to initiate against. Default to
            // Paystack so existing portal callers (which don't pass
            // Provider) stay byte-identical. Mobile callers pass
            // PayFast / Paystack explicitly. Unknown / Manual /
            // PeachPayments / Yoco are not wired into the intent flow and
            // are rejected up-front. Paystack, PayFast and Ozow each have
            // a dedicated intent-initiation service.
            var resolvedProvider = request.Provider ?? PaymentProviderType.Paystack;
            if (resolvedProvider != PaymentProviderType.Paystack
             && resolvedProvider != PaymentProviderType.PayFast
             && resolvedProvider != PaymentProviderType.Ozow)
            {
                return Result<InitiateOrderIntentPaymentResponseDto>.Failure(
                    ErrorCodes.VALIDATION_ERROR,
                    $"Provider '{resolvedProvider}' is not supported for new-order intents. Use Paystack, PayFast or Ozow.");
            }

            // Customer-facing availability gate for Ozow, mirroring
            // PaymentGatewayService.CheckCustomerProviderAvailable on the
            // invoice path. Ozow ships Enabled=false by default, so
            // without this the customer would reach the gateway and get a
            // raw "not configured" string back. Fail early with the same
            // friendly copy the invoice flow uses.
            if (resolvedProvider == PaymentProviderType.Ozow && !_ozowSettings53.Enabled)
            {
                _logger.LogInformation(
                    "[OrderAndPayApiDebug] ozow-refused — Ozow.Enabled is false. Set Ozow__Enabled=true (plus SiteCode/ApiKey/PrivateKey) to allow new-order Ozow checkout.");
                return Result<InitiateOrderIntentPaymentResponseDto>.Failure(
                    ErrorCodes.PROVIDER_NOT_CONFIGURED,
                    "This payment method is temporarily unavailable. Please use Paystack.");
            }

            var now = DateTime.UtcNow;

            var intent = new OrderIntent
            {
                IntentToken = "INT-" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant(),
                ServicePackageId = package.Id,
                ServicePackageVariantId = variant?.Id,
                FullName = Trim(request.FullName),
                Email = Trim(customerEmail),
                PhoneNumber = Trim(request.PhoneNumber),
                AddressLine1 = request.AddressLine1.Trim(),
                AddressLine2 = Trim(request.AddressLine2),
                Suburb = Trim(request.Suburb),
                City = Trim(request.City),
                Province = Trim(request.Province),
                PostalCode = Trim(request.PostalCode),
                Country = Trim(request.Country),
                Latitude = request.Latitude,
                Longitude = request.Longitude,
                GooglePlaceId = Trim(request.GooglePlaceId),
                MapProviderReference = Trim(request.MapProviderReference),
                RequestedInstallationDateUtc = request.RequestedInstallationDateUtc,
                CustomerNotes = Trim(request.CustomerNotes),
                Status = OrderIntentStatus.Pending,
                ClaimedByUserId = currentUserId,
                ClaimedAtUtc = now,
                Source = "ClientPortalOrderAndPay",
                ExpiresAtUtc = now.AddHours(2),
                // Stamp the gateway BEFORE initiating so a transport
                // error still leaves the intent provider-tagged for
                // diagnostics. Cancelled intents persist this too.
                Provider = resolvedProvider,
                PreferredBillingDay = resolvedBillingDay,
            };
            _dbContext.OrderIntents.Add(intent);
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "[OrderAndPayApiDebug] intent-saved intentId={IntentId} token={Token} provider={Provider}",
                intent.Id, intent.IntentToken, resolvedProvider);

            // Branch per provider. The two paths produce identical
            // shapes on the response so the caller doesn't need to
            // know which gateway settled — only the URL changes.
            string reference;
            string? redirectUrl;
            string? accessCode;
            decimal amountSent;
            bool overrideApplied;
            PaystackInlineCheckoutDto? paystackInline;

            if (resolvedProvider == PaymentProviderType.Paystack)
            {
                var initRes = await _paystackIntentInit.InitiateAsync(new PaystackIntentInitiationRequest
                {
                    OrderIntentId       = intent.Id,
                    CustomerEmail       = customerEmail,
                    InvoiceAmountAtTime = installationFee,
                    CallbackUrl         = request.SuccessUrl,
                    CancelUrl           = request.CancelUrl,
                }, cancellationToken);

                if (!initRes.Success)
                {
                    _logger.LogWarning(
                        "[OrderAndPayApiDebug] paystack-init-failed intentId={IntentId} reason={Reason}",
                        intent.Id, initRes.FailureReason);
                    intent.Status = OrderIntentStatus.Cancelled;
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    return Result<InitiateOrderIntentPaymentResponseDto>.Failure(
                        ErrorCodes.PAYMENT_INIT_FAILED,
                        initRes.FailureReason ?? "Paystack initiation failed.");
                }

                reference       = initRes.Reference;
                redirectUrl     = initRes.RedirectUrl;
                accessCode      = initRes.AccessCode;
                amountSent      = initRes.AmountSent;
                overrideApplied = initRes.IsTestAmountOverrideApplied;
                paystackInline  = initRes.ToInlineDto(customerEmail);
            }
            else if (resolvedProvider == PaymentProviderType.Ozow)
            {
                // Ozow — real outbound HTTP POST returning a hosted
                // checkout URL. Unlike Paystack there is NO client-side
                // verify: OzowNotifyHandler resolving this reference and
                // calling ConvertIntentPaymentToPaidOrderAsync is the
                // ONLY way this becomes an order.
                //
                // The reference is minted here (not inside the service)
                // because Ozow echoes it back verbatim as
                // TransactionReference, and the notify handler routes on
                // its "SF-INTENT-" prefix. Keeping the mint next to the
                // other providers' references keeps the format in one
                // family: SF-INTENT-{12 hex} = 22 chars, well inside
                // Ozow's 50-char cap.
                var ozowReference = $"SF-INTENT-{Guid.NewGuid().ToString("N")[..12].ToUpperInvariant()}";

                var initRes = await _ozowIntentInit.InitiateAsync(new Payments.Ozow.OzowIntentInitiationRequest
                {
                    OrderIntentId       = intent.Id,
                    Reference           = ozowReference,
                    InvoiceAmountAtTime = installationFee,
                    // Shown on the customer's bank statement (20 chars max,
                    // truncated downstream).
                    BankReferenceLabel  = "SmartFuture",
                    SuccessUrlOverride  = request.SuccessUrl,
                    CancelUrlOverride   = request.CancelUrl,
                    // No separate failure URL on the intent request DTO —
                    // the service falls back to Ozow:ErrorUrl, then to the
                    // cancel URL. /payment/result handles both identically.
                    ErrorUrlOverride    = null,
                }, cancellationToken);

                if (!initRes.Success)
                {
                    _logger.LogWarning(
                        "[OrderAndPayApiDebug] ozow-init-failed intentId={IntentId} reason={Reason}",
                        intent.Id, initRes.FailureReason);
                    intent.Status = OrderIntentStatus.Cancelled;
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    return Result<InitiateOrderIntentPaymentResponseDto>.Failure(
                        ErrorCodes.PAYMENT_INIT_FAILED,
                        initRes.FailureReason ?? "Ozow initiation failed.");
                }

                reference       = initRes.Reference;
                redirectUrl     = initRes.RedirectUrl;
                // Ozow has no Paystack-style access code. We reuse the
                // AccessCode column to persist Ozow's paymentRequestId —
                // same semantic slot ("the gateway's own id for this
                // checkout session") and it avoids a schema migration.
                // It is what a support agent needs to find the
                // transaction in the Ozow dashboard if a notification
                // goes missing.
                accessCode      = initRes.PaymentRequestId;
                amountSent      = initRes.AmountSent;
                overrideApplied = initRes.IsTestAmountOverrideApplied;
                paystackInline  = null;       // not applicable
            }
            else
            {
                // PayFast — signed redirect URL, no outbound HTTP. The
                // ITN handler (PayFastNotifyHandler) will resolve this
                // intent by IntentPaymentReference and call
                // ConvertIntentPaymentToPaidOrderAsync when PayFast
                // posts a COMPLETE status.
                var nameSplit = (request.FullName ?? string.Empty).Trim()
                    .Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var initRes = await _payFastIntentInit.InitiateAsync(new PayFastIntentInitiationRequest
                {
                    OrderIntentId       = intent.Id,
                    CustomerEmail       = customerEmail,
                    CustomerFirstName   = nameSplit.Length > 0 ? nameSplit[0] : null,
                    CustomerLastName    = nameSplit.Length > 1 ? nameSplit[1] : null,
                    InvoiceAmountAtTime = installationFee,
                    ItemName            = $"SmartFuture {package.Name}",
                    ReturnUrlOverride   = request.SuccessUrl,
                    CancelUrlOverride   = request.CancelUrl,
                    SaveForAutoRenewal  = request.SaveForAutoRenewal,
                }, cancellationToken);

                if (!initRes.Success)
                {
                    _logger.LogWarning(
                        "[OrderAndPayApiDebug] payfast-init-failed intentId={IntentId} reason={Reason}",
                        intent.Id, initRes.FailureReason);
                    intent.Status = OrderIntentStatus.Cancelled;
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    return Result<InitiateOrderIntentPaymentResponseDto>.Failure(
                        ErrorCodes.PAYMENT_INIT_FAILED,
                        initRes.FailureReason ?? "PayFast initiation failed.");
                }

                reference       = initRes.Reference;
                redirectUrl     = initRes.RedirectUrl;
                accessCode      = null;       // PayFast doesn't have one
                amountSent      = initRes.AmountSent;
                overrideApplied = initRes.IsTestAmountOverrideApplied;
                paystackInline  = null;       // not applicable
            }

            intent.IntentPaymentReference            = reference;
            intent.IntentPaymentAccessCode           = accessCode;
            intent.IntentPaymentRedirectUrl          = redirectUrl;
            intent.IntentPaymentAmount               = amountSent;
            intent.IntentInvoiceAmountAtTime         = installationFee;
            intent.IntentIsTestAmountOverrideApplied = overrideApplied;
            intent.IntentPaymentInitiatedAtUtc       = now;
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "[OrderAndPayApiDebug] intent-initiated intentId={IntentId} provider={Provider} reference={Reference} amountSent={AmountSent} override={Override}",
                intent.Id, resolvedProvider, reference, amountSent, overrideApplied);

            // Safe debug block — populated ONLY in non-Production.
            // Mobile logs this so the operator can verify the API is
            // pulling the right env vars (PayFast__MerchantId,
            // PayFast__UseTestAmountOverride, PayFast__TestAmount etc.)
            // without having to SSH into the server.
            InitiateOrderIntentDebugDto? debug = null;
            if (!_env53.IsProduction())
            {
                if (resolvedProvider == PaymentProviderType.PayFast)
                {
                    debug = new InitiateOrderIntentDebugDto
                    {
                        Provider              = "PayFast",
                        Environment           = _env53.EnvironmentName,
                        UseSandbox            = _payFastSettings53.UseSandbox,
                        MerchantId            = _payFastSettings53.MerchantId,
                        MerchantIdSource      = "PayFast__MerchantId",
                        PayFastHost           = SafeHost53(_payFastSettings53.ProcessUrl),
                        NotifyUrl             = _payFastSettings53.NotifyUrl,
                        ReturnUrl             = _payFastSettings53.ReturnUrl,
                        CancelUrl             = _payFastSettings53.CancelUrl,
                        UseTestAmountOverride = _payFastSettings53.UseTestAmountOverride,
                        TestAmount            = _payFastSettings53.TestAmount,
                        OriginalAmount        = installationFee,
                        EffectiveAmount       = amountSent,
                    };
                }
                else
                {
                    debug = new InitiateOrderIntentDebugDto
                    {
                        Provider              = "Paystack",
                        Environment           = _env53.EnvironmentName,
                        UseSandbox            = _paystackSettings53.UseTestMode,
                        // Paystack uses SecretKey, not a merchant id.
                        // Leave MerchantId blank so the mobile log
                        // doesn't render an empty string as "missing".
                        MerchantId            = null,
                        MerchantIdSource      = "Paystack__SecretKey (test/live key prefix)",
                        PayFastHost           = null,
                        NotifyUrl             = _paystackSettings53.WebhookUrl,
                        ReturnUrl             = _paystackSettings53.CallbackUrl,
                        CancelUrl             = _paystackSettings53.CancelUrl,
                        UseTestAmountOverride = _paystackSettings53.UseTestAmountOverride,
                        TestAmount            = _paystackSettings53.TestAmount,
                        OriginalAmount        = installationFee,
                        EffectiveAmount       = amountSent,
                    };
                }
            }

            return Result<InitiateOrderIntentPaymentResponseDto>.Success(new InitiateOrderIntentPaymentResponseDto
            {
                OrderIntentId               = intent.Id,
                IntentToken                 = intent.IntentToken,
                Reference                   = reference,
                Provider                    = resolvedProvider.ToString(),
                RedirectUrl                 = redirectUrl,
                PaystackInline              = paystackInline,
                InvoiceAmount               = installationFee,
                AmountSent                  = amountSent,
                IsTestAmountOverrideApplied = overrideApplied,
                Debug                       = debug,
                ActivationFeeAmount         = breakdown.ActivationFee,
                ProRataAmount               = breakdown.ProRataAmount,
                ProRataDays                 = breakdown.ProRataDays,
                ProRataPeriodStartUtc       = breakdown.ProRataPeriodStartUtc,
                ProRataPeriodEndUtc         = breakdown.ProRataPeriodEndUtc,
                PreferredBillingDay         = resolvedBillingDay,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OrderAndPayApiDebug] unhandled exception");
            var inner = ex.InnerException?.Message;
            var message = string.IsNullOrWhiteSpace(inner)
                ? $"{ex.GetType().Name}: {ex.Message}"
                : $"{ex.GetType().Name}: {ex.Message} (inner: {inner})";
            return Result<InitiateOrderIntentPaymentResponseDto>.Failure(
                ErrorCodes.EXCEPTION, message);
        }
    }

    public async Task<Result<ConvertIntentPaymentToPaidOrderOutcomeDto>> ConvertIntentPaymentToPaidOrderAsync(
        string intentPaymentReference,
        DateTime? paidAtUtc,
        string? gatewayTransactionId,
        PaystackVerifyAuthorizationSnapshot? authorizationSnapshot = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(intentPaymentReference))
            return Result<ConvertIntentPaymentToPaidOrderOutcomeDto>.Failure(
                ErrorCodes.VALIDATION_ERROR, "intentPaymentReference is required.");

        intentPaymentReference = intentPaymentReference.Trim();

        var intent = await _dbContext.OrderIntents
            .Include(i => i.ServicePackage)
            .Include(i => i.ServicePackageVariant)
            .FirstOrDefaultAsync(i => i.IntentPaymentReference == intentPaymentReference, cancellationToken);

        if (intent is null)
            return Result<ConvertIntentPaymentToPaidOrderOutcomeDto>.Failure(
                ErrorCodes.NOT_FOUND, "No OrderIntent found for that reference.");

        // Idempotency: previously converted intent returns the existing
        // order + invoice + payment so the webhook + verify-and-apply
        // can run repeatedly without duplicating rows.
        if (intent.Status == OrderIntentStatus.ConvertedToOrder && intent.ConvertedOrderId is Guid existingOrderId)
        {
            var existing = await ResolveExistingConversionAsync(intent, existingOrderId, cancellationToken);
            if (existing is not null) return Result<ConvertIntentPaymentToPaidOrderOutcomeDto>.Success(existing, "Intent already converted.");
        }

        if (intent.ServicePackage is null)
            return Result<ConvertIntentPaymentToPaidOrderOutcomeDto>.Failure(
                ErrorCodes.EXCEPTION, "OrderIntent has no linked service package.");
        if (intent.ClaimedByUserId is null || intent.ClaimedByUserId == Guid.Empty)
            return Result<ConvertIntentPaymentToPaidOrderOutcomeDto>.Failure(
                ErrorCodes.EXCEPTION, "OrderIntent is not claimed by a user.");

        var customerProfileId = await _dbContext.CustomerProfiles
            .Where(p => p.UserId == intent.ClaimedByUserId.Value)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var pkg = intent.ServicePackage;
        // The variant the customer selected at initiate-time (if any). Its
        // effective pricing must match what the gateway charged.
        var variant = intent.ServicePackageVariant;

        // Rebuild the same breakdown the initiate path saw. The billing
        // day snapshot on the intent is authoritative — if it's missing
        // (legacy intent minted before this feature), fall back to the
        // seeded default so the settle path doesn't crash a customer
        // who's already paid.
        var convertBillingDay = intent.PreferredBillingDay
            ?? await _billingDayOptions.ResolveDefaultBillingDayAsync(cancellationToken);
        // The intent was minted at initiate-time — anchor the pro-rata
        // period on that day so the customer's invoice period matches
        // the amount the gateway charged, even if the webhook lands
        // days later.
        var breakdown = ComputeCheckoutBreakdown(pkg, convertBillingDay, now: intent.CreatedAtUtc, variant: variant);
        var installationFee = breakdown.TotalDueNow;
        if (installationFee <= 0m)
            return Result<ConvertIntentPaymentToPaidOrderOutcomeDto>.Failure(
                ErrorCodes.VALIDATION_ERROR, "Total due is zero — intent should not have been initiated for payment.");

        Order? createdOrder = null;
        Invoice? createdInvoice = null;
        Payment? createdPayment = null;

        var strategy = _dbContext.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                createdOrder = null;
                createdInvoice = null;
                createdPayment = null;

                await using var tx = await _dbContext.BeginTransactionAsync(cancellationToken);
                try
                {
                    // Re-read the intent inside the strategy so a retry
                    // sees the latest status (might already be Converted).
                    var trackedIntent = await _dbContext.OrderIntents
                        .FirstAsync(i => i.Id == intent.Id, cancellationToken);

                    if (trackedIntent.Status == OrderIntentStatus.ConvertedToOrder && trackedIntent.ConvertedOrderId is Guid cid)
                    {
                        createdOrder = await _dbContext.Orders.FirstOrDefaultAsync(o => o.Id == cid, cancellationToken);
                        createdInvoice = await _dbContext.Invoices.FirstOrDefaultAsync(i => i.OrderId == cid, cancellationToken);
                        if (createdInvoice is not null)
                            createdPayment = await _dbContext.Payments.FirstOrDefaultAsync(p => p.InvoiceId == createdInvoice.Id, cancellationToken);
                        await tx.CommitAsync(cancellationToken);
                        return;
                    }

                    var stamp = now.ToString("yyyyMMdd");
                    var shortId = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

                    var order = new Order
                    {
                        OrderNumber                 = $"SF-{stamp}-{shortId}",
                        UserId                      = trackedIntent.ClaimedByUserId!.Value,
                        CustomerProfileId           = customerProfileId,
                        ServicePackageId            = pkg.Id,
                        ServicePackageVariantId     = variant?.Id,
                        Status                      = OrderStatus.PaymentReceived,
                        Source                      = OrderSource.CustomerApp,
                        SubmittedAtUtc              = now,
                        PackageName                 = pkg.Name,
                        PackageVariantName          = variant?.Name,
                        PackageType                 = pkg.Type,
                        PackageSpeedLabel           = pkg.SpeedLabel,
                        PackageDataAllowanceLabel   = pkg.DataAllowanceLabel,
                        PackageIsUncapped           = pkg.IsUncapped,
                        // Snapshot the EFFECTIVE (variant) pricing.
                        PackagePrice                = ResolveMonthlyPrice(pkg, variant),
                        PackageBillingCycle         = pkg.BillingCycle,
                        PackageContractMonths       = pkg.ContractMonths,
                        PackageHasFreeInstallation  = variant is null ? pkg.HasFreeInstallation : (variant.HasFreeInstallation ?? pkg.HasFreeInstallation),
                        PackageInstallationFee      = variant is null ? pkg.InstallationFee : (variant.InstallationFee ?? pkg.InstallationFee),
                        PackageIncludesRouter       = pkg.IncludesRouter,
                        FullName                    = trackedIntent.FullName,
                        Email                       = trackedIntent.Email,
                        PhoneNumber                 = trackedIntent.PhoneNumber,
                        AddressLine1                = trackedIntent.AddressLine1 ?? string.Empty,
                        AddressLine2                = trackedIntent.AddressLine2,
                        Suburb                      = trackedIntent.Suburb,
                        City                        = trackedIntent.City,
                        Province                    = trackedIntent.Province,
                        PostalCode                  = trackedIntent.PostalCode,
                        Country                     = trackedIntent.Country,
                        Latitude                    = trackedIntent.Latitude,
                        Longitude                   = trackedIntent.Longitude,
                        GooglePlaceId               = trackedIntent.GooglePlaceId,
                        MapProviderReference        = trackedIntent.MapProviderReference,
                        CustomerNotes               = trackedIntent.CustomerNotes,
                        RequestedInstallationDateUtc = trackedIntent.RequestedInstallationDateUtc,
                        LastStatusChangedByUserId   = trackedIntent.ClaimedByUserId,
                        PreferredBillingDay         = convertBillingDay,
                        // Stamp the pro-rata guard IF this checkout wrote a
                        // ProRata line. Prevents AdminActivateService (for
                        // Fibre) or a re-processed webhook (for Security)
                        // from generating a duplicate first pro-rata invoice.
                        FirstProRataInvoiceGeneratedAtUtc = breakdown.ProRataAmount > 0m ? now : (DateTime?)null,
                    };
                    _dbContext.Orders.Add(order);

                    var invoice = new Invoice
                    {
                        InvoiceNumber       = $"INV-{stamp}-{shortId}",
                        Order               = order,
                        Status              = InvoiceStatus.Issued,
                        SubtotalAmount      = installationFee,
                        TotalAmount         = installationFee,
                        BalanceDue          = installationFee,
                        AmountPaid          = 0m,
                        CurrencyCode        = "ZAR",
                        IssuedAtUtc         = now,
                        DueAtUtc            = now.AddDays(7),
                        // Stamp the invoice's covered period when a
                        // ProRata line is present. RecurringInvoiceGenerator
                        // reads these to advance the schedule off the
                        // customer's chosen billing day (see
                        // ServiceBillingScheduleService).
                        PeriodStartUtc      = breakdown.ProRataAmount > 0m ? breakdown.ProRataPeriodStartUtc : null,
                        PeriodEndUtc        = breakdown.ProRataAmount > 0m ? breakdown.ProRataPeriodEndUtc   : null,
                        LastStatusChangedByUserId = trackedIntent.ClaimedByUserId,
                    };
                    var lineSort = 0;
                    if (breakdown.ActivationFee > 0m)
                    {
                        invoice.LineItems.Add(new InvoiceLineItem
                        {
                            Invoice     = invoice,
                            LineType    = InvoiceLineItemType.InstallationFee,
                            Description = "Activation once-off fee",
                            Quantity    = 1,
                            UnitAmount  = breakdown.ActivationFee,
                            TotalAmount = breakdown.ActivationFee,
                            SortOrder   = lineSort++,
                        });
                    }
                    if (breakdown.ProRataAmount > 0m
                        && breakdown.ProRataPeriodStartUtc.HasValue
                        && breakdown.ProRataPeriodEndUtc.HasValue)
                    {
                        invoice.LineItems.Add(new InvoiceLineItem
                        {
                            Invoice     = invoice,
                            LineType    = InvoiceLineItemType.ProRata,
                            Description = SmartFuture.Application.Billing.ProRata.ProRataCalculator.FormatProRataDescription(
                                breakdown.ProRataPeriodStartUtc.Value, breakdown.ProRataPeriodEndUtc.Value),
                            Quantity    = 1,
                            UnitAmount  = breakdown.ProRataAmount,
                            TotalAmount = breakdown.ProRataAmount,
                            SortOrder   = lineSort++,
                        });
                    }
                    _dbContext.Invoices.Add(invoice);

                    var providerAmount = trackedIntent.IntentPaymentAmount ?? installationFee;
                    var overrideApplied = trackedIntent.IntentIsTestAmountOverrideApplied
                        && !_env53.IsProduction();

                    // Materialise Payment + PaymentInitiation with the
                    // gateway recorded on the OrderIntent. Pre-PayFast
                    // this was hardcoded to Paystack; persisting the
                    // intent's Provider keeps the audit + admin views
                    // honest about which gateway actually settled the
                    // money.
                    var intentProvider = trackedIntent.Provider;
                    var gatewayName = intentProvider switch
                    {
                        PaymentProviderType.PayFast => "PayFast",
                        _                            => "Paystack",
                    };

                    var payment = new Payment
                    {
                        PaymentNumber              = $"PAY-{stamp}-{shortId}",
                        Invoice                    = invoice,
                        Status                     = PaymentStatus.Pending,
                        Method                     = PaymentMethodType.Gateway,
                        Amount                     = overrideApplied ? providerAmount : installationFee,
                        CurrencyCode               = "ZAR",
                        GatewayName                = gatewayName,
                        GatewayReference           = intentPaymentReference,
                        GatewayTransactionId       = gatewayTransactionId,
                        IsTestAmountOverrideApplied = overrideApplied,
                        ActualProviderAmount       = overrideApplied ? providerAmount : (decimal?)null,
                        InvoiceAmountAtTime        = overrideApplied ? installationFee : (decimal?)null,
                        TestOverrideReason         = overrideApplied
                            ? $"OrderIntent UAT override (env={_env53.EnvironmentName})"
                            : null,
                        LastStatusChangedByUserId  = trackedIntent.ClaimedByUserId,
                    };
                    _dbContext.Payments.Add(payment);

                    var initiation = new PaymentInitiation
                    {
                        Invoice                     = invoice,
                        Payment                     = payment,
                        Provider                    = intentProvider,
                        Status                      = PaymentInitiationStatus.Pending,
                        Amount                      = payment.Amount,
                        CurrencyCode                = "ZAR",
                        ProviderReference           = intentPaymentReference,
                        WebhookApplyMode            = WebhookApplyMode.ApplyNormally,
                        IsTestAmountOverrideApplied = overrideApplied,
                        ActualProviderAmount        = overrideApplied ? providerAmount : (decimal?)null,
                        InvoiceAmountAtTime         = overrideApplied ? installationFee : (decimal?)null,
                    };
                    _dbContext.PaymentInitiations.Add(initiation);

                    trackedIntent.Status            = OrderIntentStatus.ConvertedToOrder;
                    trackedIntent.ConvertedOrderId  = order.Id;
                    trackedIntent.ConvertedAtUtc    = now;

                    await _dbContext.SaveChangesAsync(cancellationToken);
                    await tx.CommitAsync(cancellationToken);

                    createdOrder = order;
                    createdInvoice = invoice;
                    createdPayment = payment;
                }
                catch
                {
                    try { await tx.RollbackAsync(cancellationToken); } catch { /* swallow */ }
                    throw;
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OrderIntentConvert] atomic creation failed for reference={Reference}", intentPaymentReference);
            return Result<ConvertIntentPaymentToPaidOrderOutcomeDto>.Failure(
                ErrorCodes.EXCEPTION, $"{ex.GetType().Name}: {ex.Message}");
        }

        if (createdOrder is null || createdInvoice is null || createdPayment is null)
            return Result<ConvertIntentPaymentToPaidOrderOutcomeDto>.Failure(
                ErrorCodes.EXCEPTION, "Intent conversion did not produce an Order/Invoice/Payment.");

        // Drive the Payment Pending → Completed transition through the
        // canonical applier — it owns the UAT override settlement,
        // invoice → Paid arithmetic, and the EnsurePending
        // NetworkAccount post-commit hook. Best-effort: applier errors
        // are logged; the Order/Invoice/Payment rows already exist and
        // an admin can reconcile manually.
        var applyResult = await _paymentApplier.ApplyStatusChangeAsync(new ApplyPaymentStatusChangeRequestDto
        {
            PaymentId            = createdPayment.Id,
            NewStatus            = PaymentStatus.Completed,
            GatewayTransactionId = gatewayTransactionId,
            GatewayReference     = intentPaymentReference,
            PaidAtUtc            = paidAtUtc ?? DateTime.UtcNow,
            TriggerNotifications = true,
        }, cancellationToken);

        if (!applyResult.IsSuccess)
        {
            _logger.LogError(
                "[OrderIntentConvert] applier failed for {Reference} order {OrderNumber}: {Code} {Message}",
                intentPaymentReference, createdOrder.OrderNumber, applyResult.Code, applyResult.Message);
        }

        // Look up the NetworkAccount the applier should have ensured.
        var networkAccountId = await _dbContext.NetworkAccounts
            .AsNoTracking()
            .Where(n => n.OrderId == createdOrder.Id)
            .Select(n => (Guid?)n.Id)
            .FirstOrDefaultAsync(cancellationToken);

        // Go-live: capture the reusable Paystack authorization (if the
        // webhook / verify-and-apply caller passed one) and enable the
        // customer's AutoBillingEnabled flag so the first monthly
        // invoice can be auto-debited without a second opt-in step.
        // Best-effort — the order/invoice/payment rows already exist;
        // mandate failures are logged but never roll the conversion back.
        if (authorizationSnapshot is not null
            && authorizationSnapshot.Reusable
            && intent.ClaimedByUserId is Guid mandateUserId)
        {
            try
            {
                await _mandates.UpsertPaystackMandateAsync(new UpsertPaystackMandateRequestDto
                {
                    UserId                 = mandateUserId,
                    AuthorizationCode      = authorizationSnapshot.AuthorizationCode,
                    AuthorizationSignature = authorizationSnapshot.Signature,
                    ProviderCustomerCode   = authorizationSnapshot.ProviderCustomerCode,
                    Channel                = authorizationSnapshot.Channel,
                    CardType               = authorizationSnapshot.CardType,
                    Bank                   = authorizationSnapshot.Bank,
                    Last4                  = authorizationSnapshot.Last4,
                    ExpMonth               = authorizationSnapshot.ExpMonth,
                    ExpYear                = authorizationSnapshot.ExpYear,
                    AccountName            = authorizationSnapshot.AccountName,
                    CustomerEmail          = intent.Email,
                    IsReusable             = true,
                    ConsentSource          = CustomerMandateConsentSource.InstallationCheckout,
                    AutoEnableAutoBilling  = true,
                }, cancellationToken);
            }
            catch (Exception mandateEx)
            {
                _logger.LogError(mandateEx,
                    "[OrderIntentConvert] mandate upsert threw for reference={Reference} user={UserId} — order/invoice are intact",
                    intentPaymentReference, mandateUserId);
            }
        }
        else if (authorizationSnapshot is not null && !authorizationSnapshot.Reusable)
        {
            _logger.LogInformation(
                "[OrderIntentConvert] reference={Reference} authorization not reusable — mandate not stored, AutoBilling not flipped",
                intentPaymentReference);
        }

        _logger.LogInformation(
            "[OrderIntentConvert] reference={Reference} order={OrderNumber} invoice={InvoiceNumber} payment={PaymentNumber} networkAccount={NetworkAccountId} mandateCaptured={MandateCaptured}",
            intentPaymentReference, createdOrder.OrderNumber, createdInvoice.InvoiceNumber,
            createdPayment.PaymentNumber, networkAccountId,
            authorizationSnapshot is not null && authorizationSnapshot.Reusable);

        return Result<ConvertIntentPaymentToPaidOrderOutcomeDto>.Success(new ConvertIntentPaymentToPaidOrderOutcomeDto
        {
            Reference        = intentPaymentReference,
            OrderIntentId    = intent.Id,
            AlreadyConverted = false,
            OrderId          = createdOrder.Id,
            OrderNumber      = createdOrder.OrderNumber,
            InvoiceId        = createdInvoice.Id,
            InvoiceNumber    = createdInvoice.InvoiceNumber,
            PaymentId        = createdPayment.Id,
            PaymentNumber    = createdPayment.PaymentNumber,
            NetworkAccountId = networkAccountId,
            InvoiceAmount    = installationFee,
            ProviderAmount   = createdPayment.Amount,
            OverrideApplied  = createdPayment.IsTestAmountOverrideApplied,
        });
    }

    private static string SafeHost53(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "(empty)";
        try { return new Uri(url).Host; }
        catch { return "(unparseable)"; }
    }

    private async Task<ConvertIntentPaymentToPaidOrderOutcomeDto?> ResolveExistingConversionAsync(
        OrderIntent intent, Guid orderId, CancellationToken cancellationToken)
    {
        var order = await _dbContext.Orders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        if (order is null) return null;
        var invoice = await _dbContext.Invoices.AsNoTracking()
            .Where(i => i.OrderId == orderId)
            .OrderByDescending(i => i.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        var payment = invoice is null ? null : await _dbContext.Payments.AsNoTracking()
            .Where(p => p.InvoiceId == invoice.Id)
            .OrderByDescending(p => p.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        var networkAccountId = await _dbContext.NetworkAccounts.AsNoTracking()
            .Where(n => n.OrderId == orderId)
            .Select(n => (Guid?)n.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return new ConvertIntentPaymentToPaidOrderOutcomeDto
        {
            Reference        = intent.IntentPaymentReference ?? string.Empty,
            OrderIntentId    = intent.Id,
            AlreadyConverted = true,
            OrderId          = order.Id,
            OrderNumber      = order.OrderNumber,
            InvoiceId        = invoice?.Id ?? Guid.Empty,
            InvoiceNumber    = invoice?.InvoiceNumber ?? string.Empty,
            PaymentId        = payment?.Id ?? Guid.Empty,
            PaymentNumber    = payment?.PaymentNumber ?? string.Empty,
            NetworkAccountId = networkAccountId,
            InvoiceAmount    = invoice?.TotalAmount ?? 0m,
            ProviderAmount   = payment?.Amount ?? 0m,
            OverrideApplied  = payment?.IsTestAmountOverrideApplied ?? false,
        };
    }

}
