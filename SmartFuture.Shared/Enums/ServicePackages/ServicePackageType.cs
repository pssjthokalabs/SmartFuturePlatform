namespace SmartFuture.Shared.Enums.ServicePackages;

public enum ServicePackageType
{
    Fibre = 0,
    LTE = 1,
    Wireless = 2,
    WiFi = 3,
    Voice = 4,
    PrepaidFibre = 5,
    // New product line. Security camera / CCTV packages bypass the
    // fibre coverage check at order time and do not provision any
    // RADIUS / MikroTik account — fulfilment is a manual on-site install.
    Security = 6,
    Other = 99
}
