using System.Security.Cryptography;
using System.Text;

namespace TechTeaStudio.Billing.Providers;

/// <summary>
/// Verification of Patreon's <c>X-Patreon-Signature</c> webhook header. Per the official
/// docs the header is "the HEX digest of the message body HMAC signed (with MD5) using
/// your webhook's secret" - HMAC-MD5 (not SHA), lowercase hex (not Base64), keyed with
/// the PER-WEBHOOK secret (not the OAuth client secret; two webhooks on one campaign have
/// different secrets). The digest must be computed over the body exactly as received -
/// never over a re-serialized JSON parse (Patreon bodies can contain characters like
/// NO-BREAK SPACE that JSON round-trips normalize away, breaking the HMAC).
///
/// Pure and static so it is unit-testable without touching HTTP; the comparison is
/// constant-time (timing-attack guard). Note: <see cref="HMACMD5"/> throws under a
/// FIPS-only crypto policy - Patreon leaves no alternative algorithm.
/// </summary>
public static class PatreonSignature
{
    public static bool Verify(string rawBody, string? signatureHeader, string? secret)
    {
        if (string.IsNullOrEmpty(signatureHeader) || string.IsNullOrEmpty(secret)) return false;

        using var hmac = new HMACMD5(Encoding.UTF8.GetBytes(secret));
        var computed = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody)))
            .ToLowerInvariant();

        var a = Encoding.ASCII.GetBytes(signatureHeader.Trim().ToLowerInvariant());
        var b = Encoding.ASCII.GetBytes(computed);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>The lowercase hex HMAC-MD5 digest of <paramref name="rawBody"/> - what
    /// Patreon puts in the header. Exposed for tests and diagnostics.</summary>
    public static string Compute(string rawBody, string secret)
    {
        using var hmac = new HMACMD5(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody)))
            .ToLowerInvariant();
    }
}
