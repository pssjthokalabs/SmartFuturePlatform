using System.Globalization;
using System.Text.RegularExpressions;
using SmartFuture.Shared.Enums.Jobs;

namespace SmartFuture.Application.Jobs.Import;

// Heuristics that turn messy job-ad prose into structured fields.
//
// Ground rule: every parser returns null when it is not confident.
// A wrong closing date would hide a live job (or publish a dead one),
// and a wrong province would break the location filter — for this
// module, "unknown" is always better than "guessed".
public static partial class JobFieldParsers
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    // South African provinces + the common shorthand/typo forms boards
    // actually use. Ordered longest-first at match time so "North West"
    // isn't shadowed by "West".
    private static readonly Dictionary<string, string> ProvinceAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gauteng"] = "Gauteng",
        ["gp"] = "Gauteng",
        ["western cape"] = "Western Cape",
        ["wc"] = "Western Cape",
        ["eastern cape"] = "Eastern Cape",
        ["ec"] = "Eastern Cape",
        ["northern cape"] = "Northern Cape",
        ["nc"] = "Northern Cape",
        ["kwazulu-natal"] = "KwaZulu-Natal",
        ["kwazulu natal"] = "KwaZulu-Natal",
        ["kzn"] = "KwaZulu-Natal",
        ["free state"] = "Free State",
        ["fs"] = "Free State",
        ["mpumalanga"] = "Mpumalanga",
        ["mp"] = "Mpumalanga",
        ["limpopo"] = "Limpopo",
        ["lp"] = "Limpopo",
        ["north west"] = "North West",
        ["north-west"] = "North West",
        ["nw"] = "North West"
    };

    // Major metros mapped to their province so a listing that only says
    // "Sandton" still lands on the right province facet.
    private static readonly Dictionary<string, (string City, string Province)> KnownCities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["johannesburg"] = ("Johannesburg", "Gauteng"),
        ["joburg"] = ("Johannesburg", "Gauteng"),
        ["jhb"] = ("Johannesburg", "Gauteng"),
        ["sandton"] = ("Sandton", "Gauteng"),
        ["midrand"] = ("Midrand", "Gauteng"),
        ["centurion"] = ("Centurion", "Gauteng"),
        ["pretoria"] = ("Pretoria", "Gauteng"),
        ["tshwane"] = ("Pretoria", "Gauteng"),
        ["soweto"] = ("Soweto", "Gauteng"),
        ["benoni"] = ("Benoni", "Gauteng"),
        ["boksburg"] = ("Boksburg", "Gauteng"),
        ["kempton park"] = ("Kempton Park", "Gauteng"),
        ["roodepoort"] = ("Roodepoort", "Gauteng"),
        ["vereeniging"] = ("Vereeniging", "Gauteng"),
        ["cape town"] = ("Cape Town", "Western Cape"),
        ["stellenbosch"] = ("Stellenbosch", "Western Cape"),
        ["paarl"] = ("Paarl", "Western Cape"),
        ["george"] = ("George", "Western Cape"),
        ["bellville"] = ("Bellville", "Western Cape"),
        ["durban"] = ("Durban", "KwaZulu-Natal"),
        ["pietermaritzburg"] = ("Pietermaritzburg", "KwaZulu-Natal"),
        ["umhlanga"] = ("Umhlanga", "KwaZulu-Natal"),
        ["richards bay"] = ("Richards Bay", "KwaZulu-Natal"),
        ["gqeberha"] = ("Gqeberha", "Eastern Cape"),
        ["port elizabeth"] = ("Gqeberha", "Eastern Cape"),
        ["east london"] = ("East London", "Eastern Cape"),
        ["bloemfontein"] = ("Bloemfontein", "Free State"),
        ["kimberley"] = ("Kimberley", "Northern Cape"),
        ["upington"] = ("Upington", "Northern Cape"),
        ["polokwane"] = ("Polokwane", "Limpopo"),
        ["nelspruit"] = ("Nelspruit", "Mpumalanga"),
        ["mbombela"] = ("Nelspruit", "Mpumalanga"),
        ["witbank"] = ("Witbank", "Mpumalanga"),
        ["emalahleni"] = ("Witbank", "Mpumalanga"),
        ["rustenburg"] = ("Rustenburg", "North West"),
        ["potchefstroom"] = ("Potchefstroom", "North West"),
        ["mahikeng"] = ("Mahikeng", "North West"),
        ["klerksdorp"] = ("Klerksdorp", "North West")
    };

    [GeneratedRegex(@"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}")]
    private static partial Regex EmailRegex();

    // "Closing date: 15 August 2026", "Closes 2026-08-15",
    // "Applications close on 15/08/2026", "Deadline: 15 Aug 2026".
    [GeneratedRegex(@"(?:clos(?:ing|es|e)\s*(?:date)?|deadline|applications?\s+close(?:s)?(?:\s+on)?)\s*[:\-–]?\s*([0-9]{1,2}[\s/\-.][A-Za-z0-9]{2,9}[\s/\-.][0-9]{2,4}|[0-9]{4}-[0-9]{2}-[0-9]{2})",
        RegexOptions.IgnoreCase)]
    private static partial Regex ClosingDateRegex();

    [GeneratedRegex(@"(?:posted|published|date\s+posted)\s*[:\-–]?\s*([0-9]{1,2}[\s/\-.][A-Za-z0-9]{2,9}[\s/\-.][0-9]{2,4}|[0-9]{4}-[0-9]{2}-[0-9]{2})",
        RegexOptions.IgnoreCase)]
    private static partial Regex PostedDateRegex();

    // R 25 000, R25,000.00, ZAR 25000 — optionally a range and/or a period.
    [GeneratedRegex(@"(?:R|ZAR)\s?[0-9][0-9\s,.]{2,}(?:\s*(?:-|–|to)\s*(?:R|ZAR)?\s?[0-9][0-9\s,.]{2,})?(?:\s*(?:per|p/?)\s*(?:month|annum|year|hour|week|m|a))?",
        RegexOptions.IgnoreCase)]
    private static partial Regex SalaryRegex();

    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy", "d.M.yyyy",
        "dd MMMM yyyy", "d MMMM yyyy", "dd MMM yyyy", "d MMM yyyy", "MMMM d, yyyy", "MMM d, yyyy",
        "dd/MM/yy", "d/M/yy"
    };

    public static string? ExtractEmail(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var match = EmailRegex().Match(text);
            if (!match.Success) return null;

            var email = match.Value.Trim().TrimEnd('.', ',', ';');
            // Skip the boilerplate addresses that appear in site
            // footers rather than in the ad itself.
            if (email.StartsWith("info@", StringComparison.OrdinalIgnoreCase)
                || email.StartsWith("webmaster@", StringComparison.OrdinalIgnoreCase)
                || email.StartsWith("noreply@", StringComparison.OrdinalIgnoreCase))
            {
                // Only accept it when nothing better exists — try the
                // next match first.
                var better = EmailRegex().Matches(text)
                    .Select(m => m.Value.Trim().TrimEnd('.', ',', ';'))
                    .FirstOrDefault(v => !v.StartsWith("info@", StringComparison.OrdinalIgnoreCase)
                        && !v.StartsWith("webmaster@", StringComparison.OrdinalIgnoreCase)
                        && !v.StartsWith("noreply@", StringComparison.OrdinalIgnoreCase));
                return better ?? email;
            }

            return email;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    public static DateTime? ExtractClosingDate(string? text, DateTime nowUtc)
    {
        var raw = MatchFirstGroup(ClosingDateRegex(), text);
        var parsed = ParseDate(raw);
        if (parsed is null) return null;

        // A "closing date" more than two years out, or more than a year
        // in the past, is almost certainly a mis-parse (a phone number,
        // a company founding year). Don't act on it.
        if (parsed > nowUtc.AddYears(2) || parsed < nowUtc.AddYears(-1)) return null;
        return parsed;
    }

    public static DateTime? ExtractPostedDate(string? text, DateTime nowUtc)
    {
        var parsed = ParseDate(MatchFirstGroup(PostedDateRegex(), text));
        if (parsed is null) return null;
        // A posting date in the future is nonsense.
        if (parsed > nowUtc.AddDays(1) || parsed < nowUtc.AddYears(-3)) return null;
        return parsed;
    }

    public static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = JobTextUtilities.CollapseWhitespace(value).Trim(' ', '.', ',');

        if (DateTime.TryParseExact(cleaned, DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var exact))
        {
            return exact;
        }

        // ISO 8601 with a time component (what JSON-LD normally carries).
        if (DateTime.TryParse(cleaned, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var loose))
        {
            return loose;
        }

        return null;
    }

    public static string? ExtractSalary(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var match = SalaryRegex().Match(text);
            if (!match.Success) return null;
            var value = JobTextUtilities.CollapseWhitespace(match.Value);
            return value.Length is > 3 and < 120 ? value : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    // Resolves free-text location into (Location, City, Province).
    // Location is always the original string; City/Province are only set
    // when a known token actually matched.
    public static (string? Location, string? City, string? Province) ParseLocation(string? raw)
    {
        var location = JobTextUtilities.NullIfBlank(raw);
        if (location is null) return (null, null, null);

        var haystack = location.ToLowerInvariant();

        string? city = null;
        string? province = null;

        // Longest alias first so "east london" wins over "london" and
        // "north west" isn't clipped.
        foreach (var entry in KnownCities.OrderByDescending(e => e.Key.Length))
        {
            if (haystack.Contains(entry.Key, StringComparison.Ordinal))
            {
                city = entry.Value.City;
                province = entry.Value.Province;
                break;
            }
        }

        foreach (var entry in ProvinceAliases.OrderByDescending(e => e.Key.Length))
        {
            // Two-letter aliases are only trusted as standalone tokens —
            // "wc" inside "Newcastle" must not become Western Cape.
            var isShortAlias = entry.Key.Length <= 2;
            var matched = isShortAlias
                ? ContainsStandaloneToken(haystack, entry.Key)
                : haystack.Contains(entry.Key, StringComparison.Ordinal);

            if (matched)
            {
                province = entry.Value;
                break;
            }
        }

        return (location, city, province);
    }

    private static bool ContainsStandaloneToken(string haystack, string token)
        => haystack.Split(new[] { ' ', ',', '/', '-', '(', ')', '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(part, token, StringComparison.Ordinal));

    public static JobWorkplaceType ParseWorkplaceType(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return JobWorkplaceType.Unknown;
        var haystack = text.ToLowerInvariant();

        if (haystack.Contains("hybrid", StringComparison.Ordinal)) return JobWorkplaceType.Hybrid;
        if (haystack.Contains("fully remote", StringComparison.Ordinal)
            || haystack.Contains("work from home", StringComparison.Ordinal)
            || haystack.Contains("remote", StringComparison.Ordinal))
        {
            return JobWorkplaceType.Remote;
        }
        if (haystack.Contains("on-site", StringComparison.Ordinal)
            || haystack.Contains("onsite", StringComparison.Ordinal)
            || haystack.Contains("in office", StringComparison.Ordinal))
        {
            return JobWorkplaceType.Onsite;
        }

        return JobWorkplaceType.Unknown;
    }

    public static string? ParseEmploymentType(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // schema.org publishes these as FULL_TIME / PART_TIME / TEMPORARY.
        // Fold the underscore to a space so the same keyword table
        // handles both the structured enum and free-text prose.
        var haystack = text.ToLowerInvariant().Replace('_', ' ');

        if (haystack.Contains("full-time", StringComparison.Ordinal) || haystack.Contains("full time", StringComparison.Ordinal) || haystack.Contains("fulltime", StringComparison.Ordinal))
            return "Full-time";
        if (haystack.Contains("part-time", StringComparison.Ordinal) || haystack.Contains("part time", StringComparison.Ordinal))
            return "Part-time";
        if (haystack.Contains("internship", StringComparison.Ordinal) || haystack.Contains("intern ", StringComparison.Ordinal))
            return "Internship";
        if (haystack.Contains("learnership", StringComparison.Ordinal))
            return "Learnership";
        if (haystack.Contains("apprentice", StringComparison.Ordinal))
            return "Apprenticeship";
        if (haystack.Contains("temporary", StringComparison.Ordinal) || haystack.Contains("temp ", StringComparison.Ordinal))
            return "Temporary";
        if (haystack.Contains("contract", StringComparison.Ordinal))
            return "Contract";
        if (haystack.Contains("permanent", StringComparison.Ordinal))
            return "Permanent";

        return null;
    }

    // Coarse category inference from the title. Only used when neither
    // the source's DefaultCategory nor the feed says anything.
    private static readonly (string Category, string[] Keywords)[] CategoryKeywords =
    {
        ("Information Technology", new[] { "developer", "software", "it ", "network", "data", "devops", "cyber", "programmer", "system admin", "technician" }),
        ("Engineering", new[] { "engineer", "artisan", "millwright", "fitter", "boilermaker", "welder", "electrician" }),
        ("Healthcare", new[] { "nurse", "nursing", "medical", "clinic", "pharmac", "doctor", "caregiver", "health" }),
        ("Education", new[] { "teacher", "tutor", "lecturer", "educator", "school", "training facilitator" }),
        ("Finance", new[] { "account", "bookkeep", "financ", "audit", "payroll", "credit", "tax " }),
        ("Sales & Marketing", new[] { "sales", "marketing", "brand", "merchandis", "promoter", "business development" }),
        ("Administration", new[] { "admin", "receptionist", "secretar", "clerk", "data captur", "office manager" }),
        ("Customer Service", new[] { "call centre", "call center", "customer service", "customer care", "agent", "helpdesk", "help desk" }),
        ("Logistics & Transport", new[] { "driver", "logistic", "warehouse", "forklift", "courier", "dispatch", "supply chain" }),
        ("Security", new[] { "security guard", "security officer", "armed response", "cctv" }),
        ("Hospitality", new[] { "chef", "waiter", "waitress", "barista", "hotel", "housekeep", "restaurant" }),
        ("General Work", new[] { "general worker", "cleaner", "labour", "labor", "packer", "picker" }),
        ("Construction", new[] { "construction", "builder", "plumber", "carpenter", "site foreman" }),
        ("Human Resources", new[] { "human resource", "hr ", "recruit", "talent acquisition" })
    };

    public static string? InferCategory(string? title, string? bodyText = null)
    {
        var haystack = $"{title} {bodyText}".ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(haystack)) return null;

        foreach (var (category, keywords) in CategoryKeywords)
        {
            if (keywords.Any(k => haystack.Contains(k, StringComparison.Ordinal)))
                return category;
        }

        return null;
    }

    // Pulls the "Requirements" / "Minimum requirements" block out of a
    // plain-text ad: everything from the heading until the next heading
    // or a blank-line run.
    private static readonly string[] RequirementHeadings =
    {
        "requirements", "minimum requirements", "key requirements", "qualifications",
        "minimum qualifications", "skills required", "what you need", "criteria"
    };

    private static readonly string[] TerminatingHeadings =
    {
        "duties", "responsibilities", "how to apply", "to apply", "application",
        "closing date", "salary", "benefits", "about the company", "package"
    };

    public static string? ExtractRequirements(string? plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText)) return null;

        var lines = plainText.Split('\n');
        var start = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var normalized = lines[i].Trim().TrimEnd(':').ToLowerInvariant();
            if (normalized.Length is 0 or > 60) continue;
            if (RequirementHeadings.Any(h => normalized == h || normalized.StartsWith(h + " ", StringComparison.Ordinal)))
            {
                start = i + 1;
                break;
            }
        }

        if (start < 0 || start >= lines.Length) return null;

        var collected = new List<string>();
        for (var i = start; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            var normalized = line.TrimEnd(':').ToLowerInvariant();

            if (normalized.Length is > 0 and <= 60 && TerminatingHeadings.Any(h => normalized == h || normalized.StartsWith(h, StringComparison.Ordinal)))
                break;

            collected.Add(line);
            if (collected.Count >= 60) break;
        }

        var text = string.Join('\n', collected).Trim();
        return text.Length < 10 ? null : (text.Length > 4000 ? text[..4000] : text);
    }

    private static string? MatchFirstGroup(Regex regex, string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        try
        {
            var match = regex.Match(input);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }
}
