using Microsoft.Extensions.Options;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Payments.Recurring;
using SmartFuture.Shared.Enums.Payments;
using SmartFuture.Tests.Infrastructure;

namespace SmartFuture.Tests.Billing;

// Phase-5 tests for RecurringMandateSelector (Phase 1C).
//
// Contract:
//   • Considers only active, reusable, default mandates for supported
//     providers (Paystack, PayFast).
//   • PayFast is only chargeable when
//     AutoBillingSettings.EnablePayFastRecurring is true.
//   • Paystack is preferred (proven + synchronous). When both are
//     eligible, Paystack wins.
//   • Tie-breaks within one provider: most-recently-updated default.
//   • GetAvailabilityAsync distinguishes "no mandate" from
//     "PayFast recurring disabled".
public class RecurringMandateSelectorTests
{
    private static RecurringMandateSelector Build(SqliteTestDbFixture fx, bool enablePayFast = false) =>
        new(fx.AppDbContext,
            Options.Create(new AutoBillingSettings { EnablePayFastRecurring = enablePayFast }));

    // ─── ResolveDefaultChargeableMandate ────────────────────────────

    [Fact]
    public async Task RecurringMandateSelector_PrefersPaystackMandate_WhenAvailable()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var user = TestEntityFactory.CreateUser(fx.AppDbContext);
        // Both Paystack + PayFast default mandates present. Paystack must win.
        var payfast = TestEntityFactory.CreateMandate(fx.AppDbContext, user, PaymentProviderType.PayFast,
            updatedAtUtc: new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc));
        var paystack = TestEntityFactory.CreateMandate(fx.AppDbContext, user, PaymentProviderType.Paystack,
            updatedAtUtc: new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc));
        await fx.DbContext.SaveChangesAsync();

        var selector = Build(fx, enablePayFast: true);
        var chosen = await selector.ResolveDefaultChargeableMandateAsync(user.Id);

        chosen.Should().NotBeNull();
        chosen!.Id.Should().Be(paystack.Id, "Paystack always wins over PayFast (proven + synchronous)");
    }

    [Fact]
    public async Task RecurringMandateSelector_UsesPayFastRecurring_WhenPaystackMissingAndPayFastEnabled()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var user = TestEntityFactory.CreateUser(fx.AppDbContext);
        var payfast = TestEntityFactory.CreateMandate(fx.AppDbContext, user, PaymentProviderType.PayFast);
        await fx.DbContext.SaveChangesAsync();

        var selector = Build(fx, enablePayFast: true);
        var chosen = await selector.ResolveDefaultChargeableMandateAsync(user.Id);

        chosen.Should().NotBeNull();
        chosen!.Id.Should().Be(payfast.Id);
        chosen.Provider.Should().Be(PaymentProviderType.PayFast);
    }

    [Fact]
    public async Task RecurringMandateSelector_SkipsPayFastRecurring_WhenDisabled()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var user = TestEntityFactory.CreateUser(fx.AppDbContext);
        // Only PayFast default. PayFast disabled → nothing chargeable.
        TestEntityFactory.CreateMandate(fx.AppDbContext, user, PaymentProviderType.PayFast);
        await fx.DbContext.SaveChangesAsync();

        var selector = Build(fx, enablePayFast: false);
        var chosen = await selector.ResolveDefaultChargeableMandateAsync(user.Id);

        chosen.Should().BeNull();
    }

    [Fact]
    public async Task RecurringMandateSelector_ReturnsNull_WhenNoChargeableMandate()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var user = TestEntityFactory.CreateUser(fx.AppDbContext);
        await fx.DbContext.SaveChangesAsync();

        var selector = Build(fx);
        var chosen = await selector.ResolveDefaultChargeableMandateAsync(user.Id);
        chosen.Should().BeNull();
    }

    [Fact]
    public async Task RecurringMandateSelector_IgnoresInactiveMandates()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var user = TestEntityFactory.CreateUser(fx.AppDbContext);
        TestEntityFactory.CreateMandate(fx.AppDbContext, user, PaymentProviderType.Paystack,
            isActive: false);
        await fx.DbContext.SaveChangesAsync();

        var selector = Build(fx);
        var chosen = await selector.ResolveDefaultChargeableMandateAsync(user.Id);
        chosen.Should().BeNull();
    }

    [Fact]
    public async Task RecurringMandateSelector_IgnoresNonReusableMandates()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var user = TestEntityFactory.CreateUser(fx.AppDbContext);
        TestEntityFactory.CreateMandate(fx.AppDbContext, user, PaymentProviderType.Paystack,
            isReusable: false);
        await fx.DbContext.SaveChangesAsync();

        var selector = Build(fx);
        var chosen = await selector.ResolveDefaultChargeableMandateAsync(user.Id);
        chosen.Should().BeNull();
    }

    [Fact]
    public async Task RecurringMandateSelector_IgnoresNonDefaultMandates()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var user = TestEntityFactory.CreateUser(fx.AppDbContext);
        TestEntityFactory.CreateMandate(fx.AppDbContext, user, PaymentProviderType.Paystack,
            isDefault: false);
        await fx.DbContext.SaveChangesAsync();

        var selector = Build(fx);
        var chosen = await selector.ResolveDefaultChargeableMandateAsync(user.Id);
        chosen.Should().BeNull();
    }

    [Fact]
    public async Task RecurringMandateSelector_IgnoresMandatesForOtherUsers()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var user1 = TestEntityFactory.CreateUser(fx.AppDbContext);
        var user2 = TestEntityFactory.CreateUser(fx.AppDbContext);
        TestEntityFactory.CreateMandate(fx.AppDbContext, user2, PaymentProviderType.Paystack);
        await fx.DbContext.SaveChangesAsync();

        var selector = Build(fx);
        var chosen = await selector.ResolveDefaultChargeableMandateAsync(user1.Id);
        chosen.Should().BeNull();
    }

    // ─── GetAvailability ────────────────────────────────────────────

    [Fact]
    public async Task RecurringMandateSelector_Availability_Paystack_ReturnsAvailable()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var user = TestEntityFactory.CreateUser(fx.AppDbContext);
        TestEntityFactory.CreateMandate(fx.AppDbContext, user, PaymentProviderType.Paystack);
        await fx.DbContext.SaveChangesAsync();

        var availability = await Build(fx).GetAvailabilityAsync(user.Id);
        availability.Should().Be(MandateAvailability.Available);
    }

    [Fact]
    public async Task RecurringMandateSelector_Availability_OnlyPayFastAndDisabled_ReturnsPayFastRecurringDisabled()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var user = TestEntityFactory.CreateUser(fx.AppDbContext);
        TestEntityFactory.CreateMandate(fx.AppDbContext, user, PaymentProviderType.PayFast);
        await fx.DbContext.SaveChangesAsync();

        var availability = await Build(fx, enablePayFast: false).GetAvailabilityAsync(user.Id);
        availability.Should().Be(MandateAvailability.PayFastRecurringDisabled,
            "operator diagnostics: distinguish 'PayFast exists but disabled' from 'no mandate at all'");
    }

    [Fact]
    public async Task RecurringMandateSelector_Availability_NoMandateAtAll_ReturnsNoReusableMandate()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var user = TestEntityFactory.CreateUser(fx.AppDbContext);
        await fx.DbContext.SaveChangesAsync();

        var availability = await Build(fx).GetAvailabilityAsync(user.Id);
        availability.Should().Be(MandateAvailability.NoReusableMandate);
    }

    // ─── Tie-break rule ────────────────────────────────────────────

    [Fact]
    public async Task RecurringMandateSelector_MultiplePaystackMandates_PicksMostRecentlyUpdated()
    {
        await using var fx = await SqliteTestDbFixture.CreateAsync();
        var user = TestEntityFactory.CreateUser(fx.AppDbContext);
        var older = TestEntityFactory.CreateMandate(fx.AppDbContext, user, PaymentProviderType.Paystack,
            updatedAtUtc: new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc));
        var newer = TestEntityFactory.CreateMandate(fx.AppDbContext, user, PaymentProviderType.Paystack,
            updatedAtUtc: new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc));
        await fx.DbContext.SaveChangesAsync();

        var chosen = await Build(fx).ResolveDefaultChargeableMandateAsync(user.Id);
        chosen.Should().NotBeNull();
        chosen!.Id.Should().Be(newer.Id);
    }
}
