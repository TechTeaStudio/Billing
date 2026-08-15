using TechTeaStudio.Billing;
using Xunit;

namespace TechTeaStudio.Billing.Tests;

public class BillingPlanGuardTests
{
    private static Func<string, string?> Config(Dictionary<string, string?> values) =>
        key => values.TryGetValue(key, out var v) ? v : null;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    public void Telegram_plan_without_a_real_stars_price_is_not_purchasable(string? stars)
    {
        var config = Config(new() { ["Billing:Telegram:Plans:plus:Stars"] = stars });

        Assert.False(BillingPlanGuard.IsPurchasable(config, "telegram", "plus"));
    }

    [Fact]
    public void Telegram_plan_with_a_real_price_is_purchasable()
    {
        var config = Config(new() { ["Billing:Telegram:Plans:plus:Stars"] = "450" });

        Assert.True(BillingPlanGuard.IsPurchasable(config, "telegram", "plus"));
    }

    [Fact]
    public void Plans_are_guarded_independently()
    {
        var config = Config(new()
        {
            ["Billing:Telegram:Plans:plus:Stars"] = "450",
            ["Billing:Telegram:Plans:pro:Stars"] = "0",
        });

        Assert.True(BillingPlanGuard.IsPurchasable(config, "telegram", "plus"));
        Assert.False(BillingPlanGuard.IsPurchasable(config, "telegram", "pro"));
    }

    [Fact]
    public void Provider_name_is_case_insensitive()
    {
        var config = Config(new() { ["Billing:Telegram:Plans:plus:Stars"] = "450" });

        Assert.True(BillingPlanGuard.IsPurchasable(config, "  TELEGRAM ", "plus"));
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData("299", true)]
    [InlineData("299.50", true)]
    public void YooKassa_requires_a_positive_amount(string amount, bool expected)
    {
        var config = Config(new() { ["Billing:YooKassa:Amounts:plus"] = amount });

        Assert.Equal(expected, BillingPlanGuard.IsPurchasable(config, "yookassa", "plus"));
    }

    [Fact]
    public void Shopify_accepts_fractional_and_rejects_missing_amounts()
    {
        var priced = Config(new() { ["Billing:Shopify:Amounts:plus"] = "6.99" });
        var missing = Config(new());

        Assert.True(BillingPlanGuard.IsPurchasable(priced, "shopify", "plus"));
        Assert.False(BillingPlanGuard.IsPurchasable(missing, "shopify", "plus"));
    }

    [Fact]
    public void Stripe_requires_a_non_blank_price_id()
    {
        var priced = Config(new() { ["Billing:Stripe:PriceIds:plus"] = "price_123" });
        var blank = Config(new() { ["Billing:Stripe:PriceIds:plus"] = "  " });

        Assert.True(BillingPlanGuard.IsPurchasable(priced, "stripe", "plus"));
        Assert.False(BillingPlanGuard.IsPurchasable(blank, "stripe", "plus"));
    }

    [Fact]
    public void Unknown_provider_fails_open()
    {
        // A new integration without a price key must not be silently killed; external
        // platforms (kofi/patreon/boosty) price on their own side by design.
        var config = Config(new());

        Assert.True(BillingPlanGuard.IsPurchasable(config, "kofi", "plus"));
        Assert.True(BillingPlanGuard.IsPurchasable(config, "patreon", "plus"));
        Assert.True(BillingPlanGuard.IsPurchasable(config, "somefuturegateway", "plus"));
        Assert.Null(BillingPlanGuard.PriceKeyFor("kofi", "plus"));
    }

    [Fact]
    public void PriceKeyFor_states_the_exact_config_keys()
    {
        Assert.Equal("Billing:Telegram:Plans:plus:Stars",
            BillingPlanGuard.PriceKeyFor("telegram", "plus"));
        Assert.Equal("Billing:YooKassa:Amounts:pro",
            BillingPlanGuard.PriceKeyFor("yookassa", "pro"));
        Assert.Equal("Billing:Shopify:Amounts:plusyear",
            BillingPlanGuard.PriceKeyFor("shopify", "plusyear"));
        Assert.Equal("Billing:Stripe:PriceIds:proyear",
            BillingPlanGuard.PriceKeyFor("stripe", "proyear"));
    }
}
