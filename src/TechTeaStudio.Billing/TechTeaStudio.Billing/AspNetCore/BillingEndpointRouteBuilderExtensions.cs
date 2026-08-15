using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TechTeaStudio.Billing.Abstractions;

namespace TechTeaStudio.Billing.AspNetCore;

/// <summary>
/// Extension methods that map the provider-agnostic billing HTTP endpoints into an
/// <see cref="IEndpointRouteBuilder"/>.
///
/// The handlers are plain <see cref="RequestDelegate"/>s rather than minimal-API lambdas.
/// That overload of <c>MapPost</c> has existed since ASP.NET Core 3.0, which is what lets
/// this package target net5.0 alongside net10.0 from one source file; the minimal-API
/// <c>Delegate</c> overload and the <c>Results</c> helpers only arrived in .NET 6. Nothing
/// is lost here - these are webhook receivers that return a bare status code and never
/// model-bind, so parameter binding and typed results have no work to do.
/// </summary>
public static class BillingEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the reusable billing inbound endpoints:
    /// <list type="bullet">
    /// <item><c>POST {basePath}/webhook/{provider}</c> - provider webhook receiver.</item>
    /// <item><c>POST {basePath}/telegram/bot</c> - Telegram Bot API update receiver (Stars lifecycle).</item>
    /// </list>
    /// Returns <paramref name="endpoints"/> for chaining.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="basePath">
    /// Route prefix for all mapped endpoints. Defaults to <c>"/billing"</c>.
    /// </param>
    public static IEndpointRouteBuilder MapTechTeaStudioBilling(
        this IEndpointRouteBuilder endpoints,
        string basePath = "/billing")
    {
        // The casts pin the RequestDelegate overload: from .NET 6 on there is also a
        // MapPost(..., Delegate) overload, and a bare method group is convertible to both.
        endpoints.MapPost($"{basePath}/webhook/{{provider}}", (RequestDelegate)HandleWebhookAsync);
        endpoints.MapPost($"{basePath}/telegram/bot", (RequestDelegate)HandleTelegramUpdateAsync);
        return endpoints;
    }

    // POST {basePath}/webhook/{provider}
    // The provider authenticates the payload internally (HMAC / re-fetch / secret-token).
    // Returns 200 on acceptance (including duplicate) so the gateway stops retrying;
    // 400 only on failed authentication or parse.
    private static async Task HandleWebhookAsync(HttpContext ctx)
    {
        var billing = ctx.RequestServices.GetRequiredService<IBillingService>();

        var provider = ctx.Request.RouteValues.TryGetValue("provider", out var routeValue)
            ? routeValue?.ToString() ?? ""
            : "";

        var body = await ReadBodyAsync(ctx);
        var headers = ReadHeaders(ctx);

        var ok = await billing.HandleWebhookAsync(provider, body, headers, ctx.RequestAborted);
        ctx.Response.StatusCode = ok
            ? StatusCodes.Status200OK
            : StatusCodes.Status400BadRequest;
    }

    // POST {basePath}/telegram/bot
    // Telegram posts Bot API updates here (registered via setWebhook). Drives the Stars
    // lifecycle: approve pre_checkout_query, send the XTR invoice on "/start <payload>",
    // and on successful_payment route back through IBillingService to grant the tier.
    // Always returns 200 so Telegram stops retrying; forged updates are dropped by
    // the webhook-secret check.
    private static async Task HandleTelegramUpdateAsync(HttpContext ctx)
    {
        var billing = ctx.RequestServices.GetRequiredService<IBillingService>();
        var bot = ctx.RequestServices.GetService<ITelegramBot>();

        if (bot is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status200OK;

        var body = await ReadBodyAsync(ctx);
        var headers = ReadHeaders(ctx);

        if (!bot.IsConfigured || !bot.VerifyWebhookToken(headers)) return;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("pre_checkout_query", out var pcq)
                && pcq.TryGetProperty("id", out var pcqId))
            {
                await bot.AnswerPreCheckoutAsync(pcqId.GetString() ?? "", ctx.RequestAborted);
                return;
            }

            if (root.TryGetProperty("message", out var msg))
            {
                if (msg.TryGetProperty("successful_payment", out _))
                {
                    await billing.HandleWebhookAsync("telegram", body, headers, ctx.RequestAborted);
                    return;
                }

                if (msg.TryGetProperty("text", out var txt)
                    && msg.TryGetProperty("chat", out var chat)
                    && chat.TryGetProperty("id", out var chatId))
                {
                    var text = txt.GetString() ?? "";
                    if (text.StartsWith("/start", StringComparison.Ordinal))
                    {
                        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        var payload = parts.Length == 2 ? parts[1].Trim() : "";
                        if (!string.IsNullOrEmpty(payload))
                            await bot.SendInvoiceAsync(chatId.GetInt64(), payload, ctx.RequestAborted);
                    }
                }
            }
        }
        catch { /* malformed update - ack anyway so Telegram stops retrying */ }
    }

    private static async Task<string> ReadBodyAsync(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body);
#if NET7_0_OR_GREATER
        return await reader.ReadToEndAsync(ctx.RequestAborted);
#else
        // StreamReader.ReadToEndAsync(CancellationToken) only exists from .NET 7 on.
        // The request body stream still observes ctx.RequestAborted underneath, so an
        // aborted request faults the read rather than hanging.
        return await reader.ReadToEndAsync();
#endif
    }

    private static Dictionary<string, string> ReadHeaders(HttpContext ctx) =>
        ctx.Request.Headers.ToDictionary(
            h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
}
