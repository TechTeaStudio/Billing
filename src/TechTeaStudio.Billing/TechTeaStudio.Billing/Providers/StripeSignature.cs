using System.Security.Cryptography;
using System.Text;

namespace TechTeaStudio.Billing.Providers;

/// <summary>
/// Verification of Stripe's <c>Stripe-Signature</c> webhook header. Pure and static so
/// it is unit-testable without touching HTTP. Header form:
/// <c>t=&lt;unix&gt;,v1=&lt;hex-hmac&gt;[,v0=...]</c>.
/// A payload is authentic iff some <c>v1</c> equals HMAC-SHA256(<c>"{t}.{body}"</c>,
/// webhookSecret) AND the timestamp is within tolerance (replay guard).
/// </summary>
public static class StripeSignature
{
    public static bool Verify(
        string rawBody, string? signatureHeader, string? secret,
        DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrEmpty(signatureHeader) || string.IsNullOrEmpty(secret)) return false;

        long? t = null;
        var v1s = new List<string>();
        foreach (var part in signatureHeader.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            var key = kv[0].Trim();
            if (key == "t" && long.TryParse(kv[1].Trim(), out var ts)) t = ts;
            else if (key == "v1") v1s.Add(kv[1].Trim());
        }
        if (t is null || v1s.Count == 0) return false;

        var age = now - DateTimeOffset.FromUnixTimeSeconds(t.Value);
        if (Math.Abs(age.TotalSeconds) > tolerance.TotalSeconds) return false;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = Convert.ToHexString(
            hmac.ComputeHash(Encoding.UTF8.GetBytes($"{t.Value}.{rawBody}"))).ToLowerInvariant();

        foreach (var v1 in v1s)
        {
            var candidate = v1.ToLowerInvariant();
            if (candidate.Length == expected.Length &&
                CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(candidate), Encoding.ASCII.GetBytes(expected)))
                return true;
        }
        return false;
    }
}
