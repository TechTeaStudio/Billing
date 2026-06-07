namespace TechTeaStudio.Billing.Abstractions;

/// <summary>
/// Provider-agnostic billing orchestration. Selects a registered
/// <see cref="IBillingProvider"/> by name, starts hosted checkouts, and calls
/// <see cref="IBillingFulfillment"/> when a provider's webhook confirms payment - exactly
/// once, via the (provider, paymentId) idempotency key in <see cref="IBillingPaymentStore"/>.
/// </summary>
public interface IBillingService
{
    /// <summary>Providers that are configured and ready to take payment.</summary>
    IReadOnlyList<BillingProviderInfo> AvailableProviders();

    /// <summary>Start a checkout for a plan via a named provider. Returns null if the provider
    /// is unknown/unconfigured or <see cref="BillingCheckoutRequest.PlanId"/> is empty.</summary>
    Task<CheckoutSession?> StartCheckoutAsync(
        BillingCheckoutRequest request, string providerName, CancellationToken ct = default);

    /// <summary>Process a provider webhook: authenticate, dedupe, and on a confirmed payment
    /// call <see cref="IBillingFulfillment"/>. Returns true if the payload was accepted
    /// (including an already-applied duplicate - so the provider stops retrying);
    /// false if authentication or parsing failed.</summary>
    Task<bool> HandleWebhookAsync(
        string providerName, string rawBody,
        IReadOnlyDictionary<string, string> headers, CancellationToken ct = default);
}
