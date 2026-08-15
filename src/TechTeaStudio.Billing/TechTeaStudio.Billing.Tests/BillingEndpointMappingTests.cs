using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TechTeaStudio.Billing.Abstractions;
using TechTeaStudio.Billing.AspNetCore;
using Xunit;

namespace TechTeaStudio.Billing.Tests;

/// <summary>
/// Covers the HTTP surface of <see cref="BillingEndpointRouteBuilderExtensions"/> by pulling the
/// real <see cref="RequestDelegate"/> off the mapped endpoint and invoking it.
///
/// These exist because the handlers were converted from minimal-API lambdas to RequestDelegates
/// so the package could target net5.0/net6.0 (the Results helpers and the Delegate overload of
/// MapPost are .NET 6+). That conversion moved two things off the framework and into our code -
/// reading the {provider} route value, and setting the status code - so both are asserted here.
/// </summary>
public class BillingEndpointMappingTests
{
    [Fact]
    public async Task Webhook_route_passes_the_provider_segment_body_and_headers_through()
    {
        var billing = new RecordingBillingService(accept: true);
        var ctx = NewContext(Services(billing), body: "{\"event\":\"payment.succeeded\"}");
        ctx.Request.Headers["Stripe-Signature"] = "t=1,v1=abc";

        await InvokeAsync(MapAndFind("/billing/webhook/{provider}"), ctx, provider: "stripe");

        Assert.Equal("stripe", billing.Provider);
        Assert.Equal("{\"event\":\"payment.succeeded\"}", billing.Body);
        Assert.Equal("t=1,v1=abc", billing.Headers!["Stripe-Signature"]);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Webhook_headers_are_case_insensitive_so_providers_can_look_them_up_verbatim()
    {
        var billing = new RecordingBillingService(accept: true);
        var ctx = NewContext(Services(billing), body: "{}");
        ctx.Request.Headers["x-shopify-hmac-sha256"] = "base64hmac";

        await InvokeAsync(MapAndFind("/billing/webhook/{provider}"), ctx, provider: "shopify");

        // ShopifyBillingProvider asks for the canonical casing; the request sent it lowercase.
        Assert.Equal("base64hmac", billing.Headers!["X-Shopify-Hmac-Sha256"]);
    }

    [Fact]
    public async Task Webhook_returns_400_when_the_orchestrator_rejects_the_payload()
    {
        // A rejected payload means failed authentication or an unparsable body. 400 is what
        // tells the gateway the delivery was bad rather than that it should keep retrying.
        var billing = new RecordingBillingService(accept: false);
        var ctx = NewContext(Services(billing), body: "forged");

        await InvokeAsync(MapAndFind("/billing/webhook/{provider}"), ctx, provider: "stripe");

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Telegram_route_returns_404_when_no_bot_is_registered()
    {
        // No AddTelegramStars() means no ITelegramBot, so the endpoint must not pretend to work.
        var ctx = NewContext(Services(new RecordingBillingService(accept: true)), body: "{}");

        await InvokeAsync(MapAndFind("/billing/telegram/bot"), ctx);

        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
    }

    [Fact]
    public void Base_path_is_honoured_for_every_mapped_route()
    {
        var routes = Map("/pay")
            .Select(e => e.RoutePattern.RawText)
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "/pay/telegram/bot", "/pay/webhook/{provider}" }, routes);
    }

    // ---- Harness ----------------------------------------------------------------

    private static ServiceProvider Services(IBillingService billing)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(billing);
        return services.BuildServiceProvider();
    }

    private static List<RouteEndpoint> Map(string basePath = "/billing")
    {
        var builder = new CapturingEndpointRouteBuilder(Services(new RecordingBillingService(true)));
        builder.MapTechTeaStudioBilling(basePath);
        return builder.DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();
    }

    private static RouteEndpoint MapAndFind(string rawRoute) =>
        Map(rawRoute.StartsWith("/billing", StringComparison.Ordinal) ? "/billing" : "/pay")
            .Single(e => e.RoutePattern.RawText == rawRoute);

    private static DefaultHttpContext NewContext(IServiceProvider sp, string body)
    {
        var ctx = new DefaultHttpContext { RequestServices = sp };
        ctx.Request.Method = HttpMethods.Post;
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static Task InvokeAsync(RouteEndpoint endpoint, HttpContext ctx, string? provider = null)
    {
        if (provider is not null) ctx.Request.RouteValues["provider"] = provider;
        return endpoint.RequestDelegate!(ctx);
    }

    /// <summary>The minimum <see cref="IEndpointRouteBuilder"/> that MapPost needs, keeping the
    /// endpoints in memory so a test can invoke them without an HTTP server.</summary>
    private sealed class CapturingEndpointRouteBuilder : IEndpointRouteBuilder
    {
        public CapturingEndpointRouteBuilder(IServiceProvider sp) => ServiceProvider = sp;
        public IServiceProvider ServiceProvider { get; }
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    private sealed class RecordingBillingService : IBillingService
    {
        private readonly bool _accept;
        public RecordingBillingService(bool accept) => _accept = accept;

        public string? Provider { get; private set; }
        public string? Body { get; private set; }
        public IReadOnlyDictionary<string, string>? Headers { get; private set; }

        public IReadOnlyList<BillingProviderInfo> AvailableProviders() => [];

        public Task<CheckoutSession?> StartCheckoutAsync(
            BillingCheckoutRequest request, string providerName, CancellationToken ct = default) =>
            Task.FromResult<CheckoutSession?>(null);

        public Task<bool> HandleWebhookAsync(
            string providerName, string rawBody,
            IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
        {
            Provider = providerName;
            Body = rawBody;
            Headers = headers;
            return Task.FromResult(_accept);
        }
    }
}
