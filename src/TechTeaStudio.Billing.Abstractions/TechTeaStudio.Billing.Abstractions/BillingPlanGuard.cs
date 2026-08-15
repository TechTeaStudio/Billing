using System.Globalization;

namespace TechTeaStudio.Billing;

/// <summary>
/// Per-plan purchasability guard. A provider's pay button must NOT be offered (and a
/// checkout endpoint must refuse) when that provider has no REAL price for the plan -
/// a 0/absent price is a money hole: gateway-side amount validation is
/// "paid &gt;= configured", so a configured 0 would accept any token payment and grant
/// the plan for free. Prices stay provider-specific (regional), so the check reads the
/// same config keys each provider is bound from.
///
/// Deliberately takes a raw key lookup instead of IConfiguration so consumers'
/// application layers need no configuration package and tests can pass a dictionary.
///
/// External platforms (kofi, patreon, boosty) have NO price keys here on purpose: their
/// prices live on the platform, purchases arrive by webhook or claim rather than through
/// a checkout the app prices, and the guard fails open for them like for any unknown
/// provider.
/// </summary>
public static class BillingPlanGuard
{
    /// <summary>The config key that carries this provider's price for this plan, or null
    /// for a provider whose prices this guard does not know how to read. Stated once so
    /// deploy files can be tested against the same key the guard consults - a provider
    /// whose credentials reach the container while its prices do not is dead on arrival.</summary>
    public static string? PriceKeyFor(string provider, string planId) => provider.Trim().ToLowerInvariant() switch
    {
        "telegram" => $"Billing:Telegram:Plans:{planId}:Stars",
        "yookassa" => $"Billing:YooKassa:Amounts:{planId}",
        "shopify" => $"Billing:Shopify:Amounts:{planId}",
        // Stripe prices live in Stripe itself; the config carries a price id per plan.
        "stripe" => $"Billing:Stripe:PriceIds:{planId}",
        _ => null,
    };

    /// <param name="config">Raw config lookup, e.g. <c>key => configuration[key]</c>.</param>
    /// <param name="provider">Provider id as posted by the upgrade forms ("telegram", "stripe", ...).</param>
    /// <param name="planId">Plan id ("plus" / "plusyear", ...).</param>
    public static bool IsPurchasable(Func<string, string?> config, string provider, string planId)
    {
        var key = PriceKeyFor(provider, planId);
        // Unknown/future providers: the registry only lists configured ones and we have no
        // price key to inspect - do not silently kill a new integration.
        if (key is null) return true;

        var raw = config(key);
        return provider.Trim().ToLowerInvariant() switch
        {
            "telegram" => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stars)
                          && stars > 0,
            // Money amounts in major units, so a fractional price is legitimate.
            "yookassa" or "shopify" => decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture,
                                           out var amount) && amount > 0,
            _ => !string.IsNullOrWhiteSpace(raw),
        };
    }
}
