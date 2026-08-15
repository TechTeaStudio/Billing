namespace TechTeaStudio.Billing.Abstractions;

/// <summary>
/// A pluggable payment provider. This is the seam that makes billing modular - add a
/// new gateway by implementing this interface and registering it in DI; the rest of the app
/// stays provider-agnostic and never references Stripe or YooKassa directly.
/// </summary>
public interface IBillingProvider
{
    /// <summary>Stable lowercase id used in routes, config keys, and the UI (e.g. "stripe", "yookassa").</summary>
    string Name { get; }

    /// <summary>Human-facing label for the pay button (e.g. "Stripe", "YuKassa").</summary>
    string DisplayName { get; }

    /// <summary>False when the provider's API keys are not configured. Such providers are hidden
    /// from the upgrade UI and rejected at checkout, but the app still boots.</summary>
    bool IsConfigured { get; }

    /// <summary>Create a hosted-checkout session and return where to redirect the user to pay.</summary>
    Task<CheckoutSession> CreateCheckoutAsync(BillingCheckoutRequest request, CancellationToken ct = default);

    /// <summary>Authenticate and parse a raw webhook into a normalized notification, or null if it
    /// cannot be trusted or understood (bad signature, unknown event, replay). Implementations
    /// MUST verify authenticity before returning a non-null result.</summary>
    Task<BillingNotification?> ParseNotificationAsync(
        string rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default);
}
