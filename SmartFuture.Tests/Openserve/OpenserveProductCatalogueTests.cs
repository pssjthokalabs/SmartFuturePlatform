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
    [InlineData("OWS", "25", "Mbps")]
    [InlineData("OFC", "40", "Mbps")] // retention offer (**) — still a documented combination
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

    // Appendix D marks exactly two rows "**" (pp. 166 and 168, read with
    // pdftotext -table): OFC 40 Mbps and OWS 40 Mbps Lite. Retention offers
    // can only be ordered as a Regrade, never on a new Sales Order.
    [Fact]
    public void RetentionOffers_AreExactlyTheTwoAppendixDRows()
    {
        var retention = OpenserveProductCatalogue.Entries.Where(e => e.IsRetentionOffer).Select(e => $"{e.Sku} {e.Capacity} {e.CapacityUom}").ToList();
        Assert.Equal(new[] { "OFC 40 Mbps", "OWS 40 Mbps Lite" }, retention);
        Assert.False(OpenserveProductCatalogue.IsOrderableAsNewSalesOrder("OFC", "40", "Mbps"));
        Assert.False(OpenserveProductCatalogue.IsOrderableAsNewSalesOrder("OWS", "40", "Mbps Lite"));
        Assert.True(OpenserveProductCatalogue.IsOrderableAsNewSalesOrder("OWS", "25", "Mbps"));
        Assert.True(OpenserveProductCatalogue.IsOrderableAsNewSalesOrder("OFC", "100", "Mbps"));
        Assert.False(OpenserveProductCatalogue.IsOrderableAsNewSalesOrder("OFC", "999", "Mbps"));
    }

    [Theory]
    [InlineData("OFC", "Openserve Fibre Connect")]
    [InlineData("OFCP", "Openserve Fibre Connect Premium")]
    [InlineData("OOCF", "Openserve Office Connect")]
    [InlineData("OWS", "Openserve Webstream")]
    [InlineData("OWA", "Openserve Web Connect Air")]
    public void ProductNames_MatchTheSpecSpelling(string sku, string productName)
        => Assert.Equal(productName, OpenserveProductCatalogue.ProductNameFor(sku));

    [Fact]
    public void EveryCatalogueSku_HasADocumentedProductName()
    {
        foreach (var sku in OpenserveProductCatalogue.Entries.Select(e => e.Sku).Concat(OpenserveProductCatalogue.SkusWithoutPublishedSpeedTable).Distinct())
            Assert.False(string.IsNullOrWhiteSpace(OpenserveProductCatalogue.ProductNameFor(sku)), sku);
    }
}
