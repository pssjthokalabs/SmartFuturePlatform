namespace SmartFuture.Shared.Enums.Communication;

// Delivery channels for one-time-password / verification-code flows.
// Used by future <c>IOtpService</c> + <c>IPhoneVerificationService</c>
// implementations (Phase 36 — Twilio integration).
public enum OtpChannel
{
    Email = 0,
    Sms = 1,
    WhatsApp = 2
}

// Subset of <see cref="OtpChannel"/> that requires a mobile-network
// provider. Email-only flows reuse the existing INotificationService;
// SMS/WhatsApp need a Twilio (or other) provider.
public enum MobileOtpChannel
{
    Sms = 1,
    WhatsApp = 2
}
