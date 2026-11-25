using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TechTeaStudio.Billing.Abstractions;
using TechTeaStudio.Billing.Providers;
using Xunit;

namespace TechTeaStudio.Billing.Tests;

/// <summary>
/// Robokassa signs the payment link with Password #1 and the Result URL notification with
/// Password #2, over "OutSum:InvId:Password[:Shp_*]" (the link adds the login in front). The
/// expected values here are worked out independently of the provider, from the documented
/// formula, so a provider that hashes the wrong string fails.
/// </summary>
public class RobokassaProviderTests
{
    private static readonly Guid User = Guid.Parse("7f3a2c10-1111-4222-8333-444455556666");
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    private static RobokassaBillingProvider Provider(Action<RobokassaBillingOptions>? configure = null)
    {
        var opts = new RobokassaBillingOptions
        {
            MerchantLogin = "techteastudio",
            Password1 = "live-one",
            Password2 = "live-two",
            TestPassword1 = "test-one",
            TestPassword2 = "test-two",
            HashAlgorithm = "SHA256",
            InvoiceIdFactory = (_, _) => Task.FromResult(4242L),
        };
        opts.Amounts["pro-1"] = 490m;
        configure?.Invoke(opts);
        return new RobokassaBillingProvider(Options.Create(opts), NullLogger<RobokassaBillingProvider>.Instance);
    }

    private static string Sha256(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private static string Md5(string s) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s)));

    private static string Shp(Guid user, string plan) => $"Shp_plan={plan}:Shp_user={user:N}";

    /// <summary>A Result URL body as Robokassa posts it (form-encoded), signed with Password #2.</summary>
    private static string Result(string outSum = "490.000000", long invId = 4242, Guid? user = null, string plan = "pro-1",
        string password2 = "live-two", bool test = false, string? signature = null, Func<string, string>? hash = null)
    {
        var u = user ?? User;
        signature ??= (hash ?? Sha256)($"{outSum}:{invId}:{password2}:{Shp(u, plan)}");
        var form = new Dictionary<string, string>
        {
            ["out_summ"] = outSum,
            ["OutSum"] = outSum,
            ["inv_id"] = invId.ToString(CultureInfo.InvariantCulture),
            ["InvId"] = invId.ToString(CultureInfo.InvariantCulture),
            ["crc"] = signature,
            ["SignatureValue"] = signature,
            ["PaymentMethod"] = "BankCard",
            ["IncSum"] = outSum,
            ["IncCurrLabel"] = "BankCardPSR",
            ["EMail"] = "buyer@example.org",
            ["Fee"] = "16.66",
            ["Shp_plan"] = plan,
            ["Shp_user"] = u.ToString("N"),
        };
        if (test) form["IsTest"] = "1";
        return string.Join("&", form.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }

    private static BillingCheckoutRequest Checkout(string plan = "pro-1") =>
        new(User, "buyer@example.org", plan, "TrafficMeter Pro, 1 computer", "https://store.example/success", "https://store.example/fail");

    [Fact]
    public async Task Checkout_link_is_signed_with_password_one_over_login_sum_invoice_and_shp()
    {
        var session = await Provider().CreateCheckoutAsync(Checkout());

        Assert.Equal("robokassa", session.Provider);
        Assert.Equal("4242", session.ProviderPaymentId);
        var uri = new Uri(session.RedirectUrl);
        Assert.Equal("https://auth.robokassa.ru/Merchant/Index.aspx", uri.GetLeftPart(UriPartial.Path));
        var q = QueryHelpers.ParseQuery(uri.Query);
        Assert.Equal("techteastudio", q["MerchantLogin"]);
        Assert.Equal("490.00", q["OutSum"]);
        Assert.Equal("4242", q["InvId"]);
        Assert.Equal("TrafficMeter Pro, 1 computer", q["Description"]);
        Assert.Equal("buyer@example.org", q["Email"]);
        Assert.Equal("pro-1", q["Shp_plan"]);
        Assert.Equal(User.ToString("N"), q["Shp_user"]);
        Assert.False(q.ContainsKey("IsTest"));
        Assert.Equal(Sha256($"techteastudio:490.00:4242:live-one:{Shp(User, "pro-1")}"), q["SignatureValue"]);
    }

    // Robochecks (the self-employed seller's receipts) need the cart: the minimized JSON,
    // URL-encoded once, goes into the signature right after InvId and is encoded again in the
    // link (docs.robokassa.ru/ru/fiscalization, /ru/pay-interface).
    [Fact]
    public async Task With_receipts_the_cart_is_in_the_link_and_in_the_signature_after_the_invoice()
    {
        var session = await Provider(o => { o.SendReceipt = true; o.ReceiptPaymentObject = "intellectual_activity"; })
            .CreateCheckoutAsync(Checkout());

        var json = "{\"items\":[{\"name\":\"TrafficMeter Pro, 1 computer\",\"quantity\":1,\"sum\":490.00," +
            "\"payment_method\":\"full_payment\",\"payment_object\":\"intellectual_activity\",\"tax\":\"none\"}]}";
        var encoded = Uri.EscapeDataString(json);
        var q = QueryHelpers.ParseQuery(new Uri(session.RedirectUrl).Query);
        Assert.Equal(encoded, q["Receipt"]);
        Assert.Contains("Receipt=" + Uri.EscapeDataString(encoded), session.RedirectUrl);
        Assert.Equal(Sha256($"techteastudio:490.00:4242:{encoded}:live-one:{Shp(User, "pro-1")}"), q["SignatureValue"]);
    }

    [Fact]
    public async Task A_receipt_item_name_is_cut_to_128_characters()
    {
        var request = Checkout() with { PlanName = new string('n', 200) };

        var session = await Provider(o => o.SendReceipt = true).CreateCheckoutAsync(request);

        var json = Uri.UnescapeDataString(QueryHelpers.ParseQuery(new Uri(session.RedirectUrl).Query)["Receipt"]!);
        Assert.Contains("\"name\":\"" + new string('n', 128) + "\"", json);
        Assert.DoesNotContain(new string('n', 129), json);
    }

    [Fact]
    public async Task Without_receipts_there_is_no_cart()
    {
        var session = await Provider().CreateCheckoutAsync(Checkout());

        Assert.False(QueryHelpers.ParseQuery(new Uri(session.RedirectUrl).Query).ContainsKey("Receipt"));
    }

    [Fact]
    public async Task A_test_shop_signs_with_the_test_password_and_marks_the_link()
    {
        var session = await Provider(o => o.IsTest = true).CreateCheckoutAsync(Checkout());

        var q = QueryHelpers.ParseQuery(new Uri(session.RedirectUrl).Query);
        Assert.Equal("1", q["IsTest"]);
        Assert.Equal(Sha256($"techteastudio:490.00:4242:test-one:{Shp(User, "pro-1")}"), q["SignatureValue"]);
    }

    [Fact]
    public async Task An_unpriced_plan_is_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider().CreateCheckoutAsync(Checkout("unknown")));
    }

    [Fact]
    public async Task Without_a_factory_the_invoice_number_is_a_positive_number()
    {
        var session = await Provider(o => o.InvoiceIdFactory = null).CreateCheckoutAsync(Checkout());

        Assert.True(long.Parse(session.ProviderPaymentId, CultureInfo.InvariantCulture) > 0);
    }

    [Fact]
    public async Task A_signed_result_is_a_successful_payment()
    {
        var note = await Provider().ParseNotificationAsync(Result(), NoHeaders);

        Assert.NotNull(note);
        Assert.Equal(BillingEventKind.PaymentSucceeded, note!.Kind);
        Assert.Equal(User, note.UserId);
        Assert.Equal("pro-1", note.PlanId);
        Assert.Equal("4242", note.ProviderPaymentId);
        Assert.Equal(49000, note.AmountMinor);
        Assert.Equal("RUB", note.Currency);
    }

    [Fact]
    public async Task The_signature_is_compared_without_regard_to_case()
    {
        var lower = Sha256($"490.000000:4242:live-two:{Shp(User, "pro-1")}").ToLowerInvariant();

        var note = await Provider().ParseNotificationAsync(Result(signature: lower), NoHeaders);

        Assert.Equal(BillingEventKind.PaymentSucceeded, note!.Kind);
    }

    [Theory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("")]
    [InlineData("not hex")]
    public async Task A_wrong_signature_is_rejected(string signature)
    {
        Assert.Null(await Provider().ParseNotificationAsync(Result(signature: signature), NoHeaders));
    }

    [Fact]
    public async Task A_result_signed_with_password_one_is_rejected()
    {
        Assert.Null(await Provider().ParseNotificationAsync(Result(password2: "live-one"), NoHeaders));
    }

    [Fact]
    public async Task Changing_the_buyer_or_the_plan_after_signing_breaks_the_signature()
    {
        var signed = Result();
        var otherUser = signed.Replace(User.ToString("N"), Guid.NewGuid().ToString("N"), StringComparison.Ordinal);
        var otherPlan = signed.Replace("Shp_plan=pro-1", "Shp_plan=pro-3", StringComparison.Ordinal);

        Assert.Null(await Provider().ParseNotificationAsync(otherUser, NoHeaders));
        Assert.Null(await Provider().ParseNotificationAsync(otherPlan, NoHeaders));
    }

    [Fact]
    public async Task A_payment_below_the_plan_price_grants_nothing()
    {
        var note = await Provider().ParseNotificationAsync(Result(outSum: "1.000000"), NoHeaders);

        Assert.NotNull(note);
        Assert.Equal(BillingEventKind.Unknown, note!.Kind);
    }

    [Fact]
    public async Task A_test_payment_is_refused_on_a_live_shop()
    {
        // Signed with the test password: anyone with the test pair must not get a live key.
        var note = await Provider().ParseNotificationAsync(Result(password2: "test-two", test: true), NoHeaders);

        Assert.True(note is null || note.Kind == BillingEventKind.Unknown);
    }

    [Fact]
    public async Task A_test_shop_accepts_test_payments_signed_with_the_test_password()
    {
        var note = await Provider(o => o.IsTest = true).ParseNotificationAsync(Result(password2: "test-two", test: true), NoHeaders);

        Assert.Equal(BillingEventKind.PaymentSucceeded, note!.Kind);
    }

    [Fact]
    public async Task A_test_shop_refuses_a_payment_without_the_test_flag()
    {
        var note = await Provider(o => o.IsTest = true).ParseNotificationAsync(Result(password2: "test-two"), NoHeaders);

        Assert.True(note is null || note.Kind == BillingEventKind.Unknown);
    }

    [Fact]
    public async Task Md5_is_supported_when_the_shop_is_set_to_it()
    {
        var provider = Provider(o => o.HashAlgorithm = "MD5");

        var note = await provider.ParseNotificationAsync(Result(hash: Md5), NoHeaders);
        var session = await provider.CreateCheckoutAsync(Checkout());

        Assert.Equal(BillingEventKind.PaymentSucceeded, note!.Kind);
        Assert.Equal(Md5($"techteastudio:490.00:4242:live-one:{Shp(User, "pro-1")}"),
            QueryHelpers.ParseQuery(new Uri(session.RedirectUrl).Query)["SignatureValue"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("OutSum=490&InvId=1")]
    [InlineData("{\"json\": true}")]
    public async Task Garbage_is_rejected(string body)
    {
        Assert.Null(await Provider().ParseNotificationAsync(body, NoHeaders));
    }

    [Fact]
    public void The_result_is_answered_with_OK_and_the_invoice_number()
    {
        Assert.Equal("OK4242", Provider().ReplyTo(Result()));
        Assert.Null(Provider().ReplyTo("garbage"));
    }

    [Fact]
    public void Without_passwords_the_provider_is_not_configured()
    {
        Assert.True(Provider().IsConfigured);
        Assert.False(Provider(o => o.Password2 = null).IsConfigured);
        Assert.False(Provider(o => { o.IsTest = true; o.TestPassword1 = ""; }).IsConfigured);
    }
}
