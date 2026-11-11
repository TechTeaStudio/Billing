#if NET11_0_OR_GREATER
// CS8509 and CS8655 (a switch expression that misses a case) are errors in this project -
// see WarningsAsErrors in the .csproj - so every switch below is a compile-time proof that
// the union is closed. C# has no "#pragma warning error", hence the project-level setting.
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TechTeaStudio.Billing.Abstractions;
using TechTeaStudio.Billing.AspNetCore;
using TechTeaStudio.Billing.Services;
using Xunit;

namespace TechTeaStudio.Billing.Tests;

public class OutcomeUnionTests
{
    // ---- Fakes -----------------------------------------------------------------

    private sealed class MutableProvider : IBillingProvider
    {
        public BillingNotification? Next { get; set; }
        public string Name => "fake";
        public string DisplayName => "Fake";
        public bool IsConfigured => true;

        public Task<CheckoutSession> CreateCheckoutAsync(
            BillingCheckoutRequest request, CancellationToken ct = default) =>
            Task.FromResult(new CheckoutSession("http://fake", Name, "sess_fake"));

        public Task<BillingNotification?> ParseNotificationAsync(
            string rawBody, IReadOnlyDictionary<string, string> headers,
            CancellationToken ct = default) =>
            Task.FromResult(Next);
    }

    /// <summary>Counts grants and can be told to throw, the shape of a host database that
    /// is down for one delivery and back for the next.</summary>
    private sealed class SwitchableFulfillment : IBillingFulfillment
    {
        public bool Throw { get; set; }
        public int CallCount { get; private set; }
        public BillingNotification? Last { get; private set; }

        public Task OnPaymentSucceededAsync(BillingNotification n, CancellationToken ct = default)
        {
            if (Throw) throw new InvalidOperationException("host db down");
            CallCount++;
            Last = n;
            return Task.CompletedTask;
        }
    }

    /// <summary>A host store that implements only the two required members, so the default
    /// read-then-check <see cref="IBillingPaymentStore.TryReserveAsync"/> applies. Counts
    /// every call, which is how "touches nothing" is asserted.</summary>
    private sealed class CountingDefaultStore : IBillingPaymentStore
    {
        private readonly Dictionary<(string, string), BillingPaymentRecord> _records = new();
        public int Calls { get; private set; }

        public Task<BillingPaymentStatus?> GetStatusAsync(
            string provider, string providerPaymentId, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(
                _records.TryGetValue((provider, providerPaymentId), out var r)
                    ? (BillingPaymentStatus?)r.Status
                    : null);
        }

        public Task UpsertAsync(BillingPaymentRecord record, CancellationToken ct = default)
        {
            Calls++;
            _records[(record.Provider, record.ProviderPaymentId)] = record;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingPaymentStore : IBillingPaymentStore
    {
        public Task<BillingPaymentStatus?> GetStatusAsync(
            string provider, string providerPaymentId, CancellationToken ct = default) =>
            throw new InvalidOperationException("db down");

        public Task UpsertAsync(BillingPaymentRecord record, CancellationToken ct = default) =>
            throw new InvalidOperationException("db down");
    }

    // ---- Webhook harness ---------------------------------------------------------

    private sealed record WebhookHarness(
        BillingService Service,
        MutableProvider Provider,
        CountingDefaultStore Store,
        SwitchableFulfillment Fulfillment);

    private static WebhookHarness BuildWebhook()
    {
        var provider = new MutableProvider();
        var store = new CountingDefaultStore();
        var fulfillment = new SwitchableFulfillment();
        var service = new BillingService(
            new[] { (IBillingProvider)provider }, store, fulfillment,
            NullLogger<BillingService>.Instance);
        return new WebhookHarness(service, provider, store, fulfillment);
    }

    private static readonly IReadOnlyDictionary<string, string> NoHeaders =
        new Dictionary<string, string>();

    private static readonly Guid User = Guid.NewGuid();

    private static BillingNotification Note(BillingEventKind kind, string paymentId = "pay_1") =>
        new(kind, User, "pro", paymentId, 500, "USD");

    /// <summary>No discard arm and no null arm: this compiling IS the exhaustiveness test.</summary>
    private static string Describe(WebhookOutcome outcome) => outcome switch
    {
        WebhookFulfilled => "fulfilled",
        WebhookAlreadyProcessed => "already",
        WebhookRecorded => "recorded",
        WebhookIgnored => "ignored",
        WebhookRejected => "rejected",
        WebhookFailed => "failed",
    };

    /// <summary>The rule <see cref="IBillingService.HandleWebhookAsync"/> answers with.</summary>
    private static bool Accepted(WebhookOutcome outcome) => outcome switch
    {
        WebhookFulfilled => true,
        WebhookAlreadyProcessed => true,
        WebhookRecorded => true,
        WebhookIgnored => true,
        WebhookRejected => false,
        WebhookFailed => false,
    };

    private static string Describe(ClaimOutcome outcome) => outcome switch
    {
        ClaimFulfilled => "fulfilled",
        ClaimAlreadyProcessed => "already",
        ClaimNotHeld => "not_held",
        ClaimNeedsProduct => "needs_product",
        ClaimFailed => "failed",
    };

    // ---- Webhook tests -----------------------------------------------------------

    [Fact]
    public async Task First_success_is_Fulfilled_with_the_verified_notification()
    {
        var h = BuildWebhook();
        h.Provider.Next = Note(BillingEventKind.PaymentSucceeded);

        var outcome = await h.Service.ProcessWebhookAsync("fake", "{}", NoHeaders);

        var fulfilled = Assert.IsType<WebhookFulfilled>(outcome.Value);
        Assert.Same(h.Provider.Next, fulfilled.Notification);
        Assert.Equal(1, h.Fulfillment.CallCount);
        Assert.Equal("fulfilled", Describe(outcome));
    }

    [Fact]
    public async Task Redelivery_is_AlreadyProcessed_and_does_not_fulfil_again()
    {
        var h = BuildWebhook();
        h.Provider.Next = Note(BillingEventKind.PaymentSucceeded);

        await h.Service.ProcessWebhookAsync("fake", "{}", NoHeaders);
        var second = await h.Service.ProcessWebhookAsync("fake", "{}", NoHeaders);

        var already = Assert.IsType<WebhookAlreadyProcessed>(second.Value);
        Assert.Equal("pay_1", already.Notification.ProviderPaymentId);
        Assert.Equal(1, h.Fulfillment.CallCount);
    }

    [Fact]
    public async Task Refund_is_Recorded_with_the_refund_kind()
    {
        var h = BuildWebhook();
        h.Provider.Next = Note(BillingEventKind.PaymentSucceeded);
        await h.Service.ProcessWebhookAsync("fake", "{}", NoHeaders);

        h.Provider.Next = Note(BillingEventKind.PaymentRefunded);
        var outcome = await h.Service.ProcessWebhookAsync("fake", "{}", NoHeaders);

        var recorded = Assert.IsType<WebhookRecorded>(outcome.Value);
        Assert.Equal(BillingEventKind.PaymentRefunded, recorded.Notification.Kind);
        Assert.Equal(BillingPaymentStatus.Refunded, await h.Store.GetStatusAsync("fake", "pay_1"));
        Assert.Equal(1, h.Fulfillment.CallCount);
    }

    [Fact]
    public async Task Cancel_is_Recorded_with_the_cancel_kind()
    {
        var h = BuildWebhook();
        h.Provider.Next = Note(BillingEventKind.PaymentCanceled);

        var outcome = await h.Service.ProcessWebhookAsync("fake", "{}", NoHeaders);

        var recorded = Assert.IsType<WebhookRecorded>(outcome.Value);
        Assert.Equal(BillingEventKind.PaymentCanceled, recorded.Notification.Kind);
        Assert.Equal(0, h.Fulfillment.CallCount);
    }

    [Fact]
    public async Task Irrelevant_verified_event_is_Ignored_and_carries_only_the_provider()
    {
        var h = BuildWebhook();
        h.Provider.Next = new BillingNotification(
            BillingEventKind.Unknown, Guid.Empty, "", "evt_irrelevant", 0, "USD");

        var outcome = await h.Service.ProcessWebhookAsync("fake", "{}", NoHeaders);

        Assert.Equal(new WebhookIgnored("fake"), outcome.Value);
        Assert.Equal(0, h.Fulfillment.CallCount);
        Assert.Equal(new[] { "Provider" }, PublicProperties<WebhookIgnored>());
    }

    [Fact]
    public async Task Null_from_the_provider_is_Rejected_touches_nothing_and_leaks_no_body()
    {
        var h = BuildWebhook();
        h.Provider.Next = null;
        const string body = "{\"secret\":\"BODY-CONTENT-7f3a\"}";

        var outcome = await h.Service.ProcessWebhookAsync("fake", body, NoHeaders);

        Assert.Equal(new WebhookRejected("fake"), outcome.Value);
        Assert.Equal(0, h.Fulfillment.CallCount);
        Assert.Equal(0, h.Store.Calls);
        Assert.Equal(new[] { "Provider" }, PublicProperties<WebhookRejected>());
        Assert.DoesNotContain("BODY-CONTENT", outcome.Value!.ToString());
    }

    [Fact]
    public async Task Unknown_provider_is_Rejected()
    {
        var h = BuildWebhook();
        h.Provider.Next = Note(BillingEventKind.PaymentSucceeded);

        var outcome = await h.Service.ProcessWebhookAsync("stripe", "{}", NoHeaders);

        Assert.Equal(new WebhookRejected("stripe"), outcome.Value);
        Assert.Equal(0, h.Fulfillment.CallCount);
        Assert.Equal(0, h.Store.Calls);
    }

    [Fact]
    public async Task Throwing_fulfilment_is_Failed_and_the_next_delivery_is_Fulfilled()
    {
        // The store here uses the default read-then-check reservation, so a payment whose
        // fulfilment failed is not terminal and the provider's retry gets through.
        var h = BuildWebhook();
        h.Provider.Next = Note(BillingEventKind.PaymentSucceeded);
        h.Fulfillment.Throw = true;

        var first = await h.Service.ProcessWebhookAsync("fake", "{}", NoHeaders);

        var failed = Assert.IsType<WebhookFailed>(first.Value);
        Assert.Same(h.Provider.Next, failed.Notification);
        Assert.Equal(0, h.Fulfillment.CallCount);

        h.Fulfillment.Throw = false;
        var second = await h.Service.ProcessWebhookAsync("fake", "{}", NoHeaders);

        Assert.IsType<WebhookFulfilled>(second.Value);
        Assert.Equal(1, h.Fulfillment.CallCount);
    }

    [Fact]
    public async Task Store_errors_propagate_instead_of_becoming_a_case()
    {
        var provider = new MutableProvider { Next = Note(BillingEventKind.PaymentCanceled) };
        var service = new BillingService(
            new[] { (IBillingProvider)provider }, new ThrowingPaymentStore(),
            new SwitchableFulfillment(), NullLogger<BillingService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ProcessWebhookAsync("fake", "{}", NoHeaders));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.HandleWebhookAsync("fake", "{}", NoHeaders));
    }

    public static TheoryData<string, string> AgreementScenarios() => new()
    {
        { "success", "fulfilled" },
        { "success,success", "already" },
        { "success,refund", "recorded" },
        { "cancel", "recorded" },
        { "failed", "recorded" },
        { "success,cancel", "already" },
        { "unknown", "ignored" },
        { "null", "rejected" },
        { "wrong-provider", "rejected" },
        { "throw", "failed" },
    };

    /// <summary>Plays the same deliveries into two identical services, one through each API,
    /// and checks the bool is exactly "the outcome is not Rejected and not Failed".</summary>
    [Theory]
    [MemberData(nameof(AgreementScenarios))]
    public async Task Outcome_agrees_with_HandleWebhookAsync(string steps, string expectedLast)
    {
        var viaBool = BuildWebhook();
        var viaUnion = BuildWebhook();
        var last = "";

        foreach (var step in steps.Split(','))
        {
            var providerName = step == "wrong-provider" ? "nope" : "fake";
            BillingNotification? note = step switch
            {
                "success" or "throw" or "wrong-provider" => Note(BillingEventKind.PaymentSucceeded),
                "refund" => Note(BillingEventKind.PaymentRefunded),
                "cancel" => Note(BillingEventKind.PaymentCanceled),
                "failed" => Note(BillingEventKind.PaymentFailed),
                "unknown" => Note(BillingEventKind.Unknown),
                _ => null,
            };
            viaBool.Provider.Next = viaUnion.Provider.Next = note;
            viaBool.Fulfillment.Throw = viaUnion.Fulfillment.Throw = step == "throw";

            var accepted = await viaBool.Service.HandleWebhookAsync(providerName, "{}", NoHeaders);
            var outcome = await viaUnion.Service.ProcessWebhookAsync(providerName, "{}", NoHeaders);

            Assert.Equal(accepted, Accepted(outcome));
            last = Describe(outcome);
        }

        Assert.Equal(expectedLast, last);
        Assert.Equal(viaBool.Fulfillment.CallCount, viaUnion.Fulfillment.CallCount);
    }

    private static string[] PublicProperties<T>() =>
        typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name).ToArray();

    // ---- Claim harness -----------------------------------------------------------

    private sealed record ClaimHarness(
        ExternalPurchaseService Service,
        InMemoryUnclaimedPurchaseStore Unclaimed,
        SwitchableFulfillment Fulfillment);

    private static ClaimHarness BuildClaims(IBillingPaymentStore? store = null)
    {
        var fulfillment = new SwitchableFulfillment();
        var unclaimed = new InMemoryUnclaimedPurchaseStore();
        var pipeline = new PaymentPipeline(
            store ?? new InMemoryBillingPaymentStore(),
            fulfillment,
            NullLogger<PaymentPipeline>.Instance);
        var service = new ExternalPurchaseService(
            new InMemoryClaimCodeStore(),
            new InMemoryExternalIdentityLinkStore(),
            unclaimed,
            pipeline,
            Options.Create(new ExternalPurchaseOptions { ClaimCodePrefix = "CHR" }),
            NullLogger<ExternalPurchaseService>.Instance);
        return new ClaimHarness(service, unclaimed, fulfillment);
    }

    private static ExternalPurchase Purchase(string? productId = "credits100") =>
        new("kofi", "tx-1", "tx-1", productId, "Gold Tier", 500, "USD",
            "buyer@example.com", "Buyer", null,
            IsSubscriptionPayment: false, IsFirstSubscriptionPayment: false,
            OccurredAt: DateTimeOffset.UnixEpoch);

    // ---- Claim tests -------------------------------------------------------------

    [Fact]
    public async Task Mapped_hold_is_Fulfilled_and_consumes_the_hold()
    {
        var h = BuildClaims();
        var purchase = Purchase();
        await h.Service.HoldAsync(purchase, "unattributed");

        var outcome = await h.Service.ClaimHeldAsync("Kofi ", "tx-1", User);

        var fulfilled = Assert.IsType<ClaimFulfilled>(outcome.Value);
        Assert.Equal(purchase, fulfilled.Purchase);
        Assert.Equal("credits100", fulfilled.ProductId);
        Assert.Equal(1, h.Fulfillment.CallCount);
        Assert.Equal(User, h.Fulfillment.Last!.UserId);
        Assert.Empty(await h.Unclaimed.ListAsync());
        Assert.Equal("fulfilled", Describe(outcome));
    }

    [Fact]
    public async Task Already_fulfilled_payment_is_AlreadyProcessed_and_grants_nothing_more()
    {
        var h = BuildClaims();
        await h.Service.SubmitAsync(Purchase(), User);            // fulfilled once
        await h.Service.HoldAsync(Purchase(), "unattributed");    // stale hold

        var outcome = await h.Service.ClaimHeldAsync("kofi", "tx-1", User);

        var already = Assert.IsType<ClaimAlreadyProcessed>(outcome.Value);
        Assert.Equal("tx-1", already.Purchase.ProviderPaymentId);
        Assert.Equal(1, h.Fulfillment.CallCount);
        Assert.Empty(await h.Unclaimed.ListAsync());
    }

    [Fact]
    public async Task Unknown_payment_is_NotHeld()
    {
        var h = BuildClaims();

        var outcome = await h.Service.ClaimHeldAsync("kofi", "tx-missing", User);

        Assert.Equal(new ClaimNotHeld("kofi", "tx-missing"), outcome.Value);
        Assert.Equal(0, h.Fulfillment.CallCount);
    }

    [Fact]
    public async Task No_product_is_NeedsProduct_and_stays_held_then_an_override_fulfils()
    {
        var h = BuildClaims();
        await h.Service.HoldAsync(Purchase(productId: null), "unmapped_product");

        var first = await h.Service.ClaimHeldAsync("kofi", "tx-1", User);

        var needs = Assert.IsType<ClaimNeedsProduct>(first.Value);
        Assert.Equal("unmapped_product", needs.Held.Reason);
        Assert.Equal(0, h.Fulfillment.CallCount);
        Assert.Single(await h.Unclaimed.ListAsync());

        var second = await h.Service.ClaimHeldAsync("kofi", "tx-1", User, productIdOverride: "pro");

        var fulfilled = Assert.IsType<ClaimFulfilled>(second.Value);
        Assert.Equal("pro", fulfilled.ProductId);
        Assert.Equal("pro", h.Fulfillment.Last!.PlanId);
        Assert.Empty(await h.Unclaimed.ListAsync());
    }

    [Fact]
    public async Task Throwing_fulfilment_is_Failed_and_the_purchase_is_held_again()
    {
        var h = BuildClaims();
        h.Fulfillment.Throw = true;
        await h.Service.HoldAsync(Purchase(), "unattributed");

        var outcome = await h.Service.ClaimHeldAsync("kofi", "tx-1", User);

        var failed = Assert.IsType<ClaimFailed>(outcome.Value);
        Assert.Equal("tx-1", failed.Held.Purchase.ProviderPaymentId);
        Assert.Single(await h.Unclaimed.ListAsync()); // money is not lost
        Assert.Equal("failed", Describe(outcome));
    }

    [Fact]
    public async Task Throwing_payment_store_is_Failed_and_the_purchase_is_held_again()
    {
        var h = BuildClaims(new ThrowingPaymentStore());
        await h.Service.HoldAsync(Purchase(), "unattributed");

        var outcome = await h.Service.ClaimHeldAsync("kofi", "tx-1", User);

        Assert.IsType<ClaimFailed>(outcome.Value);
        Assert.Equal(0, h.Fulfillment.CallCount);
        Assert.Single(await h.Unclaimed.ListAsync());
    }

    [Fact]
    public async Task ClaimHeldAsync_agrees_with_ClaimAsync()
    {
        var viaBool = BuildClaims();
        var viaUnion = BuildClaims();
        await viaBool.Service.HoldAsync(Purchase(), "unattributed");
        await viaUnion.Service.HoldAsync(Purchase(), "unattributed");

        // First claim grants, the second finds nothing held.
        for (var i = 0; i < 2; i++)
        {
            var ok = await viaBool.Service.ClaimAsync("kofi", "tx-1", User);
            var outcome = await viaUnion.Service.ClaimHeldAsync("kofi", "tx-1", User);
            Assert.Equal(ok, outcome.Value is ClaimFulfilled);
        }
    }

    // ---- DI ----------------------------------------------------------------------

    [Fact]
    public void Both_outcome_services_resolve_from_DI()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTechTeaStudioBilling();

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();

        Assert.IsType<BillingService>(
            scope.ServiceProvider.GetRequiredService<IBillingWebhookProcessor>());
        Assert.IsType<ExternalPurchaseService>(
            scope.ServiceProvider.GetRequiredService<IExternalPurchaseClaims>());
    }
}
#endif
