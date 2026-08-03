namespace SmartFuture.Shared.Constants;

public static class SystemRoles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string Admin = "Admin";
    public const string Agent = "Agent";
    public const string Technician = "Technician";
    public const string Support = "Support";
    public const string Customer = "Customer";

    // Job Opportunities module. Additive and INDEPENDENT of Customer —
    // a single user can hold Customer + JobSubscriber at the same time.
    // Never granted implicitly by registration/login; it is only added
    // by the explicit job-subscriber enrolment path.
    public const string JobSubscriber = "JobSubscriber";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        SuperAdmin, Admin, Agent, Technician, Support, Customer, JobSubscriber
    };

    // Phase 38 — staff = anyone with operational access to the admin
    // portal. Used to identify "non-customer" users in admin views.
    public static IReadOnlyList<string> Staff { get; } = new[]
    {
        SuperAdmin, Admin, Agent, Technician, Support
    };
}
