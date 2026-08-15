namespace TechTeaStudio.Billing.Abstractions;

/// <summary>
/// The holding area for external purchases that could not be attributed or fulfilled
/// on arrival. Money in this store is already received - hosts must surface it in an
/// admin view and offer a self-serve claim path. Upserts are keyed by
/// (Provider, ProviderPaymentId), so webhook retries of the same payment do not
/// accumulate duplicates.
/// </summary>
public interface IUnclaimedPurchaseStore
{
    /// <summary>Insert or overwrite the held purchase, keyed by
    /// (Purchase.Provider, Purchase.ProviderPaymentId).</summary>
    Task AddAsync(UnclaimedPurchase purchase, CancellationToken ct = default);

    /// <summary>Held purchases, optionally filtered by provider and/or buyer email
    /// (email compared trimmed + lowercase).</summary>
    Task<IReadOnlyList<UnclaimedPurchase>> ListAsync(
        string? provider = null, string? buyerEmail = null, CancellationToken ct = default);

    /// <summary>Atomically removes and returns the held purchase, or null when absent.
    /// The caller that receives a non-null result owns fulfillment; on failure it must
    /// re-add the purchase so it is not lost.</summary>
    Task<UnclaimedPurchase?> TakeAsync(
        string provider, string providerPaymentId, CancellationToken ct = default);
}
