using SmartFuture.Application.Common.Paging;
using SmartFuture.Application.SupportTickets.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.SupportTickets;

public interface ISupportTicketService
{
    Task<Result<PagedResult<SupportTicketDto>>> SearchAdminAsync(
        SupportTicketFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<PagedResult<SupportTicketDto>>> GetMineAsync(
        SupportTicketFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<SupportTicketDto>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Result<SupportTicketDto>> GetMineByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<SupportTicketDto>> CreateMineAsync(
        CreateSupportTicketRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<SupportTicketDto>> AdminUpdateAsync(
        Guid id, AdminUpdateSupportTicketRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<SupportTicketDto>> AdminUpdateStatusAsync(
        Guid id, AdminUpdateSupportTicketStatusDto request, CancellationToken cancellationToken = default);

    Task<Result<SupportTicketDto>> AssignAsync(
        Guid id, AssignSupportTicketRequestDto request, CancellationToken cancellationToken = default);

    Task<Result> CloseMineAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<PagedResult<SupportTicketCommentDto>>> GetCommentsAdminAsync(
        SupportTicketCommentFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<PagedResult<SupportTicketCommentDto>>> GetCommentsMineAsync(
        SupportTicketCommentFilterRequestDto filter, CancellationToken cancellationToken = default);

    Task<Result<SupportTicketCommentDto>> AddCommentAdminAsync(
        Guid ticketId, AddSupportTicketCommentRequestDto request, CancellationToken cancellationToken = default);

    Task<Result<SupportTicketCommentDto>> AddCommentMineAsync(
        Guid ticketId, AddSupportTicketCommentRequestDto request, CancellationToken cancellationToken = default);
}
