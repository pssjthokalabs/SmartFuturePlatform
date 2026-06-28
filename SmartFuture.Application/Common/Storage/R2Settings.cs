namespace SmartFuture.Application.Common.Storage;

// Cloudflare R2 storage settings. Secrets MUST come from env vars (never
// the repo's appsettings):
//   R2__AccountId        — Cloudflare account id (32-hex; not secret but
//                          required to build the S3-compatible endpoint).
//   R2__AccessKeyId      — R2 access key id.
//   R2__SecretAccessKey  — R2 secret access key.
//   R2__BucketName       — bucket that stores admin uploads.
//   R2__PublicBaseUrl    — public CDN URL prefix for the bucket, e.g.
//                          "https://cdn.smartfuture.co.za". Object keys
//                          are appended to this for the ImageUrl we
//                          persist on ServicePackage.
//
// When ANY R2 field is unset, IFileStorageService resolves to the
// NotConfigured stub which fails-fast with a clear FailureReason
// instead of attempting an unauthenticated upload.
public class R2Settings
{
    public const string SectionName = "R2";

    public string? AccountId { get; set; }
    public string? AccessKeyId { get; set; }
    public string? SecretAccessKey { get; set; }
    public string? BucketName { get; set; }
    public string? PublicBaseUrl { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AccountId)
        && !string.IsNullOrWhiteSpace(AccessKeyId)
        && !string.IsNullOrWhiteSpace(SecretAccessKey)
        && !string.IsNullOrWhiteSpace(BucketName)
        && !string.IsNullOrWhiteSpace(PublicBaseUrl);

    public string ServiceUrl => string.IsNullOrWhiteSpace(AccountId)
        ? string.Empty
        : $"https://{AccountId}.r2.cloudflarestorage.com";
}
