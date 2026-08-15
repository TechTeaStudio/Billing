namespace TechTeaStudio.Billing.Abstractions;

/// <summary>External-purchase settings, bound from the "Billing:External" config section.</summary>
public sealed class ExternalPurchaseOptions
{
    /// <summary>
    /// Prefix of the claim codes the app hands to users (code format:
    /// "{Prefix}-XXXXXXXX"). A buyer types the code into the platform's message field
    /// (e.g. a Ko-fi donation message) so the webhook can attribute the payment.
    /// Pick something short and product-recognizable (e.g. "CHR"). Defaults to "PAY".
    /// </summary>
    public string ClaimCodePrefix { get; set; } = "PAY";
}
