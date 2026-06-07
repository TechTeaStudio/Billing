using Microsoft.Extensions.DependencyInjection;
using TechTeaStudio.Billing.Abstractions;
using TechTeaStudio.Billing.AspNetCore;
using Xunit;

namespace TechTeaStudio.Billing.Tests;

public class InMemoryBillingPaymentStoreTests
{
    private static IBillingPaymentStore BuildStore()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTechTeaStudioBilling();
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IBillingPaymentStore>();
    }

    [Fact]
    public async Task GetStatus_returns_null_when_absent()
    {
        var store = BuildStore();
        var result = await store.GetStatusAsync("fake", "pay_missing");
        Assert.Null(result);
    }

    [Fact]
    public async Task Upsert_then_get_round_trips_status()
    {
        var store = BuildStore();
        var record = new BillingPaymentRecord(
            Guid.NewGuid(), "fake", "pay_123", "plus", 500, "USD", BillingPaymentStatus.Succeeded);

        await store.UpsertAsync(record);
        var result = await store.GetStatusAsync("fake", "pay_123");

        Assert.Equal(BillingPaymentStatus.Succeeded, result);
    }

    [Fact]
    public async Task Upsert_overwrites_existing_status()
    {
        var store = BuildStore();
        var pending = new BillingPaymentRecord(
            Guid.NewGuid(), "fake", "pay_456", "plus", 500, "USD", BillingPaymentStatus.Pending);
        var succeeded = pending with { Status = BillingPaymentStatus.Succeeded };

        await store.UpsertAsync(pending);
        await store.UpsertAsync(succeeded);

        var result = await store.GetStatusAsync("fake", "pay_456");
        Assert.Equal(BillingPaymentStatus.Succeeded, result);
    }
}
