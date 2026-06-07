using Microsoft.Extensions.DependencyInjection;
using TechTeaStudio.Billing.Abstractions;
using TechTeaStudio.Billing.AspNetCore;
using Xunit;

namespace TechTeaStudio.Billing.Tests;

public class OnPaymentSucceededDelegateTests
{
    [Fact]
    public async Task Delegate_is_invoked_with_the_correct_notification()
    {
        BillingNotification? captured = null;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTechTeaStudioBilling()
            .OnPaymentSucceeded(async (sp, note, ct) =>
            {
                captured = note;
                await Task.CompletedTask;
            });

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var fulfillment = scope.ServiceProvider.GetRequiredService<IBillingFulfillment>();

        var expected = new BillingNotification(
            BillingEventKind.PaymentSucceeded, Guid.NewGuid(), "plus", "pay_delegate_001", 299, "RUB");

        await fulfillment.OnPaymentSucceededAsync(expected);

        Assert.NotNull(captured);
        Assert.Equal(expected.PlanId, captured!.PlanId);
        Assert.Equal(expected.UserId, captured.UserId);
        Assert.Equal(expected.ProviderPaymentId, captured.ProviderPaymentId);
    }

    [Fact]
    public void OnPaymentSucceeded_replaces_default_noop_fulfillment()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddTechTeaStudioBilling();
        builder.OnPaymentSucceeded((sp, note, ct) => Task.CompletedTask);

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var fulfillment = scope.ServiceProvider.GetRequiredService<IBillingFulfillment>();

        Assert.DoesNotContain("NoOp", fulfillment.GetType().Name);
    }
}
