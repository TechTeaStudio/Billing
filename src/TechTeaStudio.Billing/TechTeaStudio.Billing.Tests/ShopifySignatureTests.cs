using System.Security.Cryptography;
using System.Text;
using TechTeaStudio.Billing.Providers;
using Xunit;

namespace TechTeaStudio.Billing.Tests;

public class ShopifySignatureTests
{
    private const string Secret = "shopify_api_secret";
    private const string Body = "{\"id\":12345,\"financial_status\":\"paid\"}";

    private static string BuildHeader(string body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public void Valid_hmac_returns_true()
    {
        var header = BuildHeader(Body, Secret);
        Assert.True(ShopifySignature.Verify(Body, header, Secret));
    }

    [Fact]
    public void Wrong_secret_returns_false()
    {
        var header = BuildHeader(Body, "wrong");
        Assert.False(ShopifySignature.Verify(Body, header, Secret));
    }

    [Fact]
    public void Tampered_body_returns_false()
    {
        var header = BuildHeader(Body, Secret);
        Assert.False(ShopifySignature.Verify(Body + "x", header, Secret));
    }

    [Fact]
    public void Null_header_returns_false()
    {
        Assert.False(ShopifySignature.Verify(Body, null, Secret));
    }

    [Fact]
    public void Null_secret_returns_false()
    {
        var header = BuildHeader(Body, Secret);
        Assert.False(ShopifySignature.Verify(Body, header, null));
    }
}
