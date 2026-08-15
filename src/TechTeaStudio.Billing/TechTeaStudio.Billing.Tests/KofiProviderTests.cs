using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TechTeaStudio.Billing.Abstractions;
using TechTeaStudio.Billing.Providers;
using TechTeaStudio.Billing.Services;
using Xunit;

namespace TechTeaStudio.Billing.Tests;

public class KofiProviderTests
{
    private const string Token = "3401eedb-7a5e-4ceb-aa43-40038281222f";

    // ---- Harness: real attribution service over in-memory stores -----------------

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

    private sealed record Harness(
        KofiBillingProvider Provider,
        IExternalPurchaseService External,
        InMemoryUnclaimedPurchaseStore Unclaimed,
        CountingFulfillment Fulfillment);

    private static Harness Build(Action<KofiBillingOptions>? configure = null)
    {
        var opts = new KofiBillingOptions { VerificationToken = Token };
        configure?.Invoke(opts);

        var fulfillment = new CountingFulfillment();
        var unclaimed = new InMemoryUnclaimedPurchaseStore();
        var pipeline = new PaymentPipeline(
            new InMemoryBillingPaymentStore(), fulfillment,
            NullLogger<PaymentPipeline>.Instance);
        var external = new ExternalPurchaseService(
            new InMemoryClaimCodeStore(),
            new InMemoryExternalIdentityLinkStore(),
            unclaimed,
            pipeline,
            Options.Create(new ExternalPurchaseOptions { ClaimCodePrefix = "CHR" }),
            NullLogger<ExternalPurchaseService>.Instance);
        var provider = new KofiBillingProvider(
            Options.Create(opts), external, NullLogger<KofiBillingProvider>.Instance);
        return new Harness(provider, external, unclaimed, fulfillment);
    }

    /// <summary>Ko-fi's wire format: form-urlencoded body with a single "data" field
    /// carrying the URL-encoded JSON (exactly how Ko-fi's own test fixtures build it).</summary>
    private static string Body(object payload) =>
        "data=" + Uri.EscapeDataString(JsonSerializer.Serialize(payload));

    private static object Donation(
        string? message, string email = "jo.example@example.com",
        string amount = "3.00", string txid = "00000000-1111-2222-3333-444444444444") => new
    {
        verification_token = Token,
        message_id = "b1ac68e2-42e0-4a9a-bf43-ceee37d786d1",
        timestamp = "2024-11-22T13:23:35Z",
        type = "Donation",
        is_public = true,
        from_name = "Jo Example",
        message,
        amount,
        url = $"https://ko-fi.com/Home/CoffeeShop?txid={txid}",
        email,
        currency = "USD",
        is_subscription_payment = false,
        is_first_subscription_payment = false,
        kofi_transaction_id = txid,
        shop_items = (object?)null,
        tier_name = (string?)null,
        shipping = (object?)null,
    };

    private static readonly IReadOnlyDictionary<string, string> NoHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // ---- Tests -------------------------------------------------------------------

    [Fact]
    public async Task Donation_with_claim_code_is_attributed_and_mapped()
    {
        var h = Build(o => o.DonationProductId = "credits100");
        var userId = Guid.NewGuid();
        var code = await h.External.GetClaimCodeAsync(userId);

        var note = await h.Provider.ParseNotificationAsync(
            Body(Donation($"Удачи с интеграцией! {code}")), NoHeaders);

        Assert.NotNull(note);
        Assert.Equal(BillingEventKind.PaymentSucceeded, note!.Kind);
        Assert.Equal(userId, note.UserId);
        Assert.Equal("credits100", note.PlanId);
        Assert.Equal("00000000-1111-2222-3333-444444444444", note.ProviderPaymentId);
        Assert.Equal(300, note.AmountMinor);
        Assert.Equal("USD", note.Currency);
    }

    [Fact]
    public async Task Wrong_verification_token_is_rejected()
    {
        var h = Build(o => o.DonationProductId = "credits100");
        var payload = Donation("hi");
        var json = JsonSerializer.Serialize(payload).Replace(Token, "not-the-token");

        var note = await h.Provider.ParseNotificationAsync(
            "data=" + Uri.EscapeDataString(json), NoHeaders);

        Assert.Null(note);
    }

    [Fact]
    public async Task Body_without_data_field_is_rejected()
    {
        var h = Build();

        Assert.Null(await h.Provider.ParseNotificationAsync("foo=bar", NoHeaders));
        Assert.Null(await h.Provider.ParseNotificationAsync("", NoHeaders));
    }

    [Fact]
    public async Task Invalid_json_in_data_field_is_rejected()
    {
        var h = Build();

        var note = await h.Provider.ParseNotificationAsync(
            "data=" + Uri.EscapeDataString("{not json"), NoHeaders);

        Assert.Null(note);
    }

    [Fact]
    public async Task Unknown_event_type_is_accepted_and_ignored()
    {
        var h = Build(o => o.DonationProductId = "credits100");
        var payload = JsonSerializer.Serialize(Donation("hi")).Replace(
            "\"type\":\"Donation\"", "\"type\":\"Referral\"");

        var note = await h.Provider.ParseNotificationAsync(
            "data=" + Uri.EscapeDataString(payload), NoHeaders);

        Assert.NotNull(note);
        Assert.Equal(BillingEventKind.Unknown, note!.Kind);
        Assert.Empty(await h.Unclaimed.ListAsync());
    }

    [Fact]
    public async Task Plain_donation_with_no_product_configured_is_ignored_not_held()
    {
        var h = Build(); // no DonationProductId

        var note = await h.Provider.ParseNotificationAsync(Body(Donation("just a tip")), NoHeaders);

        Assert.NotNull(note);
        Assert.Equal(BillingEventKind.Unknown, note!.Kind);
        Assert.Empty(await h.Unclaimed.ListAsync());
    }

    [Fact]
    public async Task Unmatched_purchase_is_held_as_unattributed()
    {
        var h = Build(o => o.DonationProductId = "credits100");

        var note = await h.Provider.ParseNotificationAsync(
            Body(Donation("no code here")), NoHeaders);

        Assert.Equal(BillingEventKind.Unknown, note!.Kind);
        var held = Assert.Single(await h.Unclaimed.ListAsync());
        Assert.Equal("unattributed", held.Reason);
        Assert.Equal("credits100", held.Purchase.ProductId);
        Assert.Equal("jo.example@example.com", held.Purchase.BuyerEmail);
    }

    [Fact]
    public async Task Named_tier_without_mapping_is_held_as_unmapped_product()
    {
        var h = Build();
        var payload = new
        {
            verification_token = Token,
            message_id = "3a1fac0c-f960-4506-a60e-824979a74e74",
            timestamp = "2022-08-21T13:04:30Z",
            type = "Subscription",
            is_public = false,
            from_name = "Ko-fi Team",
            message = (string?)null,
            amount = "5.00",
            url = "https://ko-fi.com/Home/CoffeeShop?txid=0a1fac0c",
            email = "someone@example.com",
            currency = "USD",
            is_subscription_payment = true,
            is_first_subscription_payment = true,
            kofi_transaction_id = "0a1fac0c-f960-4506-a60e-824979a74e71",
            tier_name = "Gold Tier",
        };

        var note = await h.Provider.ParseNotificationAsync(Body(payload), NoHeaders);

        Assert.Equal(BillingEventKind.Unknown, note!.Kind);
        var held = Assert.Single(await h.Unclaimed.ListAsync());
        Assert.Equal("unmapped_product", held.Reason);
        Assert.Equal("Gold Tier", held.Purchase.PlatformProduct);
    }

    [Fact]
    public async Task Subscription_renewal_without_message_attaches_via_identity_link()
    {
        var h = Build(o =>
        {
            o.DonationProductId = "credits100";
            o.TierProducts["Gold Tier"] = "pro";
        });
        var userId = Guid.NewGuid();
        var code = await h.External.GetClaimCodeAsync(userId);

        // First payment carries the code and persists the (kofi, email) link.
        await h.Provider.ParseNotificationAsync(Body(Donation($"code: {code}")), NoHeaders);

        // The renewal has NO message (Ko-fi sends the message only on one-time payments).
        var renewal = new
        {
            verification_token = Token,
            message_id = "c2bd78f3-0000-4a9a-bf43-ceee37d786d2",
            timestamp = "2024-12-22T13:23:35Z",
            type = "Subscription",
            is_public = false,
            from_name = "Jo Example",
            message = (string?)null,
            amount = "5.00",
            url = "https://ko-fi.com/Home/CoffeeShop?txid=renew-1",
            email = "JO.EXAMPLE@example.com", // different case must still match
            currency = "USD",
            is_subscription_payment = true,
            is_first_subscription_payment = false,
            kofi_transaction_id = "renewal-tx-0001",
            tier_name = "Gold Tier",
        };

        var note = await h.Provider.ParseNotificationAsync(Body(renewal), NoHeaders);

        Assert.Equal(BillingEventKind.PaymentSucceeded, note!.Kind);
        Assert.Equal(userId, note.UserId);
        Assert.Equal("pro", note.PlanId);
        Assert.Equal("renewal-tx-0001", note.ProviderPaymentId);
        Assert.Equal(500, note.AmountMinor);
    }

    [Fact]
    public async Task Amount_string_parses_invariant_to_minor_units()
    {
        var h = Build(o => o.DonationProductId = "credits100");
        var userId = Guid.NewGuid();
        var code = await h.External.GetClaimCodeAsync(userId);

        var note = await h.Provider.ParseNotificationAsync(
            Body(Donation(code, amount: "27.95")), NoHeaders);

        Assert.Equal(2795, note!.AmountMinor);
    }

    [Fact]
    public async Task Unconfigured_provider_rejects_everything()
    {
        var fulfillment = new CountingFulfillment();
        var provider = new KofiBillingProvider(
            Options.Create(new KofiBillingOptions()), // no token
            Build().External,
            NullLogger<KofiBillingProvider>.Instance);

        Assert.False(provider.IsConfigured);
        Assert.Null(await provider.ParseNotificationAsync(Body(Donation("hi")), NoHeaders));
        Assert.Equal(0, fulfillment.CallCount);
    }

    [Fact]
    public async Task Data_field_that_is_json_but_not_an_object_is_rejected_not_thrown()
    {
        var h = Build();

        // A bare JSON string would make every TryGetProperty throw, turning an
        // unauthenticated payload into a 500 that Ko-fi retries forever.
        Assert.Null(await h.Provider.ParseNotificationAsync(
            "data=" + Uri.EscapeDataString("\"hello\""), NoHeaders));
        Assert.Null(await h.Provider.ParseNotificationAsync(
            "data=" + Uri.EscapeDataString("[1,2,3]"), NoHeaders));
    }

    [Fact]
    public async Task Shop_order_of_several_units_is_held_instead_of_granting_one()
    {
        var h = Build(o => o.ShopItemProducts["1a2b3c4d5e"] = "sticker");
        var userId = Guid.NewGuid();
        var code = await h.External.GetClaimCodeAsync(userId);

        var order = new
        {
            verification_token = Token,
            message_id = "order-1",
            timestamp = "2026-08-15T10:00:00Z",
            type = "Shop Order",
            is_public = true,
            from_name = "Jo Example",
            message = code,
            amount = "27.95",
            url = "https://ko-fi.com/Home/CoffeeShop?txid=shop-1",
            email = "jo.example@example.com",
            currency = "EUR",
            is_subscription_payment = false,
            is_first_subscription_payment = false,
            kofi_transaction_id = "shop-tx-1",
            shop_items = new[] { new { direct_link_code = "1a2b3c4d5e", quantity = 3 } },
        };

        var note = await h.Provider.ParseNotificationAsync(Body(order), NoHeaders);

        Assert.Equal(BillingEventKind.Unknown, note!.Kind);
        var held = Assert.Single(await h.Unclaimed.ListAsync());
        Assert.Equal("unmapped_product", held.Reason);
    }

    [Fact]
    public async Task Single_unit_shop_order_is_granted()
    {
        var h = Build(o => o.ShopItemProducts["1a2b3c4d5e"] = "sticker");
        var userId = Guid.NewGuid();
        var code = await h.External.GetClaimCodeAsync(userId);

        var order = new
        {
            verification_token = Token,
            message_id = "order-2",
            timestamp = "2026-08-15T10:00:00Z",
            type = "Shop Order",
            is_public = true,
            from_name = "Jo Example",
            message = code,
            amount = "9.99",
            url = "https://ko-fi.com/Home/CoffeeShop?txid=shop-2",
            email = "jo.example@example.com",
            currency = "EUR",
            is_subscription_payment = false,
            is_first_subscription_payment = false,
            kofi_transaction_id = "shop-tx-2",
            shop_items = new[] { new { direct_link_code = "1a2b3c4d5e", quantity = 1 } },
        };

        var note = await h.Provider.ParseNotificationAsync(Body(order), NoHeaders);

        Assert.Equal(BillingEventKind.PaymentSucceeded, note!.Kind);
        Assert.Equal("sticker", note.PlanId);
        Assert.Equal(userId, note.UserId);
    }

    [Fact]
    public async Task Donation_carrying_a_claim_code_is_held_when_no_product_is_configured()
    {
        var h = Build(); // no DonationProductId
        var userId = Guid.NewGuid();
        var code = await h.External.GetClaimCodeAsync(userId);

        var note = await h.Provider.ParseNotificationAsync(
            Body(Donation($"вот мой код {code}")), NoHeaders);

        // The buyer asked for something by typing a code - an admin must see this payment.
        Assert.Equal(BillingEventKind.Unknown, note!.Kind);
        var held = Assert.Single(await h.Unclaimed.ListAsync());
        Assert.Equal("unmapped_product", held.Reason);
    }

    [Fact]
    public async Task Tier_mapping_matches_an_env_safe_spelling_of_the_tier_name()
    {
        // "Billing__Kofi__TierProducts__Gold Tier" cannot exist as an environment variable,
        // so the same mapping must be reachable as "GoldTier".
        var h = Build(o => o.TierProducts["GoldTier"] = "pro");
        var userId = Guid.NewGuid();
        var code = await h.External.GetClaimCodeAsync(userId);

        var subscription = new
        {
            verification_token = Token,
            message_id = "sub-1",
            timestamp = "2026-08-15T10:00:00Z",
            type = "Subscription",
            is_public = false,
            from_name = "Jo Example",
            message = code,
            amount = "5.00",
            url = "https://ko-fi.com/Home/CoffeeShop?txid=sub-1",
            email = "jo.example@example.com",
            currency = "USD",
            is_subscription_payment = true,
            is_first_subscription_payment = true,
            kofi_transaction_id = "sub-tx-1",
            tier_name = "Gold Tier",
        };

        var note = await h.Provider.ParseNotificationAsync(Body(subscription), NoHeaders);

        Assert.Equal(BillingEventKind.PaymentSucceeded, note!.Kind);
        Assert.Equal("pro", note.PlanId);
    }

    [Fact]
    public async Task Checkout_redirects_to_page_url_or_is_refused()
    {
        var withPage = Build(o => o.PageUrl = "https://ko-fi.com/techteastudio");
        var session = await withPage.Provider.CreateCheckoutAsync(
            new BillingCheckoutRequest(Guid.NewGuid(), "u@e.com", "plus", "Plus", "r", "c"));
        Assert.Equal("https://ko-fi.com/techteastudio", session.RedirectUrl);

        var withoutPage = Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            withoutPage.Provider.CreateCheckoutAsync(
                new BillingCheckoutRequest(Guid.NewGuid(), "u@e.com", "plus", "Plus", "r", "c")));
    }
}
