using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SmartFuture.Application.Billing;
using SmartFuture.Application.Billing.Dtos;
using SmartFuture.Application.OrderIntents;
using SmartFuture.Application.Payments;
using SmartFuture.Application.Payments.Dtos;
using SmartFuture.Application.Payments.Mandates;
using SmartFuture.Application.Payments.PayFast;
using SmartFuture.Application.Payments.Paystack;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.OrderIntents;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.Payments;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Results;
using SmartFuture.Tests.Infrastructure;

namespace SmartFuture.Tests.Billing;

// Phase-4 DB integration tests for OrderIntentService.ConvertIntentPaymentToPaidOrderAsync.
//
// The service takes 15 constructor dependencies but ConvertIntent only
// actually touches:
//   • IAppDbContext (real via SQLite fixture)
//   • IBillingDayOptionService (mock — resolves default day)
//   • BillingSettings (concrete via IOptions)
//   • IPaymentApplierService (MOCK — we assert it's called exactly once)
//   • ICustomerPaymentMandateService (mock — only touched with authorizationSnapshot)
//   • IHostEnvironment (mock — used for UAT override guard)
//   • ILogger (NullLogger)
//
// The eleven other deps (UserManager, ICurrentUserService, IOrderService,
// intent-init services, handoff service, provider settings) are not
// invoked by this method — passed as null! at construction. If ConvertIntent
// ever starts touching them the test throws with a clear NRE on that
// specific field.
//
// The applier is MOCKED, so tests assert the entity graph the convert
// method WROTE inside the atomic transaction — not the post-apply
// state that the PaymentApplierService orchestrator tests already cover.
public class OrderIntentServiceConvertTests
{
    private static OrderIntentService BuildService(
        SqliteTestDbFixture fx,
        Mock<IPaymentApplierService> paymentApplierMock,
        int defaultBillingDay = 30,
        bool isProduction = false)
    {
        var billingDayOptions = new Mock<IBillingDayOptionService>(MockBehavior.Loose);
        billingDayOptions.Setup(x => x.ResolveDefaultBillingDayAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(defaultBillingDay);

        var env = new Mock<IHostEnvironment>(MockBehavior.Loose);
        env.SetupGet(x => x.EnvironmentName).Returns(isProduction ? "Production" : "Development");

        var mandates = new Mock<ICustomerPaymentMandateService>(MockBehavior.Loose);
        mandates.Setup(x => x.UpsertPaystackMandateAsync(
                It.IsAny<UpsertPaystackMandateRequestDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Guid>.Success(Guid.NewGuid(), "ok"));

        return new OrderIntentService(
            dbContext: fx.AppDbContext,
            currentUser: null!,
            orderService: null!,
            userManager: null!,
            handoffService: null!,
            logger: NullLogger<OrderIntentService>.Instance,
            paystackIntentInit: null!,
            payFastIntentInit: null!,
            paymentApplier: paymentApplierMock.Object,
            mandates: mandates.Object,
            env: env.Object,
            payFastSettings: Options.Create(new PayFastSettings()),
            paystackSettings: Options.Create(new PaystackSettings()),
            billingDayOptions: billingDayOptions.Object,
            billingSettings: Options.Create(new BillingSettings()));
    }

    private static Mock<IPaymentApplierService> LooseApplier()
    {
        var m = new Mock<IPaymentApplierService>(MockBehavior.Loose);
        m.Setup(x => x.ApplyStatusChangeAsync(
                It.IsAny<ApplyPaymentStatusChangeRequestDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PaymentDto>.Success(new PaymentDto(), "ok"));
        return m;
    }

    // Seeds intent with the "customer paid via the gateway" state, ready
    // for ConvertIntentPaymentToPaidOrder to run.
    private static async Task<(SqliteTestDbFixture fx,
        SmartFuture.Domain.Identity.User user,
        SmartFuture.Domain.OrderIntents.OrderIntent intent,
        string reference)>
        SeedPaidIntentAsync(
            ServicePackageType packageType = ServicePackageType.Fibre,
            decimal packagePrice = 899m,
            decimal? installationFee = 100m,
            int? preferredBillingDay = 15,
            SmartFuture.Domain.ServicePackages.ServicePackageVariant? preseededVariant = null)
    {
        var fx = await SqliteTestDbFixture.CreateAsync();
        var user = TestEntityFactory.CreateUser(fx.AppDbContext);
        var pkg = TestEntityFactory.CreateServicePackage(fx.AppDbContext,
            type: packageType, price: packagePrice, installationFee: installationFee);
        SmartFuture.Domain.ServicePackages.ServicePackageVariant? variant = null;
        if (preseededVariant is not null)
        {
            variant = preseededVariant;
            variant.ServicePackage = pkg;
            variant.ServicePackageId = pkg.Id;
            fx.AppDbContext.ServicePackageVariants.Add(variant);
        }
        await fx.DbContext.SaveChangesAsync();

        var reference = $"SF-INTENT-{Guid.NewGuid():N}"[..24];
        var intent = TestEntityFactory.CreateOrderIntent(fx.AppDbContext, user, pkg,
            nowUtc: new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc),
            variant: variant,
            preferredBillingDay: preferredBillingDay);
        intent.IntentPaymentReference = reference;
        intent.IntentPaymentAmount = 100m; // matches installation fee for the base fibre test
        intent.AddressLine1 = "12 Test Street";
        intent.City = "Johannesburg";
        intent.Province = "Gauteng";
        intent.PostalCode = "2196";
        intent.Country = "South Africa";
        intent.Email = user.Email;
        intent.PhoneNumber = "+27810000000";
        intent.FullName = "Test Customer";
        intent.RequestedInstallationDateUtc = new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc);
        await fx.DbContext.SaveChangesAsync();

        return (fx, user, intent, reference);
    }

    // ─── Fibre: activation-only invoice ────────────────────────────

    [Fact]
    public async Task ConvertIntentPaymentToPaidOrder_Fibre_CreatesPaidOrderWithActivationOnlyInvoice()
    {
        var (fx, _, intent, reference) = await SeedPaidIntentAsync(
            packageType: ServicePackageType.Fibre, packagePrice: 899m, installationFee: 100m,
            preferredBillingDay: 15);
        var applier = LooseApplier();
        var svc = BuildService(fx, applier);

        var result = await svc.ConvertIntentPaymentToPaidOrderAsync(
            reference,
            paidAtUtc: new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc),
            gatewayTransactionId: "GW-TX-1");

        result.IsSuccess.Should().BeTrue(result.Message);
        result.Data!.AlreadyConverted.Should().BeFalse();

        var order = await fx.DbContext.Orders.AsNoTracking().SingleAsync(o => o.Id == result.Data.OrderId);
        order.PackageType.Should().Be(ServicePackageType.Fibre);
        order.PackagePrice.Should().Be(899m);
        order.PackageInstallationFee.Should().Be(100m);
        order.PreferredBillingDay.Should().Be(15);
        order.Status.Should().Be(OrderStatus.PaymentReceived);
        order.FirstProRataInvoiceGeneratedAtUtc.Should().BeNull(
            "Fibre pays activation only at checkout — no pro-rata invoice → no stamp");

        var invoice = await fx.DbContext.Invoices.AsNoTracking()
            .Include(i => i.LineItems)
            .SingleAsync(i => i.Id == result.Data.InvoiceId);
        invoice.TotalAmount.Should().Be(100m);
        invoice.LineItems.Should().HaveCount(1);
        invoice.LineItems.Single().LineType.Should().Be(InvoiceLineItemType.InstallationFee);
        invoice.PeriodStartUtc.Should().BeNull("Fibre invoice has no pro-rata period");
        invoice.PeriodEndUtc.Should().BeNull();

        applier.Verify(x => x.ApplyStatusChangeAsync(
            It.Is<ApplyPaymentStatusChangeRequestDto>(r =>
                r.PaymentId == result.Data.PaymentId
                && r.NewStatus == PaymentStatus.Completed),
            It.IsAny<CancellationToken>()), Times.Once);

        await fx.DisposeAsync();
    }

    // ─── Security: activation + pro-rata invoice ───────────────────

    [Fact]
    public async Task ConvertIntentPaymentToPaidOrder_Security_CreatesPaidOrderWithActivationAndProRataInvoice()
    {
        var (fx, _, intent, reference) = await SeedPaidIntentAsync(
            packageType: ServicePackageType.Security, packagePrice: 699m, installationFee: 999m,
            preferredBillingDay: 15);
        // Security intent's IntentPaymentAmount = activation + pro-rata.
        // Pro-rata for R699/mo, 2026-06-04 → 15 = 11 days = round(699 * 11 / 30, 2) = 256.30.
        intent.IntentPaymentAmount = 999m + 256.30m;
        await fx.DbContext.SaveChangesAsync();
        var applier = LooseApplier();
        var svc = BuildService(fx, applier);

        var result = await svc.ConvertIntentPaymentToPaidOrderAsync(
            reference, paidAtUtc: null, gatewayTransactionId: "GW-TX-2");

        result.IsSuccess.Should().BeTrue(result.Message);
        var order = await fx.DbContext.Orders.AsNoTracking().SingleAsync(o => o.Id == result.Data!.OrderId);
        order.PackageType.Should().Be(ServicePackageType.Security);
        order.FirstProRataInvoiceGeneratedAtUtc.Should().NotBeNull(
            "Security bundles pro-rata at checkout — stamp prevents downstream double-charge");

        var invoice = await fx.DbContext.Invoices.AsNoTracking()
            .Include(i => i.LineItems)
            .SingleAsync(i => i.Id == result.Data.InvoiceId);
        invoice.TotalAmount.Should().Be(999m + 256.30m);
        invoice.LineItems.Should().HaveCount(2);
        invoice.LineItems.Select(l => l.LineType).Should().Contain(InvoiceLineItemType.InstallationFee);
        invoice.LineItems.Select(l => l.LineType).Should().Contain(InvoiceLineItemType.ProRata);
        invoice.PeriodStartUtc.Should().Be(new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc));
        invoice.PeriodEndUtc.Should().Be(new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc));

        await fx.DisposeAsync();
    }

    // ─── Security + variant: snapshot rules ────────────────────────

    [Fact]
    public async Task ConvertIntentPaymentToPaidOrder_SecurityVariant_SnapshotsVariantIdAndPrice()
    {
        var variant = new SmartFuture.Domain.ServicePackages.ServicePackageVariant
        {
            Id = Guid.NewGuid(), Name = "8 IP", Price = 1499m, InstallationFee = 999m,
            HasFreeInstallation = false, IsActive = true, DisplayOrder = 1,
        };
        var (fx, _, intent, reference) = await SeedPaidIntentAsync(
            packageType: ServicePackageType.Security, packagePrice: 699m, installationFee: 999m,
            preferredBillingDay: 15,
            preseededVariant: variant);
        // Pro-rata for R1499/mo, 11 days = round(1499 * 11 / 30, 2) = 549.63.
        intent.IntentPaymentAmount = 999m + 549.63m;
        await fx.DbContext.SaveChangesAsync();
        var applier = LooseApplier();
        var svc = BuildService(fx, applier);

        var result = await svc.ConvertIntentPaymentToPaidOrderAsync(reference, null, "GW-TX-3");
        result.IsSuccess.Should().BeTrue(result.Message);

        var order = await fx.DbContext.Orders.AsNoTracking().SingleAsync(o => o.Id == result.Data!.OrderId);
        order.ServicePackageVariantId.Should().Be(variant.Id);
        order.PackageVariantName.Should().Be("8 IP");
        order.PackagePrice.Should().Be(1499m,
            "variant price snapshots onto the Order — future variant price edits do NOT re-price this order");

        var invoice = await fx.DbContext.Invoices.AsNoTracking()
            .Include(i => i.LineItems)
            .SingleAsync(i => i.Id == result.Data.InvoiceId);
        invoice.TotalAmount.Should().Be(999m + 549.63m,
            "invoice = variant activation + variant-price-based pro-rata");

        await fx.DisposeAsync();
    }

    // ─── Idempotency ───────────────────────────────────────────────

    [Fact]
    public async Task ConvertIntentPaymentToPaidOrder_Replay_IsIdempotent()
    {
        var (fx, _, _, reference) = await SeedPaidIntentAsync(
            packageType: ServicePackageType.Fibre, packagePrice: 899m, installationFee: 100m);
        var applier = LooseApplier();
        var svc = BuildService(fx, applier);

        var first = await svc.ConvertIntentPaymentToPaidOrderAsync(reference, null, "GW-TX-A");
        first.IsSuccess.Should().BeTrue(first.Message);

        // Replay — same reference, different tx id. Must return the
        // EXISTING order, not create a second one.
        var replay = await svc.ConvertIntentPaymentToPaidOrderAsync(reference, null, "GW-TX-B");
        replay.IsSuccess.Should().BeTrue(replay.Message);
        replay.Data!.OrderId.Should().Be(first.Data!.OrderId);
        replay.Data.AlreadyConverted.Should().BeTrue(
            "replay must be flagged so callers know no new work was done");

        (await fx.DbContext.Orders.CountAsync()).Should().Be(1);
        (await fx.DbContext.Invoices.CountAsync()).Should().Be(1);

        // Applier called EXACTLY ONCE (on first). Replay short-circuits.
        applier.Verify(x => x.ApplyStatusChangeAsync(
            It.IsAny<ApplyPaymentStatusChangeRequestDto>(),
            It.IsAny<CancellationToken>()), Times.Once);

        await fx.DisposeAsync();
    }

    // ─── Address persistence ───────────────────────────────────────

    [Fact]
    public async Task ConvertIntentPaymentToPaidOrder_ManualAddress_PersistsAddressFields()
    {
        var (fx, _, _, reference) = await SeedPaidIntentAsync(
            packageType: ServicePackageType.Fibre, packagePrice: 899m, installationFee: 100m);
        var applier = LooseApplier();
        var svc = BuildService(fx, applier);

        var result = await svc.ConvertIntentPaymentToPaidOrderAsync(reference, null, "GW-TX-C");
        result.IsSuccess.Should().BeTrue(result.Message);

        var order = await fx.DbContext.Orders.AsNoTracking().SingleAsync(o => o.Id == result.Data!.OrderId);
        order.AddressLine1.Should().Be("12 Test Street");
        order.City.Should().Be("Johannesburg");
        order.Province.Should().Be("Gauteng");
        order.PostalCode.Should().Be("2196");
        order.Country.Should().Be("South Africa");

        await fx.DisposeAsync();
    }

    [Fact]
    public async Task ConvertIntentPaymentToPaidOrder_GoogleAddress_PersistsGooglePlaceId()
    {
        var (fx, _, intent, reference) = await SeedPaidIntentAsync(
            packageType: ServicePackageType.Fibre, packagePrice: 899m, installationFee: 100m);
        intent.GooglePlaceId = "ChIJTestPlaceId";
        intent.Latitude = -26.2m;
        intent.Longitude = 28.05m;
        await fx.DbContext.SaveChangesAsync();
        var applier = LooseApplier();
        var svc = BuildService(fx, applier);

        var result = await svc.ConvertIntentPaymentToPaidOrderAsync(reference, null, "GW-TX-D");
        result.IsSuccess.Should().BeTrue(result.Message);

        var order = await fx.DbContext.Orders.AsNoTracking().SingleAsync(o => o.Id == result.Data!.OrderId);
        order.GooglePlaceId.Should().Be("ChIJTestPlaceId");
        order.Latitude.Should().Be(-26.2m);
        order.Longitude.Should().Be(28.05m);

        await fx.DisposeAsync();
    }

    // ─── Preferred billing day ─────────────────────────────────────

    [Fact]
    public async Task ConvertIntentPaymentToPaidOrder_PreferredBillingDay_PersistsBillingDay()
    {
        var (fx, _, _, reference) = await SeedPaidIntentAsync(
            packageType: ServicePackageType.Fibre, packagePrice: 899m, installationFee: 100m,
            preferredBillingDay: 25);
        var applier = LooseApplier();
        var svc = BuildService(fx, applier);

        var result = await svc.ConvertIntentPaymentToPaidOrderAsync(reference, null, "GW-TX-E");
        result.IsSuccess.Should().BeTrue(result.Message);

        var order = await fx.DbContext.Orders.AsNoTracking().SingleAsync(o => o.Id == result.Data!.OrderId);
        order.PreferredBillingDay.Should().Be(25,
            "customer's picker choice must survive intent → order snapshot");

        await fx.DisposeAsync();
    }

    [Fact]
    public async Task ConvertIntentPaymentToPaidOrder_NoBillingDayOnIntent_FallsBackToDefault()
    {
        // Legacy intent minted before the billing-day picker existed —
        // preferredBillingDay is null on the intent. Service must fall
        // back to IBillingDayOptionService.ResolveDefaultBillingDayAsync
        // rather than crashing.
        var (fx, _, _, reference) = await SeedPaidIntentAsync(
            packageType: ServicePackageType.Fibre, packagePrice: 899m, installationFee: 100m,
            preferredBillingDay: null);
        var applier = LooseApplier();
        var svc = BuildService(fx, applier, defaultBillingDay: 30);

        var result = await svc.ConvertIntentPaymentToPaidOrderAsync(reference, null, "GW-TX-F");
        result.IsSuccess.Should().BeTrue(result.Message);

        var order = await fx.DbContext.Orders.AsNoTracking().SingleAsync(o => o.Id == result.Data!.OrderId);
        order.PreferredBillingDay.Should().Be(30);

        await fx.DisposeAsync();
    }
}
