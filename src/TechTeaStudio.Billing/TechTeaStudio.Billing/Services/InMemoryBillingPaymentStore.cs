using System.Collections.Concurrent;
using TechTeaStudio.Billing.Abstractions;

namespace TechTeaStudio.Billing.Services;

/// <summary>
/// DEVELOPMENT DEFAULT - in-memory, single-instance implementation of
/// <see cref="IBillingPaymentStore"/>. Stores payment statuses in a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by
/// <c>"{provider}:{providerPaymentId}"</c>.
///
/// WARNING: idempotency does not survive a process restart or scale-out.
/// Call <c>UsePaymentStore&lt;T&gt;()</c> on the builder with a persistent
/// store backed by your database before going to production.
/// </summary>
internal sealed class InMemoryBillingPaymentStore : IBillingPaymentStore
{
    private readonly ConcurrentDictionary<string, BillingPaymentStatus> _data = new();

    public Task<BillingPaymentStatus?> GetStatusAsync(
        string provider, string providerPaymentId, CancellationToken ct = default)
    {
        var key = $"{provider}:{providerPaymentId}";
        return _data.TryGetValue(key, out var status)
            ? Task.FromResult<BillingPaymentStatus?>(status)
            : Task.FromResult<BillingPaymentStatus?>(null);
    }

    public Task UpsertAsync(BillingPaymentRecord record, CancellationToken ct = default)
    {
        var key = $"{record.Provider}:{record.ProviderPaymentId}";
        _data[key] = record.Status;
        return Task.CompletedTask;
    }
}
