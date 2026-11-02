using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TechTeaStudio.Billing.Abstractions;
using TechTeaStudio.Billing.Providers;
using Xunit;

namespace TechTeaStudio.Billing.Tests;

public class StripeProviderTests
{
    private const string WebhookSecret = "whsec_test";
    private const string SessionId = "cs_live_1";
    private static readonly Guid User = Guid.Parse("7f3a2c10-1111-4222-8333-444455556666");

    private sealed class ApiStub : HttpMessageHandler
    {
        public string? SessionsResponse { get; set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public List<string> Requested { get; } = [];
        public string? LastForm { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri!.ToString());
            if (request.Method == HttpMethod.Post)
            {
                LastForm = await request.Content!.ReadAsStringAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"id\":\"cs_new\",\"url\":\"https://stripe.example/x\"}"),
                };
            }
            return new HttpResponseMessage(Status) { Content = new StringContent(SessionsResponse ?? "{\"data\":[]}") };
        }
    }

    private static StripeBillingProvider Provider(ApiStub? api = null, string key = "sk_live_key", Action<StripeBillingOptions>? configure = null)
    {
        var opts = new StripeBillingOptions { SecretKey = key, WebhookSecret = WebhookSecret };
        opts.PriceIds["plus"] = "price_plus";
        configure?.Invoke(opts);
        return new StripeBillingProvider(
            new HttpClient(api ?? new ApiStub()), Options.Create(opts), NullLogger<StripeBillingProvider>.Instance);
    }

    private static Dictionary<string, string> Sign(string body)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(WebhookSecret));
        var v1 = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{ts}.{body}"))).ToLowerInvariant();
        return new Dictionary<string, string> { ["Stripe-Signature"] = $"t={ts},v1={v1}" };
    }

    private static object Session(string paymentStatus = "paid") => new
    {
        id = SessionId,
        payment_status = paymentStatus,
        client_reference_id = User.ToString(),
        amount_total = 19900,
        currency = "rub",
        metadata = new { planId = "plus" },
    };

    private static string Event(string type, object obj, bool live = true) =>
        JsonSerializer.Serialize(new { type, livemode = live, data = new { @object = obj } });

    private static Task<BillingNotification?> Parse(StripeBillingProvider provider, string body) =>
        provider.ParseNotificationAsync(body, Sign(body));

    [Fact]
    public void One_off_is_the_default_mode()
    {
        Assert.Equal("payment", new StripeBillingOptions().Mode);
    }

    [Fact]
    public async Task Checkout_asks_stripe_for_a_one_off_payment_by_default()
    {
        var api = new ApiStub();

        await Provider(api).CreateCheckoutAsync(
            new BillingCheckoutRequest(User, "u@e.com", "plus", "Plus", "https://a/r", "https://a/c"));

        Assert.Contains("mode=payment", api.LastForm);
    }

    [Fact]
    public async Task An_unsigned_event_is_refused()
    {
        var body = Event("checkout.session.completed", Session());

        Assert.Null(await Provider().ParseNotificationAsync(body, new Dictionary<string, string>()));
    }

    [Fact]
    public async Task A_paid_session_grants()
    {
        var note = await Parse(Provider(), Event("checkout.session.completed", Session()));

        Assert.Equal(BillingEventKind.PaymentSucceeded, note!.Kind);
        Assert.Equal(SessionId, note.ProviderPaymentId);
        Assert.Equal(User, note.UserId);
    }

    [Fact]
    public async Task A_delayed_method_grants_when_it_settles()
    {
        var pending = await Parse(Provider(), Event("checkout.session.completed", Session("unpaid")));
        var settled = await Parse(Provider(), Event("checkout.session.async_payment_succeeded", Session()));
        var failed = await Parse(Provider(), Event("checkout.session.async_payment_failed", Session("unpaid")));

        Assert.Equal(BillingEventKind.PaymentFailed, pending!.Kind);
        Assert.Equal(BillingEventKind.PaymentSucceeded, settled!.Kind);
        Assert.Equal(BillingEventKind.PaymentFailed, failed!.Kind);
    }

    [Fact]
    public async Task A_test_event_grants_nothing_on_a_live_key()
    {
        var note = await Parse(Provider(), Event("checkout.session.completed", Session(), live: false));

        Assert.Equal(BillingEventKind.Unknown, note!.Kind);
    }

    [Fact]
    public async Task A_test_event_grants_on_a_test_key()
    {
        var note = await Parse(Provider(key: "sk_test_key"), Event("checkout.session.completed", Session(), live: false));

        Assert.Equal(BillingEventKind.PaymentSucceeded, note!.Kind);
    }

    private static string SessionList() => JsonSerializer.Serialize(new { data = new[] { Session() } });

    [Fact]
    public async Task A_full_refund_is_recorded_against_the_checkout_session()
    {
        var api = new ApiStub { SessionsResponse = SessionList() };
        var body = Event("charge.refunded", new { id = "ch_1", refunded = true, payment_intent = "pi_1" });

        var note = await Parse(Provider(api), body);

        Assert.Equal(BillingEventKind.PaymentRefunded, note!.Kind);
        Assert.Equal(SessionId, note.ProviderPaymentId);
        Assert.Equal(User, note.UserId);
        Assert.Contains("payment_intent=pi_1", api.Requested.Single());
    }

    [Fact]
    public async Task A_chargeback_counts_as_a_refund()
    {
        var api = new ApiStub { SessionsResponse = SessionList() };
        var body = Event("charge.dispute.created", new { id = "dp_1", payment_intent = "pi_1" });

        var note = await Parse(Provider(api), body);

        Assert.Equal(BillingEventKind.PaymentRefunded, note!.Kind);
        Assert.Equal(SessionId, note.ProviderPaymentId);
    }

    [Fact]
    public async Task A_partial_refund_revokes_nothing_automatically()
    {
        var api = new ApiStub { SessionsResponse = SessionList() };
        var body = Event("charge.refunded", new { id = "ch_1", refunded = false, payment_intent = "pi_1" });

        var note = await Parse(Provider(api), body);

        Assert.Equal(BillingEventKind.Unknown, note!.Kind);
        Assert.Empty(api.Requested);
    }

    [Fact]
    public async Task A_charge_from_outside_our_checkout_is_ignored()
    {
        var api = new ApiStub { SessionsResponse = "{\"data\":[]}" };
        var body = Event("charge.refunded", new { id = "ch_1", refunded = true, payment_intent = "pi_other" });

        var note = await Parse(Provider(api), body);

        Assert.Equal(BillingEventKind.Unknown, note!.Kind);
    }

    [Fact]
    public async Task A_failed_session_lookup_asks_stripe_to_deliver_again()
    {
        var api = new ApiStub { Status = HttpStatusCode.InternalServerError };
        var body = Event("charge.refunded", new { id = "ch_1", refunded = true, payment_intent = "pi_1" });

        Assert.Null(await Parse(Provider(api), body));
    }
}
