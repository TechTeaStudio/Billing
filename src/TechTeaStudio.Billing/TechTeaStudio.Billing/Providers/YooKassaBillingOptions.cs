namespace TechTeaStudio.Billing.Providers;

/// <summary>YooKassa settings, bound from the "Billing:YooKassa" config section.
/// Amounts are in major currency units (e.g. 299.00 RUB).</summary>
public sealed class YooKassaBillingOptions
{
    public string? ShopId { get; set; }
    public string? SecretKey { get; set; }

    /// <summary>Currency code, e.g. "RUB". Defaults to "RUB".</summary>
    public string Currency { get; set; } = "RUB";

    /// <summary>Attach a 54-FZ receipt to every payment. Turn it on for a shop with online receipts
    /// connected; YooKassa then requires the block and rejects payments without it. Leave it off
    /// for a self-employed shop, which issues receipts through Moy Nalog.</summary>
    public bool SendReceipt { get; set; }

    /// <summary>YooKassa vat_code for the receipt line: 1 is "without VAT".</summary>
    public int ReceiptVatCode { get; set; } = 1;

    /// <summary>Maps a planId to the amount charged in major units.
    /// Lookups are case-insensitive.</summary>
    public Dictionary<string, decimal> Amounts { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
