using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechTeaStudio.Billing.Abstractions;

namespace TechTeaStudio.Billing.Providers;

/// <summary>
/// YooKassa payments provider (RU market). Creates a redirect-confirmation payment via
/// the v3 REST API. YooKassa webhooks are UNSIGNED, so authenticity is established by
/// re-fetching the payment from the API (the source of truth) rather than trusting the
/// callback body.
/// </summary>
public sealed class YooKassaBillingProvider : IBillingProvider
{
    private const string ApiBase = "https://api.yookassa.ru/v3";
    private readonly HttpClient _http;
    private readonly YooKassaBillingOptions _opts;
    private readonly ILogger<YooKassaBillingProvider> _log;

    public YooKassaBillingProvider(
        HttpClient http,
        IOptions<YooKassaBillingOptions> opts,
        ILogger<YooKassaBillingProvider> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
    }

    public string Name => "yookassa";
    public string DisplayName => "YuKassa";
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_opts.ShopId) &&
        !string.IsNullOrWhiteSpace(_opts.SecretKey);

    public async Task<CheckoutSession> CreateCheckoutAsync(
        BillingCheckoutRequest req, CancellationToken ct = default)
    {
        var amount = AmountFor(req.PlanId);
        if (amount <= 0)
            throw new InvalidOperationException(
                $"No YooKassa amount configured for plan '{req.PlanId}'.");

        var payload = new Dictionary<string, object>
        {
            ["amount"] = new
            {
                value = amount.ToString("F2", CultureInfo.InvariantCulture),
                currency = _opts.Currency,
            },
            ["capture"] = true,
            ["confirmation"] = new { type = "redirect", return_url = req.ReturnUrl },
            ["description"] = req.PlanName,
            ["metadata"] = new { userId = req.UserId.ToString(), planId = req.PlanId },
        };
        if (_opts.SendReceipt) payload["receipt"] = BuildReceipt(req, amount);

        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/payments")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        msg.Headers.Authorization = BasicAuth();
        msg.Headers.Add("Idempotence-Key", Guid.NewGuid().ToString("N"));

        using var resp = await _http.SendAsync(msg, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogError("YooKassa payment creation failed ({Status}): {Body}",
                (int)resp.StatusCode, json);
            throw new InvalidOperationException("YooKassa payment creation failed.");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var id = root.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("YooKassa returned no payment id.");
        var url = root.GetProperty("confirmation").GetProperty("confirmation_url").GetString()
            ?? throw new InvalidOperationException("YooKassa returned no confirmation url.");
        return new CheckoutSession(url, Name, id);
    }

    public async Task<BillingNotification?> ParseNotificationAsync(
        string rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
    {
        if (!IsConfigured) return null;

        string eventName, objectId;
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            eventName = root.GetProperty("event").GetString() ?? "";
            objectId = root.GetProperty("object").GetProperty("id").GetString() ?? "";
        }
        catch { return null; }

        // The id is attacker-controlled until proven otherwise, and it ends up in a URL path. Only
        // the exact shape YooKassa issues passes, so nothing can alter which resource is fetched.
        if (!IsProviderId(objectId)) return null;

        // Authenticate by re-fetching - the webhook body is unsigned and untrusted. A refund event
        // names the refund, so that is fetched first and the payment comes from what IT says.
        var isRefundEvent = string.Equals(eventName, "refund.succeeded", StringComparison.Ordinal);
        var paymentId = objectId;
        if (isRefundEvent)
        {
            if (await FetchAsync($"refunds/{objectId}", ct) is not { } refund) return null;
            if (ReadString(refund, "status") != "succeeded") return Irrelevant();
            paymentId = ReadString(refund, "payment_id") ?? "";
            if (!IsProviderId(paymentId)) return null;
        }

        if (await FetchAsync($"payments/{paymentId}", ct) is not { } payment) return null;

        // From here on the API's own answer is the truth, the id included: the id in the body has
        // done its job, and passing it along would hand the sender the idempotency key.
        var apiId = ReadString(payment, "id");
        if (!string.Equals(apiId, paymentId, StringComparison.Ordinal))
        {
            _log.LogWarning("YooKassa returned payment {ApiId} when asked for {Id}.", apiId, paymentId);
            return null;
        }

        if (payment.TryGetProperty("test", out var test) && test.ValueKind == JsonValueKind.True
            && _opts.SecretKey?.StartsWith("test_", StringComparison.Ordinal) != true)
        {
            _log.LogWarning("YooKassa test payment {Id} refused on a live shop.", paymentId);
            return Irrelevant();
        }

        var (amountMinor, currency) = ReadMoney(payment, "amount");
        var (refundedMinor, _) = ReadMoney(payment, "refunded_amount");
        var planId = ReadPlanId(payment);
        var userId = ReadUserId(payment);
        var fullyRefunded = refundedMinor > 0 && refundedMinor >= amountMinor;

        if (fullyRefunded)
            return new BillingNotification(
                BillingEventKind.PaymentRefunded, userId, planId, paymentId, amountMinor, currency);

        if (refundedMinor > 0)
            _log.LogWarning(
                "YooKassa payment {Id} partly refunded ({Refunded} of {Amount} minor): nothing revoked automatically.",
                paymentId, refundedMinor, amountMinor);
        if (isRefundEvent) return Irrelevant();

        // The stored status decides, never the event name in the body.
        var status = ReadString(payment, "status");
        if (status == "canceled")
            return new BillingNotification(
                BillingEventKind.PaymentCanceled, userId, planId, paymentId, amountMinor, currency);

        var paid = payment.TryGetProperty("paid", out var p) && p.ValueKind == JsonValueKind.True;
        if (status != "succeeded" || !paid) return Irrelevant();

        // Defence in depth: only grant if the payment actually covers the plan's price.
        var expectedMinor = (long)Math.Round(AmountFor(planId) * 100m);
        if (expectedMinor <= 0 || amountMinor < expectedMinor
            || !string.Equals(currency, _opts.Currency, StringComparison.OrdinalIgnoreCase))
        {
            _log.LogWarning(
                "YooKassa payment {Id} does not cover plan '{Plan}': {Minor} {Currency}, expected {Expected}.",
                paymentId, planId, amountMinor, currency, expectedMinor);
            return Irrelevant();
        }

        if (userId == Guid.Empty)
        {
            _log.LogWarning("YooKassa payment {Id} carries no user id.", paymentId);
            return Irrelevant();
        }

        return new BillingNotification(
            BillingEventKind.PaymentSucceeded, userId, planId, paymentId, amountMinor, currency);
    }

    /// <summary>GET an API resource with the shop's credentials. Null on any failure, which answers
    /// the webhook with a 400, so YooKassa delivers it again later.</summary>
    private async Task<JsonElement?> FetchAsync(string path, CancellationToken ct)
    {
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/{path}");
            msg.Headers.Authorization = BasicAuth();
            using var resp = await _http.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("YooKassa re-fetch failed ({Status}) for {Path}",
                    (int)resp.StatusCode, path);
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "YooKassa re-fetch errored for {Path}", path);
            return null;
        }
    }

    /// <summary>YooKassa ids are lowercase hyphenated UUIDs (22e12f66-000f-5000-8000-18db351245c7).</summary>
    private static bool IsProviderId(string id) =>
        // The length check is load-bearing: Guid parsing forgives surrounding whitespace, and a
        // padded id would fetch the same payment under a new idempotency key.
        id.Length == 36
        && Guid.TryParseExact(id, "D", out _)
        && string.Equals(id, id.ToLowerInvariant(), StringComparison.Ordinal);

    private static string? ReadString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static BillingNotification Irrelevant() =>
        new(BillingEventKind.Unknown, Guid.Empty, "", "", 0, "");

    private decimal AmountFor(string planId) =>
        _opts.Amounts.TryGetValue(planId, out var a) ? a : 0m;

    /// <summary>The 54-FZ receipt block. Off by default: a self-employed shop issues its receipts
    /// through Moy Nalog and YooKassa refuses the block on a shop without online receipts.</summary>
    private Dictionary<string, object> BuildReceipt(BillingCheckoutRequest req, decimal amount)
    {
        if (string.IsNullOrWhiteSpace(req.Email))
            throw new InvalidOperationException("A YooKassa receipt needs the buyer's email.");

        return new Dictionary<string, object>
        {
            ["customer"] = new { email = req.Email },
            ["items"] = new[]
            {
                new
                {
                    description = req.PlanName,
                    quantity = "1.00",
                    amount = new
                    {
                        value = amount.ToString("F2", CultureInfo.InvariantCulture),
                        currency = _opts.Currency,
                    },
                    vat_code = _opts.ReceiptVatCode,
                    payment_mode = "full_payment",
                    payment_subject = "service",
                },
            },
        };
    }

    private AuthenticationHeaderValue BasicAuth()
    {
        var raw = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_opts.ShopId}:{_opts.SecretKey}"));
        return new AuthenticationHeaderValue("Basic", raw);
    }

    private static Guid ReadUserId(JsonElement payment)
    {
        if (payment.TryGetProperty("metadata", out var md) &&
            md.TryGetProperty("userId", out var uid) &&
            Guid.TryParse(uid.GetString(), out var g)) return g;
        return Guid.Empty;
    }

    private static string ReadPlanId(JsonElement payment)
    {
        if (payment.TryGetProperty("metadata", out var md) &&
            md.TryGetProperty("planId", out var p))
            return p.GetString() ?? "";
        return "";
    }

    private static (long minor, string currency) ReadMoney(JsonElement payment, string name)
    {
        if (payment.TryGetProperty(name, out var amt) && amt.ValueKind == JsonValueKind.Object)
        {
            var currency = amt.TryGetProperty("currency", out var c) ? (c.GetString() ?? "") : "";
            if (amt.TryGetProperty("value", out var v) &&
                decimal.TryParse(v.GetString(), NumberStyles.Number,
                    CultureInfo.InvariantCulture, out var dec))
                return ((long)Math.Round(dec * 100m), currency);
        }
        return (0, "");
    }
}
