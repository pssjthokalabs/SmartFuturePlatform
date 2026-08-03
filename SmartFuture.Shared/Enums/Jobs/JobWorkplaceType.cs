namespace SmartFuture.Shared.Enums.Jobs;

// Unknown is the honest default for crawled content — most South
// African job boards do not state the workplace arrangement, and
// guessing "Onsite" would mislead the public filter.
public enum JobWorkplaceType
{
    Unknown = 0,
    Onsite = 1,
    Hybrid = 2,
    Remote = 3
}
