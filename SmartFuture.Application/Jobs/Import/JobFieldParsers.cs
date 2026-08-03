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
    //
    // The leading \b is load-bearing: without it the "r" inside a word
    // matches, so "Year2025" and "September2026" were being read as the
    // salary "r2025". A word boundary forces the R to start a token.
    [GeneratedRegex(@"\b(?:R|ZAR)\s?[0-9][0-9\s,.]{2,}(?:\s*(?:-|–|to)\s*(?:R|ZAR)?\s?[0-9][0-9\s,.]{2,})?(?:\s*(?:per|p/?)\s*(?:month|annum|year|hour|week|m|a))?",
        RegexOptions.IgnoreCase)]
    private static partial Regex SalaryRegex();

    // Wording that has to be near an amount before we believe it is pay.
    private static readonly string[] SalaryContextWords =
    {
        "salary", "salaries", "remuneration", "package", "earn", "wage", "stipend",
        "basic", "income", "ctc", "cost to company", "pay ", "paid", "rate of"
    };

    private static readonly string[] SalaryPeriodWords =
    {
        "per month", "per annum", "per year", "per hour", "per week",
        "p/m", "p/a", "pm", "pa", "monthly", "annually", "hourly"
    };

    // Theme demo content and placeholder domains. An address on one of
    // these is never a real employer — "support@superio.com" ships with
    // the Superio WordPress job theme and was being imported as the
    // apply address for every listing on sites that use it.
    private static readonly string[] PlaceholderEmailDomains =
    {
        "superio.com", "example.com", "example.org", "example.net", "example.co.za",
        "yourdomain", "yourcompany", "yoursite", "sitename", "domain.com", "domain.co.za",
        "company.com", "company.co.za", "themeforest", "envato", "wpengine",
        "demo.com", "test.com", "email.com", "sample.com", "website.com"
    };

    // Real but non-specific mailboxes. Accepted only when the page
    // explicitly points applications at them.
    private static readonly string[] GenericEmailLocalParts =
    {
        "info", "webmaster", "noreply", "no-reply", "donotreply", "admin", "support",
        "contact", "sales", "marketing", "privacy", "hello", "enquiries", "enquiry",
        "office", "help", "editor", "advertise", "media"
    };

    private static readonly string[] ApplyContextWords =
    {
        "apply", "send your cv", "send cv", "submit your cv", "submit cv", "email your cv",
        "forward your cv", "forward cv", "cv to", "cvs to", "applications to", "application to",
        "submit application", "send application", "resume to", "send resume", "email us your"
    };

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

    // Only returns an amount the page actually presents as pay.
    //
    // The old version returned the first R-prefixed number it saw, which
    // on a WordPress archive meant a copyright year ("r2025"), a post
    // count or a truncated "R20". Every candidate now has to survive a
    // year check and either carry period wording, sit next to a salary
    // word, or be large enough that nothing else it could be makes sense.
    public static string? ExtractSalary(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            foreach (Match match in SalaryRegex().Matches(text))
            {
                var value = JobTextUtilities.CollapseWhitespace(match.Value).Trim(' ', '.', ',', '-', '–');
                if (value.Length is <= 3 or >= 120) continue;
                if (!IsCredibleSalary(match.Index, text, value)) continue;

                return value;
            }

            return null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static bool IsCredibleSalary(int matchIndex, string text, string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return false;

        var lowerValue = value.ToLowerInvariant();
        var hasPeriodWord = SalaryPeriodWords.Any(w => lowerValue.Contains(w, StringComparison.Ordinal));
        var hasGroupSeparator = HasSeparatorBetweenDigits(value);

        // A bare four-digit number in calendar range is a year, a
        // reference number or a page counter — never a salary. Thousands
        // separators ("R2 025") rule this out, as does explicit period
        // wording ("R2025 per month").
        if (!hasGroupSeparator && !hasPeriodWord && digits.Length == 4
            && int.TryParse(digits, out var yearLike) && yearLike is >= 1900 and <= 2100)
        {
            return false;
        }

        var contextStart = Math.Max(0, matchIndex - 80);
        var before = text[contextStart..matchIndex].ToLowerInvariant();
        if (hasPeriodWord || SalaryContextWords.Any(w => before.Contains(w, StringComparison.Ordinal))) return true;

        // No wording anywhere near it: only believe an amount too large
        // to be a count, a page number or a street number.
        var head = digits.Length > 9 ? digits[..9] : digits;
        return int.TryParse(head, out var amount) && amount >= 1000;
    }

    private static bool HasSeparatorBetweenDigits(string value)
    {
        for (var i = 1; i < value.Length - 1; i++)
        {
            if (value[i] is ' ' or ',' or '.' && char.IsDigit(value[i - 1]) && char.IsDigit(value[i + 1]))
                return true;
        }

        return false;
    }

    // The address applications should actually go to.
    //
    // Preference order: an address the page points applications at, then
    // any specific (non-generic) address, then nothing. Returning null is
    // the right answer for a page whose only address is a footer
    // "info@" — the listing keeps its source URL and the reader clicks
    // through, which beats sending CVs to a website's general mailbox.
    public static string? ExtractApplyEmail(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            var candidates = EmailRegex().Matches(text)
                .Select(m => (Email: m.Value.Trim().TrimEnd('.', ',', ';', ':'), m.Index))
                .Where(c => c.Email.Length > 0 && !IsPlaceholderEmail(c.Email))
                .ToList();

            if (candidates.Count == 0) return null;

            foreach (var (email, index) in candidates)
            {
                var start = Math.Max(0, index - 200);
                var before = text[start..index].ToLowerInvariant();
                if (ApplyContextWords.Any(w => before.Contains(w, StringComparison.Ordinal))) return email;
            }

            return candidates.FirstOrDefault(c => !IsGenericMailbox(c.Email)).Email;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    public static bool IsPlaceholderEmail(string email)
    {
        var lower = email.ToLowerInvariant();
        return PlaceholderEmailDomains.Any(d => lower.EndsWith("@" + d, StringComparison.Ordinal)
            || lower.Contains("@" + d, StringComparison.Ordinal)
            || lower.Contains("." + d, StringComparison.Ordinal));
    }

    private static bool IsGenericMailbox(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0) return true;

        var local = email[..at].ToLowerInvariant();
        return GenericEmailLocalParts.Contains(local);
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

    // Coarse category inference. Only used when neither the source's
    // DefaultCategory nor the feed says anything.
    //
    // ORDER IS THE TIE-BREAK. The first category with a match wins, so
    // specific role families come before employer-type families —
    // otherwise "Department of Health Cleaner Jobs" lands under
    // government rather than under the job somebody is actually hiring
    // for. Government is deliberately last: on SA public-sector boards
    // almost every ad mentions a department.
    private static readonly (string Category, string[] Keywords)[] CategoryKeywords =
    {
        ("Healthcare", new[] { "nurse", "nursing", "medical", "clinic", "pharmac", "doctor", "caregiver", "health", "hospital", "paramedic" }),
        ("Education", new[] { "teacher", "tutor", "lecturer", "educator", "school", "training facilitator", "principal" }),
        ("Information Technology", new[] { "developer", "software", "information technology", "it", "ict", "network", "database", "devops", "cyber", "programmer", "system administrator" }),
        ("Engineering", new[] { "engineer", "artisan", "millwright", "fitter", "boilermaker", "welder", "electrician", "technician" }),
        ("Finance", new[] { "account", "bookkeep", "financ", "audit", "payroll", "credit controller", "tax" }),
        ("Sales & Marketing", new[] { "sales", "marketing", "brand", "merchandis", "promoter", "business development" }),
        ("Customer Service", new[] { "call centre", "call center", "customer service", "customer care", "helpdesk", "help desk", "contact centre" }),
        ("Logistics & Transport", new[] { "driver", "logistic", "warehouse", "forklift", "courier", "dispatch", "supply chain", "truck", "messenger" }),
        ("General Work", new[] { "general worker", "cleaner", "cleaning", "labour", "labor", "packer", "picker", "groundsman", "patroller" }),
        ("Security", new[] { "security guard", "security officer", "armed response", "cctv", "guard" }),
        ("Hospitality", new[] { "chef", "waiter", "waitress", "barista", "hotel", "housekeep", "restaurant" }),
        ("Construction", new[] { "construction", "builder", "plumber", "carpenter", "site foreman" }),
        ("Administration", new[] { "admin", "receptionist", "secretar", "clerk", "data captur", "office manager" }),
        ("Human Resources", new[] { "human resource", "hr", "recruit", "talent acquisition" }),
        ("Government & Public Sector", new[] { "department of", "municipality", "public service", "electoral commission", "iec", "provincial government", "national treasury", "government" })
    };

    // Title first, body only as a fallback.
    //
    // The body NEVER overrides the title. A cleaning ad that happens to
    // say "submit your application" used to come back as Information
    // Technology, because the old keyword "it " matched the tail of
    // "submit ". Both halves of that bug are fixed here: matching is
    // word-aware, and a title that says what the job is settles it.
    public static string? InferCategory(string? title, string? bodyText = null)
    {
        var fromTitle = MatchCategory(title);
        if (fromTitle is not null) return fromTitle;

        return MatchCategory(bodyText);
    }

    private static string? MatchCategory(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var haystack = text.ToLowerInvariant();

        foreach (var (category, keywords) in CategoryKeywords)
        {
            if (keywords.Any(k => ContainsKeyword(haystack, k))) return category;
        }

        return null;
    }

    // Keywords match at a word BOUNDARY, never mid-word.
    //
    // Longer keywords match as prefixes so "account" still catches
    // "accountant" and "logistic" catches "logistics". Keywords of three
    // characters or fewer ("it", "hr", "tax", "iec") must match a whole
    // word — that is what stops submit/permit/audit/visit from being
    // read as IT.
    private static bool ContainsKeyword(string haystack, string keyword)
    {
        if (keyword.Length == 0) return false;

        var index = 0;
        while (index <= haystack.Length - keyword.Length)
        {
            var found = haystack.IndexOf(keyword, index, StringComparison.Ordinal);
            if (found < 0) return false;

            var startsWord = found == 0 || !IsWordCharacter(haystack[found - 1]);
            var end = found + keyword.Length;
            var endsWord = keyword.Length > 3 || end >= haystack.Length || !IsWordCharacter(haystack[end]);

            if (startsWord && endsWord) return true;
            index = found + 1;
        }

        return false;
    }

    private static bool IsWordCharacter(char c) => char.IsLetterOrDigit(c);

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

    // ─── Employer ─────────────────────────────────────────────────────

    [GeneratedRegex(@"\b((?:[A-Z][A-Za-z&'\-]*\s+){0,2}Department\s+of\s+[A-Z][A-Za-z&'\-]+(?:\s+(?:and|of|&)\s+[A-Z][A-Za-z&'\-]+|\s+[A-Z][A-Za-z&'\-]+){0,3})")]
    private static partial Regex DepartmentRegex();

    private static readonly string[] EmployerLabels =
    {
        "employer", "company", "organisation", "organization", "hiring company", "recruiter", "client", "department"
    };

    // Named public bodies that don't follow the "Department of X" shape.
    private static readonly (string Needle, string Name)[] KnownEmployers =
    {
        ("electoral commission", "Electoral Commission of South Africa (IEC)"),
        ("south african police service", "South African Police Service (SAPS)"),
        ("south african national defence force", "South African National Defence Force (SANDF)"),
        ("transnet", "Transnet"),
        ("eskom", "Eskom"),
        ("sassa", "SASSA"),
        ("sars", "SARS")
    };

    // Wording that proves a candidate is prose, not a name.
    private static readonly string[] EmployerProseMarkers =
    {
        " is hiring", " is inviting", " has opened", " invites", " are inviting", " is looking",
        " has announced", " seeks", " is seeking", " requires", " wishes to"
    };

    // The organisation actually doing the hiring, read from the ad body.
    //
    // Never falls back to the site name: on an aggregator like MyCareers
    // that is the BOARD, not the employer, and publishing it as the
    // company puts a wrong name on every listing.
    public static string? ExtractEmployer(string? plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText)) return null;

        // 1. An explicitly labelled line — "Employer: Gauteng Department
        //    of Health" is how these ads are actually structured.
        foreach (var line in plainText.Split('\n').Take(120))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0 || separator > 24) continue;

            var label = line[..separator].Trim().ToLowerInvariant();
            if (!EmployerLabels.Contains(label)) continue;

            var candidate = CleanEmployer(line[(separator + 1)..]);
            if (candidate is not null) return candidate;
        }

        // 2. "…Department of X" anywhere in the ad.
        try
        {
            var match = DepartmentRegex().Match(plainText);
            if (match.Success)
            {
                var candidate = CleanEmployer(match.Groups[1].Value);
                if (candidate is not null) return candidate;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Fall through to the known-employer table.
        }

        // 3. Well-known public entities.
        var haystack = plainText.ToLowerInvariant();
        foreach (var (needle, name) in KnownEmployers)
        {
            if (haystack.Contains(needle, StringComparison.Ordinal)) return name;
        }

        return null;
    }

    private static string? CleanEmployer(string? raw)
    {
        var value = JobTextUtilities.CollapseWhitespace(raw ?? string.Empty).Trim(' ', '.', ',', ';', '-', '–', '|');
        if (value.Length is < 3 or > 120) return null;

        // "The Department of Correctional Services" is the same employer
        // as "Department of Correctional Services"; keeping the article
        // would fingerprint and display them as two different names.
        foreach (var article in new[] { "The ", "An ", "A ", "Our " })
        {
            if (value.StartsWith(article, StringComparison.OrdinalIgnoreCase))
            {
                value = value[article.Length..].Trim();
                break;
            }
        }

        // Cut anything that turned into a sentence.
        foreach (var marker in EmployerProseMarkers)
        {
            var at = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at > 0) value = value[..at].Trim();
        }

        if (value.Length is < 3 or > 120) return null;

        // A name is a handful of words, not a paragraph.
        return value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 10 ? null : value;
    }

    // ─── Location display ─────────────────────────────────────────────

    private static readonly string[] LocationProseMarkers =
    {
        " is ", " are ", " has ", " will ", " apply", "hiring", "vacanc", "invit", "posts)", "closing"
    };

    // What a reader should SEE as the location.
    //
    // A resolved city/province always wins, because the raw string on
    // these boards is frequently a whole headline ("Limpopo Department
    // of Education is Hiring Driver/Messengers…"). The raw value is kept
    // only when nothing resolved AND it is short and place-like, so a
    // town outside the lookup table isn't lost.
    public static string? BuildDisplayLocation(string? rawLocation, string? city, string? province)
    {
        if (!string.IsNullOrWhiteSpace(city) && !string.IsNullOrWhiteSpace(province)
            && !string.Equals(city, province, StringComparison.OrdinalIgnoreCase))
        {
            return $"{city}, {province}";
        }

        if (!string.IsNullOrWhiteSpace(city)) return city;
        if (!string.IsNullOrWhiteSpace(province)) return province;

        var raw = JobTextUtilities.NullIfBlank(rawLocation);
        if (raw is null) return null;

        var collapsed = JobTextUtilities.CollapseWhitespace(raw).Trim(' ', '.', ',', ':', '-', '–');
        if (collapsed.Length is < 2 or > 60) return null;
        if (collapsed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 6) return null;

        var lower = collapsed.ToLowerInvariant();
        if (LocationProseMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal))) return null;

        return collapsed;
    }

    // ─── WordPress post meta ──────────────────────────────────────────

    [GeneratedRegex(@"^(?:\d{1,2}\s+[A-Za-z]{3,9}\s+\d{4}|[A-Za-z]{3,9}\s+\d{1,2},\s*\d{4}|\d{4}-\d{2}-\d{2})$")]
    private static partial Regex StandaloneDateRegex();

    [GeneratedRegex(@"^(?:posted\s+(?:on|by|in)|published\s+(?:on|by)|by)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PostedByRegex();

    [GeneratedRegex(@"^(?:\d+|no)\s+comments?$|^leave\s+a\s+comment$|^share\s+this$", RegexOptions.IgnoreCase)]
    private static partial Regex CommentMetaRegex();

    private static readonly string[] MetaOnlyLines =
    {
        "admin", "administrator", "jobs", "vacancies", "uncategorized", "uncategorised", "share", "0 comments"
    };

    // Drops the byline block WordPress renders above the post body —
    // author, date and category, which otherwise become the opening
    // three lines of every imported description.
    //
    // Bounded on purpose: only the first few lines are examined, and
    // scanning stops at the first line that looks like real content, so
    // an ad that genuinely opens with a short line keeps it.
    public static string? StripLeadingPostMeta(string? plainText, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(plainText)) return plainText;

        var lines = plainText.Split('\n').ToList();
        var removed = 0;
        var index = 0;

        while (index < lines.Count && removed < 6)
        {
            var line = lines[index].Trim();

            if (line.Length == 0) { index++; continue; }
            if (!IsPostMetaLine(line, title)) break;

            lines.RemoveAt(index);
            removed++;
        }

        var result = string.Join('\n', lines).Trim();
        return result.Length == 0 ? plainText.Trim() : result;
    }

    private static bool IsPostMetaLine(string line, string? title)
    {
        // The repeated headline is checked FIRST and without a length
        // limit — real post titles run well past any sensible cap, and
        // testing the cap first made this whole strip a no-op on the
        // pages it was written for.
        if (!string.IsNullOrWhiteSpace(title)
            && string.Equals(JobTextUtilities.CollapseWhitespace(line), JobTextUtilities.CollapseWhitespace(title),
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (line.Length > 60) return false;

        var lower = line.ToLowerInvariant();
        if (MetaOnlyLines.Contains(lower)) return true;

        try
        {
            return StandaloneDateRegex().IsMatch(line) || PostedByRegex().IsMatch(line) || CommentMetaRegex().IsMatch(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
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
