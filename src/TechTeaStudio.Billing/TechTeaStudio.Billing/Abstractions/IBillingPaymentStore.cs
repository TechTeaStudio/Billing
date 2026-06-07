namespace TechTeaStudio.Billing.Abstractions;

/// <summary>
/// Persists payment records for audit and idempotency. The host implements this against
/// its own database. <see cref="BillingService"/> calls <see cref="GetStatusAsync"/> before
/// calling fulfillment and <see cref="UpsertAsync"/> after fulfillment succeeds.
///
/// Important: if <see cref="UpsertAsync"/> fails after fulfillment has already run, the
/// provider will retry and fulfillment will be called again. Fulfillment implementations
/// must therefore be idempotent on <see cref="BillingPaymentRecord.ProviderPaymentId"/>.
/// </summary>
public interface IBillingPaymentStore
{
    /// <summary>Returns the stored status for the payment, or null if not yet recorded.</summary>
    Task<BillingPaymentStatus?> GetStatusAsync(
        string provider, string providerPaymentId, CancellationToken ct = default);

    /// <summary>Insert or update the payment record.</summary>
    Task UpsertAsync(BillingPaymentRecord record, CancellationToken ct = default);
}
