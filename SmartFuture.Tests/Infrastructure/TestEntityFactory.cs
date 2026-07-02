using SmartFuture.Application.Persistence;
using SmartFuture.Domain.Billing;
using SmartFuture.Domain.Identity;
using SmartFuture.Domain.NetworkAccounts;
using SmartFuture.Domain.OrderIntents;
using SmartFuture.Domain.Orders;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Shared.Enums.Billing;
using SmartFuture.Shared.Enums.NetworkAccounts;
using SmartFuture.Shared.Enums.OrderIntents;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.Payments;
using SmartFuture.Shared.Enums.ServicePackages;

namespace SmartFuture.Tests.Infrastructure;

/// <summary>
/// Factory helpers for seeding a coherent minimal graph of billing
/// entities in tests. Each helper creates ONE entity with sensible
/// defaults, tracks it on the fixture's DbContext, and returns it —
/// callers pick which fields to override before / after adding.
///
/// Nothing here calls <see cref="System.DateTime.UtcNow"/>. All dates
/// come in as parameters so tests stay deterministic. Where a helper
/// has to produce a value with no obvious caller input (e.g. a Guid
/// InvoiceNumber), it uses a monotonic counter seeded from a Guid so
/// the string is unique per test but has no wall-clock dependency.
/// </summary>
public static class TestEntityFactory
{
    private static int _sequence;
    private static int Next() => Interlocked.Increment(ref _sequence);

    public static User CreateUser(
        IAppDbContext db,
        string? email = null,
        string firstName = "Test",
        string lastName = "Customer")
    {
        var seq = Next();
        var user = new User
        {
            UserName = email ?? $"customer{seq}@test.local",
            NormalizedUserName = (email ?? $"customer{seq}@test.local").ToUpperInvariant(),
            Email = email ?? $"customer{seq}@test.local",
            NormalizedEmail = (email ?? $"customer{seq}@test.local").ToUpperInvariant(),
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString(),
            FirstName = firstName,
            LastName = lastName,
            IsActive = true,
        };
        db.Users.Add(user);
        return user;
    }

    public static ServicePackage CreateServicePackage(
        IAppDbContext db,
        ServicePackageType type = ServicePackageType.Security,
        string name = "Test CCTV 4 IP",
        decimal price = 699m,
        decimal? installationFee = 999m,
        bool hasFreeInstallation = false)
    {
        var pkg = new ServicePackage
        {
            Type = type,
            Status = ServicePackageStatus.Active,
            Name = name,
            Price = price,
            InstallationFee = installationFee,
            HasFreeInstallation = hasFreeInstallation,
            BillingCycle = ServicePackageBillingCycle.Monthly,
        };
        db.ServicePackages.Add(pkg);
        return pkg;
    }

    public static ServicePackageVariant CreateVariant(
        IAppDbContext db,
        ServicePackage package,
        string name,
        decimal price,
        decimal? installationFee = null,
        bool? hasFreeInstallation = null,
        int displayOrder = 0,
        bool isActive = true)
    {
        var variant = new ServicePackageVariant
        {
            ServicePackage = package,
            ServicePackageId = package.Id,
            Name = name,
            Price = price,
            InstallationFee = installationFee,
            HasFreeInstallation = hasFreeInstallation,
            DisplayOrder = displayOrder,
            IsActive = isActive,
        };
        db.ServicePackageVariants.Add(variant);
        return variant;
    }

    /// <summary>
    /// Create an Order with all the Package* snapshot fields already
    /// derived from the (optional) selected variant — mirrors how the
    /// real intent conversion writes them.
    /// </summary>
    public static Order CreateOrder(
        IAppDbContext db,
        User user,
        ServicePackage package,
        ServicePackageVariant? variant = null,
        OrderStatus status = OrderStatus.PaymentReceived,
        int preferredBillingDay = 30,
        string orderNumber = "SF-ORD-TEST")
    {
        var monthly = variant?.Price ?? package.Price;
        var hasFreeInst = variant?.HasFreeInstallation ?? package.HasFreeInstallation;
        var instFee = hasFreeInst ? (decimal?)0m : (variant?.InstallationFee ?? package.InstallationFee);
        var order = new Order
        {
            OrderNumber = $"{orderNumber}-{Next()}",
            User = user,
            UserId = user.Id,
            ServicePackage = package,
            ServicePackageId = package.Id,
            ServicePackageVariant = variant,
            ServicePackageVariantId = variant?.Id,
            PackageVariantName = variant?.Name,
            Status = status,
            PackageName = package.Name,
            PackageType = package.Type,
            PackagePrice = monthly,
            PackageHasFreeInstallation = hasFreeInst,
            PackageInstallationFee = instFee,
            PackageBillingCycle = ServicePackageBillingCycle.Monthly,
            AddressLine1 = "1 Test Street",
            City = "Johannesburg",
            Province = "Gauteng",
            PreferredBillingDay = preferredBillingDay,
        };
        db.Orders.Add(order);
        return order;
    }

    public static OrderIntent CreateOrderIntent(
        IAppDbContext db,
        User claimedByUser,
        ServicePackage package,
        DateTime nowUtc,
        ServicePackageVariant? variant = null,
        int? preferredBillingDay = 30,
        OrderIntentStatus status = OrderIntentStatus.Pending)
    {
        var intent = new OrderIntent
        {
            IntentToken = $"tok-{Next():x}",
            ClaimedByUser = claimedByUser,
            ClaimedByUserId = claimedByUser.Id,
            ClaimedAtUtc = nowUtc,
            ServicePackage = package,
            ServicePackageId = package.Id,
            ServicePackageVariantId = variant?.Id,
            Status = status,
            AddressLine1 = "1 Test Street",
            City = "Johannesburg",
            Province = "Gauteng",
            PreferredBillingDay = preferredBillingDay,
            CreatedAtUtc = nowUtc,
            ExpiresAtUtc = nowUtc.AddHours(72),
            Provider = PaymentProviderType.Paystack,
        };
        db.OrderIntents.Add(intent);
        return intent;
    }

    public static Invoice CreateInvoice(
        IAppDbContext db,
        Order order,
        decimal totalAmount,
        DateTime? dueAtUtc = null,
        DateTime? issuedAtUtc = null,
        InvoiceStatus status = InvoiceStatus.Issued,
        Guid? serviceBillingScheduleId = null,
        DateTime? periodStartUtc = null,
        DateTime? periodEndUtc = null,
        string? invoiceNumber = null)
    {
        var invoice = new Invoice
        {
            InvoiceNumber = invoiceNumber ?? $"INV-{Next():D8}",
            Order = order,
            OrderId = order.Id,
            Status = status,
            SubtotalAmount = totalAmount,
            TaxAmount = 0m,
            TotalAmount = totalAmount,
            AmountPaid = 0m,
            BalanceDue = totalAmount,
            CurrencyCode = "ZAR",
            IssuedAtUtc = issuedAtUtc,
            DueAtUtc = dueAtUtc,
            ServiceBillingScheduleId = serviceBillingScheduleId,
            PeriodStartUtc = periodStartUtc,
            PeriodEndUtc = periodEndUtc,
        };
        db.Invoices.Add(invoice);
        return invoice;
    }

    public static InvoiceLineItem AddLineItem(
        IAppDbContext db,
        Invoice invoice,
        InvoiceLineItemType type,
        decimal totalAmount,
        string description,
        int sortOrder = 0)
    {
        var line = new InvoiceLineItem
        {
            Invoice = invoice,
            InvoiceId = invoice.Id,
            LineType = type,
            Description = description,
            Quantity = 1,
            UnitAmount = totalAmount,
            TotalAmount = totalAmount,
            SortOrder = sortOrder,
        };
        db.InvoiceLineItems.Add(line);
        return line;
    }

    public static NetworkAccount CreateNetworkAccount(
        IAppDbContext db,
        Order order,
        NetworkAccountStatus status = NetworkAccountStatus.Active,
        string providerName = "Openserve",
        ServicePackageType packageType = ServicePackageType.Fibre,
        decimal packagePrice = 699m)
    {
        var acct = new NetworkAccount
        {
            AccountNumber = $"NA-{Next():D6}",
            Username = $"user{Next()}@sf.local",
            Order = order,
            OrderId = order.Id,
            Status = status,
            Source = NetworkAccountSource.SystemAutomated,
            ProvisioningStatus = ProvisioningStatus.Provisioned,
            ProviderName = providerName,
            PackageType = packageType,
            PackageName = order.PackageName,
            PackagePrice = packagePrice,
        };
        db.NetworkAccounts.Add(acct);
        return acct;
    }

    public static ServiceBillingSchedule CreateBillingSchedule(
        IAppDbContext db,
        NetworkAccount networkAccount,
        Order order,
        User user,
        decimal amount,
        int anchorDayOfMonth,
        DateTime? nextInvoiceDateUtc = null,
        DateTime? nextDueDateUtc = null,
        ServiceBillingScheduleStatus status = ServiceBillingScheduleStatus.Active,
        bool isAutoBillable = true)
    {
        var schedule = new ServiceBillingSchedule
        {
            NetworkAccount = networkAccount,
            NetworkAccountId = networkAccount.Id,
            OrderId = order.Id,
            UserId = user.Id,
            BillingCycle = ServicePackageBillingCycle.Monthly,
            Amount = amount,
            AnchorDayOfMonth = anchorDayOfMonth,
            NextInvoiceDateUtc = nextInvoiceDateUtc,
            NextDueDateUtc = nextDueDateUtc,
            Status = status,
            IsAutoBillable = isAutoBillable,
        };
        db.ServiceBillingSchedules.Add(schedule);
        return schedule;
    }

    public static SmartFuture.Domain.Customers.CustomerProfile CreateCustomerProfile(
        IAppDbContext db,
        User user,
        bool autoBillingEnabled = true)
    {
        var profile = new SmartFuture.Domain.Customers.CustomerProfile
        {
            UserId = user.Id,
            AutoBillingEnabled = autoBillingEnabled,
        };
        db.CustomerProfiles.Add(profile);
        return profile;
    }

    public static CustomerPaymentMandate CreateMandate(
        IAppDbContext db,
        User user,
        PaymentProviderType provider = PaymentProviderType.Paystack,
        bool isActive = true,
        bool isReusable = true,
        bool isDefault = true,
        DateTime? updatedAtUtc = null)
    {
        var m = new CustomerPaymentMandate
        {
            UserId = user.Id,
            Provider = provider,
            AuthorizationCodeProtected = $"prot-{Next():D6}",
            IsActive = isActive,
            IsReusable = isReusable,
            IsDefault = isDefault,
            UpdatedAtUtc = updatedAtUtc,
        };
        db.CustomerPaymentMandates.Add(m);
        return m;
    }

    public static PaymentInitiation CreatePaymentInitiation(
        IAppDbContext db,
        Invoice invoice,
        DateTime nowUtc,
        PaymentInitiationStatus status = PaymentInitiationStatus.Pending,
        string? providerReference = null)
    {
        var init = new PaymentInitiation
        {
            InvoiceId = invoice.Id,
            Invoice = invoice,
            Amount = invoice.TotalAmount,
            CurrencyCode = invoice.CurrencyCode,
            Provider = PaymentProviderType.Paystack,
            ProviderReference = providerReference ?? $"REF-{Next():D6}",
            Status = status,
            CreatedAtUtc = nowUtc,
        };
        db.PaymentInitiations.Add(init);
        return init;
    }

    public static PaymentRetryAttempt CreateRetryAttempt(
        IAppDbContext db,
        Invoice invoice,
        User customer,
        int attemptNumber,
        PaymentRetryAttemptStatus status = PaymentRetryAttemptStatus.Failed,
        DateTime? attemptedAtUtc = null,
        decimal amount = 699m)
    {
        var attempt = new PaymentRetryAttempt
        {
            Invoice = invoice,
            InvoiceId = invoice.Id,
            Customer = customer,
            CustomerId = customer.Id,
            AttemptNumber = attemptNumber,
            Amount = amount,
            Status = status,
            Source = AutoBillingChargeSource.Retry,
            ScheduledForUtc = attemptedAtUtc ?? new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            AttemptedUtc = attemptedAtUtc,
        };
        db.PaymentRetryAttempts.Add(attempt);
        return attempt;
    }
}
