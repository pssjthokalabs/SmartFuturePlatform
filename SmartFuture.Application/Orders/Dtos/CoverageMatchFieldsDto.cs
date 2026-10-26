namespace SmartFuture.Application.Orders.Dtos;

// The address components the coverage check matched on that a checkout
// request has no field for. Sent back at checkout so SmartFuture's own
// coverage inclusion list (Coverage Map Include rules) is re-evaluated on
// EXACTLY the components it matched during the coverage check — e.g. a
// rule on Town (Google's municipality) can't match an order that only
// carries City. Used only for that evaluation; never stored on the order.
// Same trust model as the public coverage check itself.
public class CoverageMatchFieldsDto
{
    public string? AddressText { get; set; }
    public string? StreetName { get; set; }
    public string? Town { get; set; }
    public string? FormattedAddress { get; set; }
    public string? PlaceName { get; set; }
}
