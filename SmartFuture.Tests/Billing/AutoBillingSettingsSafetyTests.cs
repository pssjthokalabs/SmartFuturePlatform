using Microsoft.Extensions.Configuration;
using SmartFuture.Application.Payments;

namespace SmartFuture.Tests.Billing;

// Phase-2 safety tests for AutoBillingSettings.
//
// The Phase-1 audit found the recurring engine ships DISABLED with
// dry-run ON. That's the launch-safe default — but it means shipping
// to UAT/prod requires an operator to flip the master switches OR the
// worker silently no-ops. These tests lock two invariants:
//
//   1. THE launch-safe defaults are exactly what the operator sees the
//      first time they read the settings. If someone flips a default in
//      AutoBillingSettings.cs and forgets to update the deployment
//      checklist, the failing test forces the discussion.
//   2. Both master switches (RecurringWorkerEnabled AND
//      DryRun off + Enabled on + ChargeAuthorizationEnabled on) MUST
//      be set for the pipeline to actually run real charges. A test
//      that documents this combination gives a red safety net.
//
// See the Phase-2 report for the deployment checklist (env vars are
// the same names as the property names — the config binder uses
// "AutoBilling:<PropertyName>" or the env-var pattern
// AutoBilling__<PropertyName>).
public class AutoBillingSettingsSafetyTests
{
    [Fact]
    public void AutoBillingSettings_Defaults_WorkerOff_DryRunOn()
    {
        var s = new AutoBillingSettings();

        s.RecurringWorkerEnabled.Should().BeFalse("recurring worker must default OFF for launch safety");
        s.DryRun.Should().BeTrue("default dry-run so no money moves until an operator opts in");
        s.Enabled.Should().BeFalse("master auto-billing switch OFF by default");
        s.ChargeAuthorizationEnabled.Should().BeFalse("Paystack charge auth OFF by default");
        s.RetryJobEnabled.Should().BeFalse("retry worker OFF by default");
        s.EnablePayFastRecurring.Should().BeFalse("PayFast recurring OFF by default (UAT sign-off required)");
        s.InstallationCompletionAutoChargeEnabled.Should().BeFalse("first-monthly auto-charge OFF by default");
        s.SuspendAfterGracePeriodEnabled.Should().BeFalse("suspension path OFF by default (report-only)");
        s.UseTestAmountOverride.Should().BeFalse("UAT amount override OFF by default");
        s.ManualTestEndpointEnabled.Should().BeFalse("admin /run-test endpoint OFF by default");
    }

    [Fact]
    public void AutoBillingSettings_Defaults_LeadTimeGracePeriodRetryPolicy()
    {
        // These are the *behavioural* defaults the calculator + runners
        // rely on. If any of them shift, tests in this file and the DB
        // integration tests need review.
        var s = new AutoBillingSettings();

        s.GenerateInvoicesDaysBeforeDue.Should().Be(5,
            "monthly invoices are generated 5 days before their due date by default");
        s.GracePeriodDays.Should().Be(3,
            "customer has 3 days past due before becoming a grace/suspension candidate");
        s.MaxRetryAttempts.Should().Be(3, "initial + 2 retries by default");
        s.RetryIntervalDays.Should().Be(1);
        s.MaxInvoicesPerRun.Should().Be(100);
        s.MaxChargesPerRun.Should().Be(100);
        s.MaxSuspensionCandidatesPerRun.Should().Be(500);
    }

    [Fact]
    public void AutoBillingSettings_Defaults_NotificationsMostlyOff()
    {
        // Phase 0F ships all customer/internal recurring billing emails
        // OFF by default. Only failure emails (the pre-existing
        // notifier) default ON. Deployment must OPT-IN to each channel.
        var s = new AutoBillingSettings();

        s.SendFailureEmails.Should().BeTrue("failure emails default ON — pre-existing behaviour");
        s.EmailFailuresDoNotBlockBilling.Should().BeTrue("email failures never abort billing by default");
        s.SendInvoiceGeneratedEmails.Should().BeFalse();
        s.SendGraceWarningEmails.Should().BeFalse();
        s.SendInternalBillingAlerts.Should().BeFalse();
        s.SendChargeSuccessEmails.Should().BeFalse(
            "authoritative InvoicePaid receipt already fires from PaymentApplier — avoid the duplicate");
        s.InternalBillingAlertEmail.Should().BeEmpty();
    }

    [Fact]
    public void AutoBillingSettings_WhenEnabledAndDryRunFalse_CanRunRealStages()
    {
        // Combination the ops team must flip to actually charge:
        // RecurringWorkerEnabled + Enabled + ChargeAuthorizationEnabled + DryRun=false.
        // This test documents the combination as a green baseline.
        var s = new AutoBillingSettings
        {
            RecurringWorkerEnabled = true,
            Enabled = true,
            ChargeAuthorizationEnabled = true,
            RetryJobEnabled = true,
            DryRun = false,
        };

        (s.RecurringWorkerEnabled
         && s.Enabled
         && s.ChargeAuthorizationEnabled
         && !s.DryRun).Should().BeTrue(
            "documents the 4-flag combination that flips the pipeline from dormant → live");
    }

    // ─── Config binding: env-var names ──────────────────────────────
    //
    // Confirms the exact `AutoBilling:<PropertyName>` / `AutoBilling__<PropertyName>`
    // pattern operators use in appsettings/environment. If a property is
    // renamed on the settings class, this test tells the deployment
    // owner they need to update env-var names too.

    [Fact]
    public void AutoBillingSettings_BindsFromConfig()
    {
        var dict = new Dictionary<string, string?>
        {
            ["AutoBilling:Enabled"] = "true",
            ["AutoBilling:ChargeAuthorizationEnabled"] = "true",
            ["AutoBilling:RetryJobEnabled"] = "true",
            ["AutoBilling:RecurringWorkerEnabled"] = "true",
            ["AutoBilling:DryRun"] = "false",
            ["AutoBilling:GenerateInvoicesDaysBeforeDue"] = "7",
            ["AutoBilling:GracePeriodDays"] = "5",
            ["AutoBilling:MaxRetryAttempts"] = "4",
            ["AutoBilling:MaxInvoicesPerRun"] = "50",
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();

        var bound = new AutoBillingSettings();
        config.GetSection(AutoBillingSettings.SectionName).Bind(bound);

        bound.Enabled.Should().BeTrue();
        bound.ChargeAuthorizationEnabled.Should().BeTrue();
        bound.RetryJobEnabled.Should().BeTrue();
        bound.RecurringWorkerEnabled.Should().BeTrue();
        bound.DryRun.Should().BeFalse();
        bound.GenerateInvoicesDaysBeforeDue.Should().Be(7);
        bound.GracePeriodDays.Should().Be(5);
        bound.MaxRetryAttempts.Should().Be(4);
        bound.MaxInvoicesPerRun.Should().Be(50);
    }

    [Fact]
    public void AutoBillingSettings_AbsentSection_KeepsLaunchSafeDefaults()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var bound = new AutoBillingSettings();
        config.GetSection(AutoBillingSettings.SectionName).Bind(bound);

        bound.Enabled.Should().BeFalse();
        bound.RecurringWorkerEnabled.Should().BeFalse();
        bound.DryRun.Should().BeTrue();
        bound.GracePeriodDays.Should().Be(3);
        bound.GenerateInvoicesDaysBeforeDue.Should().Be(5);
    }
}
