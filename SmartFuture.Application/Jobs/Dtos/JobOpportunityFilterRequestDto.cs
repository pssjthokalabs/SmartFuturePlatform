using SmartFuture.Shared.Enums.Jobs;

namespace SmartFuture.Application.Jobs.Dtos;

// Public list filter. Bound straight from the query string, so every
// member is optional and the service clamps the paging values.
public class JobOpportunityFilterRequestDto
{
    public string? Search { get; set; }
    public string? Category { get; set; }
    // Matches against Location / City / Province with a single contains.
    public string? Location { get; set; }
    public JobWorkplaceType? WorkplaceType { get; set; }
    // Source NAME, not id — the public site links facets by label.
    public string? Source { get; set; }
    public bool? IsFeatured { get; set; }

    public int? Page { get; set; }
    public int? PageSize { get; set; }

    // newest | closing | title | company. Unknown values fall back to
    // newest rather than erroring.
    public string? Sort { get; set; }
}
