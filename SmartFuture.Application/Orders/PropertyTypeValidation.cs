using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.ServicePackages;

namespace SmartFuture.Application.Orders;

// Residence/property type validation, extracted as a standalone static
// class (rather than a private method on a single service) so it's
// directly unit-testable and reusable across every order-creation
// entry point: OrderService.CreateMineAsync/CreateFreeActivationMineAsync
// AND OrderIntentService.Phase53's InitiateClientPaymentAsync (the
// "Order and Pay" checkout mobile/ClientZone actually use for a real,
// non-mock payment). Returns a plain error message (null = valid)
// rather than a typed Result<T> so every caller — each of which returns
// a DIFFERENT Result<TResponse> — can wrap it in its own failure shape.
public static class PropertyTypeValidation
{
    /// <summary>
    /// Deliberately narrow — only the two rules explicitly required:
    ///   1. PropertyType is required for a new Fibre order (every other
    ///      package type may omit it).
    ///   2. Apartment/Flat requires a unit/flat number.
    /// Complex/Estate/Townhouse/StudentResidence/BusinessOffice capture
    /// BuildingComplexName/UnitNumber when the customer supplies them but
    /// are NOT hard-required at this layer — the UI still prompts for
    /// them, this is just the server-side floor. House needs neither.
    /// Returns null when the input is valid.
    /// </summary>
    public static string? Validate(
        ServicePackageType packageType, PropertyType? propertyType,
        string? buildingComplexName, string? unitNumber)
    {
        if (packageType == ServicePackageType.Fibre && propertyType is null)
        {
            return "PropertyType is required for a Fibre installation order.";
        }

        if (propertyType == PropertyType.Apartment && string.IsNullOrWhiteSpace(unitNumber))
        {
            return "UnitNumber is required when PropertyType is Apartment.";
        }

        if (buildingComplexName is { Length: > 200 })
        {
            return "BuildingComplexName must be 200 characters or fewer.";
        }

        if (unitNumber is { Length: > 50 })
        {
            return "UnitNumber must be 50 characters or fewer.";
        }

        return null;
    }
}
