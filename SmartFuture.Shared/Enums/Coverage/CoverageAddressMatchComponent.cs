namespace SmartFuture.Shared.Enums.Coverage;

// Bitmask of address components a rule is allowed to match against.
// Persisted on CoverageMapRule.AllowedComponents as `int`.
//
// Admin picks these when creating a rule. Fields NOT ticked are simply
// ignored during matching, so a rule that lists MatchText="Centurion"
// with only Suburb+City+Town+Province ticked will NOT match a
// coincidental "12 Centurion Street" that appears in AddressLine1.
//
// The `SafeAreaDefault` bundle is what the admin form pre-ticks for new
// rules — biased conservative so admins have to opt-in to street-level
// matching.
[System.Flags]
public enum CoverageAddressMatchComponent
{
    None             = 0,
    FullAddress      = 1 << 0,   // 1     — pre-Google raw text bag
    AddressLine1     = 1 << 1,   // 2     — RISKY: contains street name
    AddressLine2     = 1 << 2,   // 4     — RISKY: unit / complex
    StreetName       = 1 << 3,   // 8     — RISKY: bare street
    Suburb           = 1 << 4,   // 16
    City             = 1 << 5,   // 32
    Town             = 1 << 6,   // 64
    Province         = 1 << 7,   // 128
    PostalCode       = 1 << 8,   // 256
    Country          = 1 << 9,   // 512
    FormattedAddress = 1 << 10,  // 1024  — RISKY: full Google string
    PlaceName        = 1 << 11,  // 2048  — RISKY: business/POI name

    // Recommended default for new rules — area-level components only.
    SafeAreaDefault  = Suburb | City | Town | Province
}
