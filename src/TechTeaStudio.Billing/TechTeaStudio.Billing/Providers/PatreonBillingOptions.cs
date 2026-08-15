namespace TechTeaStudio.Billing.Providers;

/// <summary>Patreon settings, bound from the "Billing:Patreon" config section.</summary>
public sealed class PatreonBillingOptions
{
    /// <summary>The webhook's own secret (the "secret" attribute of the webhook object,
    /// shown in the developer portal or returned by POST /api/oauth2/v2/webhooks).
    /// NOT the OAuth client secret - every webhook has its own. Required; the provider
    /// stays off without it.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>Public campaign page URL (e.g. "https://www.patreon.com/yourname").
    /// When set, checkout redirects here; when empty, checkout is refused but
    /// webhooks still work.</summary>
    public string? PageUrl { get; set; }

    /// <summary>Restricts processing to one campaign id. Empty accepts any campaign
    /// the webhook delivers (a webhook is campaign-scoped on Patreon's side anyway).</summary>
    public string? CampaignId { get; set; }

    /// <summary>Maps a Patreon tier ID (the numeric id from currently_entitled_tiers,
    /// not the tier title) to a product id. A member entitled to no mapped tier is
    /// HELD as unmapped_product. Lookups ignore case, spaces and punctuation.</summary>
    public Dictionary<string, string> TierProducts { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When true, last_charge_status "Free Trial" counts as a payment and
    /// grants the product. Default false - free trials grant nothing.</summary>
    public bool HonorFreeTrial { get; set; }

    /// <summary>Currency assumed for currently_entitled_amount_cents. The member payload
    /// does not carry a currency code; set this to your campaign currency. Defaults to "USD".</summary>
    public string Currency { get; set; } = "USD";
}
