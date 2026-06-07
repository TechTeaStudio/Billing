using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TechTeaStudio.Billing.Abstractions;
using TechTeaStudio.Billing.Services;
using Xunit;

namespace TechTeaStudio.Billing.Tests;

public class BillingServiceIdempotencyTests
{
    // ---- Fakes -----------------------------------------------------------------

    private sealed class FakeProvider : IBillingProvider
    {
        private readonly BillingNotification _note;
        public FakeProvider(BillingNotification note) => _note = note;

        public string Name => "fake";
        public string DisplayName => "Fake";
        public bool IsConfigured => true;

        public Task<CheckoutSession> CreateCheckoutAsync(
            BillingCheckoutRequest request, CancellationToken ct = default) =>
            Task.FromResult(new CheckoutSession("http://fake", Name, "sess_fake"));

        public Task<BillingNotification?> ParseNotificationAsync(
            string rawBody, IReadOnlyDictionary<string, string> headers,
            CancellationToken ct = default) =>
            Task.FromResult<BillingNotification?>(_note);
    }

    private sealed class InMemoryPaymentStore : IBillingPaymentStore
    {
        private readonly Dictionary<(string, string), BillingPaymentRecord> _records = new();

        public Task<BillingPaymentStatus?> GetStatusAsync(
            string provider, string providerPaymentId, CancellationToken ct = default)
        {
            var key = (provider, providerPaymentId);
            return Task.FromResult(
                _records.TryGetValue(key, out var r)
                    ? (BillingPaymentStatus?)r.Status
                    : null);
        }

        public Task UpsertAsync(BillingPaymentRecord record, CancellationToken ct = default)
        {
            _records[(record.Provider, record.ProviderPaymentId)] = record;
            return Task.CompletedTask;
        }
    }

    private sealed class CountingFulfillment : IBillingFulfillment
    {
        public int CallCount { get; private set; }
        public BillingNotification? LastNotification { get; private set; }

        public Task OnPaymentSucceededAsync(
            BillingNotification notification, CancellationToken ct = default)
        {
            CallCount++;
            LastNotification = notification;
            return Task.CompletedTask;
        }
    }

    // ---- Helpers ---------------------------------------------------------------

    private static BillingService Build(
        IBillingProvider provider,
        IBillingPaymentStore store,
        IBillingFulfillment fulfillment) =>
        new(
            new[] { provider },
            store,
            fulfillment,
            NullLogger<BillingService>.Instance);

    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new Dictionary<string, string>();

    // ---- Tests -----------------------------------------------------------------

    [Fact]
    public async Task Fulfillment_called_once_for_a_fresh_payment()
    {
        var note = new BillingNotification(
            BillingEventKind.PaymentSucceeded, Guid.NewGuid(), "plus", "pay_123", 500, "USD");
        var store = new InMemoryPaymentStore();
        var fulfillment = new CountingFulfillment();
        var service = Build(new FakeProvider(note), store, fulfillment);

        var result = await service.HandleWebhookAsync("fake", "{}", EmptyHeaders);

        Assert.True(result);
        Assert.Equal(1, fulfillment.CallCount);
    }

    [Fact]
    public async Task Fulfillment_not_called_again_for_duplicate_webhook()
    {
        var note = new BillingNotification(
            BillingEventKind.PaymentSucceeded, Guid.NewGuid(), "plus", "pay_456", 500, "USD");
        var store = new InMemoryPaymentStore();
        var fulfillment = new CountingFulfillment();
        var service = Build(new FakeProvider(note), store, fulfillment);

        var first = await service.HandleWebhookAsync("fake", "{}", EmptyHeaders);
        var second = await service.HandleWebhookAsync("fake", "{}", EmptyHeaders);

        Assert.True(first);
        Assert.True(second);
        Assert.Equal(1, fulfillment.CallCount);
    }

    [Fact]
    public async Task Returns_false_for_unknown_provider()
    {
        var note = new BillingNotification(
            BillingEventKind.PaymentSucceeded, Guid.NewGuid(), "plus", "pay_789", 500, "USD");
        var service = Build(
            new FakeProvider(note),
            new InMemoryPaymentStore(),
            new CountingFulfillment());

        var result = await service.HandleWebhookAsync("stripe", "{}", EmptyHeaders);

        Assert.False(result);
    }

    [Fact]
    public async Task Returns_true_and_records_canceled_payment()
    {
        var note = new BillingNotification(
            BillingEventKind.PaymentCanceled, Guid.NewGuid(), "plus", "pay_cancel", 0, "USD");
        var store = new InMemoryPaymentStore();
        var fulfillment = new CountingFulfillment();
        var service = Build(new FakeProvider(note), store, fulfillment);

        var result = await service.HandleWebhookAsync("fake", "{}", EmptyHeaders);

        Assert.True(result);
        Assert.Equal(0, fulfillment.CallCount);
        var status = await store.GetStatusAsync("fake", "pay_cancel");
        Assert.Equal(BillingPaymentStatus.Canceled, status);
    }

    [Fact]
    public async Task StartCheckout_returns_null_for_empty_planId()
    {
        var note = new BillingNotification(
            BillingEventKind.PaymentSucceeded, Guid.NewGuid(), "plus", "pay_000", 0, "USD");
        var service = Build(
            new FakeProvider(note),
            new InMemoryPaymentStore(),
            new CountingFulfillment());

        var session = await service.StartCheckoutAsync(
            new BillingCheckoutRequest(Guid.NewGuid(), "u@example.com", "", "Plan",
                "http://ret", "http://cancel"),
            "fake");

        Assert.Null(session);
    }
}
