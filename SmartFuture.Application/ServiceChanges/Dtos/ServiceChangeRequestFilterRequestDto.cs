using SmartFuture.Shared.Enums.ServiceChanges;
using SmartFuture.Shared.Queries;

namespace SmartFuture.Application.ServiceChanges.Dtos;

public class ServiceChangeRequestFilterRequestDto : PagedListQueryBase
{
    public Guid? UserId             { get; set; }
    public Guid? NetworkAccountId   { get; set; }
    public Guid? OrderId            { get; set; }
    public Guid? RequestedPackageId { get; set; }

    public ServiceChangeStatus?        StatusFilter  { get; set; }
    public ServiceChangeType?          ChangeType    { get; set; }
    public ServiceChangeEffectiveMode? EffectiveMode { get; set; }
    public ServiceChangeSource?        Source        { get; set; }
}
