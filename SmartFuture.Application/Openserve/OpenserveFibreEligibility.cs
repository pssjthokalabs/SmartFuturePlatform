using System.Globalization;
using System.Text.RegularExpressions;
using SmartFuture.Domain.Openserve;
using SmartFuture.Shared.Enums.Openserve;
using SmartFuture.Shared.Errors;

namespace SmartFuture.Application.Openserve;

/// <summary>
/// The one place that decides whether a Fibre package is orderable at an
/// address from Product Qualification evidence. The coverage check, the
/// checkout gate, the submission gate and Admin all use it, so they can't
/// disagree. Rules (spec §3.1.1.4–5):
///   1. An AMID identifies the address. It is not evidence of Fibre.
///   2. Fibre is available only when an FTTH entry is "Working"/"Available".
///      No FTTH infrastructure in the response = Fibre not available.
///   3. The package's mapped SKU must be offered on immediately-available
///      FTTH — ProductCodes match exactly (OFCTP is NOT OFC; nothing is
///      substituted) — and that product's downstreamSpeed ("maximum available
///      download speed at the address") must cover the mapped Capacity.
///   4. The address Openserve resolved must be the customer's property, or
///      an Admin must have explicitly accepted it.
/// </summary>
public static class OpenserveFibreEligibility
{
    /// <summary>FTTH availability across every infrastructure entry recorded on the evidence.</summary>
    public static OpenserveFibreAvailability EvaluateFibre(IReadOnlyCollection<OpenserveQualifiedProduct> rows)
    {
        if (rows.Count == 0) return OpenserveFibreAvailability.NotReturned;
        return rows.Any(r => r.IsImmediatelyAvailable) ? OpenserveFibreAvailability.Available : OpenserveFibreAvailability.NotYetAvailable;
    }

    /// <summary>
    /// Why a mapping's capacity can't be right for its package (e.g. a 200 Mbps
    /// package mapped to OFC 100), or null. Only checked when the package
    /// records a download speed; nothing is inferred beyond "the Openserve
    /// capacity must be the package's download speed".
    /// </summary>
    public static string? MappingCapacityConflict(string? capacity, string? capacityUom, int? packageDownloadMbps)
    {
        if (packageDownloadMbps is not { } download || string.IsNullOrWhiteSpace(capacity)) return null;
        if (!decimal.TryParse(capacity.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var mapped)) return null;
        return mapped == download
            ? null
            : $"The Openserve mapping's capacity ({capacity.Trim()} {capacityUom?.Trim()}) does not match the package's {download} Mbps download speed.";
    }

    public static (OpenserveProductEligibility Eligibility, string Reason) EvaluateProduct(IReadOnlyCollection<OpenserveQualifiedProduct> rows, OpenserveFibreAvailability fibre, string? sku,
        string? capacity, string? capacityUom, string? mappingConflict = null)
    {
        if (string.IsNullOrWhiteSpace(sku))
            return (OpenserveProductEligibility.NoMapping, "The package has no enabled Openserve mapping, so there is no Openserve product to check.");
        if (mappingConflict is not null)
            return (OpenserveProductEligibility.NoMapping, mappingConflict);

        var code = sku.Trim();
        var wanted = $"{code} {capacity?.Trim()} {capacityUom?.Trim()}".Trim();
        if (fibre != OpenserveFibreAvailability.Available)
        {
            return (OpenserveProductEligibility.FibreUnavailable, fibre == OpenserveFibreAvailability.NotYetAvailable
                ? $"{wanted} can't be ordered: Openserve reports Fibre at this address as not yet available ({Statuses(rows)})."
                : $"{wanted} can't be ordered: Openserve returned no Fibre (FTTH) infrastructure for this address.");
        }

        var offered = rows.Where(r => r.IsImmediatelyAvailable && !string.IsNullOrWhiteSpace(r.ProductCode)).ToList();
        var matches = offered.Where(r => string.Equals(r.ProductCode!.Trim(), code, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            var codes = offered.Select(r => r.ProductCode!.Trim().ToUpperInvariant()).Distinct().ToList();
            var reason = codes.Count == 0
                ? $"Openserve listed no products on the available Fibre infrastructure, so {code} can't be confirmed."
                : $"Openserve does not offer {code.ToUpperInvariant()} at this address (available: {string.Join(", ", codes)}).";
            var pending = rows.Any(r => !r.IsImmediatelyAvailable && string.Equals(r.ProductCode?.Trim(), code, StringComparison.OrdinalIgnoreCase));
            if (pending) reason += $" {code.ToUpperInvariant()} is listed only on infrastructure that is not yet available.";
            var thirdParty = codes.FirstOrDefault(c => c.StartsWith(code.ToUpperInvariant(), StringComparison.Ordinal) && c.EndsWith("TP", StringComparison.Ordinal));
            if (thirdParty is not null) reason += $" {thirdParty} (third-party infrastructure) is a different Openserve product and is not substituted.";
            return (OpenserveProductEligibility.ProductUnavailable, reason);
        }

        if (!decimal.TryParse(capacity?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var required))
            return (OpenserveProductEligibility.CapabilityUnavailable, $"The mapping's capacity '{capacity}' isn't a number, so it can't be checked against Openserve's speeds.");

        var speeds = matches.Select(m => new { Row = m, Down = m.DownstreamMbps ?? m.FibreMaxSpeedMbps }).ToList();
        var fit = speeds.Where(s => s.Down is { } down && down >= required).OrderByDescending(s => s.Down).FirstOrDefault();
        if (fit is not null)
        {
            var up = fit.Row.UpstreamMbps is { } u ? $" / {OpenserveAddressMatcher.Num(u)} Mbps up" : string.Empty;
            var network = string.IsNullOrWhiteSpace(fit.Row.InfrastructureType) ? "Openserve's network" : $"{fit.Row.InfrastructureType} infrastructure";
            return (OpenserveProductEligibility.Eligible, $"{code.ToUpperInvariant()} is offered on {network} up to {OpenserveAddressMatcher.Num(fit.Down)} Mbps down{up} — covers the mapped {wanted}.");
        }

        if (speeds.All(s => s.Down is null))
            return (OpenserveProductEligibility.CapabilityUnavailable, $"Openserve listed {code.ToUpperInvariant()} but gave no speed to confirm {wanted}.");

        var best = speeds.Max(s => s.Down ?? 0m);
        return (OpenserveProductEligibility.CapabilityUnavailable, $"{code.ToUpperInvariant()} is offered here only up to {OpenserveAddressMatcher.Num(best)} Mbps; the mapping needs {wanted}.");
    }

    /// <summary>
    /// Full assessment of an order/package against stored evidence. Fibre and
    /// address come from the evidence; product eligibility is re-evaluated
    /// against the CURRENT mapping, so a corrected mapping is picked up.
    /// </summary>
    public static OpenserveEligibilityAssessment Assess(OpenserveQualificationResult? evidence, PackageOpenserveMapping? mapping, int? packageDownloadMbps)
    {
        if (evidence is null) return new OpenserveEligibilityAssessment(OpenserveQualificationState.NoEvidence, null);
        if (!evidence.CallSucceeded) return new OpenserveEligibilityAssessment(OpenserveQualificationState.QualificationFailed, evidence);
        if (!evidence.AddressIdentified)
        {
            // No premises established: nothing about Fibre or products is known
            // for the customer's address, so neither is evaluated.
            var state = evidence.AddressResolution switch
            {
                OpenserveAddressResolution.Unresolved => OpenserveQualificationState.AddressUnresolved,
                OpenserveAddressResolution.NoCandidates => OpenserveQualificationState.NoAddressCandidates,
                _ => OpenserveQualificationState.AddressNotIdentified
            };
            return new OpenserveEligibilityAssessment(state, evidence);
        }

        var conflict = mapping is null ? null : MappingCapacityConflict(mapping.Capacity, mapping.CapacityUom, packageDownloadMbps);
        var (product, reason) = EvaluateProduct(evidence.Products.ToList(), evidence.FibreAvailability, mapping?.Sku, mapping?.Capacity, mapping?.CapacityUom, conflict);
        return new OpenserveEligibilityAssessment(OpenserveQualificationState.Evaluated, evidence, product, reason);
    }

    /// <summary>Customer wording for a coverage check (no package chosen yet).</summary>
    public static (string Title, string Message) LocationText(OpenserveEligibilityAssessment location, int eligiblePackages)
    {
        if (location.State != OpenserveQualificationState.Evaluated || !location.AddressCleared || location.Fibre != OpenserveFibreAvailability.Available)
            return location.CustomerText;
        return eligiblePackages > 0
            ? ("Good news — Fibre is available at this address.", "Openserve has confirmed Fibre at your address. Choose one of the packages below.")
            : ("Fibre is available here, but not for our current packages.",
                "Openserve has Fibre at this address, but none of our current Fibre packages can be ordered there yet. Leave your details and our team will contact you.");
    }

    private static string Statuses(IEnumerable<OpenserveQualifiedProduct> rows) =>
        string.Join(", ", rows.Select(r => r.FtthStatus).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).DefaultIfEmpty("no status"));
}

public enum OpenserveQualificationState
{
    /// <summary>No evidence recorded (never qualified, or qualified before evidence was stored).</summary>
    NoEvidence = 0,

    /// <summary>The Product Qualification call failed (HTTP/transport/business error).</summary>
    QualificationFailed = 1,

    /// <summary>Openserve answered but returned no AMID — the address isn't identified.</summary>
    AddressNotIdentified = 2,

    /// <summary>Address identified; Fibre/product/address checks evaluated.</summary>
    Evaluated = 3,

    /// <summary>FORCEVERIFY returned nearby Openserve records, but none was established as the customer's premises.</summary>
    AddressUnresolved = 4,

    /// <summary>FORCEVERIFY returned no Openserve address records near the location.</summary>
    NoAddressCandidates = 5
}

/// <summary>The verdict on one package at one address, with the reasons Admin and the customer see.</summary>
public sealed record OpenserveEligibilityAssessment(
    OpenserveQualificationState State, OpenserveQualificationResult? Evidence, OpenserveProductEligibility Product = OpenserveProductEligibility.NotEvaluated, string? ProductReason = null)
{
    public OpenserveFibreAvailability Fibre => State == OpenserveQualificationState.Evaluated ? Evidence!.FibreAvailability : OpenserveFibreAvailability.NotEvaluated;
    public OpenserveAddressMatch AddressMatch => State == OpenserveQualificationState.Evaluated ? Evidence!.AddressMatch : OpenserveAddressMatch.NotEvaluated;
    public bool AddressAccepted => Evidence?.AddressAcceptedAtUtc is not null;

    /// <summary>The address is the customer's: matched, or (review/mismatch) explicitly accepted by an Admin.</summary>
    public bool AddressCleared => AddressMatch == OpenserveAddressMatch.Matched || (AddressAccepted && AddressMatch is OpenserveAddressMatch.ReviewRequired or OpenserveAddressMatch.Mismatch);

    /// <summary>The customer's Openserve premises is established — only then do Fibre/product answers describe the customer's address.</summary>
    public bool PremisesEstablished => State == OpenserveQualificationState.Evaluated && AddressCleared;

    public bool IsEligible => State == OpenserveQualificationState.Evaluated && Fibre == OpenserveFibreAvailability.Available && Product == OpenserveProductEligibility.Eligible && AddressCleared;

    /// <summary>The one situation this is — never a generic "no coverage". Building/unit resolution comes from the order or the checkout's unit number.</summary>
    public OpenserveFibreQualificationStatus Status(bool buildingUnitResolved = true) => State switch
    {
        OpenserveQualificationState.NoEvidence => OpenserveFibreQualificationStatus.NotQualified,
        OpenserveQualificationState.QualificationFailed => OpenserveFibreQualificationStatus.QualificationFailed,
        OpenserveQualificationState.AddressUnresolved => OpenserveFibreQualificationStatus.AddressUnresolved,
        OpenserveQualificationState.NoAddressCandidates or OpenserveQualificationState.AddressNotIdentified => OpenserveFibreQualificationStatus.NoAddressCandidates,
        _ => !AddressCleared ? OpenserveFibreQualificationStatus.AddressReviewRequired
            : Fibre != OpenserveFibreAvailability.Available ? OpenserveFibreQualificationStatus.FtthUnavailable
            : Product != OpenserveProductEligibility.Eligible ? OpenserveFibreQualificationStatus.ProductUnavailable
            : !buildingUnitResolved ? OpenserveFibreQualificationStatus.BuildingUnitRequired
            : OpenserveFibreQualificationStatus.Orderable
    };

    /// <summary>The machine-readable checkout refusal code for a status that isn't orderable.</summary>
    public static string CheckoutErrorCode(OpenserveFibreQualificationStatus status) => status switch
    {
        OpenserveFibreQualificationStatus.NotQualified or OpenserveFibreQualificationStatus.QualificationFailed => ErrorCodes.UPSTREAM_UNAVAILABLE,
        OpenserveFibreQualificationStatus.FtthUnavailable => ErrorCodes.OPENSERVE_FTTH_UNAVAILABLE,
        OpenserveFibreQualificationStatus.ProductUnavailable => ErrorCodes.OPENSERVE_PRODUCT_UNAVAILABLE,
        OpenserveFibreQualificationStatus.BuildingUnitRequired => ErrorCodes.OPENSERVE_BUILDING_UNIT_REQUIRED,
        _ => ErrorCodes.OPENSERVE_ADDRESS_UNRESOLVED
    };

    /// <summary>The submission blocker (code + Admin-facing reason), or null when eligible.</summary>
    public (string Code, string Reason)? Blocker
    {
        get
        {
            switch (State)
            {
                case OpenserveQualificationState.NoEvidence:
                    return (OpenserveBlockedCodes.Qualification,
                        "Product Qualification evidence (Fibre availability, products and Openserve's address) has not been recorded for this order — it was qualified before these checks existed, or never. Run Product Qualification.");
                case OpenserveQualificationState.QualificationFailed:
                    return (OpenserveBlockedCodes.Qualification, $"The last Product Qualification failed ({Evidence!.ErrorMessage ?? "no reason given"}). Run Product Qualification again.");
                case OpenserveQualificationState.AddressNotIdentified:
                    return (OpenserveBlockedCodes.Qualification, "Openserve did not identify this address (no AMID). Check the installation address and coordinates, then run Product Qualification again.");
                case OpenserveQualificationState.AddressUnresolved:
                    return (OpenserveBlockedCodes.AddressUnresolved,
                        $"The customer's Openserve premises has not been established: {Evidence!.AddressResolutionDetail} Fibre availability at the customer's address is therefore UNKNOWN — "
                        + "no nearby record's coverage applies to it. Confirm the property with the customer, then choose the matching Openserve address (Openserve address verification), or request a coverage check.");
                case OpenserveQualificationState.NoAddressCandidates:
                    return (OpenserveBlockedCodes.AddressUnresolved,
                        "Openserve returned no address records near the customer's location, so the premises can't be established. Check the installation address/coordinates, then re-run address verification.");
            }

            var fibreNote = Fibre switch
            {
                OpenserveFibreAvailability.NotReturned => $"Openserve returned no Fibre (FTTH) infrastructure for AMID {Evidence!.Amid}{At} — Fibre is not available there. An AMID identifies the address only; it is not Fibre coverage.",
                OpenserveFibreAvailability.NotYetAvailable => $"Fibre at AMID {Evidence!.Amid}{At} is not yet available (FTTH status: {Evidence.FtthStatusSummary ?? "unknown"}).",
                _ => null
            };

            if (!AddressCleared)
            {
                // The AMID isn't established as the customer's premises, so its
                // Fibre answer (whatever it is) says nothing about the customer's address.
                var reason = $"Openserve record {Evidence!.CanonicalAddress ?? $"AMID {Evidence.Amid}"}{Distance} is not confirmed as the customer's premises ({Evidence.CustomerAddress ?? "not recorded"}): {Evidence.AddressMatchDetail} "
                    + "Fibre availability at the customer's address is therefore unknown. Re-run address verification, choose the correct Openserve address, or accept this one only if it really is the customer's property.";
                return (OpenserveBlockedCodes.AddressReview, reason);
            }

            if (fibreNote is not null) return (OpenserveBlockedCodes.FibreUnavailable, fibreNote);
            if (Product != OpenserveProductEligibility.Eligible) return (OpenserveBlockedCodes.ProductUnavailable, ProductReason ?? "The mapped Openserve product is not available at this address.");
            return null;
        }
    }

    /// <summary>Customer-safe wording (no AMID, codes or internal detail).</summary>
    public (string Title, string Message) CustomerText
    {
        get
        {
            const string requestHelp = "If you think this is wrong, request a coverage check and our team will confirm.";
            switch (State)
            {
                case OpenserveQualificationState.NoEvidence:
                case OpenserveQualificationState.QualificationFailed:
                    return ("We couldn't confirm Fibre availability right now.", "Please try again in a few minutes.");
                case OpenserveQualificationState.AddressNotIdentified:
                case OpenserveQualificationState.NoAddressCandidates:
                    return ("We couldn't find your property on the Openserve network.",
                        "Openserve has no address records near your selected location. Please check your address or request a coverage check.");
                case OpenserveQualificationState.AddressUnresolved:
                    return (UnresolvedTitle, UnresolvedMessage);
            }

            // Not "no Fibre": the record isn't established as the customer's property.
            if (!AddressCleared) return (UnresolvedTitle, UnresolvedMessage);

            // From here the premises is the customer's own — Fibre answers apply to their address.
            return Fibre switch
            {
                OpenserveFibreAvailability.NotReturned => ("Fibre isn't available at your address yet.", "There's no Openserve Fibre network at your property yet. Leave your details and we'll let you know when it becomes available."),
                OpenserveFibreAvailability.NotYetAvailable => ("Fibre is planned here but isn't available yet.", "Fibre at your property isn't ready to order yet. Leave your details and we'll let you know when it becomes available."),
                _ => Product == OpenserveProductEligibility.Eligible
                    ? ("Fibre is available at this address.", "Choose one of the packages available at your address.")
                    : ("This package isn't available at your address.", "Please choose one of the packages available at your address.")
            };
        }
    }

    public const string UnresolvedTitle = "We couldn't confirm your exact property on the Openserve network.";

    public const string UnresolvedMessage =
        "We found Openserve network records near your selected location, but we couldn't automatically match your exact property. Please confirm your address or request a coverage check.";

    private string At => string.IsNullOrWhiteSpace(Evidence?.CanonicalAddress) ? string.Empty : $" ({Evidence!.CanonicalAddress})";

    private string Distance => Evidence?.DistanceMeters is { } m ? $" {OpenserveAddressMatcher.Num(m)} m from the queried point" : string.Empty;

}

/// <summary>
/// Compares the customer's address with the canonical address Openserve
/// returned for the AMID. Qualification runs on coordinates and Openserve
/// answers with the nearest address it knows, so this is what stops a
/// neighbour's AMID from silently becoming the customer's.
///
/// Street number and street name decide; a distance (DIST_M) is reported but
/// never decides on its own. Suburb/town are compared only to catch the same
/// street in a different area — naming differs too often between Google and
/// Openserve ("Thorn Field Estate" vs "MONAVONI X 6") to require them.
/// </summary>
public static class OpenserveAddressMatcher
{
    public sealed record CustomerAddress(string? AddressLine1, string? Suburb, string? City, string? Province)
    {
        public string Display => string.Join(", ", new[] { AddressLine1, Suburb, City }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
    }

    public static (OpenserveAddressMatch Match, string Detail) Compare(CustomerAddress customer, OpenserveQualificationResult canonical)
    {
        var distance = canonical.DistanceMeters is { } d ? $" Openserve's point is {Num(d)} m from the queried location." : string.Empty;
        var openserveLine = Join(canonical.StreetNumber, canonical.StreetName, canonical.StreetType);
        if (string.IsNullOrWhiteSpace(canonical.StreetName) && string.IsNullOrWhiteSpace(canonical.StreetNumber))
            return (OpenserveAddressMatch.ReviewRequired, $"Openserve returned no street address to compare with the customer's.{distance}");

        var (customerNumber, customerStreet) = SplitStreet(customer.AddressLine1);
        var openserveNumber = NormalizeNumber(canonical.StreetNumber);
        var openserveStreet = NormalizeStreet(canonical.StreetName);
        var customerStreetKey = NormalizeStreet(customerStreet);
        var customerLine = customer.AddressLine1?.Trim();

        if (customerNumber.Length > 0 && openserveNumber.Length > 0)
        {
            if (customerNumber != openserveNumber)
            {
                var streetNote = customerStreetKey.Length > 0 && openserveStreet.Length > 0 && customerStreetKey != openserveStreet ? " and a different street" : string.Empty;
                return (OpenserveAddressMatch.Mismatch,
                    $"Street number differs: Openserve resolved number {canonical.StreetNumber!.Trim()} ({openserveLine}){streetNote}, the customer's address is number {customerNumber} ({customerLine}).{distance}");
            }

            if (customerStreetKey.Length == 0 || openserveStreet.Length == 0)
                return (OpenserveAddressMatch.ReviewRequired, $"Street number {customerNumber} agrees, but the street name can't be compared (Openserve: '{openserveLine}', customer: '{customerLine}').{distance}");
            if (customerStreetKey != openserveStreet)
                return (OpenserveAddressMatch.ReviewRequired, $"Street number {customerNumber} agrees but the street differs (Openserve: '{openserveLine}', customer: '{customerLine}').{distance}");

            var locality = LocalityConflict(customer, canonical);
            if (locality is not null) return (OpenserveAddressMatch.ReviewRequired, $"Street matches ({openserveLine}) but {locality}.{distance}");
            return (OpenserveAddressMatch.Matched, $"Street number and street match ({openserveLine}).{distance}");
        }

        if (customerNumber.Length == 0 && openserveNumber.Length > 0)
            return (OpenserveAddressMatch.ReviewRequired, $"The customer's address has no street number ('{customerLine}'); Openserve resolved number {canonical.StreetNumber!.Trim()} ({openserveLine}).{distance}");
        if (customerNumber.Length > 0)
            return (OpenserveAddressMatch.ReviewRequired, $"Openserve's address has no street number to compare with the customer's number {customerNumber} (Openserve: '{openserveLine}').{distance}");
        return (OpenserveAddressMatch.ReviewRequired, $"Neither address has a street number, so the property can't be confirmed (Openserve: '{openserveLine}', customer: '{customerLine}').{distance}");
    }

    /// <summary>"2 Palmas Street, Thorn Field Estate" → ("2", "Palmas Street"). Looks for the comma-separated part that starts with a house number.</summary>
    public static (string Number, string Street) SplitStreet(string? addressLine1)
    {
        if (string.IsNullOrWhiteSpace(addressLine1)) return (string.Empty, string.Empty);
        var parts = addressLine1.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var match = StreetPattern.Match(part);
            if (match.Success) return (NormalizeNumber(match.Groups["number"].Value), match.Groups["street"].Value.Trim());
        }
        return (string.Empty, parts.FirstOrDefault() ?? string.Empty);
    }

    public static string NormalizeNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var key = new string(value.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray()).TrimStart('0');
        return key.Length == 0 && value.Contains('0') ? "0" : key;
    }

    /// <summary>Upper-case words with the trailing street type removed: "Palmas Street" and "PALMAS" (type ST) both give "PALMAS".</summary>
    public static string NormalizeStreet(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var words = Regex.Replace(value.ToUpperInvariant(), @"[^A-Z0-9]+", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && StreetTypes.Contains(words[^1])) words.RemoveAt(words.Count - 1);
        return string.Join(' ', words);
    }

    private static string? LocalityConflict(CustomerAddress customer, OpenserveQualificationResult canonical)
    {
        var customerPlaces = new[] { customer.Suburb, customer.City }.Select(NormalizePlace).Where(p => p.Length > 0).ToList();
        var openservePlaces = new[] { canonical.Suburb, canonical.Town }.Select(NormalizePlace).Where(p => p.Length > 0).ToList();
        if (customerPlaces.Count == 0 || openservePlaces.Count == 0) return null;
        var anyAgree = customerPlaces.Any(c => openservePlaces.Any(o => o == c || o.Contains(c, StringComparison.Ordinal) || c.Contains(o, StringComparison.Ordinal)));
        return anyAgree ? null : $"the suburb/town differ (Openserve: {Join(canonical.Suburb, canonical.Town)}; customer: {Join(customer.Suburb, customer.City)})";
    }

    // "MONAVONI X 6" / "Monavoni Ext 6" / "Monavoni Extension 6" all compare as "MONAVONI".
    internal static string NormalizePlace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var upper = Regex.Replace(value.ToUpperInvariant(), @"[^A-Z0-9]+", " ").Trim();
        upper = Regex.Replace(upper, @"\s+(X|EXT|EXTENSION)\s*\d+\b.*$", string.Empty);
        return upper.Trim();
    }

    /// <summary>Numbers in messages use Openserve's own notation ("32.77"), whatever the server culture.</summary>
    internal static string Num(decimal? value) => value?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Join(params string?[] parts) => string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));

    private static readonly Regex StreetPattern = new(@"^(?:NO\.?\s*)?(?<number>\d+[A-Za-z]?)(?:\s*[-/]\s*\d+[A-Za-z]?)?\s+(?<street>[A-Za-z].*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static readonly HashSet<string> StreetTypes = new(StringComparer.Ordinal)
    {
        "STREET", "ST", "STR", "ROAD", "RD", "AVENUE", "AVE", "AV", "DRIVE", "DR", "CRESCENT", "CRES", "CR", "LANE", "LN", "CLOSE", "CL", "WAY", "PLACE", "PL", "BLV",
        "BOULEVARD", "BLVD", "COURT", "CT", "CIRCLE", "CIR", "TERRACE", "TER", "HIGHWAY", "HWY", "SQUARE", "SQ", "GROVE", "GR", "MEWS", "ALLEY",
        "STRAAT", "WEG", "LAAN", "RYLAAN", "SINGEL", "PAD", "PLEIN", "SIRKEL", "HOF"
    };
}
