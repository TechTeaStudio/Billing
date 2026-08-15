using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechTeaStudio.Billing.Abstractions;

namespace TechTeaStudio.Billing.Providers;

/// <summary>
/// Ko-fi purchases (donations, memberships, shop orders) via the official Ko-fi webhook.
///
/// Wire format (byte-level, per Ko-fi docs and every shipped receiver): the webhook is an
/// HTTP POST with Content-Type application/x-www-form-urlencoded whose single form field
/// "data" carries the JSON document URL-encoded - NEVER parse the raw body as JSON.
/// Authenticity is ONLY the static verification_token echoed inside that JSON (no HMAC,
/// no signature header, no published source IPs), compared constant-time here.
///
/// There is no checkout session: buyers pay on the Ko-fi page, so attribution runs through
/// <see cref="IExternalPurchaseService"/> - a claim code found in the donation message, or
/// a stored (provider, email) identity link. Everything else is parked in the unclaimed
/// holding area. Ko-fi retries deliveries "a reasonable number of times with the same
/// message_id" on non-200; idempotency is keyed on kofi_transaction_id.
///
/// Ko-fi has NO webhook for refunds, cancellations, or membership expiry ("limited to when
/// a payment is made") - hosts must time-box subscription entitlements and revoke manually
/// on refunds.
/// </summary>
public sealed class KofiBillingProvider : IBillingProvider
{
    private readonly KofiBillingOptions _opts;
    private readonly IExternalPurchaseService _external;
    private readonly ILogger<KofiBillingProvider> _log;

    public KofiBillingProvider(
        IOptions<KofiBillingOptions> opts,
        IExternalPurchaseService external,
        ILogger<KofiBillingProvider> log)
    {
        _opts = opts.Value;
        _external = external;
        _log = log;
    }

    public string Name => "kofi";
    public string DisplayName => "Ko-fi";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_opts.VerificationToken);

    // ---- Checkout: a link to the public Ko-fi page (no hosted session exists) ----

    public Task<CheckoutSession> CreateCheckoutAsync(
        BillingCheckoutRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opts.PageUrl))
            throw new InvalidOperationException(
                "Ko-fi has no hosted checkout; set Billing:Kofi:PageUrl to offer a support link.");
        // Nothing echoes this id back - Ko-fi attribution runs on claim codes instead.
        return Task.FromResult(new CheckoutSession(
            _opts.PageUrl, Name, $"kofi-link-{Guid.NewGuid():N}"));
    }

    // ---- Webhook ----------------------------------------------------------------

    public async Task<BillingNotification?> ParseNotificationAsync(
        string rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
    {
        if (!IsConfigured) return null;

        string? dataJson;
        try
        {
            var form = await new FormReader(rawBody).ReadFormAsync(ct);
            dataJson = form.TryGetValue("data", out var v) ? v.ToString() : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ko-fi webhook body is not a parsable form.");
            return null;
        }
        if (string.IsNullOrWhiteSpace(dataJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            var root = doc.RootElement;

            // A "data" field that is valid JSON but not an object (a bare string, an array)
            // would make every TryGetProperty below throw InvalidOperationException and turn
            // an unauthenticated payload into a 500 that Ko-fi retries forever.
            if (root.ValueKind != JsonValueKind.Object)
            {
                _log.LogWarning("Ko-fi webhook 'data' field is not a JSON object.");
                return null;
            }

            // The token inside the payload is the only authenticity proof Ko-fi offers.
            if (!FixedEquals(ReadString(root, "verification_token"), _opts.VerificationToken))
            {
                _log.LogWarning("Ko-fi webhook rejected: verification_token mismatch.");
                return null;
            }

            var type = ReadString(root, "type") ?? "";
            var messageId = ReadString(root, "message_id") ?? "";
            var transactionId = ReadString(root, "kofi_transaction_id");
            var paymentId = string.IsNullOrWhiteSpace(transactionId) ? messageId : transactionId;
            if (string.IsNullOrWhiteSpace(paymentId))
            {
                _log.LogWarning("Ko-fi webhook carries neither kofi_transaction_id nor message_id.");
                return null;
            }

            var currency = (ReadString(root, "currency") ?? "").ToUpperInvariant();
            var amountMinor = ReadAmountMinor(ReadString(root, "amount"));
            var occurredAt = ReadTimestamp(ReadString(root, "timestamp"));
            var tierName = ReadString(root, "tier_name");

            var (productId, holdIfUnmapped) = MapProduct(type, tierName, root);

            // Commission payloads are undocumented by Ko-fi - keep a trace of the first ones.
            if (type is "Commission" && productId is null)
                _log.LogDebug("Ko-fi Commission payload (unmapped): {Json}", dataJson);

            var purchase = new ExternalPurchase(
                Provider: Name,
                ProviderPaymentId: paymentId,
                DeliveryId: messageId,
                ProductId: productId,
                PlatformProduct: tierName ?? FirstShopItemCode(root) ?? type,
                AmountMinor: amountMinor,
                Currency: currency,
                BuyerEmail: ReadString(root, "email"),
                BuyerName: ReadString(root, "from_name"),
                Message: ReadString(root, "message"),
                IsSubscriptionPayment: ReadBool(root, "is_subscription_payment"),
                IsFirstSubscriptionPayment: ReadBool(root, "is_first_subscription_payment"),
                OccurredAt: occurredAt);

            // Resolve first, even when nothing maps: a buyer who typed their claim code is
            // asking for something, so their payment must reach an admin rather than be
            // written off as a tip. (Resolving also persists the identity link.)
            var userId = await _external.ResolveBuyerAsync(purchase, ct);

            if (productId is null)
            {
                if (holdIfUnmapped || userId is not null)
                    await _external.HoldAsync(purchase, "unmapped_product", ct);
                else
                    _log.LogDebug(
                        "Ko-fi event type '{Type}' ignored (no product mapping, no buyer).", type);
                return Irrelevant();
            }

            if (userId is null)
            {
                await _external.HoldAsync(purchase, "unattributed", ct);
                return Irrelevant();
            }

            return new BillingNotification(
                BillingEventKind.PaymentSucceeded, userId.Value, productId,
                paymentId, amountMinor, currency);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            _log.LogWarning(ex, "Ko-fi webhook 'data' field is not a payload we understand.");
            return null;
        }
    }

    // ---- Mapping ----------------------------------------------------------------

    /// <summary>Maps the platform event to a product id. The second value says whether an
    /// unmapped result should be HELD for an admin (money for a concrete product arrived)
    /// or silently ignored (a tip with nothing configured, an unknown event type).</summary>
    private (string? ProductId, bool HoldIfUnmapped) MapProduct(
        string type, string? tierName, JsonElement root)
    {
        switch (type)
        {
            case "Donation":
                return (_opts.DonationProductId, false);

            case "Subscription":
                if (string.IsNullOrWhiteSpace(tierName))
                    return (_opts.DonationProductId, false); // untiered membership = recurring tip
                return ProductMap.TryResolve(_opts.TierProducts, tierName, out var tierProduct)
                    ? (tierProduct, true)
                    : (null, true); // a NAMED tier with no mapping is a config gap - hold it

            case "Shop Order":
                // A product id says WHAT was bought, never HOW MANY - so only a single unit
                // can be auto-granted. Multi-item and multi-quantity orders are paid goods
                // that go to an admin instead of quietly becoming one grant.
                if (CountShopItems(root) == 1 && FirstShopItemQuantity(root) <= 1
                    && ProductMap.TryResolve(
                        _opts.ShopItemProducts, FirstShopItemCode(root), out var itemProduct))
                    return (itemProduct, true);
                return (null, true); // paid goods - always hold when unmapped or multi-unit

            case "Commission":
                return (_opts.CommissionProductId, false);

            default:
                return (null, false); // unknown/new event type - accept and ignore
        }
    }

    private static string? FirstShopItemCode(JsonElement root)
    {
        if (!root.TryGetProperty("shop_items", out var items)
            || items.ValueKind != JsonValueKind.Array
            || items.GetArrayLength() == 0) return null;
        var first = items[0];
        return first.TryGetProperty("direct_link_code", out var c) ? c.GetString() : null;
    }

    /// <summary>Quantity of the first shop item. Older Ko-fi payloads carried only
    /// direct_link_code, so an absent quantity means a single unit.</summary>
    private static int FirstShopItemQuantity(JsonElement root)
    {
        if (!root.TryGetProperty("shop_items", out var items)
            || items.ValueKind != JsonValueKind.Array
            || items.GetArrayLength() == 0) return 0;
        return items[0].TryGetProperty("quantity", out var q) && q.ValueKind == JsonValueKind.Number
            ? q.GetInt32()
            : 1;
    }

    private static int CountShopItems(JsonElement root) =>
        root.TryGetProperty("shop_items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.GetArrayLength()
            : 0;

    // ---- Field helpers ----------------------------------------------------------

    private static string? ReadString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool ReadBool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Ko-fi sends amount as a STRING like "3.00" / "27.95". Parsed invariant
    /// as decimal (never float) and converted to minor units. Zero-decimal currencies
    /// are not documented by Ko-fi; the amount is informational for grants anyway.</summary>
    private static long ReadAmountMinor(string? amount) =>
        decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? (long)Math.Round(value * 100m, MidpointRounding.AwayFromZero)
            : 0;

    private static DateTimeOffset ReadTimestamp(string? iso) =>
        DateTimeOffset.TryParse(
            iso, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts)
            ? ts
            : DateTimeOffset.UtcNow;

    private static bool FixedEquals(string? received, string? expected)
    {
        if (string.IsNullOrEmpty(received) || string.IsNullOrEmpty(expected)) return false;
        var a = Encoding.UTF8.GetBytes(received);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static BillingNotification Irrelevant() =>
        new(BillingEventKind.Unknown, Guid.Empty, "", "", 0, "");
}
