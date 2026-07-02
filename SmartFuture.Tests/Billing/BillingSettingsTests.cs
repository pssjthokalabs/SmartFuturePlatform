using Microsoft.Extensions.Configuration;
using SmartFuture.Application.Billing;
using SmartFuture.Shared.Enums.ServicePackages;

namespace SmartFuture.Tests.Billing;

// Verifies that the `Billing:` appsettings section binds cleanly onto
// BillingSettings + the ServiceType-branching helper produces the right
// answer for every product line.
public class BillingSettingsTests
{
    // ─── Defaults ────────────────────────────────────────────────────

    [Fact]
    public void Defaults_DefaultBillingDayIs30()
    {
        var s = new BillingSettings();
        s.DefaultBillingDay.Should().Be(30);
    }

    [Fact]
    public void Defaults_FibreStartsAfterActivation_SecurityStartsImmediately()
    {
        var s = new BillingSettings();

        s.ServiceFeeStartsAfterActivation.Fibre.Should().BeTrue(
            "Fibre's monthly meter starts once admin marks the installation active");
        s.ServiceFeeStartsAfterActivation.Security.Should().BeFalse(
            "Security's monthly meter starts on the order date");
    }

    [Fact]
    public void Defaults_ReconnectionFeeIsFiftyRand()
    {
        var s = new BillingSettings();
        s.Reconnection.Fee.Should().Be(50m);
        s.Reconnection.ChargeProRata.Should().BeTrue();
        s.Reconnection.RequireOutstandingInvoicesPaid.Should().BeFalse();
    }

    [Fact]
    public void ResolveReconnectionFee_ReturnsConfiguredValue()
    {
        var s = new BillingSettings { Reconnection = new ReconnectionSettings { Fee = 75m } };
        s.ResolveReconnectionFee().Should().Be(75m);
    }

    // ─── ChargeProRataAtCheckout branching ───────────────────────────

    [Fact]
    public void ChargeProRataAtCheckout_Fibre_ReturnsFalse_WithDefaults()
    {
        var s = new BillingSettings();
        s.ChargeProRataAtCheckout(ServicePackageType.Fibre).Should().BeFalse();
    }

    [Fact]
    public void ChargeProRataAtCheckout_Security_ReturnsTrue_WithDefaults()
    {
        var s = new BillingSettings();
        s.ChargeProRataAtCheckout(ServicePackageType.Security).Should().BeTrue();
    }

    [Theory]
    [InlineData(ServicePackageType.LTE)]
    [InlineData(ServicePackageType.Wireless)]
    [InlineData(ServicePackageType.WiFi)]
    [InlineData(ServicePackageType.Voice)]
    [InlineData(ServicePackageType.PrepaidFibre)]
    [InlineData(ServicePackageType.Other)]
    public void ChargeProRataAtCheckout_NonSecurityTypes_UseFibreRule(ServicePackageType type)
    {
        // Every non-Security type shares the Fibre "start after activation"
        // rule today. If a new product line ever needs a different rule,
        // this test fails first and forces the ChargeProRataAtCheckout
        // helper to be updated (rather than the branching happening
        // at every callsite).
        var s = new BillingSettings();

        s.ChargeProRataAtCheckout(type).Should().Be(
            !s.ServiceFeeStartsAfterActivation.Fibre);
    }

    [Fact]
    public void ChargeProRataAtCheckout_FlipsWhenSettingChanges()
    {
        var s = new BillingSettings
        {
            ServiceFeeStartsAfterActivation = new ServiceFeeStartTimingSettings
            {
                Fibre = false,     // Fibre now starts immediately too
                Security = true,   // Security now starts after activation
            }
        };

        s.ChargeProRataAtCheckout(ServicePackageType.Fibre).Should().BeTrue();
        s.ChargeProRataAtCheckout(ServicePackageType.Security).Should().BeFalse();
    }

    // ─── Configuration binding ───────────────────────────────────────

    [Fact]
    public void BindingFromConfig_PopulatesAllSections()
    {
        // Simulates an appsettings.json section for the whole Billing:
        // block. Verifies the standard Microsoft.Extensions.Configuration
        // binder wires every field — including the new Reconnection block —
        // so operators can change all of them via env vars.
        var dict = new Dictionary<string, string?>
        {
            ["Billing:DefaultBillingDay"] = "25",
            ["Billing:ServiceFeeStartsAfterActivation:Fibre"] = "false",
            ["Billing:ServiceFeeStartsAfterActivation:Security"] = "true",
            ["Billing:Reconnection:Fee"] = "75.50",
            ["Billing:Reconnection:ChargeProRata"] = "false",
            ["Billing:Reconnection:RequireOutstandingInvoicesPaid"] = "true",
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();

        var bound = new BillingSettings();
        config.GetSection(BillingSettings.SectionName).Bind(bound);

        bound.DefaultBillingDay.Should().Be(25);
        bound.ServiceFeeStartsAfterActivation.Fibre.Should().BeFalse();
        bound.ServiceFeeStartsAfterActivation.Security.Should().BeTrue();
        bound.Reconnection.Fee.Should().Be(75.50m);
        bound.Reconnection.ChargeProRata.Should().BeFalse();
        bound.Reconnection.RequireOutstandingInvoicesPaid.Should().BeTrue();
    }

    [Fact]
    public void BindingFromConfig_AbsentSection_KeepsLaunchSafeDefaults()
    {
        // Missing / empty Billing: section must fall back to the safe
        // launch defaults documented in BillingSettings.cs.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var bound = new BillingSettings();
        config.GetSection(BillingSettings.SectionName).Bind(bound);

        bound.DefaultBillingDay.Should().Be(30);
        bound.ServiceFeeStartsAfterActivation.Fibre.Should().BeTrue();
        bound.ServiceFeeStartsAfterActivation.Security.Should().BeFalse();
        bound.Reconnection.Fee.Should().Be(50m);
    }
}
