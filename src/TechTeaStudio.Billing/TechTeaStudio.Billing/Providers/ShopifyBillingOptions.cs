namespace TechTeaStudio.Billing.Providers;

/// <summary>Shopify settings, bound from "Billing:Shopify". Amounts are in major units
/// (e.g. 4.99 USD).</summary>
public sealed class ShopifyBillingOptions
{
    /// <summary>e.g. "your-store.myshopify.com"</summary>
    public string? ShopDomain { get; set; }

    /// <summary>Custom-app Admin API token (shpat_...).</summary>
    public string? AdminAccessToken { get; set; }

    /// <summary>App API secret - signs the orders/paid webhook (HMAC).</summary>
    public string? ApiSecret { get; set; }

    public string ApiVersion { get; set; } = "2024-10";

    /// <summary>Currency code, e.g. "USD". Defaults to "USD".</summary>
    public string Currency { get; set; } = "USD";

    /// <summary>Maps a planId to the amount charged in major units.
    /// Lookups are case-insensitive.</summary>
    public Dictionary<string, decimal> Amounts { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
