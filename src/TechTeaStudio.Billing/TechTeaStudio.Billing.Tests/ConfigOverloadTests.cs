using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TechTeaStudio.Billing.Abstractions;
using TechTeaStudio.Billing.AspNetCore;
using Xunit;

namespace TechTeaStudio.Billing.Tests;

public class ConfigOverloadTests
{
    private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void YooKassa_provider_is_registered_and_IsConfigured_true_when_section_populated()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Billing:YooKassa:ShopId"] = "shop123",
            ["Billing:YooKassa:SecretKey"] = "secret456",
            ["Billing:YooKassa:Currency"] = "RUB",
            ["Billing:YooKassa:Amounts:plus"] = "299",
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTechTeaStudioBilling(config);

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var providers = scope.ServiceProvider
            .GetRequiredService<IEnumerable<IBillingProvider>>()
            .ToList();

        var yookassa = providers.FirstOrDefault(p => p.Name == "yookassa");
        Assert.NotNull(yookassa);
        Assert.True(yookassa!.IsConfigured);
    }

    [Fact]
    public void Stripe_provider_is_registered_but_IsConfigured_false_when_section_absent()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Billing:YooKassa:ShopId"] = "shop123",
            ["Billing:YooKassa:SecretKey"] = "secret456",
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTechTeaStudioBilling(config);

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var providers = scope.ServiceProvider
            .GetRequiredService<IEnumerable<IBillingProvider>>()
            .ToList();

        var stripe = providers.FirstOrDefault(p => p.Name == "stripe");
        Assert.NotNull(stripe);
        Assert.False(stripe!.IsConfigured);
    }

    [Fact]
    public void Config_overload_uses_custom_section_name()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Payments:YooKassa:ShopId"] = "shop999",
            ["Payments:YooKassa:SecretKey"] = "key999",
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTechTeaStudioBilling(config, sectionName: "Payments");

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var providers = scope.ServiceProvider
            .GetRequiredService<IEnumerable<IBillingProvider>>()
            .ToList();

        var yookassa = providers.FirstOrDefault(p => p.Name == "yookassa");
        Assert.NotNull(yookassa);
        Assert.True(yookassa!.IsConfigured);
    }
}
