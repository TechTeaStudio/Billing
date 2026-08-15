using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TechTeaStudio.Billing.Abstractions;
using TechTeaStudio.Billing.Providers;
using TechTeaStudio.Billing.Services;
using Xunit;

namespace TechTeaStudio.Billing.Tests;

public class PatreonProviderTests
{
    private const string Secret = "webhook-secret-1";

    private sealed class CountingFulfillment : IBillingFulfillment
    {
        public int CallCount { get; private set; }
        public Task OnPaymentSucceededAsync(BillingNotification n, CancellationToken ct = default)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed record Harness(
        PatreonBillingProvider Provider,
        IExternalPurchaseService External,
        InMemoryExternalIdentityLinkStore Links,
        InMemoryUnclaimedPurchaseStore Unclaimed);

    private static Harness Build(Action<PatreonBillingOptions>? configure = null)
    {
        var opts = new PatreonBillingOptions { WebhookSecret = Secret };
        opts.TierProducts["9012345"] = "pro";
        configure?.Invoke(opts);

        var links = new InMemoryExternalIdentityLinkStore();
        var unclaimed = new InMemoryUnclaimedPurchaseStore();
        var pipeline = new PaymentPipeline(
            new InMemoryBillingPaymentStore(), new CountingFulfillment(),
            NullLogger<PaymentPipeline>.Instance);
        var external = new ExternalPurchaseService(
            new InMemoryClaimCodeStore(), links, unclaimed, pipeline,
            Options.Create(new ExternalPurchaseOptions { ClaimCodePrefix = "CHR" }),
            NullLogger<ExternalPurchaseService>.Instance);
        var provider = new PatreonBillingProvider(
            Options.Create(opts), external, NullLogger<PatreonBillingProvider>.Instance);
        return new Harness(provider, external, links, unclaimed);
    }

    private static string MemberBody(
        string memberId = "mem-1",
        string? patronStatus = "active_patron",
        string? chargeStatus = "Paid",
        string? chargeDate = "2026-08-01T10:00:00.000+00:00",
        long cents = 500,
        string? email = "patron@example.com",
        string[]? tierIds = null,
        string? campaignId = null)
    {
        tierIds ??= ["9012345"];
        var relationships = new Dictionary<string, object?>
        {
            ["user"] = new { data = new { id = "987654321", type = "user" } },
            ["currently_entitled_tiers"] = new
            {
                data = tierIds.Select(t => new { id = t, type = "tier" }).ToArray(),
            },
        };
        if (campaignId is not null)
            relationships["campaign"] = new { data = new { id = campaignId, type = "campaign" } };

        return JsonSerializer.Serialize(new
        {
            data = new
            {
                id = memberId,
                type = "member",
                attributes = new
                {
                    patron_status = patronStatus,
                    last_charge_status = chargeStatus,
                    last_charge_date = chargeDate,
                    currently_entitled_amount_cents = cents,
                    email,
                    full_name = "Test Patron",
                },
                relationships,
            },
            included = Array.Empty<object>(),
        });
    }

    private static IReadOnlyDictionary<string, string> Headers(string body, string trigger) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Patreon-Event"] = trigger,
            ["X-Patreon-Signature"] = PatreonSignature.Compute(body, Secret),
        };

    // ---- Tests -------------------------------------------------------------------

    [Fact]
    public async Task Paid_pledge_with_linked_email_produces_a_purchase()
    {
        var h = Build();
        var userId = Guid.NewGuid();
        await h.Links.LinkAsync("patreon", "patron@example.com", userId);
        var body = MemberBody();

        var note = await h.Provider.ParseNotificationAsync(
            body, Headers(body, "members:pledge:create"));

        Assert.NotNull(note);
        Assert.Equal(BillingEventKind.PaymentSucceeded, note!.Kind);
        Assert.Equal(userId, note.UserId);
        Assert.Equal("pro", note.PlanId);
        Assert.Equal("mem-1:2026-08-01T10:00:00.000+00:00", note.ProviderPaymentId);
        Assert.Equal(500, note.AmountMinor);
        Assert.Equal("USD", note.Currency);
    }

    [Fact]
    public async Task Free_member_created_is_ignored_not_held()
    {
        var h = Build();
        // The docs' own members:create example: a follower with null statuses.
        var body = MemberBody(patronStatus: null, chargeStatus: null, chargeDate: null,
            cents: 0, tierIds: []);

        var note = await h.Provider.ParseNotificationAsync(
            body, Headers(body, "members:create"));

        Assert.Equal(BillingEventKind.Unknown, note!.Kind);
        Assert.Empty(await h.Unclaimed.ListAsync());
    }

    [Fact]
    public async Task Unlinked_paid_pledge_is_held_as_unattributed()
    {
        var h = Build();
        var body = MemberBody();

        var note = await h.Provider.ParseNotificationAsync(
            body, Headers(body, "members:pledge:create"));

        Assert.Equal(BillingEventKind.Unknown, note!.Kind);
        var held = Assert.Single(await h.Unclaimed.ListAsync());
        Assert.Equal("unattributed", held.Reason);
        Assert.Equal("pro", held.Purchase.ProductId);
        Assert.True(held.Purchase.IsFirstSubscriptionPayment);
    }

    [Fact]
    public async Task Declined_charge_is_ignored_during_dunning()
    {
        var h = Build();
        var body = MemberBody(chargeStatus: "Declined");

        var note = await h.Provider.ParseNotificationAsync(
            body, Headers(body, "members:update"));

        Assert.Equal(BillingEventKind.Unknown, note!.Kind);
        Assert.Empty(await h.Unclaimed.ListAsync());
    }

    [Fact]
    public async Task Refund_produces_a_refund_notification_against_the_same_payment_id()
    {
        var h = Build();
        var userId = Guid.NewGuid();
        await h.Links.LinkAsync("patreon", "patron@example.com", userId);
        var body = MemberBody(chargeStatus: "Refunded");

        var note = await h.Provider.ParseNotificationAsync(
            body, Headers(body, "members:update"));

        Assert.Equal(BillingEventKind.PaymentRefunded, note!.Kind);
        Assert.Equal(userId, note.UserId);
        Assert.Equal("mem-1:2026-08-01T10:00:00.000+00:00", note.ProviderPaymentId);
    }

    [Fact]
    public async Task Missing_or_bad_signature_is_rejected()
    {
        var h = Build();
        var body = MemberBody();

        Assert.Null(await h.Provider.ParseNotificationAsync(
            body, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));

        var badHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Patreon-Event"] = "members:update",
            ["X-Patreon-Signature"] = PatreonSignature.Compute(body + "x", Secret),
        };
        Assert.Null(await h.Provider.ParseNotificationAsync(body, badHeaders));
    }

    [Fact]
    public async Task Non_member_triggers_are_ignored()
    {
        var h = Build();
        var body = MemberBody();

        var note = await h.Provider.ParseNotificationAsync(
            body, Headers(body, "posts:publish"));

        Assert.Equal(BillingEventKind.Unknown, note!.Kind);
    }

    [Fact]
    public async Task Entitled_tier_without_mapping_is_held_as_unmapped_product()
    {
        var h = Build();
        var userId = Guid.NewGuid();
        await h.Links.LinkAsync("patreon", "patron@example.com", userId);
        var body = MemberBody(tierIds: ["777"]);

        var note = await h.Provider.ParseNotificationAsync(
            body, Headers(body, "members:pledge:create"));

        Assert.Equal(BillingEventKind.Unknown, note!.Kind);
        var held = Assert.Single(await h.Unclaimed.ListAsync());
        Assert.Equal("unmapped_product", held.Reason);
        Assert.Equal("777", held.Purchase.PlatformProduct);
    }

    [Fact]
    public async Task Foreign_campaign_is_filtered_when_campaign_id_is_set()
    {
        var h = Build(o => o.CampaignId = "111");
        var body = MemberBody(campaignId: "222");

        var note = await h.Provider.ParseNotificationAsync(
            body, Headers(body, "members:pledge:create"));

        Assert.Equal(BillingEventKind.Unknown, note!.Kind);
        Assert.Empty(await h.Unclaimed.ListAsync());
    }

    [Fact]
    public async Task Free_trial_is_ignored_unless_honored()
    {
        var strict = Build();
        var body = MemberBody(chargeStatus: "Free Trial");
        var note = await strict.Provider.ParseNotificationAsync(
            body, Headers(body, "members:update"));
        Assert.Equal(BillingEventKind.Unknown, note!.Kind);

        var lenient = Build(o => o.HonorFreeTrial = true);
        var userId = Guid.NewGuid();
        await lenient.Links.LinkAsync("patreon", "patron@example.com", userId);
        var note2 = await lenient.Provider.ParseNotificationAsync(
            body, Headers(body, "members:update"));
        Assert.Equal(BillingEventKind.PaymentSucceeded, note2!.Kind);
        Assert.Equal(userId, note2.UserId);
    }
}
