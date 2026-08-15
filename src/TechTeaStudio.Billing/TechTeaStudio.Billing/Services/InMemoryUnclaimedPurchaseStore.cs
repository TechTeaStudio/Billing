using System.Collections.Concurrent;
using TechTeaStudio.Billing.Abstractions;

namespace TechTeaStudio.Billing.Services;

/// <summary>
/// DEVELOPMENT DEFAULT - in-memory implementation of <see cref="IUnclaimedPurchaseStore"/>.
///
/// WARNING: held purchases are MONEY ALREADY RECEIVED, and this store loses them on
/// restart. Call <c>UseUnclaimedPurchaseStore&lt;T&gt;()</c> with a database-backed store
/// before going to production.
/// </summary>
internal sealed class InMemoryUnclaimedPurchaseStore : IUnclaimedPurchaseStore
{
    private readonly ConcurrentDictionary<(string Provider, string PaymentId), UnclaimedPurchase> _held = new();

    public Task AddAsync(UnclaimedPurchase purchase, CancellationToken ct = default)
    {
        _held[Key(purchase.Purchase.Provider, purchase.Purchase.ProviderPaymentId)] = purchase;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UnclaimedPurchase>> ListAsync(
        string? provider = null, string? buyerEmail = null, CancellationToken ct = default)
    {
        var providerKey = provider?.Trim().ToLowerInvariant();
        var emailKey = string.IsNullOrWhiteSpace(buyerEmail)
            ? null : buyerEmail.Trim().ToLowerInvariant();

        IReadOnlyList<UnclaimedPurchase> result = _held.Values
            .Where(h => providerKey is null
                || h.Purchase.Provider.Trim().ToLowerInvariant() == providerKey)
            .Where(h => emailKey is null
                || h.Purchase.BuyerEmail?.Trim().ToLowerInvariant() == emailKey)
            .OrderBy(h => h.HeldAt)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<UnclaimedPurchase?> TakeAsync(
        string provider, string providerPaymentId, CancellationToken ct = default) =>
        Task.FromResult(
            _held.TryRemove(Key(provider, providerPaymentId), out var taken) ? taken : null);

    private static (string, string) Key(string provider, string paymentId) =>
        (provider.Trim().ToLowerInvariant(), paymentId);
}
