namespace TechTeaStudio.Billing.Abstractions;

/// <summary>What a checkout should buy, and where to send the user afterwards.</summary>
/// <param name="UserId">The application user who is paying.</param>
/// <param name="Email">User email - passed to the gateway for receipts.</param>
/// <param name="PlanId">Stable plan key (e.g. "plus", "pro"). Must be non-empty for a checkout to proceed.</param>
/// <param name="PlanName">Human-readable plan title used in invoice line items (e.g. "Chronos Plus").</param>
/// <param name="ReturnUrl">Where to send the user after a successful payment.</param>
/// <param name="CancelUrl">Where to send the user if they abandon checkout.</param>
public sealed record BillingCheckoutRequest(
    Guid UserId,
    string Email,
    string PlanId,
    string PlanName,
    string ReturnUrl,
    string CancelUrl);

/// <summary>A provider's hosted-checkout handle.
/// <see cref="ProviderPaymentId"/> is the id the provider will echo back in its webhook,
/// so it doubles as the idempotency key.</summary>
public sealed record CheckoutSession(string RedirectUrl, string Provider, string ProviderPaymentId);

/// <summary>Normalized webhook outcome, provider-agnostic.</summary>
public enum BillingEventKind
{
    Unknown = 0,
    PaymentSucceeded = 1,
    PaymentCanceled = 2,
    PaymentFailed = 3,

    /// <summary>A previously confirmed payment was refunded (or flagged as fraud) by the
    /// provider. Recorded against the original payment; fulfillment is never re-run and
    /// revocation is left to the host (watch the store for <see cref="BillingPaymentStatus.Refunded"/>).</summary>
    PaymentRefunded = 4,
}

/// <summary>A verified, normalized webhook notification a provider produces from a raw callback.</summary>
/// <param name="PlanId">The plan the user paid for. Empty string for irrelevant/unknown events.</param>
public sealed record BillingNotification(
    BillingEventKind Kind,
    Guid UserId,
    string PlanId,
    string ProviderPaymentId,
    long AmountMinor,
    string Currency);

/// <summary>A configured provider, for the upgrade UI's pay-with buttons.</summary>
public sealed record BillingProviderInfo(string Name, string DisplayName);

/// <summary>The persisted status of a tracked payment.</summary>
public enum BillingPaymentStatus
{
    Pending = 0,
    Succeeded = 1,
    Canceled = 2,
    Failed = 3,

    /// <summary>The payment succeeded earlier and was later refunded. A payment in this
    /// state is never fulfilled again, even if the original success webhook is redelivered.</summary>
    Refunded = 4,
}

/// <summary>An immutable snapshot of a payment record stored by <see cref="IBillingPaymentStore"/>.</summary>
public sealed record BillingPaymentRecord(
    Guid UserId,
    string Provider,
    string ProviderPaymentId,
    string PlanId,
    long AmountMinor,
    string Currency,
    BillingPaymentStatus Status);
