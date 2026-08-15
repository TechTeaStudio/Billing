namespace TechTeaStudio.Billing.Abstractions;

/// <summary>
/// Bot-side primitives the Telegram Stars flow needs from its update webhook. Kept in
/// Abstractions so the web endpoint can drive the bot lifecycle without referencing the
/// concrete provider. Implemented by <c>TelegramStarsBillingProvider</c>.
/// </summary>
public interface ITelegramBot
{
    /// <summary>False when the bot token or username are not configured.</summary>
    bool IsConfigured { get; }

    /// <summary>True if no webhook secret is set, or the request carries the matching
    /// <c>X-Telegram-Bot-Api-Secret-Token</c>. Guards pre_checkout and invoice actions
    /// from forged updates.</summary>
    bool VerifyWebhookToken(IReadOnlyDictionary<string, string> headers);

    /// <summary>Send an XTR (Stars) invoice to a chat for the plan encoded in
    /// <paramref name="payload"/>.</summary>
    Task SendInvoiceAsync(long chatId, string payload, CancellationToken ct = default);

    /// <summary>Approve a pre_checkout_query (Telegram requires an answer within ~10 s).</summary>
    Task AnswerPreCheckoutAsync(string preCheckoutQueryId, CancellationToken ct = default);
}
