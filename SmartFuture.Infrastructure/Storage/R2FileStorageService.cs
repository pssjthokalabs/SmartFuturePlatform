using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Common.Storage;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.Infrastructure.Storage;

// Cloudflare R2 implementation of IFileStorageService. R2 is fully
// S3-compatible so we reuse AWSSDK.S3 against the account-scoped
// R2 endpoint (https://{AccountId}.r2.cloudflarestorage.com). Public
// reads are served through the configured PublicBaseUrl (custom domain
// or r2.dev URL) so uploaded objects do not require signed URLs at the
// public catalogue's call sites.
public class R2FileStorageService : IFileStorageService, IDisposable
{
    private readonly R2Settings _settings;
    private readonly ILogger<R2FileStorageService> _logger;
    private readonly AmazonS3Client? _client;

    public R2FileStorageService(IOptions<R2Settings> settings, ILogger<R2FileStorageService> logger)
    {
        _settings = settings.Value;
        _logger = logger;

        if (_settings.IsConfigured)
        {
            var config = new AmazonS3Config
            {
                ServiceURL = _settings.ServiceUrl,
                ForcePathStyle = true,
                // R2 is region-agnostic but the SDK still requires one.
                AuthenticationRegion = "auto"
            };
            _client = new AmazonS3Client(_settings.AccessKeyId, _settings.SecretAccessKey, config);
        }
    }

    public async Task<Result<FileUploadResultDto>> UploadAsync(
        Stream content, string folder, string fileName, string contentType, long sizeBytes,
        CancellationToken cancellationToken = default)
    {
        if (_client is null || !_settings.IsConfigured)
            return Result<FileUploadResultDto>.Failure(
                ErrorCodes.PROVIDER_NOT_CONFIGURED,
                "Cloudflare R2 is not configured. Set R2__AccountId / R2__AccessKeyId / R2__SecretAccessKey / R2__BucketName / R2__PublicBaseUrl env vars.");

        var safeFolder = (folder ?? string.Empty).Trim('/').Trim();
        var safeName = SanitizeFileName(fileName);
        var key = string.IsNullOrEmpty(safeFolder) ? safeName : $"{safeFolder}/{safeName}";

        try
        {
            var request = new PutObjectRequest
            {
                BucketName = _settings.BucketName,
                Key = key,
                InputStream = content,
                ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
                DisablePayloadSigning = true,
                AutoCloseStream = false
            };

            await _client.PutObjectAsync(request, cancellationToken);

            var publicUrl = $"{_settings.PublicBaseUrl!.TrimEnd('/')}/{key}";
            return Result<FileUploadResultDto>.Success(new FileUploadResultDto
            {
                StorageKey = key,
                PublicUrl = publicUrl,
                ContentType = request.ContentType,
                SizeBytes = sizeBytes
            });
        }
        catch (AmazonS3Exception ex)
        {
            _logger.LogError(ex, "R2 upload failed for key {Key}", key);
            return Result<FileUploadResultDto>.Failure(
                ErrorCodes.EXCEPTION,
                "Upload to Cloudflare R2 failed. See server logs for details.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "R2 upload threw for key {Key}", key);
            return Result<FileUploadResultDto>.Failure(
                ErrorCodes.EXCEPTION, "Unexpected error uploading file.");
        }
    }

    public async Task<Result> DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        if (_client is null || !_settings.IsConfigured)
            return Result.Failure(
                ErrorCodes.PROVIDER_NOT_CONFIGURED, "Cloudflare R2 is not configured.");

        if (string.IsNullOrWhiteSpace(storageKey))
            return Result.Failure(ErrorCodes.BAD_REQUEST, "Storage key is required.");

        try
        {
            await _client.DeleteObjectAsync(_settings.BucketName, storageKey, cancellationToken);
            return Result.Success("Object deleted.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "R2 delete failed for key {Key}", storageKey);
            return Result.Failure(ErrorCodes.EXCEPTION, "Delete from Cloudflare R2 failed.");
        }
    }

    // Streams a private object back through the API. Job subscriber CVs
    // and cover letters live under a private prefix and must never be
    // served via the public CDN URL, so every read goes through here (or
    // through a short-lived pre-signed URL below).
    public async Task<Result<FileDownloadResultDto>> DownloadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        if (_client is null || !_settings.IsConfigured)
            return Result<FileDownloadResultDto>.Failure(
                ErrorCodes.PROVIDER_NOT_CONFIGURED, "Cloudflare R2 is not configured.");

        if (string.IsNullOrWhiteSpace(storageKey))
            return Result<FileDownloadResultDto>.Failure(ErrorCodes.BAD_REQUEST, "Storage key is required.");

        try
        {
            var response = await _client.GetObjectAsync(_settings.BucketName, storageKey, cancellationToken);

            // The response stream stays OPEN — the caller streams it to
            // the HTTP response and disposes it. Copying to memory first
            // would defeat the point for multi-MB documents.
            return Result<FileDownloadResultDto>.Success(new FileDownloadResultDto
            {
                Content = response.ResponseStream,
                ContentType = string.IsNullOrWhiteSpace(response.Headers.ContentType) ? "application/octet-stream" : response.Headers.ContentType,
                FileName = storageKey.Split('/').LastOrDefault() ?? "file",
                SizeBytes = response.ContentLength <= 0 ? null : response.ContentLength
            });
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning("R2 object not found for key {Key}", storageKey);
            return Result<FileDownloadResultDto>.Failure(ErrorCodes.NOT_FOUND, "The requested file could not be found in storage.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "R2 download failed for key {Key}", storageKey);
            return Result<FileDownloadResultDto>.Failure(ErrorCodes.EXCEPTION, "Could not read the file from Cloudflare R2.");
        }
    }

    // Pre-signed GET. Clamped to 1 minute … 1 hour: long enough for an
    // admin to open a PDF preview, short enough that a leaked URL from a
    // browser history or referrer header expires quickly.
    public Task<Result<string>> GetPresignedDownloadUrlAsync(string storageKey, TimeSpan expiresIn, CancellationToken cancellationToken = default)
    {
        if (_client is null || !_settings.IsConfigured)
            return Task.FromResult(Result<string>.Failure(ErrorCodes.PROVIDER_NOT_CONFIGURED, "Cloudflare R2 is not configured."));

        if (string.IsNullOrWhiteSpace(storageKey))
            return Task.FromResult(Result<string>.Failure(ErrorCodes.BAD_REQUEST, "Storage key is required."));

        var clamped = TimeSpan.FromMinutes(Math.Clamp(expiresIn.TotalMinutes, 1, 60));

        try
        {
            var request = new GetPreSignedUrlRequest
            {
                BucketName = _settings.BucketName,
                Key = storageKey,
                Verb = HttpVerb.GET,
                Expires = DateTime.UtcNow.Add(clamped)
            };

            return Task.FromResult(Result<string>.Success(_client.GetPreSignedURL(request)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "R2 pre-signed URL generation failed for key {Key}", storageKey);
            return Task.FromResult(Result<string>.Failure(ErrorCodes.EXCEPTION, "Could not generate a download link for the file."));
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        var trimmed = (fileName ?? "file").Trim();
        var chars = trimmed.Select(c =>
            char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' ? c : '-').ToArray();
        var sanitized = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "file" : sanitized;
    }

    public void Dispose() => _client?.Dispose();
}
