namespace TechTeaStudio.Billing.Abstractions;

/// <summary>
/// Called by <see cref="BillingService"/> exactly once per confirmed payment (idempotency
/// is enforced by <see cref="IBillingPaymentStore"/>). Implementations grant the subscription
/// plan, top up a credit wallet, send a receipt - whatever the host domain requires.
/// </summary>
public interface IBillingFulfillment
{
    /// <summary>Apply the business effect of a confirmed payment.
    /// <see cref="BillingNotification.PlanId"/> identifies the plan;
    /// <see cref="BillingNotification.UserId"/> identifies the user.</summary>
    Task OnPaymentSucceededAsync(BillingNotification notification, CancellationToken ct = default);
}
