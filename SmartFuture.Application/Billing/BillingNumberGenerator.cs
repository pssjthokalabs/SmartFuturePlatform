using System.Security.Cryptography;

namespace SmartFuture.Application.Billing;

internal static class BillingNumberGenerator
{
    // Excludes 0/O/1/I/L to avoid transcription ambiguity in support calls.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static string GenerateSuffix(int length = 6)
    {
        var buffer = new byte[length];
        RandomNumberGenerator.Fill(buffer);

        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = Alphabet[buffer[i] % Alphabet.Length];

        return new string(chars);
    }

    public static string BuildCandidate(string prefix, DateTime now, int suffixLength = 6)
        => $"{prefix}-{now:yyyyMMdd}-{GenerateSuffix(suffixLength)}";
}
