namespace TechTeaStudio.Billing.Providers;

/// <summary>Stripe settings, bound from the "Billing:Stripe" config section.</summary>
public sealed class StripeBillingOptions
{
    /// <summary>Stripe secret key (sk_live_... or sk_test_...).</summary>
    public string? SecretKey { get; set; }

    /// <summary>Stripe webhook signing secret (whsec_...).</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>"payment" (one-off, the default) or "subscription" (recurring). The webhook only
    /// grants on the first checkout, so "subscription" also needs the host to handle Stripe's
    /// renewal invoices itself, or the customer is charged every period for one grant.</summary>
    public string Mode { get; set; } = "payment";

    /// <summary>Maps a planId to its Stripe Price ID (price_...).
    /// Lookups are case-insensitive.</summary>
    public Dictionary<string, string> PriceIds { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
