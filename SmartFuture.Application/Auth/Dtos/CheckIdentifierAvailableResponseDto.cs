namespace SmartFuture.Application.Auth.Dtos;

/// <summary>
/// Response shape for the lightweight email / phone availability probes used
/// by the mobile registration wizard. The mobile app calls these to decide
/// whether to advance to the next step BEFORE collecting more data, so the
/// shape is intentionally minimal: a single boolean plus the canonical form
/// of the identifier the server compared against (so the client can display
/// the value it would actually be stored as for a phone number).
/// </summary>
public class CheckIdentifierAvailableResponseDto
{
    public bool Available { get; set; }

    /// <summary>
    /// Canonical form of the checked identifier (lower-cased email, or
    /// E.164-style normalised phone). Optional — null when the input could
    /// not be normalised.
    /// </summary>
    public string? Normalised { get; set; }
}
