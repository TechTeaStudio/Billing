namespace TechTeaStudio.Billing.Providers;

/// <summary>Stripe settings, bound from the "Billing:Stripe" config section.</summary>
public sealed class StripeBillingOptions
{
    /// <summary>Stripe secret key (sk_live_... or sk_test_...).</summary>
    public string? SecretKey { get; set; }

    /// <summary>Stripe webhook signing secret (whsec_...).</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>"subscription" (recurring) or "payment" (one-off). Defaults to "subscription".</summary>
    public string Mode { get; set; } = "subscription";

    /// <summary>Maps a planId to its Stripe Price ID (price_...).
    /// Lookups are case-insensitive.</summary>
    public Dictionary<string, string> PriceIds { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
