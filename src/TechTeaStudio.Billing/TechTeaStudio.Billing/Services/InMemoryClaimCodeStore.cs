using TechTeaStudio.Billing.Abstractions;

namespace TechTeaStudio.Billing.Services;

/// <summary>
/// DEVELOPMENT DEFAULT - in-memory implementation of <see cref="IClaimCodeStore"/>.
///
/// WARNING: codes do not survive a process restart or scale-out, which orphans any code
/// a user already pasted into a platform message. Call <c>UseClaimCodeStore&lt;T&gt;()</c>
/// with a database-backed store before going to production.
/// </summary>
internal sealed class InMemoryClaimCodeStore : IClaimCodeStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, string> _byUser = new();
    private readonly Dictionary<string, Guid> _byCode = new(StringComparer.OrdinalIgnoreCase);

    public Task<string> GetOrAddAsync(Guid userId, string newCode, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_byUser.TryGetValue(userId, out var existing))
                return Task.FromResult(existing);
            _byUser[userId] = newCode;
            _byCode[newCode] = userId;
            return Task.FromResult(newCode);
        }
    }

    public Task<Guid?> FindUserAsync(string code, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(
                _byCode.TryGetValue(code, out var userId) ? (Guid?)userId : null);
        }
    }
}
