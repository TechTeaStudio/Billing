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

#if NET5_0_OR_GREATER
        Span<char> chars = stackalloc char[CodeLength];
#else
        // No Span on netstandard2.0 without dragging in System.Memory, and a dependency-free
        // Abstractions package is the whole point. Eight chars on the heap, once per user.
        var chars = new char[CodeLength];
#endif
        for (var i = 0; i < CodeLength; i++)
            chars[i] = Alphabet[NextIndex(Alphabet.Length)];
        return $"{prefix.ToUpperInvariant()}-{new string(chars)}";
    }

#if NET5_0_OR_GREATER
    private static int NextIndex(int exclusiveMax) => RandomNumberGenerator.GetInt32(exclusiveMax);
#else
    // RandomNumberGenerator.GetInt32 arrived in .NET Core 3.0. GetBytes is thread-safe on the
    // .NET Framework implementation, so one shared instance is fine.
    private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

    /// <summary>Uniform random index in [0, <paramref name="exclusiveMax"/>), same contract as
    /// <c>RandomNumberGenerator.GetInt32</c>.</summary>
    private static int NextIndex(int exclusiveMax)
    {
        // Rejection sampling. Reducing a raw 32-bit draw with % would bias the first
        // (2^32 % 30) values of the alphabet upward - small, but this is a security token,
        // so discard the short trailing block instead of skewing it.
        const ulong Total = 1UL << 32;
        var usable = Total - (Total % (ulong)exclusiveMax);
        var buffer = new byte[4];
        ulong value;
        do
        {
            Rng.GetBytes(buffer);
            value = BitConverter.ToUInt32(buffer, 0);
        }
        while (value >= usable);
        return (int)(value % (ulong)exclusiveMax);
    }
#endif

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
