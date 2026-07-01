namespace SmartFuture.Shared.Enums.Coverage;

// What produced the CoverageAvailable value on a coverage response.
// Openserve is the default (existing behaviour); the CoverageMap*
// values are stamped when an admin-configured include/exclude rule
// short-circuited the check before Openserve ran.
public enum CoverageMatchSource
{
    Openserve          = 0,
    CoverageMapInclude = 1,
    CoverageMapExclude = 2
}
