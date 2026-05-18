namespace SmartFuture.Application.Billing;

// Payment-related runtime configuration.
//
//   PaymentSettings__MockCheckoutEnabled=true|false
//
// When `true`, the customer-facing order-create endpoint will, in
// addition to creating the Order, also persist:
//   - one Invoice (Status=Paid, Subtotal/Total = monthly + installation)
//   - one Payment  (Status=Completed, GatewayName="Ozow", reference from the request)
// …when the request includes `MockCheckoutPaymentProvider="Ozow"` and a
// `MockCheckoutPaymentReference`. The order itself is still left in
// `Submitted` status — admins keep ownership of service activation.
//
// This flow exists so UAT can exercise the billing surface end-to-end
// before the real Ozow integration (signed redirect + webhook) lands.
// **It MUST stay false in Production.** When false, the mock fields on
// the order request are ignored and no Invoice/Payment is created.
public class PaymentSettings
{
    public const string SectionName = "PaymentSettings";

    public bool MockCheckoutEnabled { get; set; }
}
