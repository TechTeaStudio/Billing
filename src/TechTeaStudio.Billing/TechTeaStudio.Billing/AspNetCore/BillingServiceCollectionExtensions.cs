using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TechTeaStudio.Billing.Abstractions;
using TechTeaStudio.Billing.Providers;
using TechTeaStudio.Billing.Services;

namespace TechTeaStudio.Billing.AspNetCore;

/// <summary>
/// Fluent builder returned by <see cref="BillingServiceCollectionExtensions.AddTechTeaStudioBilling"/>.
/// </summary>
public interface IBillingBuilder
{
    IServiceCollection Services { get; }

    IBillingBuilder AddStripe(Action<StripeBillingOptions> configure);
    IBillingBuilder AddYooKassa(Action<YooKassaBillingOptions> configure);
    IBillingBuilder AddShopify(Action<ShopifyBillingOptions> configure);
    IBillingBuilder AddTelegramStars(Action<TelegramBillingOptions> configure);

    /// <summary>Register the host's payment-store implementation.</summary>
    IBillingBuilder UsePaymentStore<TStore>() where TStore : class, IBillingPaymentStore;

    /// <summary>Register the host's fulfillment implementation.</summary>
    IBillingBuilder UseFulfillment<TFulfillment>() where TFulfillment : class, IBillingFulfillment;
}

/// <summary>
/// One-call DI bootstrap for TechTeaStudio.Billing. Registers the orchestrator and returns
/// an <see cref="IBillingBuilder"/> to wire providers and host seams.
/// </summary>
public static class BillingServiceCollectionExtensions
{
    public static IBillingBuilder AddTechTeaStudioBilling(this IServiceCollection services)
    {
        services.AddScoped<IBillingService, BillingService>();
        return new BillingBuilder(services);
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
        Services.AddScoped<IBillingPaymentStore, TStore>();
        return this;
    }

    public IBillingBuilder UseFulfillment<TFulfillment>()
        where TFulfillment : class, IBillingFulfillment
    {
        Services.AddScoped<IBillingFulfillment, TFulfillment>();
        return this;
    }
}
