namespace SmartFuture.Application.Jobs.Dtos;

// Facet rows for the public filter chips. Counts reflect the SAME
// visibility rule as the list endpoint (Active + not closed), so a chip
// never advertises jobs the list won't return.
public class JobCategoryDto
{
    public string Category { get; set; } = string.Empty;
    public int JobCount { get; set; }
}

public class JobLocationDto
{
    public string Location { get; set; } = string.Empty;
    public string? Province { get; set; }
    public int JobCount { get; set; }
}

public class JobSourceFacetDto
{
    public string SourceName { get; set; } = string.Empty;
    public int JobCount { get; set; }
}
