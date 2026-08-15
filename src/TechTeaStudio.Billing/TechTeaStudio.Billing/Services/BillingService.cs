using Microsoft.Extensions.Logging;
using TechTeaStudio.Billing.Abstractions;

namespace TechTeaStudio.Billing.Services;

/// <summary>
/// Provider-agnostic billing orchestration. Selects a registered provider by name,
/// starts hosted checkouts, and calls <see cref="IBillingFulfillment"/> when a webhook
/// confirms payment - exactly once per (provider, paymentId) pair, enforced via
/// <see cref="IBillingPaymentStore"/>.
/// </summary>
public sealed class BillingService : IBillingService
{
    private readonly IReadOnlyDictionary<string, IBillingProvider> _providers;
    private readonly PaymentPipeline _pipeline;
    private readonly ILogger<BillingService> _log;

    public BillingService(
        IEnumerable<IBillingProvider> providers,
        IBillingPaymentStore store,
        IBillingFulfillment fulfillment,
        ILogger<BillingService> log)
    {
        // GroupBy, not ToDictionary: a host that calls AddTechTeaStudioBilling twice (or mixes
        // the config and manual overloads) registers each provider twice, and a duplicate-key
        // throw here would break every webhook and checkout at runtime.
        _providers = providers
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _pipeline = new PaymentPipeline(store, fulfillment, log);
        _log = log;
    }

    public IReadOnlyList<BillingProviderInfo> AvailableProviders() =>
        _providers.Values
            .Where(p => p.IsConfigured)
            .Select(p => new BillingProviderInfo(p.Name, p.DisplayName))
            .ToList();

    public async Task<CheckoutSession?> StartCheckoutAsync(
        BillingCheckoutRequest request, string providerName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.PlanId)) return null;
        if (!_providers.TryGetValue(providerName, out var provider) || !provider.IsConfigured)
            return null;

        try
        {
            return await provider.CreateCheckoutAsync(request, ct);
        }
        catch (InvalidOperationException ex)
        {
            // A provider refusing a checkout (unpriced plan, no support-page URL) is an
            // "unavailable" answer to the caller, not a 500 for the user.
            _log.LogWarning(ex,
                "Checkout refused by provider={Provider} for plan={PlanId}.",
                provider.Name, request.PlanId);
            return null;
        }
    }

    public async Task<bool> HandleWebhookAsync(
        string providerName, string rawBody,
        IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
    {
        if (!_providers.TryGetValue(providerName, out var provider)) return false;

        var note = await provider.ParseNotificationAsync(rawBody, headers, ct);
        if (note is null) return false;

        return await _pipeline.ProcessAsync(provider.Name, note, ct) != PaymentOutcome.Failed;
    }
}
