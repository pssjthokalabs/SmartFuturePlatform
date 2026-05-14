namespace SmartFuture.Shared.Constants;

public static class AuthorizationPolicies
{
    public const string RequireAdmin = "RequireAdmin";
    public const string RequireCustomer = "RequireCustomer";
    public const string RequireActiveUser = "RequireActiveUser";

    public const string AccountStatusClaim = "account_status";
    public const string ActiveAccountStatus = "Active";
}
