using SmartFuture.Application.Openserve;
using Xunit;

namespace SmartFuture.Tests.Openserve;

// Locks in the Appendix D transcription (Openserve Fulfilment API Spec
// ITSD-179559 Rev 04.002) so a future edit can't silently corrupt the
// catalogue used to validate package mappings.
public class OpenserveProductCatalogueTests
{
    [Theory]
    [InlineData("OFC", "75", "Mbps")]
    [InlineData("OFC", "10", "Mbps Lite")]
    [InlineData("OFCP", "500", "Mbps")]
    [InlineData("OWS", "25", "Mbps")] // retention offer — still a documented combination
    [InlineData("OWSP", "50", "Mbps")]
    [InlineData("OCC", "5", "Mbps")]
    public void IsValidCombination_ReturnsTrue_ForDocumentedEntries(string sku, string capacity, string uom)
    {
        Assert.True(OpenserveProductCatalogue.IsValidCombination(sku, capacity, uom));
    }

    [Theory]
    [InlineData("OFC", "999", "Mbps")]
    [InlineData("OFC", "75", "Kbps")]
    [InlineData("OWCA", "10", "Mbps")]
    public void IsValidCombination_ReturnsFalse_ForUndocumentedCombinations(string sku, string capacity, string uom)
    {
        Assert.False(OpenserveProductCatalogue.IsValidCombination(sku, capacity, uom));
    }

    [Fact]
    public void IsKnownSku_ReturnsFalse_ForMadeUpSku()
    {
        Assert.False(OpenserveProductCatalogue.IsKnownSku("OFX"));
    }

    [Theory]
    [InlineData("OFTR")]
    [InlineData("OIB")]
    [InlineData("OMC")]
    public void IsKnownSku_ReturnsTrue_ForSkusWithoutAPublishedSpeedTable(string sku)
    {
        Assert.True(OpenserveProductCatalogue.IsKnownSku(sku));
    }
}
