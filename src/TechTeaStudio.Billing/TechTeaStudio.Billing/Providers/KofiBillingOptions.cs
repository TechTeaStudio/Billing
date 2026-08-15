namespace TechTeaStudio.Billing.Providers;

/// <summary>Ko-fi settings, bound from the "Billing:Kofi" config section.
/// Ko-fi has no HMAC signing: the ONLY authenticity check is the static
/// <see cref="VerificationToken"/> echoed inside every webhook payload
/// (ko-fi.com/manage/webhooks, Advanced dropdown). Treat it like a password.</summary>
public sealed class KofiBillingOptions
{
    /// <summary>The verification token from ko-fi.com/manage/webhooks. Required -
    /// the provider stays off without it, because an unauthenticated payload could
    /// forge purchases.</summary>
    public string? VerificationToken { get; set; }

    /// <summary>Public support-page URL (e.g. "https://ko-fi.com/yourname"). When set,
    /// checkout for this provider redirects here; the host UI should tell the buyer to
    /// paste their claim code into the Ko-fi message. When empty, checkout is refused
    /// but webhooks still work.</summary>
    public string? PageUrl { get; set; }

    /// <summary>Maps a Ko-fi membership tier_name (e.g. "Gold Tier") to a product id.
    /// A named tier with no mapping is HELD as unmapped_product rather than dropped.
    /// Lookups ignore case, spaces and punctuation, so a tier that cannot be spelled as an
    /// environment variable ("Gold Tier") can be configured as
    /// <c>Billing__Kofi__TierProducts__GoldTier</c>.</summary>
    public Dictionary<string, string> TierProducts { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Maps a Ko-fi Shop item direct_link_code to a product id. Only a single unit
    /// is mapped automatically - multi-item and multi-quantity orders are held for an admin,
    /// because a product id says what was bought, never how many. Lookups ignore case,
    /// spaces and punctuation.</summary>
    public Dictionary<string, string> ShopItemProducts { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Product id granted for one-off donations and for untiered (non-tier)
    /// subscription payments. Leave empty to treat plain donations as tips that grant
    /// nothing (they are then ignored, not held).</summary>
    public string? DonationProductId { get; set; }

    /// <summary>Product id granted for commission payments. Leave empty to ignore
    /// commissions. Note: Ko-fi publishes no example Commission payload - the raw JSON
    /// is logged on receipt so the first real event can be inspected.</summary>
    public string? CommissionProductId { get; set; }
}
