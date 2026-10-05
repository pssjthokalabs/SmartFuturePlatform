namespace SmartFuture.Shared.Enums.Openserve;

// Why a Product Qualification ran — persisted on each evidence row.
public enum OpenserveQualificationPurpose
{
    /// <summary>Customer/website/app coverage check (before any order exists).</summary>
    CoverageCheck = 0,

    /// <summary>Backend gate before payment / order creation.</summary>
    CheckoutGate = 1,

    OrderCreated = 2,
    PaymentConversion = 3,
    SubmissionSelfHeal = 4,
    AdminManual = 5,
    BuildingCandidatesRefresh = 6,

    /// <summary>Admin chose an AddressVerify candidate; its AMID was qualified.</summary>
    AdminCandidateSelection = 7,

    /// <summary>The customer chose (and confirmed) an AddressVerify candidate as their Openserve service location; its AMID was qualified.</summary>
    CustomerPremisesSelection = 8
}
