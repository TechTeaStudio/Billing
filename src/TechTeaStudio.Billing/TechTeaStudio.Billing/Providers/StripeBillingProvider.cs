using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechTeaStudio.Billing.Abstractions;

namespace TechTeaStudio.Billing.Providers;

/// <summary>
/// Stripe Checkout provider. Talks to the REST API directly via HttpClient (no SDK
/// dependency). Checkout creates a hosted session; the webhook is HMAC-verified
/// (<see cref="StripeSignature"/>) before any fulfillment is triggered.
/// </summary>
public sealed class StripeBillingProvider : IBillingProvider
{
    private const string ApiBase = "https://api.stripe.com/v1";
    private readonly HttpClient _http;
    private readonly StripeBillingOptions _opts;
    private readonly ILogger<StripeBillingProvider> _log;

    public StripeBillingProvider(
        HttpClient http,
        IOptions<StripeBillingOptions> opts,
        ILogger<StripeBillingProvider> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
    }

    public string Name => "stripe";
    public string DisplayName => "Stripe";
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_opts.SecretKey) &&
        !string.IsNullOrWhiteSpace(_opts.WebhookSecret);

    public async Task<CheckoutSession> CreateCheckoutAsync(
        BillingCheckoutRequest req, CancellationToken ct = default)
    {
        var price = PriceFor(req.PlanId)
            ?? throw new InvalidOperationException(
                $"No Stripe price configured for plan '{req.PlanId}'.");

        var form = new Dictionary<string, string>
        {
            ["mode"] = _opts.Mode,
            ["line_items[0][price]"] = price,
            ["line_items[0][quantity]"] = "1",
            ["success_url"] = req.ReturnUrl,
            ["cancel_url"] = req.CancelUrl,
            ["client_reference_id"] = req.UserId.ToString(),
            ["customer_email"] = req.Email,
            ["metadata[userId]"] = req.UserId.ToString(),
            ["metadata[planId]"] = req.PlanId,
        };

        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/checkout/sessions")
        {
            Content = new FormUrlEncodedContent(form),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opts.SecretKey);

        using var resp = await _http.SendAsync(msg, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogError("Stripe checkout failed ({Status}): {Body}", (int)resp.StatusCode, json);
            throw new InvalidOperationException("Stripe checkout creation failed.");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var id = root.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Stripe returned no session id.");
        var url = root.GetProperty("url").GetString()
            ?? throw new InvalidOperationException("Stripe returned no checkout url.");
        return new CheckoutSession(url, Name, id);
    }

    public Task<BillingNotification?> ParseNotificationAsync(
        string rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
    {
        headers.TryGetValue("Stripe-Signature", out var sig);
        if (!StripeSignature.Verify(rawBody, sig, _opts.WebhookSecret, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)))
            return Task.FromResult<BillingNotification?>(null);

        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            var obj = root.GetProperty("data").GetProperty("object");

            var kind = type switch
            {
                "checkout.session.completed" => BillingEventKind.PaymentSucceeded,
                "checkout.session.expired" => BillingEventKind.PaymentCanceled,
                _ => BillingEventKind.Unknown,
            };
            if (kind == BillingEventKind.Unknown)
                return Task.FromResult<BillingNotification?>(Irrelevant());

            // A completed session must actually be paid (free trials / async methods report
            // "unpaid" / "no_payment_required") - otherwise treat it as a non-grant.
            if (kind == BillingEventKind.PaymentSucceeded
                && obj.TryGetProperty("payment_status", out var ps)
                && ps.GetString() is not "paid")
                kind = BillingEventKind.PaymentFailed;

            var id = obj.GetProperty("id").GetString() ?? "";
            long amount = obj.TryGetProperty("amount_total", out var at) &&
                          at.ValueKind == JsonValueKind.Number ? at.GetInt64() : 0;
            var currency = obj.TryGetProperty("currency", out var cur)
                ? (cur.GetString() ?? "").ToUpperInvariant() : "";
            return Task.FromResult<BillingNotification?>(
                new BillingNotification(kind, ReadUserId(obj), ReadPlanId(obj), id, amount, currency));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Stripe webhook body parse failed after a valid signature.");
            return Task.FromResult<BillingNotification?>(null);
        }
    }

    private string? PriceFor(string planId) =>
        _opts.PriceIds.TryGetValue(planId, out var p) ? p : null;

    private static BillingNotification Irrelevant() =>
        new(BillingEventKind.Unknown, Guid.Empty, "", "", 0, "");

    private static Guid ReadUserId(JsonElement obj)
    {
        if (obj.TryGetProperty("client_reference_id", out var cri) &&
            Guid.TryParse(cri.GetString(), out var g)) return g;
        if (obj.TryGetProperty("metadata", out var md) &&
            md.TryGetProperty("userId", out var uid) &&
            Guid.TryParse(uid.GetString(), out var g2)) return g2;
        return Guid.Empty;
    }

    private static string ReadPlanId(JsonElement obj)
    {
        if (obj.TryGetProperty("metadata", out var md) &&
            md.TryGetProperty("planId", out var p))
            return p.GetString() ?? "";
        return "";
    }
}
