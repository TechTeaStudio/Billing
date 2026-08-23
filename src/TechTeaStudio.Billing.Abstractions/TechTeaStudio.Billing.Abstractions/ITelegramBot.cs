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

    /// <summary>
    /// v0.5.3: mint a Telegram invoice LINK for the payload - Bot API createInvoiceLink. Opening
    /// the returned URL shows the payment sheet directly in any Telegram client, with no bot
    /// chat and no /start in between.
    ///
    /// The deep-link flow this supplements is quietly broken for returning users: a
    /// t.me/bot?start=payload link only offers the START button to somebody who has NEVER
    /// started the bot. A returning user just gets the chat opened, the payload is dropped, and
    /// no invoice ever arrives unless they type "/start payload" by hand.
    ///
    /// Returns null when the payload does not parse, the plan is unknown, or Telegram refused -
    /// callers fall back to the deep link.
    /// </summary>
    Task<string?> CreateInvoiceLinkAsync(string payload, CancellationToken ct = default);
}
