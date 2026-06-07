using System.Security.Cryptography;
using System.Text;
using TechTeaStudio.Billing.Providers;
using Xunit;

namespace TechTeaStudio.Billing.Tests;

public class StripeSignatureTests
{
    private const string Secret = "whsec_test_secret";
    private const string Body = "{\"id\":\"evt_test\",\"type\":\"checkout.session.completed\"}";

    private static string BuildHeader(string body, string secret, long timestamp)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var sig = Convert.ToHexString(
            hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{body}"))).ToLowerInvariant();
        return $"t={timestamp},v1={sig}";
    }

    [Fact]
    public void Valid_signature_returns_true()
    {
        var now = DateTimeOffset.UtcNow;
        var ts = now.ToUnixTimeSeconds();
        var header = BuildHeader(Body, Secret, ts);

        Assert.True(StripeSignature.Verify(Body, header, Secret, now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Invalid_secret_returns_false()
    {
        var now = DateTimeOffset.UtcNow;
        var ts = now.ToUnixTimeSeconds();
        var header = BuildHeader(Body, "wrong_secret", ts);

        Assert.False(StripeSignature.Verify(Body, header, Secret, now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Expired_timestamp_returns_false()
    {
        var past = DateTimeOffset.UtcNow.AddMinutes(-10);
        var ts = past.ToUnixTimeSeconds();
        var header = BuildHeader(Body, Secret, ts);

        Assert.False(StripeSignature.Verify(Body, header, Secret, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Tampered_body_returns_false()
    {
        var now = DateTimeOffset.UtcNow;
        var ts = now.ToUnixTimeSeconds();
        var header = BuildHeader(Body, Secret, ts);
        var tamperedBody = Body + " ";

        Assert.False(StripeSignature.Verify(tamperedBody, header, Secret, now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Null_header_returns_false()
    {
        Assert.False(StripeSignature.Verify(Body, null, Secret, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Null_secret_returns_false()
    {
        var now = DateTimeOffset.UtcNow;
        var ts = now.ToUnixTimeSeconds();
        var header = BuildHeader(Body, Secret, ts);

        Assert.False(StripeSignature.Verify(Body, header, null, now, TimeSpan.FromMinutes(5)));
    }
}
