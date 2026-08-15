using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace TechTeaStudio.Billing.Abstractions;

/// <summary>
/// Claim-code generation and extraction. A claim code is a short human-typable token
/// ("CHR-K7M2P9XA") the app shows to a user; the user pastes it into the message field
/// of an external platform payment (Ko-fi donation message, Boosty DM) and the webhook
/// handler extracts it to attribute the payment. Pure and static so it is unit-testable.
///
/// The alphabet deliberately omits characters that are easy to misread or mistype
/// (0/O, 1/I/L, U): "ABCDEFGHJKMNPQRSTVWXYZ23456789". Extraction is case-insensitive
/// and codes are normalized to uppercase.
/// </summary>
public static class ClaimCode
{
    private const string Alphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789";

    /// <summary>Length of the random part after the prefix and dash.</summary>
    public const int CodeLength = 8;

    /// <summary>Generates a new code "{prefix}-XXXXXXXX" using a cryptographic RNG.
    /// 30^8 combinations make blind guessing impractical, but stores should still
    /// rate-limit lookups.</summary>
    public static string Generate(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            throw new ArgumentException("Claim-code prefix must be non-empty.", nameof(prefix));

        Span<char> chars = stackalloc char[CodeLength];
        for (var i = 0; i < CodeLength; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return $"{prefix.ToUpperInvariant()}-{new string(chars)}";
    }

    /// <summary>
    /// Finds a claim code with the given prefix inside free text (a donation message).
    /// Case-insensitive; the returned code is normalized to uppercase. Returns false
    /// when the text contains no code.
    /// </summary>
    public static bool TryExtract(string? text, string prefix, out string code)
    {
        code = "";
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(prefix)) return false;

        // CultureInvariant is load-bearing next to IgnoreCase: under a Turkish culture the
        // dotted/dotless I would stop a prefix like "CHR" from matching the user's "chr".
        var match = Regex.Match(
            text,
            $@"(?<![A-Za-z0-9]){Regex.Escape(prefix)}-([A-Za-z0-9]{{{CodeLength}}})(?![A-Za-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        if (!match.Success) return false;

        code = $"{prefix.ToUpperInvariant()}-{match.Groups[1].Value.ToUpperInvariant()}";
        return true;
    }
}
