using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechTeaStudio.Billing.Abstractions;

namespace TechTeaStudio.Billing.Services;

/// <summary>
/// Default implementation of <see cref="IExternalPurchaseService"/>. Attribution order:
/// claim code in the payment message, then the (provider, email) identity link, then the
/// unclaimed holding area. All fulfillment funnels through <see cref="PaymentPipeline"/>,
/// so claims and manual submissions share the webhook path's exactly-once guarantee.
/// </summary>
internal sealed class ExternalPurchaseService : IExternalPurchaseService
{
    private readonly IClaimCodeStore _codes;
    private readonly IExternalIdentityLinkStore _links;
    private readonly IUnclaimedPurchaseStore _unclaimed;
    private readonly PaymentPipeline _pipeline;
    private readonly ExternalPurchaseOptions _opts;
    private readonly ILogger<ExternalPurchaseService> _log;

    public ExternalPurchaseService(
        IClaimCodeStore codes,
        IExternalIdentityLinkStore links,
        IUnclaimedPurchaseStore unclaimed,
        PaymentPipeline pipeline,
        IOptions<ExternalPurchaseOptions> opts,
        ILogger<ExternalPurchaseService> log)
    {
        _codes = codes;
        _links = links;
        _unclaimed = unclaimed;
        _pipeline = pipeline;
        _opts = opts.Value;
        _log = log;
    }

    public Task<string> GetClaimCodeAsync(Guid userId, CancellationToken ct = default) =>
        _codes.GetOrAddAsync(userId, ClaimCode.Generate(_opts.ClaimCodePrefix), ct);

    public async Task<Guid?> ResolveBuyerAsync(
        ExternalPurchase purchase, CancellationToken ct = default)
    {
        if (ClaimCode.TryExtract(purchase.Message, _opts.ClaimCodePrefix, out var code))
        {
            var user = await _codes.FindUserAsync(code, ct);
            if (user is not null)
            {
                // Persist the identity so message-less renewals attach automatically.
                if (NormalizeEmail(purchase.BuyerEmail) is { } email)
                    await _links.LinkAsync(ProviderKey(purchase.Provider), email, user.Value, ct);
                return user;
            }
            _log.LogWarning(
                "Claim code {Code} in a {Provider} payment matches no user.",
                code, purchase.Provider);
        }

        if (NormalizeEmail(purchase.BuyerEmail) is { } knownEmail)
            return await _links.FindUserAsync(ProviderKey(purchase.Provider), knownEmail, ct);

        return null;
    }

    public async Task HoldAsync(
        ExternalPurchase purchase, string reason, CancellationToken ct = default)
    {
        await _unclaimed.AddAsync(
            new UnclaimedPurchase(purchase, reason, DateTimeOffset.UtcNow), ct);
        _log.LogInformation(
            "Held {Provider} payment {PaymentId} ({Amount} {Currency}): {Reason}.",
            purchase.Provider, purchase.ProviderPaymentId,
            purchase.AmountMinor, purchase.Currency, reason);
    }

    public Task<IReadOnlyList<UnclaimedPurchase>> ListUnclaimedAsync(
        string? provider = null, string? buyerEmail = null, CancellationToken ct = default) =>
        _unclaimed.ListAsync(provider, buyerEmail, ct);

    public async Task<int> ClaimByEmailAsync(
        string provider, string verifiedEmail, Guid userId, CancellationToken ct = default)
    {
        var providerKey = ProviderKey(provider);
        if (NormalizeEmail(verifiedEmail) is not { } email)
        {
            _log.LogWarning("ClaimByEmail called with an empty email for {Provider}.", provider);
            return 0;
        }

        // The link first: even with nothing currently held, future renewals now attach.
        await _links.LinkAsync(providerKey, email, userId, ct);

        var held = await _unclaimed.ListAsync(providerKey, email, ct);
        var granted = 0;
        foreach (var h in held)
        {
            var outcome = await ClaimCoreAsync(
                providerKey, h.Purchase.ProviderPaymentId, userId, null, ct);
            // Only a first-time grant counts: a payment that was already fulfilled or
            // refunded consumes its stale hold without giving the user anything.
            if (outcome == PaymentOutcome.Fulfilled) granted++;
        }
        return granted;
    }

    public async Task<bool> ClaimAsync(
        string provider, string providerPaymentId, Guid userId,
        string? productIdOverride = null, CancellationToken ct = default) =>
        await ClaimCoreAsync(
            ProviderKey(provider), providerPaymentId, userId, productIdOverride, ct)
            == PaymentOutcome.Fulfilled;

    public async Task<bool> SubmitAsync(
        ExternalPurchase purchase, Guid userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(purchase.ProductId))
        {
            _log.LogWarning(
                "Manual submit of {Provider} payment {PaymentId} refused: no ProductId.",
                purchase.Provider, purchase.ProviderPaymentId);
            return false;
        }
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(purchase.ProviderPaymentId))
            return false;

        var providerKey = ProviderKey(purchase.Provider);
        if (NormalizeEmail(purchase.BuyerEmail) is { } email)
            await _links.LinkAsync(providerKey, email, userId, ct);

        var outcome = await _pipeline.ProcessAsync(
            providerKey,
            new BillingNotification(
                BillingEventKind.PaymentSucceeded, userId, purchase.ProductId,
                purchase.ProviderPaymentId, purchase.AmountMinor, purchase.Currency), ct);
        // Duplicate == success: a resubmitted payment is accepted without granting twice.
        return outcome != PaymentOutcome.Failed;
    }

    // ---- Internals --------------------------------------------------------------

    /// <summary>Claims one held purchase. The hold is only consumed when the payment reached
    /// a terminal outcome; ANY failure - including an exception from a flaky store - puts it
    /// back, because a held purchase is money already received and must never vanish.</summary>
    private async Task<PaymentOutcome> ClaimCoreAsync(
        string providerKey, string providerPaymentId, Guid userId,
        string? productIdOverride, CancellationToken ct)
    {
        var taken = await _unclaimed.TakeAsync(providerKey, providerPaymentId, ct);
        if (taken is null) return PaymentOutcome.Failed;

        var productId = productIdOverride ?? taken.Purchase.ProductId;
        if (string.IsNullOrWhiteSpace(productId))
        {
            // Still no product to grant - keep it held for an admin with an override.
            await ReHoldAsync(taken);
            return PaymentOutcome.Failed;
        }

        PaymentOutcome outcome;
        try
        {
            outcome = await _pipeline.ProcessAsync(
                providerKey,
                new BillingNotification(
                    BillingEventKind.PaymentSucceeded, userId, productId,
                    taken.Purchase.ProviderPaymentId, taken.Purchase.AmountMinor,
                    taken.Purchase.Currency), ct);
        }
        catch (Exception ex)
        {
            // The pipeline can still throw around fulfillment (a flaky payment store, a
            // canceled request). Without this the purchase would be gone from every store.
            _log.LogError(ex,
                "Claim of {Provider} payment {PaymentId} threw - re-holding the purchase.",
                providerKey, providerPaymentId);
            await ReHoldAsync(taken);
            return PaymentOutcome.Failed;
        }

        if (outcome == PaymentOutcome.Failed)
        {
            await ReHoldAsync(taken);
            return outcome;
        }

        // Persist the identity so refunds and the next renewal find this account. Admin
        // claims went through this path too, which is why the link is written here and not
        // only in ClaimByEmailAsync.
        if (NormalizeEmail(taken.Purchase.BuyerEmail) is { } email)
            await _links.LinkAsync(providerKey, email, userId, CancellationToken.None);

        return outcome;
    }

    /// <summary>Puts a taken purchase back. Deliberately uncancellable: the usual reason the
    /// claim failed is a canceled request, and honoring that token here would drop the money.</summary>
    private async Task ReHoldAsync(UnclaimedPurchase taken)
    {
        try
        {
            await _unclaimed.AddAsync(taken, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Nothing left to do but shout: this is received money with no home.
            _log.LogCritical(ex,
                "LOST HOLD: {Provider} payment {PaymentId} ({Amount} {Currency}) was taken from " +
                "the unclaimed store and could not be put back. Restore it manually.",
                taken.Purchase.Provider, taken.Purchase.ProviderPaymentId,
                taken.Purchase.AmountMinor, taken.Purchase.Currency);
        }
    }

    private static string ProviderKey(string provider) => provider.Trim().ToLowerInvariant();

    private static string? NormalizeEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
}
