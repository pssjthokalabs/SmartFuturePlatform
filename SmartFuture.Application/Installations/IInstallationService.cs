using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.Installations.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Installations;

public interface IInstallationService
{
    Task<Result<PagedResult<InstallationDto>>> SearchAdminAsync(
        InstallationFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<PagedResult<InstallationDto>>> GetMineAsync(
        InstallationFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<InstallationDto>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<InstallationDto>> GetMineByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<InstallationDto>> CreateAsync(
        CreateInstallationRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<InstallationDto>> AdminUpdateAsync(
        Guid id, AdminUpdateInstallationRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<InstallationDto>> AdminUpdateStatusAsync(
        Guid id, AdminUpdateInstallationStatusDto request, CancellationToken cancellationToken = default);

    // ─── Technician-scoped (go-live alignment) ───────────────────
    //
    // Mirror of the admin GETs but filtered to installations whose
    // TechnicianUserId matches the calling user. Technicians don't
    // see anyone else's work.
    Task<Result<PagedResult<InstallationDto>>> SearchAssignedToMeAsync(
        InstallationFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<InstallationDto>> GetAssignedToMeByIdAsync(
        Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Technician self-update of installation status. Validates that
    /// the calling user is the assigned technician. When moving to
    /// Completed, captures the completion details (router make/model,
    /// serial, MAC, ONT ref, etc.) and triggers the same
    /// post-completion pipeline as the admin update (first monthly
    /// invoice + auto-debit). Service status flips to
    /// OrderStatus.PendingPayment — never Active (Openserve activation
    /// is manual).
    /// </summary>
    Task<Result<InstallationDto>> TechnicianUpdateStatusAsync(
        Guid id, TechnicianUpdateInstallationStatusDto request, CancellationToken cancellationToken = default);
}
