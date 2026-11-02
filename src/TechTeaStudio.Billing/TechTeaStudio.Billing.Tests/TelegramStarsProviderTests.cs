using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TechTeaStudio.Billing.Abstractions;
using TechTeaStudio.Billing.AspNetCore;
using TechTeaStudio.Billing.Providers;
using Xunit;

namespace TechTeaStudio.Billing.Tests;

public class TelegramStarsProviderTests
{
    private const string Secret = "webhook-secret";
    private static readonly Guid User = Guid.Parse("7f3a2c10-1111-4222-8333-444455556666");
    private static readonly string Payload = TelegramStarsBillingProvider.BuildPayload(User, "plus");

    private static readonly IReadOnlyDictionary<string, string> Signed =
        new Dictionary<string, string> { ["X-Telegram-Bot-Api-Secret-Token"] = Secret };

    private sealed class BotApiStub : HttpMessageHandler
    {
        public List<(string Url, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.RequestUri!.ToString(), await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"result\":true}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private static TelegramStarsBillingProvider Provider(BotApiStub? api = null)
    {
        var opts = new TelegramBillingOptions
        {
            BotToken = "123:token",
            BotUsername = "bot",
            WebhookSecret = Secret,
            PreCheckoutDeclinedMessage = "declined",
        };
        opts.Plans["plus"] = new TelegramStarsPlan(133, "Plus", "Plus");
        return new TelegramStarsBillingProvider(
            new HttpClient(api ?? new BotApiStub()), Options.Create(opts),
            NullLogger<TelegramStarsBillingProvider>.Instance);
    }

    private static string PaymentUpdate(
        string field = "successful_payment", string charge = "charge_1", long amount = 133, string? payload = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["message"] = new Dictionary<string, object>
            {
                [field] = new
                {
                    currency = "XTR",
                    total_amount = amount,
                    invoice_payload = payload ?? Payload,
                    telegram_payment_charge_id = charge,
                },
            },
        });

    // ---- Webhook parsing ----------------------------------------------------------

    [Fact]
    public async Task A_signed_payment_grants_the_plan()
    {
        var note = await Provider().ParseNotificationAsync(PaymentUpdate(), Signed);

        Assert.Equal(BillingEventKind.PaymentSucceeded, note!.Kind);
        Assert.Equal(User, note.UserId);
        Assert.Equal("plus", note.PlanId);
        Assert.Equal("charge_1", note.ProviderPaymentId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("webhook-secre")]
    [InlineData("webhook-secret2")]
    [InlineData("WEBHOOK-SECRET")]
    public async Task A_wrong_secret_is_refused(string? secret)
    {
        var headers = new Dictionary<string, string>();
        if (secret is not null) headers["X-Telegram-Bot-Api-Secret-Token"] = secret;

        Assert.Null(await Provider().ParseNotificationAsync(PaymentUpdate(), headers));
    }

    [Fact]
    public async Task A_payment_without_a_charge_id_grants_nothing()
    {
        var note = await Provider().ParseNotificationAsync(PaymentUpdate(charge: ""), Signed);

        Assert.Equal(BillingEventKind.Unknown, note!.Kind);
    }

    [Fact]
    public async Task Stars_taken_at_the_previous_price_still_grant_what_was_paid_for()
    {
        var note = await Provider().ParseNotificationAsync(PaymentUpdate(amount: 100), Signed);

        Assert.Equal(BillingEventKind.PaymentSucceeded, note!.Kind);
    }

    [Fact]
    public async Task A_refunded_payment_update_reports_the_refund_under_the_same_charge()
    {
        var note = await Provider().ParseNotificationAsync(PaymentUpdate(field: "refunded_payment"), Signed);

        Assert.Equal(BillingEventKind.PaymentRefunded, note!.Kind);
        Assert.Equal("charge_1", note.ProviderPaymentId);
        Assert.Equal(User, note.UserId);
    }

    // ---- Pre-checkout: the last moment to stop a charge --------------------------------

    private static async Task<JsonDocument> AnswerAsync(long amount, string? currency = "XTR", string? payload = null)
    {
        var api = new BotApiStub();
        await Provider(api).AnswerPreCheckoutAsync("q1", payload ?? Payload, amount, currency);
        var call = Assert.Single(api.Calls);
        Assert.EndsWith("/answerPreCheckoutQuery", call.Url);
        return JsonDocument.Parse(call.Body);
    }

    [Fact]
    public async Task The_current_price_is_approved()
    {
        using var answer = await AnswerAsync(133);

        Assert.True(answer.RootElement.GetProperty("ok").GetBoolean());
    }

    [Theory]
    [InlineData(100, "XTR")]
    [InlineData(150, "XTR")]
    [InlineData(133, "USD")]
    [InlineData(133, null)]
    public async Task A_stale_invoice_is_declined_before_money_moves(long amount, string? currency)
    {
        using var answer = await AnswerAsync(amount, currency);

        Assert.False(answer.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("declined", answer.RootElement.GetProperty("error_message").GetString());
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("pay-00000000000000000000000000000000-gone")]
    public async Task An_invoice_for_no_current_plan_is_declined(string payload)
    {
        using var answer = await AnswerAsync(133, payload: payload);

        Assert.False(answer.RootElement.GetProperty("ok").GetBoolean());
    }

    // ---- The bot endpoint --------------------------------------------------------------

    private sealed class StubBilling : IBillingService
    {
        public bool Accept { get; init; } = true;
        public Exception? Throw { get; init; }
        public int Calls { get; private set; }

        public IReadOnlyList<BillingProviderInfo> AvailableProviders() => [];

        public Task<CheckoutSession?> StartCheckoutAsync(
            BillingCheckoutRequest request, string providerName, CancellationToken ct = default) =>
            Task.FromResult<CheckoutSession?>(null);

        public Task<bool> HandleWebhookAsync(
            string providerName, string rawBody, IReadOnlyDictionary<string, string> headers,
            CancellationToken ct = default)
        {
            Calls++;
            return Throw is null ? Task.FromResult(Accept) : Task.FromException<bool>(Throw);
        }
    }

    private sealed class CapturingRoutes(IServiceProvider sp) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = sp;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    private static async Task<int> PostAsync(IBillingService billing, string body, string secret = Secret, BotApiStub? api = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(billing);
        services.AddSingleton<ITelegramBot>(Provider(api));
        var sp = services.BuildServiceProvider();

        var routes = new CapturingRoutes(sp);
        routes.MapTechTeaStudioBilling();
        var endpoint = routes.DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/billing/telegram/bot");

        var ctx = new DefaultHttpContext { RequestServices = sp };
        ctx.Request.Method = HttpMethods.Post;
        ctx.Request.Headers["X-Telegram-Bot-Api-Secret-Token"] = secret;
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        await endpoint.RequestDelegate!(ctx);
        return ctx.Response.StatusCode;
    }

    [Fact]
    public async Task A_processed_payment_answers_200()
    {
        var billing = new StubBilling();

        Assert.Equal(200, await PostAsync(billing, PaymentUpdate()));
        Assert.Equal(1, billing.Calls);
    }

    [Fact]
    public async Task A_payment_whose_fulfilment_failed_answers_500_so_telegram_redelivers()
    {
        Assert.Equal(500, await PostAsync(new StubBilling { Accept = false }, PaymentUpdate()));
    }

    [Fact]
    public async Task A_payment_that_threw_answers_500_so_telegram_redelivers()
    {
        var billing = new StubBilling { Throw = new InvalidOperationException("db down") };

        Assert.Equal(500, await PostAsync(billing, PaymentUpdate()));
    }

    [Fact]
    public async Task A_refund_update_reaches_the_billing_service()
    {
        var billing = new StubBilling();

        Assert.Equal(200, await PostAsync(billing, PaymentUpdate(field: "refunded_payment")));
        Assert.Equal(1, billing.Calls);
    }

    [Fact]
    public async Task A_forged_update_is_dropped_with_200()
    {
        var billing = new StubBilling();

        Assert.Equal(200, await PostAsync(billing, PaymentUpdate(), secret: "wrong"));
        Assert.Equal(0, billing.Calls);
    }

    [Fact]
    public async Task Malformed_json_answers_200_so_telegram_stops()
    {
        Assert.Equal(200, await PostAsync(new StubBilling(), "{not json"));
    }

    [Fact]
    public async Task The_endpoint_passes_the_query_amount_to_the_pre_checkout_answer()
    {
        var api = new BotApiStub();
        var update = JsonSerializer.Serialize(new
        {
            pre_checkout_query = new { id = "q1", currency = "XTR", total_amount = 100, invoice_payload = Payload },
        });

        await PostAsync(new StubBilling(), update, api: api);

        using var answer = JsonDocument.Parse(Assert.Single(api.Calls).Body);
        Assert.False(answer.RootElement.GetProperty("ok").GetBoolean());
    }
}
