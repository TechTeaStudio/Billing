namespace TechTeaStudio.Billing.Abstractions;

/// <summary>
/// Persists payment records for audit and idempotency. The host implements this against
/// its own database. The orchestrator reserves a payment via <see cref="TryReserveAsync"/>
/// before calling fulfillment and records the outcome with <see cref="UpsertAsync"/>.
///
/// Exactly-once fulfillment is only as strong as this store. The default
/// <see cref="TryReserveAsync"/> is a read-then-check, which two concurrent deliveries of
/// the same payment can both pass - production stores SHOULD override it with an atomic
/// insert-first claim (<c>INSERT ... ON CONFLICT DO NOTHING</c>, or a unique index on
/// (Provider, ProviderPaymentId) plus catching the duplicate-key exception). Until then,
/// fulfillment must be idempotent on <see cref="BillingPaymentRecord.ProviderPaymentId"/>.
///
/// The same applies after fulfillment: if <see cref="UpsertAsync"/> fails once fulfillment
/// has run, the provider will retry and fulfillment will be called again.
/// </summary>
public interface IBillingPaymentStore
{
    /// <summary>Returns the stored status for the payment, or null if not yet recorded.</summary>
    Task<BillingPaymentStatus?> GetStatusAsync(
        string provider, string providerPaymentId, CancellationToken ct = default);

    /// <summary>Insert or update the payment record.</summary>
    Task UpsertAsync(BillingPaymentRecord record, CancellationToken ct = default);

    /// <summary>
    /// Claims the right to fulfill <paramref name="pending"/>. Returns true when this caller
    /// won the claim and must proceed to fulfillment, false when the payment is already
    /// terminal (succeeded or refunded) or another caller is fulfilling it right now.
    ///
    /// The default implementation is a NON-ATOMIC status read, kept so existing stores keep
    /// compiling: it prevents re-fulfilling a finished payment but not two simultaneous
    /// first deliveries. Override it with an atomic insert of a Pending row keyed uniquely on
    /// (Provider, ProviderPaymentId) - the caller that inserts the row wins, everyone else
    /// gets false - to make exactly-once real.
    /// </summary>
#if NET5_0_OR_GREATER
    async Task<bool> TryReserveAsync(BillingPaymentRecord pending, CancellationToken ct = default)
    {
        var existing = await GetStatusAsync(pending.Provider, pending.ProviderPaymentId, ct);
        return existing is not (BillingPaymentStatus.Succeeded or BillingPaymentStatus.Refunded);
    }
#else
    // .NET Framework and netstandard2.0 have no runtime support for default interface members,
    // so on those frameworks this is a member you must implement. That is the behaviour the
    // docs above ask for anyway; the default only ever existed to keep pre-0.3.0 stores
    // compiling. Reproduce it verbatim if you want the old semantics:
    //     var existing = await GetStatusAsync(pending.Provider, pending.ProviderPaymentId, ct);
    //     return existing is not (BillingPaymentStatus.Succeeded or BillingPaymentStatus.Refunded);
    Task<bool> TryReserveAsync(BillingPaymentRecord pending, CancellationToken ct = default);
#endif

    /// <summary>
    /// Marks a payment refunded. <paramref name="refundContext"/> carries refund-time values,
    /// which are frequently WORSE than the ones already stored: a fully refunded Patreon
    /// membership reports no entitled tier and zero cents, so PlanId is empty and AmountMinor
    /// is 0, and the buyer may be unattributable (UserId <see cref="Guid.Empty"/>).
    ///
    /// The default implementation upserts that record as-is. Production stores SHOULD override
    /// it to flip ONLY the status of the existing row, preserving the original UserId, PlanId
    /// and AmountMinor - those fields are what the host revokes against. When no row exists
    /// yet (a refund that overtook its own success webhook) the record is a tombstone and must
    /// still be written, otherwise a late success delivery would fulfill refunded money.
    /// </summary>
#if NET5_0_OR_GREATER
    Task MarkRefundedAsync(BillingPaymentRecord refundContext, CancellationToken ct = default) =>
        UpsertAsync(refundContext with { Status = BillingPaymentStatus.Refunded }, ct);
#else
    // Same story as TryReserveAsync - no default interface members below .NET 5. The default was:
    //     UpsertAsync(refundContext with { Status = BillingPaymentStatus.Refunded }, ct);
    Task MarkRefundedAsync(BillingPaymentRecord refundContext, CancellationToken ct = default);
#endif
}
