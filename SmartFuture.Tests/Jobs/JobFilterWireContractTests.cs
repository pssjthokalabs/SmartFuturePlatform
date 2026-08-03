using System.ComponentModel;
using SmartFuture.Shared.Enums.Jobs;

namespace SmartFuture.Tests.Jobs;

// Pins the WIRE contract for GET /api/jobs?workplaceType=… .
//
// The mobile app and the public website both build this query string, so
// what the binder will and won't accept is a real integration contract,
// not an implementation detail. ASP.NET Core binds a simple query-string
// value to an enum through TypeDescriptor's EnumConverter, which is
// exactly what these assertions exercise.
//
// The practical rule these tests encode: SEND THE INTEGER. Enum member
// names happen to work, but human display labels ("On-site", "Not
// stated") do NOT — and a value the binder rejects fails the whole
// request with a 400 rather than being quietly ignored.
public class JobFilterWireContractTests
{
    private static readonly TypeConverter Converter = TypeDescriptor.GetConverter(typeof(JobWorkplaceType));

    private static object? Convert(string value) => Converter.ConvertFromInvariantString(value);

    [Theory]
    [InlineData("0", JobWorkplaceType.Unknown)]
    [InlineData("1", JobWorkplaceType.Onsite)]
    [InlineData("2", JobWorkplaceType.Hybrid)]
    [InlineData("3", JobWorkplaceType.Remote)]
    public void Integer_values_bind(string wire, JobWorkplaceType expected)
    {
        Convert(wire).Should().Be(expected);
    }

    [Theory]
    [InlineData("Onsite", JobWorkplaceType.Onsite)]
    [InlineData("remote", JobWorkplaceType.Remote)]
    [InlineData("HYBRID", JobWorkplaceType.Hybrid)]
    public void Enum_member_names_bind_case_insensitively(string wire, JobWorkplaceType expected)
    {
        // This is why `workplaceTypeLabel` off a DTO round-trips: the
        // label IS the member name.
        Convert(wire).Should().Be(expected);
    }

    [Theory]
    [InlineData("On-site")]   // portal display label
    [InlineData("Not stated")] // portal display label for Unknown
    [InlineData("Work from home")]
    public void Human_display_labels_do_not_bind(string wire)
    {
        // A rejected value surfaces as a 400 on the whole request — the
        // filter is NOT silently dropped. Clients must map their display
        // label to the integer before sending.
        var act = () => Convert(wire);
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void Workplace_type_integer_mapping_is_stable()
    {
        // Guards the published contract table. Changing any of these
        // numbers silently breaks every already-shipped mobile build.
        ((int)JobWorkplaceType.Unknown).Should().Be(0);
        ((int)JobWorkplaceType.Onsite).Should().Be(1);
        ((int)JobWorkplaceType.Hybrid).Should().Be(2);
        ((int)JobWorkplaceType.Remote).Should().Be(3);
    }
}
