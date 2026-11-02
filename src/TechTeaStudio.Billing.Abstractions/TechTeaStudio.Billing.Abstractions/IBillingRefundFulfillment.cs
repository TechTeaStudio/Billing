namespace TechTeaStudio.Billing.Abstractions;

/// <summary>
/// Optional second interface on the host's <see cref="IBillingFulfillment"/>: take back what a
/// payment granted when the money goes back. A host that does not implement it keeps the old
/// behaviour, where a refund only flips the stored status and nothing is revoked.
///
/// Called before the status flips, so a failure leaves the payment retryable, and only while the
/// stored status is not yet Refunded. That means a crash between revocation and the flip calls it
/// again: implementations must be idempotent on <see cref="BillingNotification.ProviderPaymentId"/>.
///
/// The notification carries refund-time values, which can be worse than the original ones: a
/// Patreon refund reports no plan, zero cents and sometimes no user. Resolve what to revoke from
/// the host's own record of that payment, not from these fields. A refund that overtook its own
/// success webhook arrives here too and has nothing to revoke.
/// </summary>
public interface IBillingRefundFulfillment
{
    /// <summary>Undo the business effect of a refunded or charged-back payment.</summary>
    Task OnPaymentRefundedAsync(BillingNotification notification, CancellationToken ct = default);
}
