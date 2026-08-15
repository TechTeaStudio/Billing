using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TechTeaStudio.Billing.Abstractions;
using TechTeaStudio.Billing.AspNetCore;
using Xunit;

namespace TechTeaStudio.Billing.Tests;

public class ExternalProvidersConfigTests
{
    private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IServiceProvider BuildServices(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTechTeaStudioBilling(BuildConfig(values));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Kofi_is_configured_when_the_verification_token_is_set()
    {
        using var scope = ((ServiceProvider)BuildServices(new Dictionary<string, string?>
        {
            ["Billing:Kofi:VerificationToken"] = "tok-123",
            ["Billing:Kofi:TierProducts:Gold Tier"] = "pro",
        })).CreateScope();

        var kofi = scope.ServiceProvider
            .GetRequiredService<IEnumerable<IBillingProvider>>()
            .Single(p => p.Name == "kofi");
        Assert.True(kofi.IsConfigured);
    }

    [Fact]
    public void Kofi_and_patreon_are_registered_but_dormant_when_sections_are_absent()
    {
        using var scope = ((ServiceProvider)BuildServices(new Dictionary<string, string?>()))
            .CreateScope();

        var providers = scope.ServiceProvider
            .GetRequiredService<IEnumerable<IBillingProvider>>()
            .ToList();

        Assert.False(providers.Single(p => p.Name == "kofi").IsConfigured);
        Assert.False(providers.Single(p => p.Name == "patreon").IsConfigured);
    }

    [Fact]
    public void Patreon_is_configured_when_the_webhook_secret_is_set()
    {
        using var scope = ((ServiceProvider)BuildServices(new Dictionary<string, string?>
        {
            ["Billing:Patreon:WebhookSecret"] = "sec-123",
        })).CreateScope();

        var patreon = scope.ServiceProvider
            .GetRequiredService<IEnumerable<IBillingProvider>>()
            .Single(p => p.Name == "patreon");
        Assert.True(patreon.IsConfigured);
    }

    [Fact]
    public async Task Claim_code_prefix_binds_from_the_external_section()
    {
        using var scope = ((ServiceProvider)BuildServices(new Dictionary<string, string?>
        {
            ["Billing:External:ClaimCodePrefix"] = "CHR",
        })).CreateScope();

        var external = scope.ServiceProvider.GetRequiredService<IExternalPurchaseService>();
        var code = await external.GetClaimCodeAsync(Guid.NewGuid());
        Assert.StartsWith("CHR-", code);
    }

    [Fact]
    public async Task External_purchase_service_resolves_with_defaults_and_no_config()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTechTeaStudioBilling(); // parameterless overload
        using var scope = services.BuildServiceProvider().CreateScope();

        var external = scope.ServiceProvider.GetRequiredService<IExternalPurchaseService>();
        var code = await external.GetClaimCodeAsync(Guid.NewGuid());
        Assert.StartsWith("PAY-", code);
    }
}
