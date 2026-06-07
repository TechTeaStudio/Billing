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
    private readonly IBillingPaymentStore _store;
    private readonly IBillingFulfillment _fulfillment;
    private readonly ILogger<BillingService> _log;

    public BillingService(
        IEnumerable<IBillingProvider> providers,
        IBillingPaymentStore store,
        IBillingFulfillment fulfillment,
        ILogger<BillingService> log)
    {
        _providers = providers.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        _store = store;
        _fulfillment = fulfillment;
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

        return await provider.CreateCheckoutAsync(request, ct);
    }

    public async Task<bool> HandleWebhookAsync(
        string providerName, string rawBody,
        IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
    {
        if (!_providers.TryGetValue(providerName, out var provider)) return false;

        var note = await provider.ParseNotificationAsync(rawBody, headers, ct);
        if (note is null) return false;

        var existingStatus = await _store.GetStatusAsync(provider.Name, note.ProviderPaymentId, ct);

        // Idempotency: already succeeded - stop retries.
        if (existingStatus == BillingPaymentStatus.Succeeded) return true;

        if (note.Kind is BillingEventKind.PaymentCanceled or BillingEventKind.PaymentFailed)
        {
            var terminal = note.Kind == BillingEventKind.PaymentCanceled
                ? BillingPaymentStatus.Canceled
                : BillingPaymentStatus.Failed;
            await _store.UpsertAsync(
                new BillingPaymentRecord(
                    note.UserId, provider.Name, note.ProviderPaymentId, note.PlanId,
                    note.AmountMinor, note.Currency, terminal), ct);
            return true;
        }

        // Authenticated but irrelevant event - accept so the provider stops retrying.
        if (note.Kind != BillingEventKind.PaymentSucceeded) return true;

        // PaymentSucceeded path: call fulfillment then persist.
        try
        {
            await _fulfillment.OnPaymentSucceededAsync(note, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Fulfillment failed for provider={Provider} paymentId={PaymentId}.",
                provider.Name, note.ProviderPaymentId);
            return false;
        }

        try
        {
            await _store.UpsertAsync(
                new BillingPaymentRecord(
                    note.UserId, provider.Name, note.ProviderPaymentId, note.PlanId,
                    note.AmountMinor, note.Currency, BillingPaymentStatus.Succeeded), ct);
        }
        catch (Exception ex)
        {
            // Fulfillment already ran - prefer returning true so the provider does not retry
            // and fulfillment is not called a second time. The host's fulfillment must be
            // idempotent on ProviderPaymentId to guard against any race that does cause a retry.
            _log.LogError(ex,
                "Store upsert failed after fulfillment succeeded for provider={Provider} paymentId={PaymentId}.",
                provider.Name, note.ProviderPaymentId);
        }

        return true;
    }
}
