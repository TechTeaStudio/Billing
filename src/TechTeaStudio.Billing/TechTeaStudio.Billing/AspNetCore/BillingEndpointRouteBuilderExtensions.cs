using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TechTeaStudio.Billing.Abstractions;

namespace TechTeaStudio.Billing.AspNetCore;

/// <summary>
/// Extension methods that map the provider-agnostic billing HTTP endpoints into a
/// minimal-API <see cref="IEndpointRouteBuilder"/>.
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
        // POST {basePath}/webhook/{provider}
        // The provider authenticates the payload internally (HMAC / re-fetch / secret-token).
        // Returns 200 on acceptance (including duplicate) so the gateway stops retrying;
        // 400 only on failed authentication or parse.
        endpoints.MapPost($"{basePath}/webhook/{{provider}}", async (
            HttpContext ctx,
            string provider,
            IBillingService billing) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ctx.RequestAborted);
            var headers = ctx.Request.Headers.ToDictionary(
                h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

            var ok = await billing.HandleWebhookAsync(provider, body, headers, ctx.RequestAborted);
            return ok ? Results.Ok() : Results.BadRequest();
        });

        // POST {basePath}/telegram/bot
        // Telegram posts Bot API updates here (registered via setWebhook). Drives the Stars
        // lifecycle: approve pre_checkout_query, send the XTR invoice on "/start <payload>",
        // and on successful_payment route back through IBillingService to grant the tier.
        // Always returns 200 so Telegram stops retrying; forged updates are dropped by
        // the webhook-secret check.
        endpoints.MapPost($"{basePath}/telegram/bot", async (HttpContext ctx) =>
        {
            var billing = ctx.RequestServices.GetRequiredService<IBillingService>();
            var bot = ctx.RequestServices.GetService<ITelegramBot>();

            if (bot is null) return Results.NotFound();

            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ctx.RequestAborted);
            var headers = ctx.Request.Headers.ToDictionary(
                h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

            if (!bot.IsConfigured || !bot.VerifyWebhookToken(headers)) return Results.Ok();

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.TryGetProperty("pre_checkout_query", out var pcq)
                    && pcq.TryGetProperty("id", out var pcqId))
                {
                    await bot.AnswerPreCheckoutAsync(pcqId.GetString() ?? "", ctx.RequestAborted);
                    return Results.Ok();
                }

                if (root.TryGetProperty("message", out var msg))
                {
                    if (msg.TryGetProperty("successful_payment", out _))
                    {
                        await billing.HandleWebhookAsync("telegram", body, headers, ctx.RequestAborted);
                        return Results.Ok();
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

            return Results.Ok();
        });

        return endpoints;
    }
}
