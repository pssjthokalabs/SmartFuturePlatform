using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Common.Storage;

public class FileUploadResultDto
{
    public string StorageKey { get; set; } = string.Empty;
    public string PublicUrl { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
}

// Streamed object read. The caller owns (and must dispose) Content.
// Used by the authenticated CV / cover-letter download endpoints —
// private objects are never linked to directly.
public class FileDownloadResultDto
{
    public Stream Content { get; set; } = Stream.Null;
    public string ContentType { get; set; } = "application/octet-stream";
    public string FileName { get; set; } = "file";
    public long? SizeBytes { get; set; }
}

public interface IFileStorageService
{
    Task<Result<FileUploadResultDto>> UploadAsync(
        Stream content,
        string folder,
        string fileName,
        string contentType,
        long sizeBytes,
        CancellationToken cancellationToken = default);

    Task<Result> DeleteAsync(string storageKey, CancellationToken cancellationToken = default);

    // Streams a stored object back. Used for PRIVATE files (job
    // subscriber CVs / cover letters) where handing out the public CDN
    // URL would expose the document to anyone who guessed the key.
    Task<Result<FileDownloadResultDto>> DownloadAsync(string storageKey, CancellationToken cancellationToken = default);

    // Short-lived pre-signed GET URL for the same private objects. The
    // admin portal uses it for in-browser PDF preview, where streaming
    // through the API would fight the viewer's range requests.
    // `expiresIn` is clamped by the implementation.
    Task<Result<string>> GetPresignedDownloadUrlAsync(string storageKey, TimeSpan expiresIn, CancellationToken cancellationToken = default);
}
