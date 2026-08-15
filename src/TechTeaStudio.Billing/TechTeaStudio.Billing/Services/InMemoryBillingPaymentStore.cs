using TechTeaStudio.Billing.Abstractions;

namespace TechTeaStudio.Billing.Services;

/// <summary>
/// DEVELOPMENT DEFAULT - in-memory, single-instance implementation of
/// <see cref="IBillingPaymentStore"/>, keyed by <c>"{provider}:{providerPaymentId}"</c>.
/// The reservation is genuinely atomic (single lock), so the dev experience matches the
/// exactly-once contract a production store is expected to provide via a unique index.
///
/// WARNING: idempotency does not survive a process restart or scale-out.
/// Call <c>UsePaymentStore&lt;T&gt;()</c> on the builder with a persistent
/// store backed by your database before going to production.
/// </summary>
internal sealed class InMemoryBillingPaymentStore : IBillingPaymentStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, BillingPaymentRecord> _data = new();

    public Task<BillingPaymentStatus?> GetStatusAsync(
        string provider, string providerPaymentId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(
                _data.TryGetValue(Key(provider, providerPaymentId), out var record)
                    ? (BillingPaymentStatus?)record.Status
                    : null);
        }
    }

    public Task UpsertAsync(BillingPaymentRecord record, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _data[Key(record.Provider, record.ProviderPaymentId)] = record;
            return Task.CompletedTask;
        }
    }

    /// <summary>The whole stored record, for diagnostics. Not part of
    /// <see cref="IBillingPaymentStore"/> - hosts read their own table.</summary>
    public Task<BillingPaymentRecord?> FindAsync(
        string provider, string providerPaymentId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(
                _data.TryGetValue(Key(provider, providerPaymentId), out var record)
                    ? record
                    : null);
        }
    }

    /// <summary>Atomic claim: the caller that writes the Pending row wins. A payment already
    /// Pending is being fulfilled by someone else; Succeeded and Refunded are terminal. A
    /// previously canceled or failed payment may be retried.</summary>
    public Task<bool> TryReserveAsync(BillingPaymentRecord pending, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var key = Key(pending.Provider, pending.ProviderPaymentId);
            if (_data.TryGetValue(key, out var existing)
                && existing.Status is BillingPaymentStatus.Succeeded
                    or BillingPaymentStatus.Refunded
                    or BillingPaymentStatus.Pending)
                return Task.FromResult(false);

            _data[key] = pending with { Status = BillingPaymentStatus.Pending };
            return Task.FromResult(true);
        }
    }

    /// <summary>Flips the status only, so a refund whose payload carries no plan or amount
    /// (a fully refunded membership) cannot erase the original payment's details.</summary>
    public Task MarkRefundedAsync(
        BillingPaymentRecord refundContext, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var key = Key(refundContext.Provider, refundContext.ProviderPaymentId);
            _data[key] = _data.TryGetValue(key, out var existing)
                ? existing with { Status = BillingPaymentStatus.Refunded }
                : refundContext with { Status = BillingPaymentStatus.Refunded };
            return Task.CompletedTask;
        }
    }

    private static string Key(string provider, string providerPaymentId) =>
        $"{provider}:{providerPaymentId}";
}
