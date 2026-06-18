namespace SmartFuture.Shared.Enums.AppVersion;

/// <summary>
/// Distribution channel for an app-version rule. One APK/AAB can't tell
/// Google from Huawei at runtime, so the app declares its channel
/// (EXPO_PUBLIC_APP_CHANNEL); iOS forces <see cref="Apple"/>. <see cref="Generic"/>
/// is a per-platform fallback used when no channel-specific rule exists.
/// </summary>
public enum MobileAppChannel
{
    Google = 0,
    Huawei = 1,
    Apple = 2,
    Generic = 3
}
