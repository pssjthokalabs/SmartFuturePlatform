using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartFuture.Application.Common.Storage;
using SmartFuture.Shared.Constants;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.API.Controllers;

[Route("api/uploads")]
[Authorize(Policy = AuthorizationPolicies.RequireAdmin)]
public class UploadsController : BaseController
{
    private readonly IFileStorageService _storage;

    public UploadsController(IFileStorageService storage)
    {
        _storage = storage;
    }

    private static readonly string[] AllowedImageContentTypes =
        { "image/jpeg", "image/png", "image/webp" };

    private const long MaxImageSizeBytes = 5 * 1024 * 1024; // 5 MB

    // Admin-only image upload for ServicePackage cards (Security CCTV
    // packages always need one; Fibre may have one). Multipart form-data
    // body with a single `file` field. Returns the persistent storage
    // key + public CDN URL so the admin form can patch them onto the
    // package on save.
    [HttpPost("service-package-image")]
    [RequestSizeLimit(MaxImageSizeBytes)]
    public async Task<IActionResult> UploadServicePackageImage(
        [FromForm] IFormFile? file,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return ToActionResult(Result<FileUploadResultDto>.Failure(
                ErrorCodes.BAD_REQUEST, "A file is required."));

        if (file.Length > MaxImageSizeBytes)
            return ToActionResult(Result<FileUploadResultDto>.Failure(
                ErrorCodes.VALIDATION_ERROR,
                $"Image is too large. Maximum size is {MaxImageSizeBytes / 1024 / 1024} MB."));

        if (!AllowedImageContentTypes.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
            return ToActionResult(Result<FileUploadResultDto>.Failure(
                ErrorCodes.VALIDATION_ERROR,
                "Unsupported image type. Use JPEG, PNG, or WebP."));

        await using var stream = file.OpenReadStream();
        var unique = $"{Guid.NewGuid():N}-{file.FileName}";

        var result = await _storage.UploadAsync(
            stream,
            folder: "service-packages",
            fileName: unique,
            contentType: file.ContentType,
            sizeBytes: file.Length,
            cancellationToken);

        return ToActionResult(result);
    }
}
