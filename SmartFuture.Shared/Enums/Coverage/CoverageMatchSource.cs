namespace SmartFuture.Shared.Enums.Coverage;

// What produced the CoverageAvailable value on a coverage response.
// Openserve (public GIS lookup) is the legacy default; the CoverageMap*
// values are stamped when an admin-configured include/exclude rule
// short-circuited the check before Openserve ran. OpenserveQualification
// = the authenticated Product Qualification decided (the authority while
// the Openserve integration is enabled).
public enum CoverageMatchSource
{
    Openserve              = 0,
    CoverageMapInclude     = 1,
    CoverageMapExclude     = 2,
    OpenserveQualification = 3
}
