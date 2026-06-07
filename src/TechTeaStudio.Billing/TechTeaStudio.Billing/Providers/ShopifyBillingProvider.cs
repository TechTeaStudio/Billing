using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechTeaStudio.Billing.Abstractions;

namespace TechTeaStudio.Billing.Providers;

/// <summary>
/// Shopify payments provider. Checkout creates a Draft Order via the Admin REST API and
/// hands the customer its hosted invoice_url. The grant lands via the orders/paid webhook,
/// HMAC-verified (<see cref="ShopifySignature"/>) before any fulfillment is triggered. The
/// user id and planId ride along in the draft order's note_attributes and echo back on the
/// paid order.
/// </summary>
public sealed class ShopifyBillingProvider : IBillingProvider
{
    private readonly HttpClient _http;
    private readonly ShopifyBillingOptions _opts;
    private readonly ILogger<ShopifyBillingProvider> _log;

    public ShopifyBillingProvider(
        HttpClient http,
        IOptions<ShopifyBillingOptions> opts,
        ILogger<ShopifyBillingProvider> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
    }

    public string Name => "shopify";
    public string DisplayName => "Shopify";
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_opts.ShopDomain) &&
        !string.IsNullOrWhiteSpace(_opts.AdminAccessToken) &&
        !string.IsNullOrWhiteSpace(_opts.ApiSecret);

    private string AdminBase =>
        $"https://{_opts.ShopDomain}/admin/api/{_opts.ApiVersion}";

    public async Task<CheckoutSession> CreateCheckoutAsync(
        BillingCheckoutRequest req, CancellationToken ct = default)
    {
        var amount = AmountFor(req.PlanId);
        if (amount <= 0)
            throw new InvalidOperationException(
                $"No Shopify amount configured for plan '{req.PlanId}'.");

        var payload = new
        {
            draft_order = new
            {
                line_items = new[]
                {
                    new
                    {
                        title = req.PlanName,
                        price = amount.ToString("F2", CultureInfo.InvariantCulture),
                        quantity = 1,
                    },
                },
                email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email,
                note = req.PlanName,
                tags = "billing",
                note_attributes = new[]
                {
                    new { name = "userId", value = req.UserId.ToString() },
                    new { name = "planId", value = req.PlanId },
                },
            },
        };

        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{AdminBase}/draft_orders.json")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        msg.Headers.Add("X-Shopify-Access-Token", _opts.AdminAccessToken);

        using var resp = await _http.SendAsync(msg, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogError("Shopify draft order failed ({Status}): {Body}",
                (int)resp.StatusCode, json);
            throw new InvalidOperationException("Shopify draft order creation failed.");
        }

        using var doc = JsonDocument.Parse(json);
        var draft = doc.RootElement.GetProperty("draft_order");
        var id = ReadId(draft);
        var url = draft.GetProperty("invoice_url").GetString()
            ?? throw new InvalidOperationException("Shopify returned no invoice_url.");
        return new CheckoutSession(url, Name, id);
    }

    public Task<BillingNotification?> ParseNotificationAsync(
        string rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
    {
        headers.TryGetValue("X-Shopify-Hmac-Sha256", out var hmac);
        if (!ShopifySignature.Verify(rawBody, hmac, _opts.ApiSecret))
            return Task.FromResult<BillingNotification?>(null);

        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var order = doc.RootElement;
            var financial = order.TryGetProperty("financial_status", out var fs)
                ? fs.GetString() : null;
            var id = ReadId(order);

            var kind = financial == "paid"
                ? BillingEventKind.PaymentSucceeded : BillingEventKind.Unknown;
            if (kind == BillingEventKind.Unknown)
                return Task.FromResult<BillingNotification?>(Irrelevant());

            var (userId, planId) = ReadAttributes(order);
            var (minor, currency) = ReadAmount(order);

            // Defence in depth: note_attributes (planId) are buyer-influenceable, so only grant
            // if the order actually paid at least the configured price for that plan.
            var expectedMinor = (long)Math.Round(AmountFor(planId) * 100m);
            if (expectedMinor <= 0 || minor < expectedMinor)
            {
                _log.LogWarning(
                    "Shopify order {Id} underpaid for plan '{Plan}': {Minor} < {Expected} minor.",
                    id, planId, minor, expectedMinor);
                return Task.FromResult<BillingNotification?>(Irrelevant());
            }

            return Task.FromResult<BillingNotification?>(
                new BillingNotification(kind, userId, planId, id, minor, currency));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Shopify webhook body parse failed after a valid HMAC.");
            return Task.FromResult<BillingNotification?>(null);
        }
    }

    private decimal AmountFor(string planId) =>
        _opts.Amounts.TryGetValue(planId, out var a) ? a : 0m;

    private static BillingNotification Irrelevant() =>
        new(BillingEventKind.Unknown, Guid.Empty, "", "", 0, "");

    private static string ReadId(JsonElement el) =>
        el.TryGetProperty("id", out var id)
            ? (id.ValueKind == JsonValueKind.Number
                ? id.GetInt64().ToString(CultureInfo.InvariantCulture)
                : id.GetString() ?? "")
            : "";

    private static (Guid userId, string planId) ReadAttributes(JsonElement order)
    {
        var userId = Guid.Empty;
        var planId = "";
        if (order.TryGetProperty("note_attributes", out var attrs) &&
            attrs.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in attrs.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var value = a.TryGetProperty("value", out var v) ? v.GetString() : null;
                if (name == "userId" && Guid.TryParse(value, out var g)) userId = g;
                else if (name == "planId" && value is not null) planId = value;
            }
        }
        return (userId, planId);
    }

    private static (long minor, string currency) ReadAmount(JsonElement order)
    {
        var currency = order.TryGetProperty("currency", out var c) ? (c.GetString() ?? "") : "";
        if (order.TryGetProperty("total_price", out var tp) &&
            decimal.TryParse(tp.GetString(), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var dec))
            return ((long)Math.Round(dec * 100m), currency);
        return (0, currency);
    }
}
