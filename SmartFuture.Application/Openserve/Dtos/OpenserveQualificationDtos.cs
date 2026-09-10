namespace SmartFuture.Application.Openserve.Dtos;

// Wire-format DTOs for the Product Qualification API (spec §3).
// Field names are copied verbatim from the spec's own response sample
// (§3.2, "Response") — including its inconsistent casing (AM_ID,
// BLD_NUM_ID, LR_SUBURB are SCREAMING_SNAKE; AddressInfo, ftthProductInfo
// are camelCase/PascalCase). Deserialized with
// PropertyNameCaseInsensitive=true (handles Pascal/camel drift) but the
// underscore names must still match exactly — case-insensitivity does
// not fold "AM_ID" and "AmId" together.
public class OpenserveQualificationResponse
{
    public int? ErrorCode { get; set; }
    public string? ErrorString { get; set; }
    public string? Message { get; set; }
    public OpenserveQualificationResults? Results { get; set; }
}

public class OpenserveQualificationResults
{
    public OpenserveQualificationPayload? Payload { get; set; }
}

public class OpenserveQualificationPayload
{
    public OpenserveQualificationAddressInfo? AddressInfo { get; set; }
    public OpenserveQualificationFtthInfrastructure? FtthInfrastructure { get; set; }
}

public class OpenserveQualificationAddressInfo
{
    public string? AMID { get; set; }
    public string? LR_Address { get; set; }
    public string? LR_LAT { get; set; }
    public string? LR_LON { get; set; }
    public string? LR_SUBURB { get; set; }
    public string? LR_STREET { get; set; }
    public string? LR_STREET_TYPE { get; set; }
    public string? LR_STREET_NO { get; set; }
    public string? LR_TOWN { get; set; }
    public string? LR_PROVINCE { get; set; }
    public string? LR_COUNTRY { get; set; }
    public string? DIST_M { get; set; }
    public string? MDU_Verification { get; set; }
    public OpenserveQualificationBuildingNumberInfo? buildingNumberInfo { get; set; }
}

public class OpenserveQualificationBuildingNumberInfo
{
    public List<OpenserveQualificationBuildingInfo>? buildingInfo { get; set; }
}

public class OpenserveQualificationBuildingInfo
{
    public string? AM_ID { get; set; }
    public string? BLD_NUM_ID { get; set; }
    public string? BLD_ID { get; set; }
    public string? FLOOR_ID { get; set; }
    public string? NUM { get; set; }
    public string? BUILDING_NAME { get; set; }
    public string? FLOOR { get; set; }
}

public class OpenserveQualificationFtthInfrastructure
{
    public List<OpenserveQualificationFtthInfo>? ftthInfo { get; set; }
}

public class OpenserveQualificationFtthInfo
{
    public string? FTTH_Status { get; set; }
    public decimal? fibreMaxSpeed { get; set; }
    public string? fibreMaxSpeedUnit { get; set; }
    public List<OpenserveQualificationProductInfo>? ftthProductInfo { get; set; }
}

public class OpenserveQualificationProductInfo
{
    public string? ProductName { get; set; }
    public string? ProductCode { get; set; }
    public string? upstreamSpeed { get; set; }
    public string? downstreamSpeed { get; set; }
}
