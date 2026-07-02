using System.Security.Cryptography;
using System.Text;
using SmartFuture.Application.Payments.PayFast;

namespace SmartFuture.Tests.Billing;

// Phase-4 tests for PayFastSignatureCalculator — the already-pure
// signature helper for PayFast ITN + outgoing calls. These lock the
// PHP-compatible URL-encoding + MD5 hashing rules so a change to any
// of the three algorithms (redirect / ITN / recurring-API) trips a
// red test rather than a live-signature mismatch in production.
//
// We build the expected hashes by rebuilding the PayFast documented
// base string with our own MD5 — that way if the helper's private
// pipeline changes but produces a different result, we catch it. If
// PayFast ever changes their algorithm the tests + the helper both
// need updating in lockstep.
public class PayFastSignatureCalculatorTests
{
    private static string Md5Hex(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = MD5.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    // ─── PhpUrlEncode ──────────────────────────────────────────────

    [Theory]
    [InlineData("hello", "hello")]
    [InlineData("hello world", "hello+world")]
    [InlineData("hello!", "hello%21")]
    [InlineData("*", "%2A")]
    [InlineData("~", "%7E")]
    [InlineData("(", "%28")]
    [InlineData(")", "%29")]
    [InlineData("'", "%27")]
    [InlineData("test@example.com", "test%40example.com")]
    public void PhpUrlEncode_MatchesPhpUrlencode(string input, string expected)
    {
        PayFastSignatureCalculator.PhpUrlEncode(input).Should().Be(expected);
    }

    // ─── FormatAmount ─────────────────────────────────────────────

    [Theory]
    [InlineData("349.77", 349.77)]
    [InlineData("100.00", 100)]
    [InlineData("0.50", 0.5)]
    [InlineData("1000000.00", 1_000_000)]
    public void FormatAmount_TwoDecimalPlacesInvariantCulture(string expected, decimal input)
    {
        PayFastSignatureCalculator.FormatAmount(input).Should().Be(expected);
    }

    // ─── ITN signature (posted-order, include-empties) ────────────

    [Fact]
    public void GenerateItnSignature_WithoutPassphrase_MatchesDocumentedAlgorithm()
    {
        var posted = new List<KeyValuePair<string, string>>
        {
            new("m_payment_id",    "ORD-1"),
            new("pf_payment_id",   "PF-1000"),
            new("payment_status",  "COMPLETE"),
            new("amount_gross",    "349.77"),
            new("merchant_id",     "10000100"),
        };
        var expected = Md5Hex(
            "m_payment_id=ORD-1&pf_payment_id=PF-1000&payment_status=COMPLETE&amount_gross=349.77&merchant_id=10000100");

        var actual = PayFastSignatureCalculator.GenerateItnSignature(posted, passphrase: null, out var diag);

        actual.Should().Be(expected);
        diag.PassphraseConfigured.Should().BeFalse();
        diag.FieldNamesInOrder.Should().Equal("m_payment_id", "pf_payment_id", "payment_status", "amount_gross", "merchant_id");
        diag.BaseStringRedacted.Should().NotContain("passphrase=[REDACTED]", "no passphrase configured");
    }

    [Fact]
    public void GenerateItnSignature_WithPassphrase_AppendsBeforeHashing()
    {
        var posted = new List<KeyValuePair<string, string>>
        {
            new("m_payment_id",    "ORD-1"),
            new("payment_status",  "COMPLETE"),
            new("amount_gross",    "349.77"),
            new("merchant_id",     "10000100"),
        };
        var expected = Md5Hex(
            "m_payment_id=ORD-1&payment_status=COMPLETE&amount_gross=349.77&merchant_id=10000100&passphrase=my-secret-phrase");

        var actual = PayFastSignatureCalculator.GenerateItnSignature(posted, passphrase: "my-secret-phrase", out var diag);

        actual.Should().Be(expected);
        diag.PassphraseConfigured.Should().BeTrue();
        diag.BaseStringRedacted.Should().Contain("passphrase=[REDACTED]");
        diag.BaseStringRedacted.Should().NotContain("my-secret-phrase",
            "the passphrase value must be REDACTED in diagnostics — safe for forensic logs");
    }

    [Fact]
    public void GenerateItnSignature_ExcludesSignatureField_IncludesEmpties()
    {
        // PayFast's algorithm includes `key=` for empty values and
        // strips ONLY the `signature` field.
        var posted = new List<KeyValuePair<string, string>>
        {
            new("m_payment_id",   "ORD-1"),
            new("amount_gross",   "349.77"),
            new("amount_fee",     "-2.70"),  // negative fees are legitimate
            new("custom_str1",    ""),       // empty must be INCLUDED as key=
            new("merchant_id",    "10000100"),
            new("signature",      "SHOULD-BE-STRIPPED"),
        };
        var expected = Md5Hex(
            "m_payment_id=ORD-1&amount_gross=349.77&amount_fee=-2.70&custom_str1=&merchant_id=10000100");

        PayFastSignatureCalculator.GenerateItnSignature(posted, null, out _).Should().Be(expected);
    }

    [Fact]
    public void GenerateItnSignature_WrongPassphrase_ProducesDifferentSignature()
    {
        var posted = new List<KeyValuePair<string, string>>
        {
            new("m_payment_id", "ORD-1"),
            new("merchant_id",  "10000100"),
        };
        var right = PayFastSignatureCalculator.GenerateItnSignature(posted, "correct-phrase", out _);
        var wrong = PayFastSignatureCalculator.GenerateItnSignature(posted, "attacker-phrase", out _);
        right.Should().NotBe(wrong, "signature MUST be different under a different passphrase — otherwise it's forgeable");
    }

    // ─── SignaturesMatch (constant-time helper) ──────────────────

    [Fact]
    public void SignaturesMatch_HappyPath()
    {
        PayFastSignatureCalculator.SignaturesMatch("abcdef1234567890", "abcdef1234567890").Should().BeTrue();
    }

    [Fact]
    public void SignaturesMatch_CaseInsensitiveViaLower()
    {
        PayFastSignatureCalculator.SignaturesMatch("ABCDEF1234567890", "abcdef1234567890").Should().BeTrue();
    }

    [Fact]
    public void SignaturesMatch_DifferentLength_Fails()
    {
        PayFastSignatureCalculator.SignaturesMatch("abcdef", "abcdef12").Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void SignaturesMatch_NullOrEmpty_Fails(string? actual)
    {
        PayFastSignatureCalculator.SignaturesMatch("abcdef1234567890", actual).Should().BeFalse();
    }
}
