namespace SmartFuture.Shared.Enums.Orders;

// Customer-declared residence/property type captured alongside the
// installation address during order creation. Structured (not free
// text) so admin/install tooling and, later, Openserve MDU
// building/unit matching can reason about it. Optional/nullable on
// Order — every order created before this field existed simply has
// null here; nothing backfills it and no existing read/write path is
// required to populate it.
public enum PropertyType
{
    House = 0,
    Apartment = 1,
    Townhouse = 2,
    ComplexEstate = 3,
    Duplex = 4,
    StudentResidence = 5,
    BusinessOffice = 6,
    Other = 99
}
