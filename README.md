<p align="center">
  <img src="https://raw.githubusercontent.com/TechTeaStudio/Billing/product/icon.png" alt="TechTeaStudio.Billing logo" width="160" />
</p>

<h1 align="center">TechTeaStudio.Billing</h1>

<p align="center">
  Provider-agnostic payment billing for ASP.NET Core. One seam: Stripe, YooKassa, Shopify, Telegram Stars, Ko-fi, Patreon.
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/TechTeaStudio.Billing"><img alt="NuGet" src="https://img.shields.io/nuget/v/TechTeaStudio.Billing.svg?logo=nuget&label=NuGet" /></a>
  <a href="https://www.nuget.org/packages/TechTeaStudio.Billing"><img alt="Downloads" src="https://img.shields.io/nuget/dt/TechTeaStudio.Billing.svg?logo=nuget&label=Downloads" /></a>
  <img alt=".NET" src="https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-512BD4?logo=dotnet&logoColor=white" />
  <a href="https://github.com/TechTeaStudio/Billing/actions/workflows/dotnet.yml"><img alt="Build" src="https://img.shields.io/github/actions/workflow/status/TechTeaStudio/Billing/dotnet.yml?branch=product&logo=github&label=build" /></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/badge/license-MIT-blue.svg" /></a>
</p>

## Quick start (5 minutes)

Pass your configuration and billing works - providers self-detect from config (`IsConfigured`). Only the sections you fill in activate.

```csharp
// Program.cs
builder.Services.AddTechTeaStudioBilling(builder.Configuration)
    .OnPaymentSucceeded(async (sp, note, ct) =>
    {
        // note.PlanId, note.UserId, note.AmountMinor - grant the purchase here
    });

var app = builder.Build();
app.MapTechTeaStudioBilling();   // POST /billing/webhook/{provider} + /billing/telegram/bot
```

```json
// appsettings.json - configure only the provider(s) you use
"Billing": {
  "YooKassa": {
    "ShopId": "...",
    "SecretKey": "...",
    "Currency": "RUB",
    "Amounts": { "plus": 299, "pro": 599 }
  },
  "Telegram": {
    "BotToken": "...",
    "BotUsername": "mybot",
    "WebhookSecret": "...",
    "Plans": {
      "plus": { "Stars": 100, "Title": "Plus", "Description": "Plus tier" }
    }
  }
}
```

Providers whose config section is absent or empty stay dormant - their `IsConfigured` is `false` and they are hidden from the upgrade UI. The in-memory payment store is the dev default: idempotency does not survive a restart or scale-out. Call `UsePaymentStore<T>()` with a persistent store in production.

## What this gives you

A single `IBillingProvider` seam that fronts six payment surfaces behind one orchestrator. Wire up Stripe, YooKassa, Shopify, Telegram Stars, Ko-fi, or Patreon (or all at once) and your business logic never references a gateway directly.

- Hosted checkout sessions with a redirect URL for each gateway provider.
- Verified, idempotent webhook processing. Every provider uses its own hardened verification: HMAC-SHA256 for Stripe and Shopify, re-fetch from the API for YooKassa, secret-token header for Telegram, constant-time verification token for Ko-fi, HMAC-MD5 (Patreon's mandated scheme) for Patreon.
- External purchases: donations and memberships that happen ON the platform (Ko-fi, Patreon, Boosty) are attributed to app users via claim codes and identity links; anything unattributable lands in an unclaimed holding area instead of being lost. See "External purchases" below.
- Refund tracking: a refunded payment is marked `Refunded` and a redelivered success webhook can never re-grant it.
- Pluggable `IBillingPaymentStore` so you own the payment record in your own database.
- Pluggable `IBillingFulfillment` called exactly once per confirmed payment - grant a plan, credit a wallet, send an email, whatever your domain requires.
- Plan/product IDs are plain strings (`"plus"`, `"pro"`, `"credits100"`) - no enum coupling to your subscription model. `BillingPlanGuard` refuses checkout for plans whose provider price is 0/absent (a configured 0 is a money hole).

## Install

```bash
dotnet add package TechTeaStudio.Billing
```

## Manual setup (full control)

Use the parameterless overload when you need to configure providers programmatically instead of from appsettings.

```csharp
// Program.cs
builder.Services
    .AddTechTeaStudioBilling()
    .AddStripe(o =>
    {
        o.SecretKey = builder.Configuration["Billing:Stripe:SecretKey"];
        o.WebhookSecret = builder.Configuration["Billing:Stripe:WebhookSecret"];
        o.Mode = "subscription";
        o.PriceIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["plus"] = "price_plus_monthly",
            ["pro"]  = "price_pro_monthly",
        };
    })
    .UsePaymentStore<MyBillingPaymentStore>()
    .UseFulfillment<MyBillingFulfillment>();
```

Minimal-API webhook endpoint:

```csharp
app.MapPost("/billing/webhook/{provider}", async (
    string provider,
    HttpRequest request,
    IBillingService billing,
    CancellationToken ct) =>
{
    var body = await new StreamReader(request.Body).ReadToEndAsync(ct);
    var headers = request.Headers
        .ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

    var accepted = await billing.HandleWebhookAsync(provider, body, headers, ct);
    return accepted ? Results.Ok() : Results.BadRequest();
});
```

Start a checkout from a controller or service:

```csharp
var session = await billing.StartCheckoutAsync(
    new BillingCheckoutRequest(
        UserId:     user.Id,
        Email:      user.Email,
        PlanId:     "plus",
        PlanName:   "Chronos Plus",
        ReturnUrl:  "https://myapp.com/billing/success",
        CancelUrl:  "https://myapp.com/billing/cancel"),
    providerName: "stripe",
    ct);

if (session is null) return BadRequest("Provider unavailable or plan not billable.");
return Redirect(session.RedirectUrl);
```

## Providers

| Provider | Verification method | Notes |
|---|---|---|
| Stripe | HMAC-SHA256 of `{timestamp}.{body}` via `Stripe-Signature` header | 5-minute replay window; `payment_status == "paid"` guard |
| YooKassa | Re-fetch payment from the API (webhook body is unsigned) | Underpaid defence compares amount to `Amounts[planId]` |
| Shopify | HMAC-SHA256 Base64 via `X-Shopify-Hmac-Sha256` header | Underpaid defence on `note_attributes` - buyer-influenceable |
| Telegram Stars | `X-Telegram-Bot-Api-Secret-Token` exact-match | Deep-link checkout; bot sends XTR invoice; `WebhookSecret` required |
| Ko-fi | Static `verification_token` inside the payload, constant-time compare (Ko-fi offers no HMAC) | Body is form-urlencoded with a `data` field carrying JSON; no refund/cancel webhooks exist - time-box entitlements |
| Patreon | HMAC-MD5 lowercase hex of the RAW body via `X-Patreon-Signature` (Patreon's mandated scheme) | Per-WEBHOOK secret, not the OAuth secret; `members:*` triggers; refunds recorded; portal "Send Test" payloads do not match production |

## External purchases (Ko-fi, Patreon, Boosty)

Gateway checkouts know the buyer up front. Donation platforms do not: a Ko-fi payment arrives carrying only an email, a display name, and a typed message. The external-purchase layer turns those into app-user grants safely:

1. **Claim codes.** `IExternalPurchaseService.GetClaimCodeAsync(userId)` returns the user's stable code (`CHR-K7M2P9XA`; prefix from `Billing:External:ClaimCodePrefix`). Your UI shows it next to the "Support us on Ko-fi" link; the buyer pastes it into the payment message. The webhook extracts it and attributes the payment - and persists a (provider, email) identity link so message-less subscription RENEWALS attach automatically (Ko-fi sends the message only on one-time payments).
2. **Product mapping.** Platform tiers/items map to your product ids in options: `Billing:Kofi:TierProducts:Gold Tier = pro`, `Billing:Patreon:TierProducts:9012345 = pro` (Patreon maps by tier ID, not title), `Billing:Kofi:ShopItemProducts:<direct_link_code> = ...`, `Billing:Kofi:DonationProductId` for plain donations. What a product id means is your fulfillment's decision.
3. **Unclaimed holding area.** A payment with no code, no link, or no product mapping is parked in `IUnclaimedPurchaseStore` - money already received must never be dropped. Claim paths: `ClaimByEmailAsync(provider, verifiedEmail, userId)` (pass ONLY emails whose ownership the user has proven), targeted `ClaimAsync(...)` with an optional product override for admins, and `ListUnclaimedAsync(...)` for the admin view.
4. **Boosty.** Boosty has NO official API and NO webhooks (everything on api.boosty.to is reverse-engineered from the SPA and unstable, with a browser-session token). The supported path is therefore manual: the creator sees the payment in the Boosty dashboard or the official @boosty_to_bot Telegram notifications and records it with `SubmitAsync(purchase, userId)` - same idempotent pipeline, provider name `"boosty"`. Claim codes still help: ask subscribers to send theirs via Boosty DM.

Production checklist: the three attribution stores default to in-memory dev implementations - swap ALL of them (`UseClaimCodeStore<T>()`, `UseExternalIdentityLinkStore<T>()`, `UseUnclaimedPurchaseStore<T>()`) with database-backed stores, exactly like `UsePaymentStore<T>()`. Ko-fi cannot notify you about refunds or membership endings, so grant time-boxed entitlements keyed to the last renewal payment and revoke manually on refunds; Patreon refunds arrive via `members:update` and are recorded as `Refunded` automatically; Patreon decline states are unreliable - reconcile periodically against `GET /api/oauth2/v2/campaigns/{id}/members`.

## The two seams you implement

### IBillingPaymentStore

Persists payment records so the orchestrator can detect duplicate webhook deliveries.

```csharp
public interface IBillingPaymentStore
{
    Task<BillingPaymentStatus?> GetStatusAsync(
        string provider, string providerPaymentId, CancellationToken ct = default);

    Task UpsertAsync(BillingPaymentRecord record, CancellationToken ct = default);

    // Both have default implementations - override them in production (see below).
    Task<bool> TryReserveAsync(BillingPaymentRecord pending, CancellationToken ct = default);
    Task MarkRefundedAsync(BillingPaymentRecord refundContext, CancellationToken ct = default);
}
```

`TryReserveAsync` decides who fulfills. The default is a status read, which stops a finished payment from being fulfilled again but cannot separate two *simultaneous* first deliveries - so **override it with an atomic insert-first claim** (`INSERT ... ON CONFLICT DO NOTHING`, or a unique index on `(Provider, ProviderPaymentId)` and catching the duplicate-key exception): the caller that inserts the Pending row wins, everyone else gets `false`. Until you do, fulfillment must be idempotent on `ProviderPaymentId`.

`MarkRefundedAsync` receives refund-time values, which are often worse than what you already stored - a fully refunded Patreon membership reports no tier and zero cents, and the buyer may be unattributable. **Override it to flip only the status** of the existing row so `UserId`/`PlanId`/`AmountMinor` survive; those are what you revoke against. When no row exists (a refund that overtook its own success webhook) the record is a tombstone and must still be written, otherwise the late success would fulfill refunded money.

`UpsertAsync` is called after fulfillment succeeds. If it fails at that point the provider will retry and fulfillment runs again - another reason fulfillment should be idempotent.

### IBillingFulfillment

Called exactly once per confirmed payment (idempotency is enforced by the store).

```csharp
public interface IBillingFulfillment
{
    Task OnPaymentSucceededAsync(
        BillingNotification notification, CancellationToken ct = default);
}
```

`notification.PlanId` carries the plan the user paid for. `notification.UserId` is the user to fulfill. Grant the subscription tier, send a welcome email, top up a credit wallet - your call.

## Project layout

```
src/TechTeaStudio.Billing/
  TechTeaStudio.Billing/
    Abstractions/    IBillingProvider, IBillingService, IBillingPaymentStore,
                     IBillingFulfillment, ITelegramBot, IExternalPurchaseService,
                     IClaimCodeStore, IExternalIdentityLinkStore,
                     IUnclaimedPurchaseStore, ClaimCode, models, enums
    Providers/       StripeBillingProvider, YooKassaBillingProvider,
                     ShopifyBillingProvider, TelegramStarsBillingProvider,
                     KofiBillingProvider, PatreonBillingProvider,
                     StripeSignature, ShopifySignature, PatreonSignature, options
    Services/        BillingService, ExternalPurchaseService, PaymentPipeline,
                     in-memory dev stores
    AspNetCore/      BillingServiceCollectionExtensions, IBillingBuilder
    BillingPlanGuard.cs
  TechTeaStudio.Billing.Tests/
    tests for signatures, payload encoding, provider parsing, attribution,
    refunds, and orchestrator idempotency
```

## Build and test

```bash
dotnet build src/TechTeaStudio.Billing/TechTeaStudio.Billing.sln -c Release
dotnet test  src/TechTeaStudio.Billing/TechTeaStudio.Billing.sln -c Release
```

## Versioning and release

Commit format: `vX.Y.Z Short description` (one line, 72 chars max).

The release branch is `product`. GitHub Actions publish to NuGet on push to `product`.

## License

MIT. See [LICENSE](LICENSE).
