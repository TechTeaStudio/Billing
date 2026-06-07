using TechTeaStudio.Billing.Abstractions;

namespace TechTeaStudio.Billing.Services;

/// <summary>
/// Fulfillment implementation that invokes the delegate registered via
/// <c>IBillingBuilder.OnPaymentSucceeded(...)</c>.
/// </summary>
internal sealed class DelegateBillingFulfillment : IBillingFulfillment
{
    private readonly Func<IServiceProvider, BillingNotification, CancellationToken, Task> _handler;
    private readonly IServiceProvider _sp;

    public DelegateBillingFulfillment(
        Func<IServiceProvider, BillingNotification, CancellationToken, Task> handler,
        IServiceProvider sp)
    {
        _handler = handler;
        _sp = sp;
    }

    public Task OnPaymentSucceededAsync(BillingNotification notification, CancellationToken ct = default) =>
        _handler(_sp, notification, ct);
}
