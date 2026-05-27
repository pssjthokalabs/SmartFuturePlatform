namespace SmartFuture.Application.Payments.PayFast;

public class PayFastSettings
{
    public string MerchantId  { get; set; } = string.Empty;
    public string MerchantKey { get; set; } = string.Empty;
    public string Passphrase  { get; set; } = string.Empty;

    public bool UseSandbox { get; set; } = true;

    public string NotifyUrl  { get; set; } = string.Empty;
    public string ReturnUrl  { get; set; } = string.Empty;
    public string CancelUrl  { get; set; } = string.Empty;

    public bool UseTestAmountOverride { get; set; }
    public decimal? TestAmount { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(MerchantId)
        && !string.IsNullOrWhiteSpace(MerchantKey)
        && !string.IsNullOrWhiteSpace(Passphrase)
        && !string.IsNullOrWhiteSpace(NotifyUrl);

    public string ProcessUrl => UseSandbox
        ? "https://sandbox.payfast.co.za/eng/process"
        : "https://www.payfast.co.za/eng/process";

    public string ValidateUrl => UseSandbox
        ? "https://sandbox.payfast.co.za/eng/query/validate"
        : "https://www.payfast.co.za/eng/query/validate";
}
