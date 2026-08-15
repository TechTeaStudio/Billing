using TechTeaStudio.Billing.Abstractions;
using Xunit;

namespace TechTeaStudio.Billing.Tests;

public class ClaimCodeTests
{
    [Fact]
    public void Generate_produces_prefix_dash_and_eight_safe_chars()
    {
        var code = ClaimCode.Generate("chr");

        Assert.StartsWith("CHR-", code);
        var random = code["CHR-".Length..];
        Assert.Equal(ClaimCode.CodeLength, random.Length);
        // The alphabet omits ambiguous characters entirely.
        Assert.All(random, c => Assert.Contains(c, "ABCDEFGHJKMNPQRSTVWXYZ23456789"));
    }

    [Fact]
    public void Generate_refuses_empty_prefix()
    {
        Assert.Throws<ArgumentException>(() => ClaimCode.Generate(" "));
    }

    [Fact]
    public void TryExtract_finds_code_inside_free_text()
    {
        var code = ClaimCode.Generate("CHR");

        var found = ClaimCode.TryExtract(
            $"Спасибо за приложение! {code} - вот мой код.", "CHR", out var extracted);

        Assert.True(found);
        Assert.Equal(code, extracted);
    }

    [Fact]
    public void TryExtract_is_case_insensitive_and_normalizes_to_uppercase()
    {
        var found = ClaimCode.TryExtract("code: chr-abcdefgh thanks", "CHR", out var extracted);

        Assert.True(found);
        Assert.Equal("CHR-ABCDEFGH", extracted);
    }

    [Fact]
    public void TryExtract_rejects_text_without_a_code()
    {
        Assert.False(ClaimCode.TryExtract("just a nice message", "CHR", out _));
        Assert.False(ClaimCode.TryExtract(null, "CHR", out _));
        Assert.False(ClaimCode.TryExtract("CHR-SHORT", "CHR", out _));
    }

    [Fact]
    public void TryExtract_does_not_match_a_code_embedded_in_a_longer_token()
    {
        // Nine trailing characters - the code boundary must not cut one out of a longer word.
        Assert.False(ClaimCode.TryExtract("XCHR-ABCDEFGH", "CHR", out _));
        Assert.False(ClaimCode.TryExtract("CHR-ABCDEFGHJ", "CHR", out _));
    }
}
