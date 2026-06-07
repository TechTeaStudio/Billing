using Microsoft.Extensions.Logging;
using TechTeaStudio.Billing.Abstractions;

namespace TechTeaStudio.Billing.Services;

/// <summary>
/// DEVELOPMENT DEFAULT - no-op <see cref="IBillingFulfillment"/> that logs a one-time
/// warning so developers know payments are not being granted.
///
/// Replace this by calling <c>OnPaymentSucceeded(...)</c> or
/// <c>UseFulfillment&lt;T&gt;()</c> on the builder in production.
/// </summary>
internal sealed class NoOpBillingFulfillment : IBillingFulfillment
{
    private readonly ILogger<NoOpBillingFulfillment> _log;
    private static int _warned;

    public NoOpBillingFulfillment(ILogger<NoOpBillingFulfillment> log) => _log = log;

    public Task OnPaymentSucceededAsync(BillingNotification notification, CancellationToken ct = default)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _warned, 1, 0) == 0)
            _log.LogWarning(
                "TechTeaStudio.Billing: a payment succeeded but no fulfillment is configured - " +
                "call OnPaymentSucceeded(...) or UseFulfillment<T>() to grant the purchase.");

        return Task.CompletedTask;
    }
}
