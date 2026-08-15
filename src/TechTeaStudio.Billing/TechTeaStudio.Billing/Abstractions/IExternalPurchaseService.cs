namespace TechTeaStudio.Billing.Abstractions;

/// <summary>
/// Attribution and claiming of external-platform purchases (Ko-fi, Patreon, Boosty).
/// Providers call <see cref="ResolveBuyerAsync"/> / <see cref="HoldAsync"/> while parsing
/// a webhook; the host calls the claim methods from its settings/admin UI. Fulfillment
/// always funnels through the same idempotent pipeline as webhook payments, so a claimed
/// or manually submitted purchase can never double-grant.
/// </summary>
public interface IExternalPurchaseService
{
    /// <summary>The user's stable claim code ("CHR-K7M2P9XA"), creating one on first call.
    /// Show it wherever you point users at your Ko-fi/Boosty page and tell them to paste
    /// it into the payment message.</summary>
    Task<string> GetClaimCodeAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Resolves the buyer to an app user: first a claim code found in
    /// <see cref="ExternalPurchase.Message"/>, then a stored identity link for
    /// (provider, buyer email). A successful code match also persists the identity link
    /// so future message-less renewals attach automatically. Returns null when nothing
    /// matches - callers should then <see cref="HoldAsync"/> the purchase.
    /// </summary>
    Task<Guid?> ResolveBuyerAsync(ExternalPurchase purchase, CancellationToken ct = default);

    /// <summary>Parks a purchase in the unclaimed holding area (upsert by payment id).</summary>
    Task HoldAsync(ExternalPurchase purchase, string reason, CancellationToken ct = default);

    /// <summary>Held purchases for an admin view or a user-facing claim page.</summary>
    Task<IReadOnlyList<UnclaimedPurchase>> ListUnclaimedAsync(
        string? provider = null, string? buyerEmail = null, CancellationToken ct = default);

    /// <summary>
    /// Claims every held purchase of (provider, email) for the user and fulfills the ones
    /// with a mapped product id; unmapped ones stay held for an admin. Also persists the
    /// identity link. SECURITY: callers must pass an email whose ownership the user has
    /// PROVEN (the app's own verified email or a completed email-verification round trip) -
    /// passing user-typed input hands the purchase to whoever typed the address first.
    /// Returns the number of purchases fulfilled.
    /// </summary>
    Task<int> ClaimByEmailAsync(
        string provider, string verifiedEmail, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Claims one held purchase for the user (admin path). <paramref name="productIdOverride"/>
    /// resolves "unmapped_product" holds by naming the product explicitly. Returns false when
    /// the purchase is not held, has no product id, or fulfillment failed (the purchase is
    /// re-held in that case).
    /// </summary>
    Task<bool> ClaimAsync(
        string provider, string providerPaymentId, Guid userId,
        string? productIdOverride = null, CancellationToken ct = default);

    /// <summary>
    /// Manually submits a purchase confirmed OUTSIDE any webhook - the path for platforms
    /// with no usable API (Boosty: the creator sees the payment in the dashboard or the
    /// official Telegram bot and records it here). Idempotent on
    /// (Provider, ProviderPaymentId); requires a non-null ProductId. Also persists the
    /// identity link when the purchase carries a buyer email.
    /// </summary>
    Task<bool> SubmitAsync(ExternalPurchase purchase, Guid userId, CancellationToken ct = default);
}
