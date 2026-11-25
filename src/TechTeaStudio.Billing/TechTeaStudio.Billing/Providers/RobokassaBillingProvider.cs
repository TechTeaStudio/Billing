using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechTeaStudio.Billing.Abstractions;

namespace TechTeaStudio.Billing.Providers;

/// <summary>
/// Robokassa (RU market), the provider a self-employed seller can use with receipts sent to
/// Moy Nalog by Robokassa itself. The payment link is signed with Password #1 over
/// "MerchantLogin:OutSum:InvId:Password1[:Shp_*]"; Robokassa calls the shop's Result URL with a
/// form signed with Password #2 over "OutSum:InvId:Password2[:Shp_*]", and that notification is
/// the only proof of payment. The buyer and the plan travel as Shp_user and Shp_plan, so they are
/// covered by both signatures. The invoice number (InvId) is the idempotency key.
/// </summary>
public sealed class RobokassaBillingProvider : IBillingProvider, IBillingWebhookReply
{
    private const string Currency = "RUB";
    private readonly RobokassaBillingOptions _opts;
    private readonly ILogger<RobokassaBillingProvider> _log;

    public RobokassaBillingProvider(IOptions<RobokassaBillingOptions> opts, ILogger<RobokassaBillingProvider> log)
    {
        _opts = opts.Value;
        _log = log;
    }

    public string Name => "robokassa";
    public string DisplayName => "Robokassa";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_opts.MerchantLogin)
        && !string.IsNullOrWhiteSpace(Password1)
        && !string.IsNullOrWhiteSpace(Password2);

    private string? Password1 => _opts.IsTest ? _opts.TestPassword1 : _opts.Password1;
    private string? Password2 => _opts.IsTest ? _opts.TestPassword2 : _opts.Password2;

    public async Task<CheckoutSession> CreateCheckoutAsync(BillingCheckoutRequest req, CancellationToken ct = default)
    {
        var amount = AmountFor(req.PlanId);
        if (amount <= 0)
            throw new InvalidOperationException($"No Robokassa amount configured for plan '{req.PlanId}'.");

        var invId = _opts.InvoiceIdFactory is { } factory
            ? await factory(req, ct)
            : RandomNumberGenerator.GetInt32(1, int.MaxValue);
        if (invId <= 0) throw new InvalidOperationException("The invoice number must be positive.");

        var outSum = amount.ToString("0.00", CultureInfo.InvariantCulture);
        var inv = invId.ToString(CultureInfo.InvariantCulture);
        var shp = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["Shp_plan"] = req.PlanId,
            ["Shp_user"] = req.UserId.ToString("N"),
        };
        // The cart for the receipt: minimized JSON, URL-encoded once, signed right after InvId
        // ("MerchantLogin:OutSum:InvId:Receipt:Password1[:Shp_*]") and encoded again in the link.
        var receipt = _opts.SendReceipt ? Uri.EscapeDataString(ReceiptJson(req.PlanName, outSum)) : null;
        var signature = Hash($"{_opts.MerchantLogin}:{outSum}:{inv}:{(receipt is null ? "" : receipt + ":")}{Password1}{ShpTail(shp)}");

        // Built by hand: QueryHelpers.AddQueryString changed its signature between net5 and net6.
        var query = new List<(string Key, string Value)>
        {
            ("MerchantLogin", _opts.MerchantLogin!),
            ("OutSum", outSum),
            ("InvId", inv),
            ("Description", req.PlanName.Length > 100 ? req.PlanName.Substring(0, 100) : req.PlanName),
            ("SignatureValue", signature),
        };
        if (receipt is not null) query.Add(("Receipt", receipt));
        if (!string.IsNullOrWhiteSpace(req.Email)) query.Add(("Email", req.Email));
        if (_opts.IsTest) query.Add(("IsTest", "1"));
        foreach (var kv in shp) query.Add((kv.Key, kv.Value));

        var url = _opts.PaymentUrl + "?" + string.Join("&",
            query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        return new CheckoutSession(url, Name, inv);
    }

    public Task<BillingNotification?> ParseNotificationAsync(
        string rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
    {
        return Task.FromResult(Parse(rawBody));
    }

    /// <summary>"OK" and the invoice number: Robokassa's sign that the notification arrived.</summary>
    public string? ReplyTo(string rawBody) =>
        Read(rawBody) is { } form && TryInvoice(form, out var inv) ? "OK" + inv.ToString(CultureInfo.InvariantCulture) : null;

    private BillingNotification? Parse(string rawBody)
    {
        if (!IsConfigured || Read(rawBody) is not { } form) return null;
        if (!form.TryGetValue("OutSum", out var outSum) || !TryInvoice(form, out var invId)
            || !form.TryGetValue("SignatureValue", out var received)) return null;

        // A live shop takes no test payment and a test shop no live one: whoever holds the test
        // pair must not get what a real payment buys.
        var isTest = form.TryGetValue("IsTest", out var t) && t == "1";
        if (isTest != _opts.IsTest)
        {
            _log.LogWarning("Robokassa {Kind} payment {InvId} refused on a {Shop} shop.",
                isTest ? "test" : "live", invId, _opts.IsTest ? "test" : "live");
            return Irrelevant();
        }

        var shp = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in form)
            if (kv.Key.StartsWith("Shp_", StringComparison.OrdinalIgnoreCase)) shp[kv.Key] = kv.Value;

        // The signature covers the strings exactly as posted, OutSum included ("490.000000").
        var expected = Hash($"{outSum}:{invId.ToString(CultureInfo.InvariantCulture)}:{Password2}{ShpTail(shp)}");
        if (!SameHex(expected, received))
        {
            _log.LogWarning("Robokassa notification {InvId} with a wrong signature.", invId);
            return null;
        }

        var planId = shp.TryGetValue("Shp_plan", out var p) ? p : "";
        var userId = shp.TryGetValue("Shp_user", out var u) && Guid.TryParse(u, out var g) ? g : Guid.Empty;
        if (!decimal.TryParse(outSum, NumberStyles.Number, CultureInfo.InvariantCulture, out var paid)) return null;
        var paidMinor = (long)Math.Round(paid * 100m);

        // Defence in depth: only grant if the payment covers the plan's price.
        var expectedMinor = (long)Math.Round(AmountFor(planId) * 100m);
        if (expectedMinor <= 0 || paidMinor < expectedMinor)
        {
            _log.LogWarning("Robokassa payment {InvId} does not cover plan '{Plan}': {Paid} minor, expected {Expected}.",
                invId, planId, paidMinor, expectedMinor);
            return Irrelevant();
        }
        if (userId == Guid.Empty)
        {
            _log.LogWarning("Robokassa payment {InvId} carries no user id.", invId);
            return Irrelevant();
        }

        return new BillingNotification(BillingEventKind.PaymentSucceeded, userId, planId,
            invId.ToString(CultureInfo.InvariantCulture), paidMinor, Currency);
    }

    private static Dictionary<string, string>? Read(string rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody) || rawBody.TrimStart().StartsWith("{", StringComparison.Ordinal)) return null;
        try
        {
            return QueryHelpers.ParseQuery(rawBody)
                .ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.Ordinal);
        }
        catch (Exception) { return null; }
    }

    private static bool TryInvoice(Dictionary<string, string> form, out long invId)
    {
        invId = 0;
        return form.TryGetValue("InvId", out var raw)
            && long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out invId)
            && invId > 0;
    }

    /// <summary>One item: the plan, once, for the whole sum. Robokassa allows 128 characters of name.</summary>
    private string ReceiptJson(string planName, string outSum)
    {
        var name = planName.Length > 128 ? planName.Substring(0, 128) : planName;
        return "{\"items\":[{\"name\":" + JsonSerializer.Serialize(name) + ",\"quantity\":1,\"sum\":" + outSum
            + ",\"payment_method\":" + JsonSerializer.Serialize(_opts.ReceiptPaymentMethod)
            + ",\"payment_object\":" + JsonSerializer.Serialize(_opts.ReceiptPaymentObject)
            + ",\"tax\":" + JsonSerializer.Serialize(_opts.ReceiptTax) + "}]}";
    }

    private static string ShpTail(SortedDictionary<string, string> shp)
    {
        var sb = new StringBuilder();
        foreach (var kv in shp) sb.Append(':').Append(kv.Key).Append('=').Append(kv.Value);
        return sb.ToString();
    }

    private string Hash(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using HashAlgorithm algorithm = _opts.HashAlgorithm.ToUpperInvariant() switch
        {
            "MD5" => MD5.Create(),
            "SHA1" => SHA1.Create(),
            "SHA256" => SHA256.Create(),
            "SHA384" => SHA384.Create(),
            "SHA512" => SHA512.Create(),
            var other => throw new InvalidOperationException($"Robokassa hash '{other}' is not supported."),
        };
        return Convert.ToHexString(algorithm.ComputeHash(bytes));
    }

    /// <summary>Hex digests compared in constant time and without regard to case.</summary>
    private static bool SameHex(string expected, string received)
    {
        var a = Encoding.ASCII.GetBytes(expected.ToUpperInvariant());
        var b = Encoding.ASCII.GetBytes((received ?? "").ToUpperInvariant());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private decimal AmountFor(string planId) =>
        _opts.Amounts.TryGetValue(planId, out var a) ? a : 0m;

    private static BillingNotification Irrelevant() =>
        new(BillingEventKind.Unknown, Guid.Empty, "", "", 0, "");
}
