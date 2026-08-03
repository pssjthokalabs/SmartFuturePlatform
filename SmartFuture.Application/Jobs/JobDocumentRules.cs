namespace SmartFuture.Application.Jobs;

// Upload policy for job-seeker documents. Kept separate from the service
// so the controller can enforce the size ceiling BEFORE buffering a body,
// and so the rules are unit-testable on their own.
public static class JobDocumentRules
{
    public const long MaxDocumentSizeBytes = 5 * 1024 * 1024; // 5 MB

    // Private R2 prefix. Never served through the public CDN base URL —
    // reads go through the authenticated endpoints or a short-lived
    // pre-signed URL.
    public const string StorageFolder = "job-documents";

    // Extension is the authority, not the browser-declared content type:
    // mobile clients routinely send application/octet-stream for a
    // perfectly valid PDF, and a hostile client can send anything.
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".txt", ".rtf"
    };

    private static readonly Dictionary<string, string> ExtensionContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".txt"] = "text/plain",
        [".rtf"] = "application/rtf"
    };

    public static bool IsAllowedExtension(string? fileName)
        => AllowedExtensions.Contains(GetExtension(fileName));

    public static string AllowedExtensionsLabel => "PDF, DOC, DOCX, TXT or RTF";

    public static string GetExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return string.Empty;
        var ext = Path.GetExtension(fileName.Trim());
        return string.IsNullOrWhiteSpace(ext) ? string.Empty : ext;
    }

    // Canonical content type derived from the extension. We store THIS
    // rather than whatever the client claimed, so a later download always
    // sends a header the browser can act on.
    public static string ResolveContentType(string? fileName, string? declaredContentType)
    {
        var ext = GetExtension(fileName);
        if (ExtensionContentTypes.TryGetValue(ext, out var known)) return known;
        return string.IsNullOrWhiteSpace(declaredContentType) ? "application/octet-stream" : declaredContentType;
    }

    // Object key: job-documents/{userId}/{cv|cover-letter}/{guid}{ext}.
    // The user id segment makes an accidental cross-user read obvious in
    // logs, and the guid stops one upload overwriting another.
    public static string BuildObjectFolder(Guid userId, string documentSlug)
        => $"{StorageFolder}/{userId:N}/{documentSlug}";
}
