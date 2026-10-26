using Microsoft.Extensions.Logging;
using SmartFuture.Application.Coverage;
using SmartFuture.Application.Coverage.Dtos;
using SmartFuture.Application.Openserve;
using SmartFuture.Application.Orders.Dtos;
using SmartFuture.Application.Persistence;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Errors;

namespace SmartFuture.Application.Orders;

/// <param name="Allowed">False → refuse the checkout with <paramref name="ErrorCode"/>/<paramref name="Message"/>.</param>
/// <param name="Route">Who fulfils the order; persisted on the Order / OrderIntent.</param>
/// <param name="EvidenceId">Openserve Product Qualification evidence that cleared an Openserve order.</param>
/// <param name="InclusionRuleId">The Coverage Map Include rule that qualified an internal order.</param>
public sealed record FibreRouteDecision(bool Allowed, OrderFulfilmentRoute Route, Guid? EvidenceId = null, Guid? InclusionRuleId = null, string? InclusionRuleName = null,
    string? ErrorCode = null, string? Message = null);

// Decides WHO fulfils a Fibre order at checkout — the single place that does,
// so order creation, the payment-first intent and the address change agree.
//
// Precedence (business decision 2026-10-25, applied per package):
//   1. Openserve first. While the integration can qualify, the authenticated
//      Product Qualification gate decides; a package it allows is an Openserve
//      order (route Openserve, its evidence kept).
//   2. SmartFuture's coverage inclusion list as the fallback. When Openserve
//      can't order the package there (no Fibre, product/speed not offered,
//      address not matched, unit unresolved, Openserve unreachable) an ACTIVE
//      Coverage Map Include rule — with no Exclude rule matching — makes it an
//      internal SmartFuture order (route InternalInclusion): no AMID, no service
//      premises, no qualification, never sent to Openserve.
//   3. Neither → refused with Openserve's customer-safe reason (unchanged).
// Integration off (legacy): there is no gate, and the inclusion list decides
// exactly as the legacy coverage check does — an Include match is internal;
// anything else is Openserve's network (the public GIS lookup).
public static class FibreFulfilmentRouting
{
    public static async Task<FibreRouteDecision> DecideAsync(IAppDbContext dbContext, IOpenserveQualificationService qualification, Guid servicePackageId,
        OpenserveLocationQuery openserveQuery, CoverageCheckRequestDto inclusionAddress, ILogger logger, CancellationToken cancellationToken = default)
    {
        var gate = await qualification.CheckFibreCheckoutAsync(openserveQuery, servicePackageId, cancellationToken);
        if (gate.Applies && gate.Allowed) return new FibreRouteDecision(true, OrderFulfilmentRoute.Openserve, gate.EvidenceId);

        var inclusion = await CoverageMapRuleEvaluator.EvaluateAsync(dbContext, inclusionAddress, cancellationToken);
        var included = CoverageMapRuleEvaluator.IsInclusion(inclusion);

        if (!gate.Applies) return included ? Internal(inclusion) : new FibreRouteDecision(true, OrderFulfilmentRoute.Openserve);

        if (included)
        {
            logger.LogInformation("Fibre package {PackageId}: Openserve can't order it here ({Code}); coverage inclusion rule {RuleId} ({RuleName}) applies — SmartFuture fulfils it, never sent to Openserve.",
                servicePackageId, gate.ErrorCode, inclusion.MatchedRuleId, inclusion.MatchedRuleName);
            return Internal(inclusion);
        }

        return new FibreRouteDecision(false, OrderFulfilmentRoute.NotRecorded, ErrorCode: gate.ErrorCode ?? ErrorCodes.FIBRE_NOT_ELIGIBLE,
            Message: gate.Message ?? "This Fibre package isn't available at your address.");
    }

    /// <summary>
    /// The coverage-check shape of a checkout address: the installation fields the
    /// order carries plus the extra components the client matched on during the
    /// coverage check (<see cref="CoverageMatchFieldsDto"/>).
    /// </summary>
    public static CoverageCheckRequestDto InclusionAddress(string? addressLine1, string? addressLine2, string? suburb, string? city, string? province, string? postalCode, string? country,
        CoverageMatchFieldsDto? coverageMatch) => new()
    {
        AddressText = coverageMatch?.AddressText,
        AddressLine1 = addressLine1,
        AddressLine2 = addressLine2,
        StreetName = coverageMatch?.StreetName,
        Suburb = suburb,
        City = city,
        Town = coverageMatch?.Town,
        Province = province,
        PostalCode = postalCode,
        Country = country,
        FormattedAddress = coverageMatch?.FormattedAddress,
        PlaceName = coverageMatch?.PlaceName
    };

    private static FibreRouteDecision Internal(CoverageMapEvaluationResult inclusion) =>
        new(true, OrderFulfilmentRoute.InternalInclusion, InclusionRuleId: inclusion.MatchedRuleId, InclusionRuleName: inclusion.MatchedRuleName);
}
