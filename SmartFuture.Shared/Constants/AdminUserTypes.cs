namespace SmartFuture.Shared.Constants;

// Canonical user-type labels used by the admin Users page. These are not
// roles — a single user may hold multiple Identity roles (e.g. SuperAdmin
// also has Admin). The "type" collapses that role set into one bucket so
// the admin UI can group/list users meaningfully.
public static class AdminUserTypes
{
    public const string Customer   = "Customer";
    public const string Admin      = "Admin";
    public const string Agent      = "Agent";
    public const string Technician = "Technician";
    public const string Support    = "Support";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        Customer, Admin, Agent, Technician, Support
    };

    // Resolve canonical user type from the user's role set. Priority:
    // Admin > Agent > Technician > Support > Customer. SuperAdmin folds
    // into Admin so the page doesn't need a separate bucket.
    public static string FromRoles(IEnumerable<string> roles)
    {
        if (roles == null) return Customer;
        var set = new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase);

        if (set.Contains(SystemRoles.Admin) || set.Contains(SystemRoles.SuperAdmin)) return Admin;
        if (set.Contains(SystemRoles.Agent)) return Agent;
        if (set.Contains(SystemRoles.Technician)) return Technician;
        if (set.Contains(SystemRoles.Support)) return Support;
        return Customer;
    }

    // Map a user-type filter to the role names that should be matched.
    // Returns null when the caller passed "All" / empty / unknown.
    public static IReadOnlyList<string>? RolesForType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        return type.Trim().ToLowerInvariant() switch
        {
            "customer"   => new[] { SystemRoles.Customer },
            "admin"      => new[] { SystemRoles.Admin, SystemRoles.SuperAdmin },
            "agent"      => new[] { SystemRoles.Agent },
            "technician" => new[] { SystemRoles.Technician },
            "support"    => new[] { SystemRoles.Support },
            "all"        => null,
            _            => null
        };
    }
}
