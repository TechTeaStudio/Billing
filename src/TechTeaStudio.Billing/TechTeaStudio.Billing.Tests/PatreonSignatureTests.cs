using System.Security.Cryptography;
using System.Text;
using TechTeaStudio.Billing.Providers;
using Xunit;

namespace TechTeaStudio.Billing.Tests;

public class PatreonSignatureTests
{
    private const string Secret = "hereisaverycomplexsecret";
    private const string Body = /*lang=json*/ """{"data":{"id":"mem-1","type":"member"}}""";

    [Fact]
    public void Compute_is_hmac_md5_lowercase_hex_of_the_raw_body()
    {
        using var hmac = new HMACMD5(Encoding.UTF8.GetBytes(Secret));
        var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(Body)))
            .ToLowerInvariant();

        Assert.Equal(expected, PatreonSignature.Compute(Body, Secret));
    }

    [Fact]
    public void Valid_signature_is_accepted()
    {
        var header = PatreonSignature.Compute(Body, Secret);

        Assert.True(PatreonSignature.Verify(Body, header, Secret));
    }

    [Fact]
    public void Uppercase_header_is_accepted()
    {
        var header = PatreonSignature.Compute(Body, Secret).ToUpperInvariant();

        Assert.True(PatreonSignature.Verify(Body, header, Secret));
    }

    [Fact]
    public void Tampered_body_is_rejected()
    {
        var header = PatreonSignature.Compute(Body, Secret);

        Assert.False(PatreonSignature.Verify(Body + " ", header, Secret));
    }

    [Fact]
    public void Wrong_secret_is_rejected()
    {
        var header = PatreonSignature.Compute(Body, Secret);

        Assert.False(PatreonSignature.Verify(Body, header, "other-secret"));
    }

    [Fact]
    public void Missing_header_or_secret_is_rejected()
    {
        Assert.False(PatreonSignature.Verify(Body, null, Secret));
        Assert.False(PatreonSignature.Verify(Body, "", Secret));
        Assert.False(PatreonSignature.Verify(Body, PatreonSignature.Compute(Body, Secret), null));
    }

    [Fact]
    public void No_break_space_normalization_breaks_the_signature()
    {
        // Patreon bodies can carry NO-BREAK SPACE (U+00A0); JSON round-trips normalize it
        // away, which is why the digest must be computed over the body exactly as received.
        var body = "{\"summary\":\"link here\"}";
        var header = PatreonSignature.Compute(body, Secret);

        Assert.True(PatreonSignature.Verify(body, header, Secret));
        Assert.False(PatreonSignature.Verify(body.Replace(" ", "\u00a0"), header, Secret));
    }
}
