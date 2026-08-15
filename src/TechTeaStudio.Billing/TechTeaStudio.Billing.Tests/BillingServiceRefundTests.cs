using Microsoft.Extensions.Logging.Abstractions;
using TechTeaStudio.Billing.Abstractions;
using TechTeaStudio.Billing.Services;
using Xunit;

namespace TechTeaStudio.Billing.Tests;

public class BillingServiceRefundTests
{
    private sealed class MutableProvider : IBillingProvider
    {
        public BillingNotification? Next { get; set; }
        public string Name => "fake";
        public string DisplayName => "Fake";
        public bool IsConfigured => true;

        public Task<CheckoutSession> CreateCheckoutAsync(
            BillingCheckoutRequest request, CancellationToken ct = default) =>
            Task.FromResult(new CheckoutSession("http://fake", Name, "sess_fake"));

        public Task<BillingNotification?> ParseNotificationAsync(
            string rawBody, IReadOnlyDictionary<string, string> headers,
            CancellationToken ct = default) =>
            Task.FromResult(Next);
    }

    private sealed class CountingFulfillment : IBillingFulfillment
    {
        public int CallCount { get; private set; }
        public Task OnPaymentSucceededAsync(BillingNotification n, CancellationToken ct = default)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }

    /// <summary>Holds every caller inside fulfillment until all of them have arrived, so a
    /// pipeline that reserves non-atomically would let more than one through.</summary>
    private sealed class SlowCountingFulfillment : IBillingFulfillment
    {
        private int _count;
        public int CallCount => Volatile.Read(ref _count);

        public async Task OnPaymentSucceededAsync(
            BillingNotification n, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _count);
            await Task.Delay(50, ct);
        }
    }

    private sealed class ThrowingProvider : IBillingProvider
    {
        public string Name => "throwy";
        public string DisplayName => "Throwy";
        public bool IsConfigured => true;

        public Task<CheckoutSession> CreateCheckoutAsync(
            BillingCheckoutRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("no page url configured");

        public Task<BillingNotification?> ParseNotificationAsync(
            string rawBody, IReadOnlyDictionary<string, string> headers,
            CancellationToken ct = default) =>
            Task.FromResult<BillingNotification?>(null);
    }

    private static readonly IReadOnlyDictionary<string, string> NoHeaders =
        new Dictionary<string, string>();

    [Fact]
    public async Task Refund_after_success_marks_refunded_and_blocks_refulfillment()
    {
        var userId = Guid.NewGuid();
        var provider = new MutableProvider();
        var store = new InMemoryBillingPaymentStore();
        var fulfillment = new CountingFulfillment();
        var service = new BillingService(
            new[] { (IBillingProvider)provider }, store, fulfillment,
            NullLogger<BillingService>.Instance);

        provider.Next = new BillingNotification(
            BillingEventKind.PaymentSucceeded, userId, "pro", "pay_1", 500, "USD");
        Assert.True(await service.HandleWebhookAsync("fake", "{}", NoHeaders));
        Assert.Equal(1, fulfillment.CallCount);

        provider.Next = new BillingNotification(
            BillingEventKind.PaymentRefunded, userId, "pro", "pay_1", 500, "USD");
        Assert.True(await service.HandleWebhookAsync("fake", "{}", NoHeaders));
        Assert.Equal(BillingPaymentStatus.Refunded, await store.GetStatusAsync("fake", "pay_1"));

        // A redelivered success webhook for a refunded payment must never grant again.
        provider.Next = new BillingNotification(
            BillingEventKind.PaymentSucceeded, userId, "pro", "pay_1", 500, "USD");
        Assert.True(await service.HandleWebhookAsync("fake", "{}", NoHeaders));
        Assert.Equal(1, fulfillment.CallCount);
        Assert.Equal(BillingPaymentStatus.Refunded, await store.GetStatusAsync("fake", "pay_1"));
    }

    [Fact]
    public async Task Refund_that_overtakes_its_own_success_leaves_a_tombstone()
    {
        // Providers redeliver out of order: the success can be retried AFTER the refund
        // lands. Without a tombstone the late success would fulfill refunded money.
        var userId = Guid.NewGuid();
        var provider = new MutableProvider();
        var store = new InMemoryBillingPaymentStore();
        var fulfillment = new CountingFulfillment();
        var service = new BillingService(
            new[] { (IBillingProvider)provider }, store, fulfillment,
            NullLogger<BillingService>.Instance);

        provider.Next = new BillingNotification(
            BillingEventKind.PaymentRefunded, userId, "pro", "pay_early", 500, "USD");
        Assert.True(await service.HandleWebhookAsync("fake", "{}", NoHeaders));
        Assert.Equal(BillingPaymentStatus.Refunded, await store.GetStatusAsync("fake", "pay_early"));

        provider.Next = new BillingNotification(
            BillingEventKind.PaymentSucceeded, userId, "pro", "pay_early", 500, "USD");
        Assert.True(await service.HandleWebhookAsync("fake", "{}", NoHeaders));
        Assert.Equal(0, fulfillment.CallCount);
        Assert.Equal(BillingPaymentStatus.Refunded, await store.GetStatusAsync("fake", "pay_early"));
    }

    [Fact]
    public async Task Unattributed_refund_is_recorded_without_erasing_the_original_details()
    {
        var userId = Guid.NewGuid();
        var provider = new MutableProvider();
        var store = new InMemoryBillingPaymentStore();
        var fulfillment = new CountingFulfillment();
        var service = new BillingService(
            new[] { (IBillingProvider)provider }, store, fulfillment,
            NullLogger<BillingService>.Instance);

        provider.Next = new BillingNotification(
            BillingEventKind.PaymentSucceeded, userId, "pro", "pay_2", 500, "USD");
        await service.HandleWebhookAsync("fake", "{}", NoHeaders);

        // A fully refunded membership reports no user, no plan and zero cents - the stored
        // record must still say WHO bought WHAT, because that is what the host revokes.
        provider.Next = new BillingNotification(
            BillingEventKind.PaymentRefunded, Guid.Empty, "", "pay_2", 0, "USD");
        Assert.True(await service.HandleWebhookAsync("fake", "{}", NoHeaders));

        Assert.Equal(BillingPaymentStatus.Refunded, await store.GetStatusAsync("fake", "pay_2"));
        var stored = await store.FindAsync("fake", "pay_2");
        Assert.Equal(userId, stored!.UserId);
        Assert.Equal("pro", stored.PlanId);
        Assert.Equal(500, stored.AmountMinor);
    }

    [Fact]
    public async Task Concurrent_first_deliveries_fulfill_exactly_once()
    {
        var provider = new MutableProvider
        {
            Next = new BillingNotification(
                BillingEventKind.PaymentSucceeded, Guid.NewGuid(), "pro", "pay_race", 500, "USD"),
        };
        var store = new InMemoryBillingPaymentStore();
        var fulfillment = new SlowCountingFulfillment();
        var service = new BillingService(
            new[] { (IBillingProvider)provider }, store, fulfillment,
            NullLogger<BillingService>.Instance);

        // Both deliveries reach the pipeline while neither has finished fulfilling.
        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => service.HandleWebhookAsync("fake", "{}", NoHeaders)));

        Assert.All(results, Assert.True);
        Assert.Equal(1, fulfillment.CallCount);
    }

    [Fact]
    public async Task Late_cancel_never_overwrites_a_succeeded_payment()
    {
        var userId = Guid.NewGuid();
        var provider = new MutableProvider();
        var store = new InMemoryBillingPaymentStore();
        var service = new BillingService(
            new[] { (IBillingProvider)provider }, store, new CountingFulfillment(),
            NullLogger<BillingService>.Instance);

        provider.Next = new BillingNotification(
            BillingEventKind.PaymentSucceeded, userId, "pro", "pay_3", 500, "USD");
        await service.HandleWebhookAsync("fake", "{}", NoHeaders);

        provider.Next = new BillingNotification(
            BillingEventKind.PaymentCanceled, userId, "pro", "pay_3", 500, "USD");
        Assert.True(await service.HandleWebhookAsync("fake", "{}", NoHeaders));

        Assert.Equal(BillingPaymentStatus.Succeeded, await store.GetStatusAsync("fake", "pay_3"));
    }

    [Fact]
    public async Task Duplicate_provider_registrations_do_not_break_webhooks()
    {
        var provider = new MutableProvider
        {
            Next = new BillingNotification(
                BillingEventKind.PaymentSucceeded, Guid.NewGuid(), "pro", "pay_dup", 500, "USD"),
        };
        var fulfillment = new CountingFulfillment();
        // AddTechTeaStudioBilling called twice registers every provider twice.
        var service = new BillingService(
            new[] { (IBillingProvider)provider, provider },
            new InMemoryBillingPaymentStore(), fulfillment,
            NullLogger<BillingService>.Instance);

        Assert.True(await service.HandleWebhookAsync("fake", "{}", NoHeaders));
        Assert.Equal(1, fulfillment.CallCount);
        Assert.Single(service.AvailableProviders());
    }

    [Fact]
    public async Task Checkout_refused_by_the_provider_returns_null_instead_of_throwing()
    {
        var service = new BillingService(
            new[] { (IBillingProvider)new ThrowingProvider() },
            new InMemoryBillingPaymentStore(),
            new CountingFulfillment(),
            NullLogger<BillingService>.Instance);

        var session = await service.StartCheckoutAsync(
            new BillingCheckoutRequest(Guid.NewGuid(), "u@e.com", "plus", "Plus", "r", "c"),
            "throwy");

        Assert.Null(session);
    }
}
