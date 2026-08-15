using System.Collections.Concurrent;
using TechTeaStudio.Billing.Abstractions;

namespace TechTeaStudio.Billing.Services;

/// <summary>
/// DEVELOPMENT DEFAULT - in-memory implementation of <see cref="IExternalIdentityLinkStore"/>.
///
/// WARNING: links do not survive a process restart, which detaches every subscription
/// renewal from its account. Call <c>UseExternalIdentityLinkStore&lt;T&gt;()</c> with a
/// database-backed store before going to production.
/// </summary>
internal sealed class InMemoryExternalIdentityLinkStore : IExternalIdentityLinkStore
{
    private readonly ConcurrentDictionary<(string Provider, string Email), Guid> _links = new();

    public Task<Guid?> FindUserAsync(string provider, string email, CancellationToken ct = default) =>
        Task.FromResult(
            _links.TryGetValue(Key(provider, email), out var userId) ? (Guid?)userId : null);

    public Task LinkAsync(string provider, string email, Guid userId, CancellationToken ct = default)
    {
        _links[Key(provider, email)] = userId;
        return Task.CompletedTask;
    }

    private static (string, string) Key(string provider, string email) =>
        (provider.Trim().ToLowerInvariant(), email.Trim().ToLowerInvariant());
}
