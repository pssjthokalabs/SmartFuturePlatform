using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Common.Storage;

public class FileUploadResultDto
{
    public string StorageKey { get; set; } = string.Empty;
    public string PublicUrl { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
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
}
