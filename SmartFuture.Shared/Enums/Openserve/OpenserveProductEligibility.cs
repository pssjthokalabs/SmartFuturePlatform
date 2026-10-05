namespace SmartFuture.Shared.Enums.Openserve;

// Whether a package's Openserve mapping (SKU + capacity) is orderable at
// the address according to the Product Qualification response.
public enum OpenserveProductEligibility
{
    NotEvaluated = 0,

    /// <summary>The mapped ProductCode is offered on immediately-available FTTH and supports the mapped capacity.</summary>
    Eligible = 1,

    /// <summary>No immediately-available FTTH at the address.</summary>
    FibreUnavailable = 2,

    /// <summary>FTTH is available, but the mapped ProductCode is not offered there.</summary>
    ProductUnavailable = 3,

    /// <summary>The ProductCode is offered, but not at the mapped capacity (or Openserve gave no speed to confirm it).</summary>
    CapabilityUnavailable = 4,

    /// <summary>The package has no enabled, consistent Openserve mapping to check.</summary>
    NoMapping = 5
}
