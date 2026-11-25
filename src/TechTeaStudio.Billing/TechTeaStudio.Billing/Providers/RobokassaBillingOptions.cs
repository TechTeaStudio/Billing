using TechTeaStudio.Billing.Abstractions;

namespace TechTeaStudio.Billing.Providers;

/// <summary>Robokassa settings, bound from the "Billing:Robokassa" config section. The passwords
/// are the shop's Password #1 (signs the payment link) and Password #2 (signs the Result URL
/// notification), plus the separate pair for test payments. Amounts are in rubles.</summary>
public sealed class RobokassaBillingOptions
{
    /// <summary>The shop's identifier in Robokassa (MerchantLogin).</summary>
    public string? MerchantLogin { get; set; }

    public string? Password1 { get; set; }
    public string? Password2 { get; set; }
    public string? TestPassword1 { get; set; }
    public string? TestPassword2 { get; set; }

    /// <summary>Use the test passwords and mark every payment link with IsTest=1. A test shop
    /// accepts only test payments and a live shop only live ones.</summary>
    public bool IsTest { get; set; }

    /// <summary>The hash the shop is set to in Robokassa's technical settings: MD5, SHA1,
    /// SHA256, SHA384 or SHA512. Defaults to SHA256.</summary>
    public string HashAlgorithm { get; set; } = "SHA256";

    /// <summary>Send the cart (Receipt) with every payment: one item, the plan's name and price.
    /// Robokassa's receipts (Robochecks for the self-employed, cloud fiscalisation) are built from
    /// it; without it the receipt is not formed correctly.</summary>
    public bool SendReceipt { get; set; }

    /// <summary>The item's VAT: "none" for a seller without VAT (the self-employed).</summary>
    public string ReceiptTax { get; set; } = "none";

    /// <summary>The item's payment method: "full_payment" for a product handed over on payment.</summary>
    public string ReceiptPaymentMethod { get; set; } = "full_payment";

    /// <summary>What the item is: "service", "commodity", "intellectual_activity" (a software
    /// license) and the rest of Robokassa's list.</summary>
    public string ReceiptPaymentObject { get; set; } = "service";

    /// <summary>Robokassa's payment page.</summary>
    public string PaymentUrl { get; set; } = "https://auth.robokassa.ru/Merchant/Index.aspx";

    /// <summary>Maps a planId to the amount charged in rubles. Lookups are case-insensitive.</summary>
    public Dictionary<string, decimal> Amounts { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The invoice number (InvId) for a new payment: Robokassa wants a number unique to
    /// the shop, and it is the payment's idempotency key. A host with a database hands out its own
    /// sequence here; without one a random positive number is used. Not bindable from config.</summary>
    public Func<BillingCheckoutRequest, CancellationToken, Task<long>>? InvoiceIdFactory { get; set; }
}
