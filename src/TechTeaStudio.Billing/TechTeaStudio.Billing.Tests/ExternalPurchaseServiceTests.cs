using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TechTeaStudio.Billing.Abstractions;
using TechTeaStudio.Billing.Services;
using Xunit;

namespace TechTeaStudio.Billing.Tests;

public class ExternalPurchaseServiceTests
{
    private sealed class CountingFulfillment : IBillingFulfillment
    {
        public int CallCount { get; private set; }
        public BillingNotification? Last { get; private set; }

        public Task OnPaymentSucceededAsync(BillingNotification n, CancellationToken ct = default)
        {
            CallCount++;
            Last = n;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingFulfillment : IBillingFulfillment
    {
        public Task OnPaymentSucceededAsync(BillingNotification n, CancellationToken ct = default) =>
            throw new InvalidOperationException("host db down");
    }

    /// <summary>A host store whose very first call fails - the shape of a flaky database
    /// during an admin claim.</summary>
    private sealed class ThrowingPaymentStore : IBillingPaymentStore
    {
        public Task<BillingPaymentStatus?> GetStatusAsync(
            string provider, string providerPaymentId, CancellationToken ct = default) =>
            throw new InvalidOperationException("db down");

        public Task UpsertAsync(BillingPaymentRecord record, CancellationToken ct = default) =>
            throw new InvalidOperationException("db down");
    }

    private sealed record Harness(
        ExternalPurchaseService Service,
        InMemoryUnclaimedPurchaseStore Unclaimed,
        InMemoryExternalIdentityLinkStore Links,
        CountingFulfillment Fulfillment);

    private static Harness Build(
        IBillingFulfillment? fulfillment = null, IBillingPaymentStore? store = null)
    {
        var counting = new CountingFulfillment();
        var unclaimed = new InMemoryUnclaimedPurchaseStore();
        var links = new InMemoryExternalIdentityLinkStore();
        var pipeline = new PaymentPipeline(
            store ?? new InMemoryBillingPaymentStore(),
            fulfillment ?? counting,
            NullLogger<PaymentPipeline>.Instance);
        var service = new ExternalPurchaseService(
            new InMemoryClaimCodeStore(),
            links,
            unclaimed,
            pipeline,
            Options.Create(new ExternalPurchaseOptions { ClaimCodePrefix = "CHR" }),
            NullLogger<ExternalPurchaseService>.Instance);
        return new Harness(service, unclaimed, links, counting);
    }

    private static ExternalPurchase Purchase(
        string provider = "kofi",
        string paymentId = "tx-1",
        string? productId = "credits100",
        string? email = "buyer@example.com",
        string? message = null) =>
        new(provider, paymentId, paymentId, productId, "Gold Tier", 500, "USD",
            email, "Buyer", message,
            IsSubscriptionPayment: false, IsFirstSubscriptionPayment: false,
            OccurredAt: DateTimeOffset.UnixEpoch);

    // ---- Tests -------------------------------------------------------------------

    [Fact]
    public async Task Claim_code_is_stable_per_user()
    {
        var h = Build();
        var userId = Guid.NewGuid();

        var first = await h.Service.GetClaimCodeAsync(userId);
        var second = await h.Service.GetClaimCodeAsync(userId);

        Assert.Equal(first, second);
        Assert.StartsWith("CHR-", first);
    }

    [Fact]
    public async Task Resolve_prefers_claim_code_and_persists_the_identity_link()
    {
        var h = Build();
        var userId = Guid.NewGuid();
        var code = await h.Service.GetClaimCodeAsync(userId);

        var byCode = await h.Service.ResolveBuyerAsync(
            Purchase(message: $"спасибо! {code}"));
        Assert.Equal(userId, byCode);

        // The next payment has no message - the link created above must carry it.
        var byLink = await h.Service.ResolveBuyerAsync(Purchase(paymentId: "tx-2"));
        Assert.Equal(userId, byLink);
    }

    [Fact]
    public async Task Resolve_returns_null_for_a_stranger()
    {
        var h = Build();

        Assert.Null(await h.Service.ResolveBuyerAsync(Purchase(message: "no code")));
    }

    [Fact]
    public async Task ClaimByEmail_fulfills_held_purchases_exactly_once()
    {
        var h = Build();
        var userId = Guid.NewGuid();
        await h.Service.HoldAsync(Purchase(), "unattributed");

        var first = await h.Service.ClaimByEmailAsync("kofi", "Buyer@Example.com ", userId);
        var second = await h.Service.ClaimByEmailAsync("kofi", "buyer@example.com", userId);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Equal(1, h.Fulfillment.CallCount);
        Assert.Equal(userId, h.Fulfillment.Last!.UserId);
        Assert.Equal("credits100", h.Fulfillment.Last.PlanId);
        Assert.Empty(await h.Unclaimed.ListAsync());
    }

    [Fact]
    public async Task ClaimByEmail_leaves_unmapped_purchases_held()
    {
        var h = Build();
        await h.Service.HoldAsync(Purchase(productId: null), "unmapped_product");

        var granted = await h.Service.ClaimByEmailAsync("kofi", "buyer@example.com", Guid.NewGuid());

        Assert.Equal(0, granted);
        Assert.Equal(0, h.Fulfillment.CallCount);
        Assert.Single(await h.Unclaimed.ListAsync());
    }

    [Fact]
    public async Task Claim_with_product_override_resolves_an_unmapped_hold()
    {
        var h = Build();
        await h.Service.HoldAsync(Purchase(productId: null), "unmapped_product");
        var userId = Guid.NewGuid();

        var ok = await h.Service.ClaimAsync("kofi", "tx-1", userId, productIdOverride: "pro");

        Assert.True(ok);
        Assert.Equal(1, h.Fulfillment.CallCount);
        Assert.Equal("pro", h.Fulfillment.Last!.PlanId);
        Assert.Empty(await h.Unclaimed.ListAsync());
    }

    [Fact]
    public async Task Failed_fulfillment_re_holds_the_purchase()
    {
        var h = Build(new ThrowingFulfillment());
        await h.Service.HoldAsync(Purchase(), "unattributed");

        var granted = await h.Service.ClaimByEmailAsync("kofi", "buyer@example.com", Guid.NewGuid());

        Assert.Equal(0, granted);
        Assert.Single(await h.Unclaimed.ListAsync()); // money is not lost
    }

    [Fact]
    public async Task Manual_submit_is_idempotent_on_payment_id()
    {
        var h = Build();
        var userId = Guid.NewGuid();
        var boosty = Purchase(provider: "boosty", paymentId: "boosty-sub-42", productId: "plus");

        var first = await h.Service.SubmitAsync(boosty, userId);
        var second = await h.Service.SubmitAsync(boosty, userId);

        Assert.True(first);
        Assert.True(second); // duplicate == success, but no second grant
        Assert.Equal(1, h.Fulfillment.CallCount);
    }

    [Fact]
    public async Task Manual_submit_without_product_or_user_is_refused()
    {
        var h = Build();

        Assert.False(await h.Service.SubmitAsync(Purchase(productId: null), Guid.NewGuid()));
        Assert.False(await h.Service.SubmitAsync(Purchase(), Guid.Empty));
        Assert.Equal(0, h.Fulfillment.CallCount);
    }

    [Fact]
    public async Task A_throwing_payment_store_never_swallows_a_held_purchase()
    {
        var h = Build(store: new ThrowingPaymentStore());
        await h.Service.HoldAsync(Purchase(), "unattributed");

        var ok = await h.Service.ClaimAsync("kofi", "tx-1", Guid.NewGuid());

        Assert.False(ok);
        Assert.Single(await h.Unclaimed.ListAsync()); // money still visible to an admin
    }

    [Fact]
    public async Task A_canceled_claim_still_puts_the_purchase_back()
    {
        var h = Build(new ThrowingFulfillment());
        await h.Service.HoldAsync(Purchase(), "unattributed");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ok = await h.Service.ClaimAsync("kofi", "tx-1", Guid.NewGuid(), null, cts.Token);

        Assert.False(ok);
        Assert.Single(await h.Unclaimed.ListAsync());
    }

    [Fact]
    public async Task Admin_claim_persists_the_identity_link_for_later_renewals()
    {
        var h = Build();
        await h.Service.HoldAsync(Purchase(), "unattributed");
        var userId = Guid.NewGuid();

        Assert.True(await h.Service.ClaimAsync("kofi", "tx-1", userId));

        // The next renewal carries no message - only the link can attach it.
        var renewal = await h.Service.ResolveBuyerAsync(Purchase(paymentId: "tx-2"));
        Assert.Equal(userId, renewal);
    }

    [Fact]
    public async Task An_already_fulfilled_payment_is_not_counted_as_a_fresh_grant()
    {
        var h = Build();
        var userId = Guid.NewGuid();
        await h.Service.SubmitAsync(Purchase(), userId);          // fulfilled once
        await h.Service.HoldAsync(Purchase(), "unattributed");     // stale re-hold

        var granted = await h.Service.ClaimByEmailAsync("kofi", "buyer@example.com", userId);

        Assert.Equal(0, granted);                 // nothing new was granted
        Assert.Equal(1, h.Fulfillment.CallCount); // and nothing was granted twice
        Assert.Empty(await h.Unclaimed.ListAsync());
    }

    [Fact]
    public async Task Hold_upserts_by_payment_id_so_retries_do_not_duplicate()
    {
        var h = Build();

        await h.Service.HoldAsync(Purchase(), "unattributed");
        await h.Service.HoldAsync(Purchase(), "unattributed"); // webhook retry

        Assert.Single(await h.Unclaimed.ListAsync());
    }
}
