namespace SmartFuture.Shared.Enums.Openserve;

// The single answer to "can this Fibre package be ordered at this
// customer's address, and if not, why?" — computed from qualification
// evidence + the package mapping. Each value is a different situation and
// must never collapse into a generic "no coverage": only FtthUnavailable
// says Fibre is unavailable, and only for an Openserve premises that was
// established as the customer's.
public enum OpenserveFibreQualificationStatus
{
    /// <summary>No qualification evidence yet.</summary>
    NotQualified = 0,

    /// <summary>Openserve couldn't be asked / didn't answer usefully.</summary>
    QualificationFailed = 1,

    /// <summary>Nearby Openserve addresses were found but none is established as the customer's premises.</summary>
    AddressUnresolved = 2,

    /// <summary>No Openserve address candidates near the customer's location.</summary>
    NoAddressCandidates = 3,

    /// <summary>Legacy evidence: the AMID's address disagrees with the customer's and nobody confirmed it.</summary>
    AddressReviewRequired = 4,

    /// <summary>The customer's Openserve premises is established and has no immediately-available FTTH.</summary>
    FtthUnavailable = 5,

    /// <summary>FTTH is available, but the package's mapped Openserve product/speed is not.</summary>
    ProductUnavailable = 6,

    /// <summary>FTTH and product are eligible, but the MDU building/unit (BLD_NUM_ID) is not resolved.</summary>
    BuildingUnitRequired = 7,

    /// <summary>Premises established, FTTH + mapped product eligible, building/unit resolved — orderable.</summary>
    Orderable = 8
}
