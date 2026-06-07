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

        var payload = new
        {
            amount = new
            {
                value = amount.ToString("F2", CultureInfo.InvariantCulture),
                currency = _opts.Currency,
            },
            capture = true,
            confirmation = new { type = "redirect", return_url = req.ReturnUrl },
            description = req.PlanName,
            metadata = new { userId = req.UserId.ToString(), planId = req.PlanId },
        };

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

        string eventName, paymentId;
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            eventName = root.GetProperty("event").GetString() ?? "";
            paymentId = root.GetProperty("object").GetProperty("id").GetString() ?? "";
        }
        catch { return null; }
        if (string.IsNullOrEmpty(paymentId)) return null;

        // Authenticate by re-fetching the payment - the webhook body is unsigned and untrusted.
        JsonElement payment;
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/payments/{paymentId}");
            msg.Headers.Authorization = BasicAuth();
            using var resp = await _http.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("YooKassa re-fetch failed ({Status}) for {Id}",
                    (int)resp.StatusCode, paymentId);
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            payment = doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "YooKassa re-fetch errored for {Id}", paymentId);
            return null;
        }

        var status = payment.TryGetProperty("status", out var st) ? st.GetString() : null;
        var kind = (eventName, status) switch
        {
            ("payment.succeeded", "succeeded") => BillingEventKind.PaymentSucceeded,
            ("payment.canceled", _) => BillingEventKind.PaymentCanceled,
            (_, "canceled") => BillingEventKind.PaymentCanceled,
            _ => BillingEventKind.Unknown,
        };
        if (kind == BillingEventKind.Unknown)
            return new BillingNotification(BillingEventKind.Unknown, Guid.Empty, "", "", 0, "");

        var (amountMinor, currency) = ReadAmount(payment);
        var planId = ReadPlanId(payment);

        // Defence in depth: only grant if the payment actually covers the plan's price.
        if (kind == BillingEventKind.PaymentSucceeded)
        {
            var expectedMinor = (long)Math.Round(AmountFor(planId) * 100m);
            if (expectedMinor <= 0 || amountMinor < expectedMinor)
            {
                _log.LogWarning(
                    "YooKassa payment {Id} underpaid for plan '{Plan}': {Minor} < {Expected} minor.",
                    paymentId, planId, amountMinor, expectedMinor);
                return new BillingNotification(BillingEventKind.Unknown, Guid.Empty, "", "", 0, "");
            }
        }

        return new BillingNotification(kind, ReadUserId(payment), planId, paymentId, amountMinor, currency);
    }

    private decimal AmountFor(string planId) =>
        _opts.Amounts.TryGetValue(planId, out var a) ? a : 0m;

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

    private static (long minor, string currency) ReadAmount(JsonElement payment)
    {
        if (payment.TryGetProperty("amount", out var amt))
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
