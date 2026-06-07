namespace TechTeaStudio.Billing.Providers;

/// <summary>YooKassa settings, bound from the "Billing:YooKassa" config section.
/// Amounts are in major currency units (e.g. 299.00 RUB).</summary>
public sealed class YooKassaBillingOptions
{
    public string? ShopId { get; set; }
    public string? SecretKey { get; set; }

    /// <summary>Currency code, e.g. "RUB". Defaults to "RUB".</summary>
    public string Currency { get; set; } = "RUB";

    /// <summary>Maps a planId to the amount charged in major units.
    /// Lookups are case-insensitive.</summary>
    public Dictionary<string, decimal> Amounts { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
