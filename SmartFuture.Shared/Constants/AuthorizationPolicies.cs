namespace SmartFuture.Shared.Constants;

public static class AuthorizationPolicies
{
    public const string RequireAdmin = "RequireAdmin";
    public const string RequireCustomer = "RequireCustomer";
    public const string RequireActiveUser = "RequireActiveUser";

    // Go-live alignment — technician portal scope. The technician role
    // already exists in <see cref="SystemRoles"/>; this policy is
    // what controllers attach to gate /api/technician/* endpoints.
    public const string RequireTechnician = "RequireTechnician";

    // Job Opportunities — gates /api/job-subscribers/me/* . Admins also
    // satisfy it so support staff can exercise the endpoints without a
    // second account, exactly like RequireTechnician.
    public const string RequireJobSubscriber = "RequireJobSubscriber";

    public const string AccountStatusClaim = "account_status";
    public const string ActiveAccountStatus = "Active";
}
