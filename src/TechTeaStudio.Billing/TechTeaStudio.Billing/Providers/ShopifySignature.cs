using System.Security.Cryptography;
using System.Text;

namespace TechTeaStudio.Billing.Providers;

/// <summary>
/// Verification of Shopify's <c>X-Shopify-Hmac-Sha256</c> webhook header. A payload is
/// authentic iff the header equals Base64(HMAC-SHA256(rawBody, apiSecret)). Pure and static
/// so it is unit-testable without touching HTTP; the comparison is constant-time
/// (timing-attack guard).
/// </summary>
public static class ShopifySignature
{
    public static bool Verify(string rawBody, string? hmacHeader, string? secret)
    {
        if (string.IsNullOrEmpty(hmacHeader) || string.IsNullOrEmpty(secret)) return false;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var computed = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody)));

        var a = Encoding.ASCII.GetBytes(hmacHeader.Trim());
        var b = Encoding.ASCII.GetBytes(computed);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
