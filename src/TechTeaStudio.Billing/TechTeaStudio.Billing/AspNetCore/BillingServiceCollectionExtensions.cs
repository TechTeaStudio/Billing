using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TechTeaStudio.Billing.Abstractions;
using TechTeaStudio.Billing.Providers;
using TechTeaStudio.Billing.Services;

namespace TechTeaStudio.Billing.AspNetCore;

/// <summary>
/// Fluent builder returned by <see cref="BillingServiceCollectionExtensions"/>.
/// </summary>
public interface IBillingBuilder
{
    IServiceCollection Services { get; }

    IBillingBuilder AddStripe(Action<StripeBillingOptions> configure);
    IBillingBuilder AddYooKassa(Action<YooKassaBillingOptions> configure);
    IBillingBuilder AddShopify(Action<ShopifyBillingOptions> configure);
    IBillingBuilder AddTelegramStars(Action<TelegramBillingOptions> configure);

    /// <summary>Register the host's payment-store implementation, replacing the default in-memory store.</summary>
    IBillingBuilder UsePaymentStore<TStore>() where TStore : class, IBillingPaymentStore;

    /// <summary>Register the host's fulfillment implementation, replacing the default no-op.</summary>
    IBillingBuilder UseFulfillment<TFulfillment>() where TFulfillment : class, IBillingFulfillment;

    /// <summary>
    /// Register an inline fulfillment delegate, replacing the default no-op. The simplest
    /// path for apps that do not need a dedicated <see cref="IBillingFulfillment"/> class.
    /// <example>
    /// <code>
    /// builder.Services
    ///     .AddTechTeaStudioBilling(builder.Configuration)
    ///     .OnPaymentSucceeded(async (sp, note, ct) =>
    ///     {
    ///         // note.PlanId, note.UserId, note.AmountMinor - grant the purchase here
    ///     });
    /// </code>
    /// </example>
    /// </summary>
    IBillingBuilder OnPaymentSucceeded(
        Func<IServiceProvider, BillingNotification, CancellationToken, Task> handler);
}

/// <summary>
/// One-call DI bootstrap for TechTeaStudio.Billing. Registers the orchestrator and returns
/// an <see cref="IBillingBuilder"/> to wire providers and host seams.
/// </summary>
public static class BillingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the billing orchestrator with sensible defaults (in-memory payment store,
    /// no-op fulfillment). Use the returned builder to wire providers and host seams.
    /// </summary>
    public static IBillingBuilder AddTechTeaStudioBilling(this IServiceCollection services)
    {
        services.AddScoped<IBillingService, BillingService>();
        services.TryAddSingleton<IBillingPaymentStore, InMemoryBillingPaymentStore>();
        services.TryAddScoped<IBillingFulfillment, NoOpBillingFulfillment>();
        return new BillingBuilder(services);
    }

    /// <summary>
    /// Registers the billing orchestrator and binds all four provider option classes from
    /// <paramref name="config"/> sub-sections, so a consumer only needs to fill in the
    /// provider sections they use in <c>appsettings.json</c>. Providers whose config section
    /// is absent or empty remain registered but return <c>IsConfigured == false</c> and are
    /// hidden from the upgrade UI.
    /// </summary>
    /// <param name="services">The application service collection.</param>
    /// <param name="config">The root configuration (e.g. <c>builder.Configuration</c>).</param>
    /// <param name="sectionName">
    /// The top-level configuration section that contains the provider sub-sections.
    /// Defaults to <c>"Billing"</c>.
    /// </param>
    /// <example>
    /// <code>
    /// // appsettings.json
    /// "Billing": {
    ///   "YooKassa": { "ShopId": "...", "SecretKey": "...", "Currency": "RUB", "Amounts": { "plus": 299 } },
    ///   "Telegram": { "BotToken": "...", "BotUsername": "mybot", "WebhookSecret": "..." }
    /// }
    ///
    /// // Program.cs
    /// builder.Services
    ///     .AddTechTeaStudioBilling(builder.Configuration)
    ///     .OnPaymentSucceeded(async (sp, note, ct) => { /* grant purchase */ });
    /// </code>
    /// </example>
    public static IBillingBuilder AddTechTeaStudioBilling(
        this IServiceCollection services,
        IConfiguration config,
        string sectionName = "Billing")
    {
        var builder = services.AddTechTeaStudioBilling();

        builder.AddStripe(o => config.GetSection($"{sectionName}:Stripe").Bind(o));
        builder.AddYooKassa(o => config.GetSection($"{sectionName}:YooKassa").Bind(o));
        builder.AddShopify(o => config.GetSection($"{sectionName}:Shopify").Bind(o));
        builder.AddTelegramStars(o => config.GetSection($"{sectionName}:Telegram").Bind(o));

        return builder;
    }
}

internal sealed class BillingBuilder : IBillingBuilder
{
    public BillingBuilder(IServiceCollection services) => Services = services;
    public IServiceCollection Services { get; }

    public IBillingBuilder AddStripe(Action<StripeBillingOptions> configure)
    {
        Services.Configure(configure);
        Services.AddHttpClient<StripeBillingProvider>();
        Services.AddScoped<IBillingProvider>(
            sp => sp.GetRequiredService<StripeBillingProvider>());
        return this;
    }

    public IBillingBuilder AddYooKassa(Action<YooKassaBillingOptions> configure)
    {
        Services.Configure(configure);
        Services.AddHttpClient<YooKassaBillingProvider>();
        Services.AddScoped<IBillingProvider>(
            sp => sp.GetRequiredService<YooKassaBillingProvider>());
        return this;
    }

    public IBillingBuilder AddShopify(Action<ShopifyBillingOptions> configure)
    {
        Services.Configure(configure);
        Services.AddHttpClient<ShopifyBillingProvider>();
        Services.AddScoped<IBillingProvider>(
            sp => sp.GetRequiredService<ShopifyBillingProvider>());
        return this;
    }

    public IBillingBuilder AddTelegramStars(Action<TelegramBillingOptions> configure)
    {
        Services.Configure(configure);
        Services.AddHttpClient<TelegramStarsBillingProvider>();
        Services.AddScoped<IBillingProvider>(
            sp => sp.GetRequiredService<TelegramStarsBillingProvider>());
        Services.AddScoped<ITelegramBot>(
            sp => sp.GetRequiredService<TelegramStarsBillingProvider>());
        return this;
    }

    public IBillingBuilder UsePaymentStore<TStore>() where TStore : class, IBillingPaymentStore
    {
        Services.RemoveAll<IBillingPaymentStore>();
        Services.AddScoped<IBillingPaymentStore, TStore>();
        return this;
    }

    public IBillingBuilder UseFulfillment<TFulfillment>()
        where TFulfillment : class, IBillingFulfillment
    {
        Services.RemoveAll<IBillingFulfillment>();
        Services.AddScoped<IBillingFulfillment, TFulfillment>();
        return this;
    }

    public IBillingBuilder OnPaymentSucceeded(
        Func<IServiceProvider, BillingNotification, CancellationToken, Task> handler)
    {
        Services.AddSingleton(handler);
        Services.RemoveAll<IBillingFulfillment>();
        Services.AddScoped<IBillingFulfillment, DelegateBillingFulfillment>();
        return this;
    }
}
