#if NET11_0_OR_GREATER
// C# 15 unions need UnionAttribute and IUnion, which exist only in the .NET 11 runtime, so
// this whole file is net11.0 only. Older targets keep the bool-returning APIs on
// IBillingService and IExternalPurchaseService, which stay on every target.
namespace TechTeaStudio.Billing.Abstractions;

// ---- Webhook -------------------------------------------------------------------

/// <summary>The webhook confirmed a payment and fulfillment ran for the first time.</summary>
/// <param name="Notification">The verified notification that was fulfilled.</param>
public sealed record WebhookFulfilled(BillingNotification Notification);

/// <summary>The webhook was verified but the payment was already terminal (succeeded or
/// refunded) or is being fulfilled by a concurrent delivery. Nothing was granted. Accepted,
/// so the provider stops retrying.</summary>
/// <param name="Notification">The verified notification that changed nothing.</param>
public sealed record WebhookAlreadyProcessed(BillingNotification Notification);

/// <summary>A verified non-granting event was recorded: a cancel, a failure or a refund.
/// <see cref="BillingNotification.Kind"/> says which.</summary>
/// <param name="Notification">The verified notification that was recorded.</param>
public sealed record WebhookRecorded(BillingNotification Notification);

/// <summary>The webhook was verified but the event is irrelevant to billing
/// (<see cref="BillingEventKind.Unknown"/>). Accepted, so the provider stops retrying.
/// Carries the provider name only, never anything from the body.</summary>
/// <param name="Provider">The provider name the webhook was delivered for.</param>
public sealed record WebhookIgnored(string Provider);

/// <summary>
/// The webhook was not accepted: the provider is not registered, authentication failed, or
/// the payload was not understood. Nothing was read from or written to any store.
/// SECURITY: this is deliberately ONE case with no reason and no body content - telling a
/// forged signature from an unknown event shape would tell a caller how far a forgery got.
/// </summary>
/// <param name="Provider">The provider name the caller asked for. It is caller input (the
/// route value), not a verified fact.</param>
public sealed record WebhookRejected(string Provider);

/// <summary>The webhook was verified but fulfillment, or the revocation of a refund, threw.
/// Nothing was marked terminal, so the provider's retry gets another attempt.</summary>
/// <param name="Notification">The verified notification that could not be applied.</param>
public sealed record WebhookFailed(BillingNotification Notification);

/// <summary>
/// What <see cref="IBillingWebhookProcessor.ProcessWebhookAsync"/> did with a webhook. A
/// closed set: a <c>switch</c> over the six cases is exhaustive without a discard arm.
/// A notification-carrying case exists only after the provider authenticated the payload.
/// The library returns outcomes and never accepts one as input.
/// </summary>
public union WebhookOutcome(
    WebhookFulfilled, WebhookAlreadyProcessed, WebhookRecorded,
    WebhookIgnored, WebhookRejected, WebhookFailed);

/// <summary>
/// The webhook entry point of <see cref="IBillingService"/> with a typed answer instead of
/// a bool. Same pipeline, same exactly-once guarantee, same exceptions: a store error or a
/// cancellation still propagates and is never folded into a case. .NET 11 and later only.
/// </summary>
public interface IBillingWebhookProcessor
{
    /// <summary>Process a provider webhook: authenticate, dedupe, and on a confirmed payment
    /// call <see cref="IBillingFulfillment"/>. <see cref="IBillingService.HandleWebhookAsync"/>
    /// returns true exactly when this returns anything but <see cref="WebhookRejected"/> or
    /// <see cref="WebhookFailed"/>.</summary>
    Task<WebhookOutcome> ProcessWebhookAsync(
        string providerName, string rawBody,
        IReadOnlyDictionary<string, string> headers, CancellationToken ct = default);
}

// ---- Claim of a held external purchase -----------------------------------------

/// <summary>The held purchase was fulfilled for the user and its hold is consumed.</summary>
/// <param name="Purchase">The purchase that was claimed.</param>
/// <param name="ProductId">The product id that was granted: the override when one was
/// passed, otherwise the purchase's own mapped product id.</param>
public sealed record ClaimFulfilled(ExternalPurchase Purchase, string ProductId);

/// <summary>The payment behind the hold was already fulfilled or refunded. The stale hold
/// is consumed and nothing was granted.</summary>
/// <param name="Purchase">The purchase whose hold was consumed.</param>
public sealed record ClaimAlreadyProcessed(ExternalPurchase Purchase);

/// <summary>No purchase is held under this provider and payment id.</summary>
/// <param name="Provider">Provider id, normalized to lowercase.</param>
/// <param name="ProviderPaymentId">The payment id that was asked for.</param>
public sealed record ClaimNotHeld(string Provider, string ProviderPaymentId);

/// <summary>The purchase has no mapped product id and no override was passed. It is still
/// held - claim it again with a <c>productIdOverride</c>.</summary>
/// <param name="Held">The purchase, still in the holding area.</param>
public sealed record ClaimNeedsProduct(UnclaimedPurchase Held);

/// <summary>Fulfillment failed or a store threw. The purchase was put back in the holding
/// area, so the claim can be retried.</summary>
/// <param name="Held">The purchase as it was taken and re-held.</param>
public sealed record ClaimFailed(UnclaimedPurchase Held);

/// <summary>
/// What <see cref="IExternalPurchaseClaims.ClaimHeldAsync"/> did with a held purchase. A
/// closed set: a <c>switch</c> over the five cases is exhaustive without a discard arm.
/// </summary>
public union ClaimOutcome(
    ClaimFulfilled, ClaimAlreadyProcessed, ClaimNotHeld,
    ClaimNeedsProduct, ClaimFailed);

/// <summary>
/// The targeted claim of <see cref="IExternalPurchaseService"/> with a typed answer instead
/// of a bool. A separate interface, so a host that replaces
/// <see cref="IExternalPurchaseService"/> with its own implementation still resolves the
/// library's claim logic here. .NET 11 and later only.
/// </summary>
public interface IExternalPurchaseClaims
{
    /// <summary>
    /// Claims one held purchase for the user (admin path). <paramref name="productIdOverride"/>
    /// resolves "unmapped_product" holds by naming the product explicitly.
    /// <see cref="IExternalPurchaseService.ClaimAsync"/> returns true exactly when this
    /// returns <see cref="ClaimFulfilled"/>.
    /// </summary>
    Task<ClaimOutcome> ClaimHeldAsync(
        string provider, string providerPaymentId, Guid userId,
        string? productIdOverride = null, CancellationToken ct = default);
}
#endif
