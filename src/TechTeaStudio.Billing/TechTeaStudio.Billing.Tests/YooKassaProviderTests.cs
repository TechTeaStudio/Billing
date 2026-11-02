using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TechTeaStudio.Billing.Abstractions;
using TechTeaStudio.Billing.Providers;
using TechTeaStudio.Billing.Services;
using Xunit;

namespace TechTeaStudio.Billing.Tests;

/// <summary>
/// The YooKassa webhook is unsigned and anyone can post one, so every test drives the real provider
/// against a stand-in for the YooKassa API that answers only exact URLs, as the real one does.
/// </summary>
public class YooKassaProviderTests
{
    private const string PaymentId = "22e12f66-000f-5000-8000-18db351245c7";
    private const string RefundId = "216749f7-0016-50be-b000-078d43a63ae4";
    private static readonly Guid User = Guid.Parse("7f3a2c10-1111-4222-8333-444455556666");
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    // ---- Harness ----------------------------------------------------------------

    private sealed class ApiStub : HttpMessageHandler
    {
        public Dictionary<string, string> Responses { get; } = new(StringComparer.Ordinal);
        public List<string> Requested { get; } = [];
        public string? LastPostBody { get; private set; }
        public string? PostResponse { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.GetLeftPart(UriPartial.Query);
            Requested.Add(url);
            if (request.Method == HttpMethod.Post)
            {
                LastPostBody = await request.Content!.ReadAsStringAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(PostResponse ?? "{}", Encoding.UTF8, "application/json"),
                };
            }
            return Responses.TryGetValue(url, out var json)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static string PaymentUrl(string id = PaymentId) => $"https://api.yookassa.ru/v3/payments/{id}";
    private static string RefundUrl(string id = RefundId) => $"https://api.yookassa.ru/v3/refunds/{id}";

    private static YooKassaBillingProvider Provider(ApiStub api, Action<YooKassaBillingOptions>? configure = null)
    {
        var opts = new YooKassaBillingOptions { ShopId = "shop", SecretKey = "live_secret" };
        opts.Amounts["plus"] = 199m;
        configure?.Invoke(opts);
        return new YooKassaBillingProvider(
            new HttpClient(api), Options.Create(opts), NullLogger<YooKassaBillingProvider>.Instance);
    }

    private static string Payment(
        string id = PaymentId, string status = "succeeded", bool paid = true, string value = "199.00",
        string currency = "RUB", string? refunded = null, bool test = false, string? userId = null,
        string planId = "plus") =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["status"] = status,
            ["paid"] = paid,
            ["test"] = test,
            ["amount"] = new { value, currency },
            ["refunded_amount"] = refunded is null ? null : new { value = refunded, currency },
            ["metadata"] = new { userId = userId ?? User.ToString(), planId },
        });

    private static string Webhook(string id, string eventName = "payment.succeeded") =>
        JsonSerializer.Serialize(new { type = "notification", @event = eventName, @object = new { id } });

    private sealed class CountingFulfillment : IBillingFulfillment
    {
        public int CallCount { get; private set; }
        public Task OnPaymentSucceededAsync(BillingNotification n, CancellationToken ct = default)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }

    // ---- The id is untrusted --------------------------------------------------------

    [Theory]
    [InlineData(PaymentId + "#1")]
    [InlineData(PaymentId + "?x=1")]
    [InlineData("./" + PaymentId)]
    [InlineData(PaymentId + "/")]
    [InlineData("../payments/" + PaymentId)]
    [InlineData(" " + PaymentId)]
    [InlineData(PaymentId + " ")]
    [InlineData(PaymentId + "\n")]
    [InlineData("22E12F66-000F-5000-8000-18DB351245C7")]
    [InlineData("22e12f66000f5000800018db351245c7")]
    [InlineData("")]
    public async Task A_disguised_id_is_refused_before_any_api_call(string id)
    {
        var api = new ApiStub();
        api.Responses[PaymentUrl()] = Payment();

        var note = await Provider(api).ParseNotificationAsync(Webhook(id), NoHeaders);

        Assert.Null(note);
        Assert.Empty(api.Requested);
    }

    [Fact]
    public async Task The_notification_carries_the_id_the_api_returned()
    {
        var api = new ApiStub();
        api.Responses[PaymentUrl()] = Payment();

        var note = await Provider(api).ParseNotificationAsync(Webhook(PaymentId), NoHeaders);

        Assert.Equal(BillingEventKind.PaymentSucceeded, note!.Kind);
        Assert.Equal(PaymentId, note.ProviderPaymentId);
        Assert.Equal(User, note.UserId);
        Assert.Equal("plus", note.PlanId);
    }

    [Fact]
    public async Task An_api_answer_about_another_payment_is_refused()
    {
        var api = new ApiStub();
        api.Responses[PaymentUrl()] = Payment(id: RefundId);

        Assert.Null(await Provider(api).ParseNotificationAsync(Webhook(PaymentId), NoHeaders));
    }

    [Fact]
    public async Task One_payment_replayed_in_every_disguise_grants_once_end_to_end()
    {
        var api = new ApiStub();
        api.Responses[PaymentUrl()] = Payment();
        var fulfillment = new CountingFulfillment();
        var service = new BillingService(
            new IBillingProvider[] { Provider(api) }, new InMemoryBillingPaymentStore(), fulfillment,
            NullLogger<BillingService>.Instance);

        foreach (var id in new[]
                 {
                     PaymentId, PaymentId + "#1", PaymentId + "#2", "./" + PaymentId, " " + PaymentId,
                     PaymentId + "?a=1", "22E12F66-000F-5000-8000-18DB351245C7", PaymentId,
                 })
        {
            await service.HandleWebhookAsync("yookassa", Webhook(id), NoHeaders);
        }

        Assert.Equal(1, fulfillment.CallCount);
    }

    // ---- What counts as paid --------------------------------------------------------

    [Fact]
    public async Task The_event_name_in_the_body_cannot_cancel_a_paid_payment()
    {
        var api = new ApiStub();
        api.Responses[PaymentUrl()] = Payment();

        var note = await Provider(api).ParseNotificationAsync(Webhook(PaymentId, "payment.canceled"), NoHeaders);

        Assert.Equal(BillingEventKind.PaymentSucceeded, note!.Kind);
    }

    [Fact]
    public async Task The_event_name_in_the_body_cannot_make_a_pending_payment_paid()
    {
        var api = new ApiStub();
        api.Responses[PaymentUrl()] = Payment(status: "pending", paid: false);

        var note = await Provider(api).ParseNotificationAsync(Webhook(PaymentId), NoHeaders);

        Assert.Equal(BillingEventKind.Unknown, note!.Kind);
    }

    [Fact]
    public async Task A_canceled_payment_reports_canceled()
    {
        var api = new ApiStub();
        api.Responses[PaymentUrl()] = Payment(status: "canceled", paid: false);

        var note = await Provider(api).ParseNotificationAsync(Webhook(PaymentId, "payment.canceled"), NoHeaders);

        Assert.Equal(BillingEventKind.PaymentCanceled, note!.Kind);
    }

    [Theory]
    [InlineData("198.99", "RUB", "plus")]
    [InlineData("199.00", "USD", "plus")]
    [InlineData("199.00", "RUB", "unpriced")]
    public async Task A_payment_that_does_not_cover_its_plan_grants_nothing(string value, string currency, string plan)
    {
        var api = new ApiStub();
        api.Responses[PaymentUrl()] = Payment(value: value, currency: currency, planId: plan);

        var note = await Provider(api).ParseNotificationAsync(Webhook(PaymentId), NoHeaders);

        Assert.Equal(BillingEventKind.Unknown, note!.Kind);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task A_payment_without_a_buyer_grants_nothing(string userId)
    {
        var api = new ApiStub();
        api.Responses[PaymentUrl()] = Payment(userId: userId);

        var note = await Provider(api).ParseNotificationAsync(Webhook(PaymentId), NoHeaders);

        Assert.Equal(BillingEventKind.Unknown, note!.Kind);
    }

    [Fact]
    public async Task A_test_payment_is_refused_on_a_live_key()
    {
        var api = new ApiStub();
        api.Responses[PaymentUrl()] = Payment(test: true);

        var note = await Provider(api).ParseNotificationAsync(Webhook(PaymentId), NoHeaders);

        Assert.Equal(BillingEventKind.Unknown, note!.Kind);
    }

    [Fact]
    public async Task A_test_payment_is_accepted_on_a_test_key()
    {
        var api = new ApiStub();
        api.Responses[PaymentUrl()] = Payment(test: true);

        var note = await Provider(api, o => o.SecretKey = "test_secret")
            .ParseNotificationAsync(Webhook(PaymentId), NoHeaders);

        Assert.Equal(BillingEventKind.PaymentSucceeded, note!.Kind);
    }

    // ---- Refunds ----------------------------------------------------------------------

    [Fact]
    public async Task A_refund_event_resolves_the_payment_through_the_api()
    {
        var api = new ApiStub();
        api.Responses[RefundUrl()] = JsonSerializer.Serialize(
            new { id = RefundId, status = "succeeded", payment_id = PaymentId });
        api.Responses[PaymentUrl()] = Payment(refunded: "199.00");

        var note = await Provider(api).ParseNotificationAsync(Webhook(RefundId, "refund.succeeded"), NoHeaders);

        Assert.Equal(BillingEventKind.PaymentRefunded, note!.Kind);
        Assert.Equal(PaymentId, note.ProviderPaymentId);
        Assert.Equal(User, note.UserId);
    }

    [Fact]
    public async Task A_refund_event_naming_a_payment_id_finds_no_refund()
    {
        var api = new ApiStub();
        api.Responses[PaymentUrl()] = Payment();

        Assert.Null(await Provider(api).ParseNotificationAsync(Webhook(PaymentId, "refund.succeeded"), NoHeaders));
    }

    [Fact]
    public async Task A_partial_refund_revokes_nothing_automatically()
    {
        var api = new ApiStub();
        api.Responses[RefundUrl()] = JsonSerializer.Serialize(
            new { id = RefundId, status = "succeeded", payment_id = PaymentId });
        api.Responses[PaymentUrl()] = Payment(refunded: "50.00");

        var note = await Provider(api).ParseNotificationAsync(Webhook(RefundId, "refund.succeeded"), NoHeaders);

        Assert.Equal(BillingEventKind.Unknown, note!.Kind);
    }

    [Fact]
    public async Task A_success_webhook_replayed_after_a_full_refund_reports_the_refund()
    {
        var api = new ApiStub();
        api.Responses[PaymentUrl()] = Payment(refunded: "199.00");

        var note = await Provider(api).ParseNotificationAsync(Webhook(PaymentId), NoHeaders);

        Assert.Equal(BillingEventKind.PaymentRefunded, note!.Kind);
    }

    // ---- Receipts ---------------------------------------------------------------------

    private const string CreatedPayment =
        "{\"id\":\"" + PaymentId + "\",\"confirmation\":{\"confirmation_url\":\"https://pay.example/x\"}}";

    private static BillingCheckoutRequest Checkout(string email = "buyer@example.com") =>
        new(User, email, "plus", "Plus", "https://app/return", "https://app/cancel");

    [Fact]
    public async Task No_receipt_is_sent_by_default()
    {
        var api = new ApiStub { PostResponse = CreatedPayment };

        await Provider(api).CreateCheckoutAsync(Checkout());

        using var body = JsonDocument.Parse(api.LastPostBody!);
        Assert.False(body.RootElement.TryGetProperty("receipt", out _));
        Assert.Equal("199.00", body.RootElement.GetProperty("amount").GetProperty("value").GetString());
    }

    [Fact]
    public async Task A_receipt_carries_the_buyer_and_the_price_when_turned_on()
    {
        var api = new ApiStub { PostResponse = CreatedPayment };

        await Provider(api, o => o.SendReceipt = true).CreateCheckoutAsync(Checkout());

        using var body = JsonDocument.Parse(api.LastPostBody!);
        var receipt = body.RootElement.GetProperty("receipt");
        Assert.Equal("buyer@example.com", receipt.GetProperty("customer").GetProperty("email").GetString());
        var line = receipt.GetProperty("items")[0];
        Assert.Equal("199.00", line.GetProperty("amount").GetProperty("value").GetString());
        Assert.Equal(1, line.GetProperty("vat_code").GetInt32());
    }

    [Fact]
    public async Task A_receipt_without_an_email_refuses_the_checkout()
    {
        var api = new ApiStub { PostResponse = CreatedPayment };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Provider(api, o => o.SendReceipt = true).CreateCheckoutAsync(Checkout(email: "")));
        Assert.Empty(api.Requested);
    }
}
