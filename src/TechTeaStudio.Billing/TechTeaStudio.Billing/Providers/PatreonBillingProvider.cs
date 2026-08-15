using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechTeaStudio.Billing.Abstractions;

namespace TechTeaStudio.Billing.Providers;

/// <summary>
/// Patreon memberships via APIv2 webhooks (members:* / members:pledge:* triggers).
///
/// Byte-level contract: deliveries carry X-Patreon-Event (the trigger string) and
/// X-Patreon-Signature (lowercase hex HMAC-MD5 of the RAW body keyed with the
/// per-webhook secret - see <see cref="PatreonSignature"/>). The payload is a JSON:API
/// document: the member's app-relevant fields sit in data.attributes, the Patreon user id
/// in data.relationships.user.data.id, entitled tier ids in
/// data.relationships.currently_entitled_tiers.data[].
///
/// Payment semantics (per official docs + staff answers): members:pledge:create fires on
/// a new paid pledge, members:update carries charge outcomes; members:create alone also
/// fires for FREE members (patron_status null) and is never a purchase. A payment is
/// recognized iff patron_status == "active_patron" and last_charge_status == "Paid"
/// (plus "Free Trial" when <see cref="PatreonBillingOptions.HonorFreeTrial"/>).
/// Refund statuses ("Refunded", "Fraud", "Refunded by Patreon", "Partially Refunded")
/// produce a <see cref="BillingEventKind.PaymentRefunded"/> record against the original
/// payment. Idempotency key: "{memberId}:{last_charge_date}" - a member's re-bill gets a
/// new charge date, duplicate deliveries of the same charge share one key.
///
/// There is NO decline webhook during Patreon's ~3-day retry grace, and status-transition
/// webhooks are known to be unreliable - a money-correct host should also reconcile
/// periodically via GET /api/oauth2/v2/campaigns/{id}/members. The portal's "Send Test"
/// payload does NOT match production payloads; never validate parsing against it.
///
/// Patreon carries no buyer-typed message, so attribution uses the (provider, email)
/// identity link or the claim flows on <see cref="IExternalPurchaseService"/>.
/// </summary>
public sealed class PatreonBillingProvider : IBillingProvider
{
    private static readonly string[] RefundStatuses =
        ["Refunded", "Fraud", "Refunded by Patreon", "Partially Refunded"];

    private readonly PatreonBillingOptions _opts;
    private readonly IExternalPurchaseService _external;
    private readonly ILogger<PatreonBillingProvider> _log;

    public PatreonBillingProvider(
        IOptions<PatreonBillingOptions> opts,
        IExternalPurchaseService external,
        ILogger<PatreonBillingProvider> log)
    {
        _opts = opts.Value;
        _external = external;
        _log = log;
    }

    public string Name => "patreon";
    public string DisplayName => "Patreon";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_opts.WebhookSecret);

    // ---- Checkout: a link to the public campaign page ---------------------------

    public Task<CheckoutSession> CreateCheckoutAsync(
        BillingCheckoutRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opts.PageUrl))
            throw new InvalidOperationException(
                "Patreon has no hosted checkout; set Billing:Patreon:PageUrl to offer a membership link.");
        return Task.FromResult(new CheckoutSession(
            _opts.PageUrl, Name, $"patreon-link-{Guid.NewGuid():N}"));
    }

    // ---- Webhook ----------------------------------------------------------------

    public async Task<BillingNotification?> ParseNotificationAsync(
        string rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
    {
        if (!IsConfigured) return null;

        headers.TryGetValue("X-Patreon-Signature", out var signature);
        if (!PatreonSignature.Verify(rawBody, signature, _opts.WebhookSecret))
        {
            _log.LogWarning("Patreon webhook rejected: signature mismatch.");
            return null;
        }

        headers.TryGetValue("X-Patreon-Event", out var trigger);
        trigger ??= "";
        if (!trigger.StartsWith("members:", StringComparison.Ordinal))
        {
            _log.LogDebug("Patreon trigger '{Trigger}' ignored.", trigger);
            return Irrelevant();
        }

        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data)) return Irrelevant();

            // Deliberately fails OPEN when the payload carries no campaign relationship: a
            // webhook is campaign-scoped on Patreon's side anyway, so dropping a payment over
            // a missing optional field would lose real money to guard against nothing.
            if (!string.IsNullOrWhiteSpace(_opts.CampaignId)
                && RelationshipId(data, "campaign") is { } campaignId
                && !string.Equals(campaignId, _opts.CampaignId, StringComparison.Ordinal))
                return Irrelevant();

            var memberId = data.TryGetProperty("id", out var mid) ? mid.GetString() ?? "" : "";
            data.TryGetProperty("attributes", out var attrs);

            var patronStatus = ReadString(attrs, "patron_status");
            var chargeStatus = ReadString(attrs, "last_charge_status");
            var chargeDate = ReadString(attrs, "last_charge_date");
            var email = ReadString(attrs, "email");
            var fullName = ReadString(attrs, "full_name");
            long amountMinor = attrs.ValueKind == JsonValueKind.Object
                && attrs.TryGetProperty("currently_entitled_amount_cents", out var cents)
                && cents.ValueKind == JsonValueKind.Number ? cents.GetInt64() : 0;

            // Free members and pledge-less events carry null statuses - never a purchase.
            if (string.IsNullOrEmpty(memberId) || string.IsNullOrEmpty(chargeDate)
                || string.IsNullOrEmpty(chargeStatus))
                return Irrelevant();

            var paymentId = $"{memberId}:{chargeDate}";
            var productId = MapTier(data);
            var currency = _opts.Currency.ToUpperInvariant();

            if (RefundStatuses.Contains(chargeStatus, StringComparer.OrdinalIgnoreCase))
            {
                // Annotate the ORIGINAL payment (same member + charge date key). The user
                // resolves via the identity link persisted when the payment was claimed.
                var refundUser = email is null
                    ? null
                    : await _external.ResolveBuyerAsync(
                        RefundContext(paymentId, memberId, productId, amountMinor,
                            currency, email, fullName), ct);
                return new BillingNotification(
                    BillingEventKind.PaymentRefunded, refundUser ?? Guid.Empty,
                    productId ?? "", paymentId, amountMinor, currency);
            }

            var paid = string.Equals(chargeStatus, "Paid", StringComparison.OrdinalIgnoreCase)
                || (_opts.HonorFreeTrial
                    && string.Equals(chargeStatus, "Free Trial", StringComparison.OrdinalIgnoreCase));
            // pledge:update is an upgrade/downgrade, which can carry its own charge. Letting
            // it through costs nothing when it does not: the paymentId is keyed on
            // last_charge_date, so an unchanged charge is deduped by the payment store.
            var isPaymentTrigger = trigger
                is "members:pledge:create" or "members:pledge:update" or "members:update";

            if (!isPaymentTrigger || !paid
                || !string.Equals(patronStatus, "active_patron", StringComparison.Ordinal))
                return Irrelevant(); // declines stay in Patreon's dunning grace - no action

            var purchase = new ExternalPurchase(
                Provider: Name,
                ProviderPaymentId: paymentId,
                DeliveryId: paymentId,
                ProductId: productId,
                PlatformProduct: FirstTierId(data) ?? "",
                AmountMinor: amountMinor,
                Currency: currency,
                BuyerEmail: email,
                BuyerName: fullName,
                Message: null, // Patreon has no buyer-typed message field
                IsSubscriptionPayment: true,
                IsFirstSubscriptionPayment: trigger == "members:pledge:create",
                OccurredAt: ReadTimestamp(chargeDate));

            if (productId is null)
            {
                await _external.HoldAsync(purchase, "unmapped_product", ct);
                return Irrelevant();
            }

            var userId = await _external.ResolveBuyerAsync(purchase, ct);
            if (userId is null)
            {
                await _external.HoldAsync(purchase, "unattributed", ct);
                return Irrelevant();
            }

            return new BillingNotification(
                BillingEventKind.PaymentSucceeded, userId.Value, productId,
                paymentId, amountMinor, currency);
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Patreon webhook body parse failed after a valid signature.");
            return null;
        }
    }

    // ---- JSON:API helpers -------------------------------------------------------

    private string? MapTier(JsonElement data)
    {
        foreach (var tierId in TierIds(data))
            if (ProductMap.TryResolve(_opts.TierProducts, tierId, out var product))
                return product;
        return null;
    }

    private static IEnumerable<string> TierIds(JsonElement data)
    {
        if (!data.TryGetProperty("relationships", out var rel)
            || !rel.TryGetProperty("currently_entitled_tiers", out var tiers)
            || !tiers.TryGetProperty("data", out var arr)
            || arr.ValueKind != JsonValueKind.Array) yield break;
        foreach (var tier in arr.EnumerateArray())
            if (tier.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } s)
                yield return s;
    }

    private static string? FirstTierId(JsonElement data) => TierIds(data).FirstOrDefault();

    private static string? RelationshipId(JsonElement data, string name) =>
        data.TryGetProperty("relationships", out var rel)
        && rel.TryGetProperty(name, out var r)
        && r.TryGetProperty("data", out var rd)
        && rd.ValueKind == JsonValueKind.Object
        && rd.TryGetProperty("id", out var id)
            ? id.GetString()
            : null;

    private static string? ReadString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static DateTimeOffset ReadTimestamp(string? iso) =>
        DateTimeOffset.TryParse(
            iso, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts)
            ? ts
            : DateTimeOffset.UtcNow;

    private ExternalPurchase RefundContext(
        string paymentId, string memberId, string? productId, long amountMinor,
        string currency, string email, string? fullName) =>
        new(Name, paymentId, paymentId, productId, memberId, amountMinor, currency,
            email, fullName, Message: null, IsSubscriptionPayment: true,
            IsFirstSubscriptionPayment: false, OccurredAt: DateTimeOffset.UtcNow);

    private static BillingNotification Irrelevant() =>
        new(BillingEventKind.Unknown, Guid.Empty, "", "", 0, "");
}
