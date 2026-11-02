using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TechTeaStudio.Billing.Abstractions;
using TechTeaStudio.Billing.Providers;
using TechTeaStudio.Billing.Services;
using Xunit;

namespace TechTeaStudio.Billing.Tests;

public class PaymentRefundRevocationTests
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    private sealed class ScriptedProvider : IBillingProvider
    {
        public BillingNotification? Next { get; set; }
        public string Name => "fake";
        public string DisplayName => "Fake";
        public bool IsConfigured => true;

        public Task<CheckoutSession> CreateCheckoutAsync(BillingCheckoutRequest request, CancellationToken ct = default) =>
            Task.FromResult(new CheckoutSession("http://fake", Name, "sess"));

        public Task<BillingNotification?> ParseNotificationAsync(
            string rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default) =>
            Task.FromResult(Next);
    }

    private sealed class RevokingFulfillment : IBillingFulfillment, IBillingRefundFulfillment
    {
        public int Grants { get; private set; }
        public int Revocations { get; private set; }
        public int FailuresLeft { get; set; }
        public BillingPaymentStatus? StatusSeenAtRevocation { get; private set; }
        public IBillingPaymentStore? Store { get; set; }

        public Task OnPaymentSucceededAsync(BillingNotification n, CancellationToken ct = default)
        {
            Grants++;
            return Task.CompletedTask;
        }

        public async Task OnPaymentRefundedAsync(BillingNotification n, CancellationToken ct = default)
        {
            if (FailuresLeft-- > 0) throw new InvalidOperationException("db down");
            Revocations++;
            StatusSeenAtRevocation = await Store!.GetStatusAsync("fake", n.ProviderPaymentId, ct);
        }
    }

    private static (BillingService, ScriptedProvider, InMemoryBillingPaymentStore, RevokingFulfillment) Build()
    {
        var provider = new ScriptedProvider();
        var store = new InMemoryBillingPaymentStore();
        var fulfillment = new RevokingFulfillment { Store = store };
        var service = new BillingService(
            new IBillingProvider[] { provider }, store, fulfillment, NullLogger<BillingService>.Instance);
        return (service, provider, store, fulfillment);
    }

    private static BillingNotification Note(BillingEventKind kind) =>
        new(kind, Guid.NewGuid(), "pro", "pay_1", 500, "RUB");

    [Fact]
    public async Task A_refund_revokes_while_the_status_is_still_unflipped()
    {
        var (service, provider, store, fulfillment) = Build();
        provider.Next = Note(BillingEventKind.PaymentSucceeded);
        await service.HandleWebhookAsync("fake", "{}", NoHeaders);

        provider.Next = Note(BillingEventKind.PaymentRefunded);
        Assert.True(await service.HandleWebhookAsync("fake", "{}", NoHeaders));

        Assert.Equal(1, fulfillment.Revocations);
        Assert.Equal(BillingPaymentStatus.Succeeded, fulfillment.StatusSeenAtRevocation);
        Assert.Equal(BillingPaymentStatus.Refunded, await store.GetStatusAsync("fake", "pay_1"));
    }

    [Fact]
    public async Task A_redelivered_refund_revokes_once()
    {
        var (service, provider, _, fulfillment) = Build();
        provider.Next = Note(BillingEventKind.PaymentSucceeded);
        await service.HandleWebhookAsync("fake", "{}", NoHeaders);

        provider.Next = Note(BillingEventKind.PaymentRefunded);
        await service.HandleWebhookAsync("fake", "{}", NoHeaders);
        await service.HandleWebhookAsync("fake", "{}", NoHeaders);

        Assert.Equal(1, fulfillment.Revocations);
    }

    [Fact]
    public async Task A_failed_revocation_stays_retryable()
    {
        var (service, provider, store, fulfillment) = Build();
        provider.Next = Note(BillingEventKind.PaymentSucceeded);
        await service.HandleWebhookAsync("fake", "{}", NoHeaders);

        fulfillment.FailuresLeft = 1;
        provider.Next = Note(BillingEventKind.PaymentRefunded);

        Assert.False(await service.HandleWebhookAsync("fake", "{}", NoHeaders));
        Assert.Equal(BillingPaymentStatus.Succeeded, await store.GetStatusAsync("fake", "pay_1"));

        Assert.True(await service.HandleWebhookAsync("fake", "{}", NoHeaders));
        Assert.Equal(1, fulfillment.Revocations);
        Assert.Equal(BillingPaymentStatus.Refunded, await store.GetStatusAsync("fake", "pay_1"));
    }

    [Fact]
    public async Task A_host_without_the_interface_only_records_the_refund()
    {
        var provider = new ScriptedProvider();
        var store = new InMemoryBillingPaymentStore();
        var service = new BillingService(
            new IBillingProvider[] { provider }, store, new NoOpBillingFulfillment(NullLogger<NoOpBillingFulfillment>.Instance),
            NullLogger<BillingService>.Instance);

        provider.Next = Note(BillingEventKind.PaymentRefunded);

        Assert.True(await service.HandleWebhookAsync("fake", "{}", NoHeaders));
        Assert.Equal(BillingPaymentStatus.Refunded, await store.GetStatusAsync("fake", "pay_1"));
    }

    // ---- Shopify reaches the same path ----------------------------------------------

    private static ShopifyBillingProvider Shopify()
    {
        var opts = new ShopifyBillingOptions { ShopDomain = "x.myshopify.com", AdminAccessToken = "a", ApiSecret = "s" };
        opts.Amounts["plus"] = 199m;
        return new ShopifyBillingProvider(
            new HttpClient(), Options.Create(opts), NullLogger<ShopifyBillingProvider>.Instance);
    }

    private static Dictionary<string, string> ShopifySign(string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("s"));
        return new Dictionary<string, string>
        {
            ["X-Shopify-Hmac-Sha256"] = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))),
        };
    }

    private static string ShopifyOrder(string financial) => JsonSerializer.Serialize(new
    {
        id = 42,
        financial_status = financial,
        currency = "RUB",
        total_price = "199.00",
        note_attributes = new[]
        {
            new { name = "userId", value = Guid.NewGuid().ToString() },
            new { name = "planId", value = "plus" },
        },
    });

    [Fact]
    public async Task A_refunded_shopify_order_reports_the_refund()
    {
        var body = ShopifyOrder("refunded");

        var note = await Shopify().ParseNotificationAsync(body, ShopifySign(body));

        Assert.Equal(BillingEventKind.PaymentRefunded, note!.Kind);
        Assert.Equal("42", note.ProviderPaymentId);
    }

    [Fact]
    public async Task A_partly_refunded_shopify_order_revokes_nothing()
    {
        var body = ShopifyOrder("partially_refunded");

        var note = await Shopify().ParseNotificationAsync(body, ShopifySign(body));

        Assert.Equal(BillingEventKind.Unknown, note!.Kind);
    }
}
