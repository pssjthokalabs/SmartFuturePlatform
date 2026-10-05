using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SmartFuture.Shared.Enums.Openserve;

namespace SmartFuture.Application.Openserve;

/// <summary>
/// Chooses the customer's Openserve premises from a FORCEVERIFY=Y answer
/// (AddressVerify[]) — or deliberately chooses nothing.
///
/// Openserve's Address Master and Google/municipal addresses can disagree
/// even at the same coordinates (staging: the pin of "2 Palmas Street,
/// Thorn Field Estate" IS "8 PALMAS ST MONAVONI X 6" at DIST 0). So:
///   • a candidate matches only when its street name AND street number
///     equal the customer's (locality must not contradict);
///   • distance never decides — DIST 0 with another house number is not a
///     match, and the closest record is never assumed;
///   • exactly one matching AMID → AutoMatched; none, or several → Unresolved
///     (never guessed); an empty list → NoCandidates.
/// </summary>
public static class OpenserveAddressCandidateMatcher
{
    public static OpenserveAddressResolutionResult Resolve(OpenserveAddressMatcher.CustomerAddress customer, IReadOnlyList<OpenserveAddressCandidate>? candidates)
    {
        var list = (candidates ?? Array.Empty<OpenserveAddressCandidate>()).Where(c => !string.IsNullOrWhiteSpace(c.Amid)).ToList();
        if (list.Count == 0)
            return new OpenserveAddressResolutionResult(OpenserveAddressResolution.NoCandidates, null, Array.Empty<OpenserveCandidateAssessment>(),
                "Openserve returned no address records near the customer's location.");

        var assessments = list.Select(c => Assess(customer, c)).ToList();
        var matched = assessments.Where(a => a.Match == OpenserveCandidateMatch.Matched).GroupBy(a => a.Candidate.Amid!.Trim(), StringComparer.OrdinalIgnoreCase).ToList();
        var customerLine = string.IsNullOrWhiteSpace(customer.AddressLine1) ? "(no street address)" : customer.AddressLine1.Trim();

        if (matched.Count == 1)
        {
            var chosen = matched[0].First().Candidate;
            return new OpenserveAddressResolutionResult(OpenserveAddressResolution.AutoMatched, chosen, assessments,
                $"Openserve record {chosen.Address} (AMID {chosen.Amid}) matches the customer's street number and street ('{customerLine}').");
        }

        if (matched.Count > 1)
            return new OpenserveAddressResolutionResult(OpenserveAddressResolution.Unresolved, null, assessments,
                $"{matched.Count} different Openserve records match '{customerLine}' ({string.Join(", ", matched.Select(m => $"AMID {m.Key}"))}) — not guessed; choose one after confirming with the customer.");

        var (number, _) = OpenserveAddressMatcher.SplitStreet(customer.AddressLine1);
        var why = number.Length == 0
            ? $"The customer's address ('{customerLine}') has no street number, so no Openserve record can be matched automatically."
            : $"None of the {list.Count} Openserve address record(s) near the customer's location matches '{customerLine}'.";
        return new OpenserveAddressResolutionResult(OpenserveAddressResolution.Unresolved, null, assessments, why);
    }

    public static OpenserveCandidateAssessment Assess(OpenserveAddressMatcher.CustomerAddress customer, OpenserveAddressCandidate candidate)
    {
        var (customerNumber, customerStreetText) = OpenserveAddressMatcher.SplitStreet(customer.AddressLine1);
        var customerStreet = OpenserveAddressMatcher.NormalizeStreet(customerStreetText);
        var parsed = ParseLrAddress(candidate.Address);

        if (parsed.Number.Length == 0 || parsed.Tokens.Count == 0)
            return new OpenserveCandidateAssessment(candidate, OpenserveCandidateMatch.NotComparable, "Openserve's record has no street number to compare.");
        if (customerNumber.Length == 0 || customerStreet.Length == 0)
            return new OpenserveCandidateAssessment(candidate, OpenserveCandidateMatch.NotComparable, "The customer's address has no street number and street to compare.");

        // Street first: a record on another street is a different street even if the number happens to agree.
        var sameStreet = parsed.Street is { Length: > 0 } street
            ? street == customerStreet
            : StartsWithWords(parsed.AfterNumber, customerStreet);
        if (!sameStreet)
            return new OpenserveCandidateAssessment(candidate, OpenserveCandidateMatch.StreetMismatch,
                $"Different street ({parsed.Street ?? string.Join(' ', parsed.AfterNumber)} vs {customerStreet}).");

        if (parsed.Number != customerNumber)
            return new OpenserveCandidateAssessment(candidate, OpenserveCandidateMatch.StreetNumberMismatch, $"Street number differs ({parsed.Number} vs {customerNumber}).");

        // Same street name AND number elsewhere is only a match if the area doesn't contradict it.
        var customerPlaces = new[] { customer.Suburb, customer.City }.Select(OpenserveAddressMatcher.NormalizePlace).Where(p => p.Length > 0).ToList();
        var locality = $" {Regex.Replace(string.Join(' ', parsed.Locality), @"\b(X|EXT|EXTENSION)\s*\d+\b", " ")} ";
        if (customerPlaces.Count > 0 && locality.Trim().Length > 0 && !customerPlaces.Any(p => Regex.Replace(locality, @"\s+", " ").Contains($" {p} ", StringComparison.Ordinal)))
            return new OpenserveCandidateAssessment(candidate, OpenserveCandidateMatch.LocalityMismatch,
                $"Street number and street agree, but the area differs ({string.Join(' ', parsed.Locality)} vs {string.Join(", ", new[] { customer.Suburb, customer.City }.Where(p => !string.IsNullOrWhiteSpace(p)))}).");

        return new OpenserveCandidateAssessment(candidate, OpenserveCandidateMatch.Matched, "Street number and street match.");
    }

    /// <summary>
    /// "8 PALMAS ST MONAVONI X 6 CENTURION" → number 8, street PALMAS, type ST,
    /// locality [MONAVONI, X, 6, CENTURION]. "2 A PALMAS ST" → number 2A. Without
    /// a recognisable street type the street can't be separated from the area
    /// (Street = null; AfterNumber keeps every word).
    /// </summary>
    public static ParsedOpenserveAddress ParseLrAddress(string? address)
    {
        var tokens = string.IsNullOrWhiteSpace(address)
            ? new List<string>()
            : Regex.Replace(address.ToUpperInvariant(), @"[^A-Z0-9]+", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (tokens.Count == 0 || !char.IsDigit(tokens[0][0])) return new ParsedOpenserveAddress(string.Empty, null, tokens, tokens);

        var number = tokens[0];
        var index = 1;
        if (tokens.Count > 3 && tokens[1].Length == 1 && char.IsLetter(tokens[1][0]) && !OpenserveAddressMatcher.StreetTypes.Contains(tokens[2]))
        {
            number += tokens[1];
            index = 2;
        }

        var after = tokens.Skip(index).ToList();
        var typeAt = after.Count > 1 ? after.FindIndex(1, t => OpenserveAddressMatcher.StreetTypes.Contains(t)) : -1;
        if (typeAt > 0)
            return new ParsedOpenserveAddress(OpenserveAddressMatcher.NormalizeNumber(number), string.Join(' ', after.Take(typeAt)), after, after.Skip(typeAt + 1).ToList(), tokens);
        return new ParsedOpenserveAddress(OpenserveAddressMatcher.NormalizeNumber(number), null, after, new List<string>(), tokens);
    }

    private static bool StartsWithWords(IReadOnlyList<string> words, string phrase)
    {
        var wanted = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return wanted.Length > 0 && words.Count >= wanted.Length && wanted.Select((w, i) => words[i] == w).All(x => x);
    }

    // ─── persisted shape ────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>The candidates with their assessment, as stored on the evidence row (Openserve data + SmartFuture's verdict only).</summary>
    public static string? Serialize(IReadOnlyList<OpenserveCandidateAssessment> assessments) =>
        assessments.Count == 0
            ? null
            : JsonSerializer.Serialize(assessments.Take(200).Select(a => new StoredAddressCandidate(a.Candidate.Amid, a.Candidate.Address, a.Candidate.Latitude, a.Candidate.Longitude,
                a.Candidate.DistanceMeters is { } d ? Math.Round(d, 2) : null, a.Candidate.DistanceText, a.Match, a.Detail)).ToList(), JsonOptions);

    public static IReadOnlyList<StoredAddressCandidate> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<StoredAddressCandidate>();
        try
        {
            return JsonSerializer.Deserialize<List<StoredAddressCandidate>>(json, JsonOptions) ?? new List<StoredAddressCandidate>();
        }
        catch (JsonException)
        {
            return Array.Empty<StoredAddressCandidate>();
        }
    }
}

public enum OpenserveCandidateMatch
{
    /// <summary>Street name and street number equal the customer's (and the area doesn't contradict).</summary>
    Matched = 0,
    StreetNumberMismatch = 1,
    StreetMismatch = 2,
    LocalityMismatch = 3,

    /// <summary>One side has no street number/street to compare.</summary>
    NotComparable = 4
}

public sealed record OpenserveCandidateAssessment(OpenserveAddressCandidate Candidate, OpenserveCandidateMatch Match, string Detail);

public sealed record OpenserveAddressResolutionResult(OpenserveAddressResolution Resolution, OpenserveAddressCandidate? Selected, IReadOnlyList<OpenserveCandidateAssessment> Assessments, string Detail);

public sealed record ParsedOpenserveAddress(string Number, string? Street, IReadOnlyList<string> AfterNumber, IReadOnlyList<string> Locality, IReadOnlyList<string>? Tokens = null)
{
    public IReadOnlyList<string> Tokens { get; } = Tokens ?? AfterNumber;
}

/// <summary>One stored AddressVerify candidate.</summary>
public sealed record StoredAddressCandidate(string? Amid, string? Address, decimal? Latitude, decimal? Longitude, decimal? DistanceMeters, string? DistanceText,
    OpenserveCandidateMatch Match, string? MatchDetail)
{
    public OpenserveAddressCandidate ToCandidate() => new(Amid, DistanceMeters, DistanceText, Address, Latitude, Longitude);
}
