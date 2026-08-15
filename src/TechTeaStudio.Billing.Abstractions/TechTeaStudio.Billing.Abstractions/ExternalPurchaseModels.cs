namespace TechTeaStudio.Billing.Abstractions;

/// <summary>
/// A verified purchase that happened on an external donation/membership platform
/// (Ko-fi, Patreon, Boosty, ...) rather than through a checkout session this app started.
/// Unlike <see cref="BillingNotification"/> it carries no app user id - the buyer is
/// identified only by what the platform reveals (email, display name, a typed message),
/// and attribution to an app account happens separately
/// (see <see cref="IExternalPurchaseService"/>).
/// </summary>
/// <param name="Provider">Provider id, lowercase (e.g. "kofi", "patreon", "boosty").</param>
/// <param name="ProviderPaymentId">Stable identity of the PAYMENT (not the delivery):
/// Ko-fi kofi_transaction_id, Patreon "{memberId}:{last_charge_date}". This is the
/// idempotency key - retries and duplicate deliveries carry the same value.</param>
/// <param name="DeliveryId">Per-delivery id for logging/tracing (Ko-fi message_id).
/// May equal <paramref name="ProviderPaymentId"/> when the platform has no separate one.</param>
/// <param name="ProductId">Normalized product id after mapping the platform tier/item
/// through provider options, or null when nothing maps. What a product id MEANS
/// (a subscription tier, credits, a cosmetic) is entirely the host's decision.</param>
/// <param name="PlatformProduct">The raw platform-side product name or id
/// (Ko-fi tier_name / shop direct_link_code, Patreon tier id). Kept for diagnostics
/// and for admin claim flows on unmapped purchases.</param>
/// <param name="AmountMinor">Amount in the currency's minor units (cents, kopecks).</param>
/// <param name="Currency">ISO 4217 currency code, uppercase.</param>
/// <param name="BuyerEmail">The email the buyer paid with, as reported by the platform.
/// NOT verified by this app and often different from the buyer's app-account email.</param>
/// <param name="BuyerName">Buyer display name. User-controlled text - sanitize before display.</param>
/// <param name="Message">The buyer-typed message, when the platform carries one
/// (Ko-fi donations). This is the claim-code carrier. Null on Ko-fi subscription
/// RENEWALS - renewals attach via a stored identity link, never via the code.</param>
/// <param name="IsSubscriptionPayment">True for recurring membership payments.</param>
/// <param name="IsFirstSubscriptionPayment">True only on the first payment of a subscription.</param>
/// <param name="OccurredAt">When the platform says the payment happened (UTC).</param>
public sealed record ExternalPurchase(
    string Provider,
    string ProviderPaymentId,
    string DeliveryId,
    string? ProductId,
    string? PlatformProduct,
    long AmountMinor,
    string Currency,
    string? BuyerEmail,
    string? BuyerName,
    string? Message,
    bool IsSubscriptionPayment,
    bool IsFirstSubscriptionPayment,
    DateTimeOffset OccurredAt);

/// <summary>
/// An external purchase that could not be fulfilled yet and is parked for a later claim:
/// no claim code matched, no identity link existed, or the platform product is not mapped
/// to a product id. Held purchases are money already received - surfacing them in an admin
/// view is required, not optional.
/// </summary>
/// <param name="Purchase">The purchase as parsed from the platform.</param>
/// <param name="Reason">Why it was held: "unattributed" (buyer unknown) or
/// "unmapped_product" (buyer possibly known, product id missing).</param>
/// <param name="HeldAt">When it was parked (UTC).</param>
public sealed record UnclaimedPurchase(
    ExternalPurchase Purchase,
    string Reason,
    DateTimeOffset HeldAt);
