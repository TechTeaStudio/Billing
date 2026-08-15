namespace TechTeaStudio.Billing.Abstractions;

/// <summary>
/// Persists the app-user-to-claim-code mapping. Codes are stable: a user keeps the same
/// code across sessions so it can be printed in UI and support articles. The host
/// implements this against its own database; the in-memory default does not survive
/// a restart. Codes are stored and looked up uppercase (callers normalize).
/// </summary>
public interface IClaimCodeStore
{
    /// <summary>Returns the user's existing code, or persists <paramref name="newCode"/>
    /// as theirs and returns it. Implementations must keep code-to-user unique.</summary>
    Task<string> GetOrAddAsync(Guid userId, string newCode, CancellationToken ct = default);

    /// <summary>Resolves a code (uppercase) to the owning user, or null. Implementations
    /// should rate-limit callers upstream - codes are guessable in principle.</summary>
    Task<Guid?> FindUserAsync(string code, CancellationToken ct = default);
}
