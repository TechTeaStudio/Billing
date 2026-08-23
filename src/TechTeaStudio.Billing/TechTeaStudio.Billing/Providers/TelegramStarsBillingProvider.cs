using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechTeaStudio.Billing.Abstractions;

namespace TechTeaStudio.Billing.Providers;

/// <summary>
/// Telegram Stars payments via a bot. Unlike redirect gateways, "checkout" is a deep link
/// into the bot (t.me/bot?start=payload); the bot then sends an XTR invoice. The bot's
/// update webhook drives the lifecycle: answer pre_checkout_query, send the invoice on
/// /start, and on successful_payment route back through IBillingService to fulfill.
///
/// Payload format: "pay-{guidN}-{planId}" where guidN is the user GUID in N format (32 hex
/// chars, no hyphens) and planId must contain only [A-Za-z0-9] characters. The total payload
/// must stay within Telegram's 64-character limit. With the "pay-" prefix (4 chars), the dash
/// separator (1 char), and the 32-char guid, 27 characters remain for planId.
///
/// WebhookSecret is REQUIRED - without it the bot-update endpoint cannot authenticate
/// Telegram, so a forged successful_payment would fulfill for free.
/// </summary>
public sealed class TelegramStarsBillingProvider : IBillingProvider, ITelegramBot
{
    private const string PayloadPrefix = "pay";
    private readonly HttpClient _http;
    private readonly TelegramBillingOptions _opts;
    private readonly ILogger<TelegramStarsBillingProvider> _log;

    public TelegramStarsBillingProvider(
        HttpClient http,
        IOptions<TelegramBillingOptions> opts,
        ILogger<TelegramStarsBillingProvider> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
    }

    public string Name => "telegram";
    public string DisplayName => "Telegram Stars";
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_opts.BotToken) &&
        !string.IsNullOrWhiteSpace(_opts.BotUsername) &&
        !string.IsNullOrWhiteSpace(_opts.WebhookSecret);

    // ---- Checkout: a deep link into the bot (no hosted page to redirect to) ----

    public Task<CheckoutSession> CreateCheckoutAsync(
        BillingCheckoutRequest req, CancellationToken ct = default)
    {
        if (StarsFor(req.PlanId) <= 0)
            throw new InvalidOperationException(
                $"No Telegram Stars price for plan '{req.PlanId}'.");
        var payload = BuildPayload(req.UserId, req.PlanId);
        var url = $"https://t.me/{_opts.BotUsername}?start={payload}";
        return Task.FromResult(new CheckoutSession(url, Name, payload));
    }

    // ---- Webhook: a successful_payment update fulfills the plan ----------------

    public Task<BillingNotification?> ParseNotificationAsync(
        string rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
    {
        if (!VerifyWebhookToken(headers))
            return Task.FromResult<BillingNotification?>(null);

        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            if (!doc.RootElement.TryGetProperty("message", out var msg) ||
                !msg.TryGetProperty("successful_payment", out var sp))
                return Task.FromResult<BillingNotification?>(Irrelevant());

            var payload = sp.TryGetProperty("invoice_payload", out var p)
                ? p.GetString() : null;
            if (!TryParsePayload(payload, out var userId, out var planId))
                return Task.FromResult<BillingNotification?>(Irrelevant());

            var chargeId = sp.TryGetProperty("telegram_payment_charge_id", out var ci)
                ? ci.GetString() ?? "" : "";
            long amount = sp.TryGetProperty("total_amount", out var ta) &&
                          ta.ValueKind == JsonValueKind.Number ? ta.GetInt64() : 0;
            var currency = sp.TryGetProperty("currency", out var cur)
                ? cur.GetString() ?? "XTR" : "XTR";

            // Defence in depth: the bot sets the invoice price from the plan, so an amount
            // below the configured Stars for that plan means a forged / tampered update.
            if (amount < StarsFor(planId))
            {
                _log.LogWarning(
                    "Telegram payment {Charge} underpaid for plan '{Plan}': {Amount} < {Expected} XTR.",
                    chargeId, planId, amount, StarsFor(planId));
                return Task.FromResult<BillingNotification?>(Irrelevant());
            }

            return Task.FromResult<BillingNotification?>(
                new BillingNotification(BillingEventKind.PaymentSucceeded, userId, planId,
                    chargeId, amount, currency));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Telegram update parse failed.");
            return Task.FromResult<BillingNotification?>(null);
        }
    }

    // ---- ITelegramBot: bot-update lifecycle primitives -------------------------

    public bool VerifyWebhookToken(IReadOnlyDictionary<string, string> headers)
    {
        if (string.IsNullOrWhiteSpace(_opts.WebhookSecret)) return false;
        headers.TryGetValue("X-Telegram-Bot-Api-Secret-Token", out var token);
        return string.Equals(token, _opts.WebhookSecret, StringComparison.Ordinal);
    }

    public async Task SendInvoiceAsync(long chatId, string payload, CancellationToken ct = default)
    {
        if (!TryParsePayload(payload, out _, out var planId)) return;
        if (!_opts.Plans.TryGetValue(planId, out var plan)) return;
        if (plan.Stars <= 0) return;

        var body = new
        {
            chat_id = chatId,
            title = plan.Title,
            description = plan.Description,
            payload,
            provider_token = "",   // empty for Telegram Stars (XTR)
            currency = "XTR",
            prices = new[] { new { label = plan.Title, amount = plan.Stars } },
        };
        await CallAsync("sendInvoice", body, ct);
    }

    /// <inheritdoc cref="ITelegramBot.CreateInvoiceLinkAsync"/>
    public async Task<string?> CreateInvoiceLinkAsync(string payload, CancellationToken ct = default)
    {
        if (!TryParsePayload(payload, out _, out var planId)) return null;
        if (!_opts.Plans.TryGetValue(planId, out var plan)) return null;
        if (plan.Stars <= 0) return null;

        var body = new
        {
            title = plan.Title,
            description = plan.Description,
            payload,
            provider_token = "",   // empty for Telegram Stars (XTR)
            currency = "XTR",
            prices = new[] { new { label = plan.Title, amount = plan.Stars } },
        };
        return await CallForStringAsync("createInvoiceLink", body, ct);
    }

    public Task AnswerPreCheckoutAsync(string preCheckoutQueryId, CancellationToken ct = default) =>
        CallAsync("answerPreCheckoutQuery",
            new { pre_checkout_query_id = preCheckoutQueryId, ok = true }, ct);

    /// <summary>POST a Bot API method and hand back its string `result`, or null on any failure.
    /// The sibling <see cref="CallAsync"/> discards the response body - fine for sendInvoice and
    /// answerPreCheckoutQuery, useless for createInvoiceLink, whose whole point IS the body.</summary>
    private async Task<string?> CallForStringAsync(string method, object body, CancellationToken ct)
    {
        try
        {
            using var msg = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://api.telegram.org/bot{_opts.BotToken}/{method}")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            using var resp = await _http.SendAsync(msg, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Telegram {Method} failed ({Status}): {Body}",
                    method, (int)resp.StatusCode, raw);
                return null;
            }

            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean()
                && doc.RootElement.TryGetProperty("result", out var result)
                && result.ValueKind == JsonValueKind.String)
                return result.GetString();

            _log.LogWarning("Telegram {Method} answered without a string result: {Body}", method, raw);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Telegram {Method} errored.", method);
            return null;
        }
    }

    private async Task CallAsync(string method, object body, CancellationToken ct)
    {
        try
        {
            using var msg = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://api.telegram.org/bot{_opts.BotToken}/{method}")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            using var resp = await _http.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode)
                _log.LogWarning("Telegram {Method} failed ({Status}): {Body}",
                    method, (int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) { _log.LogWarning(ex, "Telegram {Method} errored.", method); }
    }

    // ---- Payload and pricing helpers -------------------------------------------

    private int StarsFor(string planId) =>
        _opts.Plans.TryGetValue(planId, out var p) ? p.Stars : 0;

    /// <summary>Encodes user id and planId into a deep-link / invoice payload:
    /// "pay-{guidN}-{planId}". The planId must contain only [A-Za-z0-9] characters
    /// (no hyphens) because the payload splits on '-'. Total length must be at most
    /// 64 characters (Telegram limit).</summary>
    public static string BuildPayload(Guid userId, string planId) =>
        $"{PayloadPrefix}-{userId:N}-{planId}";

    /// <summary>Parses a payload produced by <see cref="BuildPayload"/>.</summary>
    public static bool TryParsePayload(
        string? payload, out Guid userId, out string planId)
    {
        userId = Guid.Empty;
        planId = "";
        if (string.IsNullOrEmpty(payload)) return false;

        // Format: "pay-{32hexGuid}-{planId}" - split into exactly 3 parts on the first two '-'.
        var firstDash = payload.IndexOf('-');
        if (firstDash < 0) return false;
        var secondDash = payload.IndexOf('-', firstDash + 1);
        if (secondDash < 0) return false;

        var prefix = payload[..firstDash];
        var guidPart = payload[(firstDash + 1)..secondDash];
        var planPart = payload[(secondDash + 1)..];

        if (prefix != PayloadPrefix) return false;
        if (!Guid.TryParseExact(guidPart, "N", out userId)) return false;
        if (string.IsNullOrEmpty(planPart)) return false;

        planId = planPart;
        return true;
    }

    private static BillingNotification Irrelevant() =>
        new(BillingEventKind.Unknown, Guid.Empty, "", "", 0, "");
}
