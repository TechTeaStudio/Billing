using TechTeaStudio.Billing.Providers;
using Xunit;

namespace TechTeaStudio.Billing.Tests;

public class TelegramPayloadTests
{
    [Theory]
    [InlineData("plus")]
    [InlineData("pro")]
    [InlineData("annual")]
    [InlineData("Pro")]
    public void BuildPayload_round_trips_via_TryParsePayload(string planId)
    {
        var userId = Guid.NewGuid();
        var payload = TelegramStarsBillingProvider.BuildPayload(userId, planId);

        var ok = TelegramStarsBillingProvider.TryParsePayload(payload, out var parsedUserId, out var parsedPlanId);

        Assert.True(ok);
        Assert.Equal(userId, parsedUserId);
        Assert.Equal(planId, parsedPlanId);
    }

    [Fact]
    public void BuildPayload_stays_within_64_chars_for_short_planId()
    {
        var payload = TelegramStarsBillingProvider.BuildPayload(Guid.NewGuid(), "plus");
        Assert.True(payload.Length <= 64,
            $"Payload '{payload}' is {payload.Length} chars, exceeds Telegram 64-char limit.");
    }

    [Fact]
    public void TryParsePayload_returns_false_for_null()
    {
        Assert.False(TelegramStarsBillingProvider.TryParsePayload(null, out _, out _));
    }

    [Fact]
    public void TryParsePayload_returns_false_for_empty()
    {
        Assert.False(TelegramStarsBillingProvider.TryParsePayload("", out _, out _));
    }

    [Fact]
    public void TryParsePayload_returns_false_for_wrong_prefix()
    {
        var userId = Guid.NewGuid();
        var payload = $"buy-{userId:N}-plus";
        Assert.False(TelegramStarsBillingProvider.TryParsePayload(payload, out _, out _));
    }

    [Fact]
    public void TryParsePayload_returns_false_for_invalid_guid()
    {
        var payload = "pay-notAguid-plus";
        Assert.False(TelegramStarsBillingProvider.TryParsePayload(payload, out _, out _));
    }

    [Fact]
    public void TryParsePayload_returns_false_for_missing_planId()
    {
        var userId = Guid.NewGuid();
        var payload = $"pay-{userId:N}-";
        Assert.False(TelegramStarsBillingProvider.TryParsePayload(payload, out _, out _));
    }
}
