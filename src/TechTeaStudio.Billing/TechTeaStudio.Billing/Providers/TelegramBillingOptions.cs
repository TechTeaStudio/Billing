namespace TechTeaStudio.Billing.Providers;

/// <summary>A single Telegram Stars plan entry.</summary>
/// <param name="Stars">Price in whole Telegram Stars (XTR).</param>
/// <param name="Title">Invoice title shown to the user in the bot.</param>
/// <param name="Description">Invoice description shown to the user in the bot.</param>
public sealed record TelegramStarsPlan(int Stars, string Title, string Description);

/// <summary>Telegram Stars settings, bound from "Billing:Telegram". Amounts are whole Stars (XTR).
///
/// Important: planId values used as keys in <see cref="Plans"/> must contain only
/// [A-Za-z0-9] characters (no hyphens, no spaces) and must be short enough so that the
/// deep-link payload "pay-{guidN}-{planId}" stays within Telegram's 64-character payload limit.
/// A 32-char guid N-format leaves 26 characters for the prefix and planId, so planId should be
/// at most 21 characters.
/// </summary>
public sealed class TelegramBillingOptions
{
    /// <summary>Bot token from @BotFather.</summary>
    public string? BotToken { get; set; }

    /// <summary>Bot username without @, used in the t.me deep link.</summary>
    public string? BotUsername { get; set; }

    /// <summary>Optional secret echoed by Telegram in X-Telegram-Bot-Api-Secret-Token.
    /// When set, updates without a matching token are rejected. Required for IsConfigured.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>Maps a planId to its Stars price and invoice text.
    /// Lookups are case-insensitive.</summary>
    public Dictionary<string, TelegramStarsPlan> Plans { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
