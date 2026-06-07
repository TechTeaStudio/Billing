<p align="center">
  <img src="https://raw.githubusercontent.com/TechTeaStudio/Billing/product/icon.png" alt="TechTeaStudio.Billing logo" width="160" />
</p>

<h1 align="center">TechTeaStudio.Billing</h1>

<p align="center">
  Provider-agnostic payment billing for ASP.NET Core. One seam, four gateways.
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/TechTeaStudio.Billing"><img alt="NuGet" src="https://img.shields.io/nuget/v/TechTeaStudio.Billing.svg?logo=nuget&label=NuGet" /></a>
  <a href="https://www.nuget.org/packages/TechTeaStudio.Billing"><img alt="Downloads" src="https://img.shields.io/nuget/dt/TechTeaStudio.Billing.svg?logo=nuget&label=Downloads" /></a>
  <img alt=".NET" src="https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-512BD4?logo=dotnet&logoColor=white" />
  <a href="https://github.com/TechTeaStudio/Billing/actions/workflows/dotnet.yml"><img alt="Build" src="https://img.shields.io/github/actions/workflow/status/TechTeaStudio/Billing/dotnet.yml?branch=product&logo=github&label=build" /></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/badge/license-MIT-blue.svg" /></a>
</p>

## What this gives you

A single `IBillingProvider` seam that fronts four payment gateways behind one orchestrator. Wire up Stripe, YooKassa, Shopify, or Telegram Stars (or all four at once) and your business logic never references a gateway directly.

- Hosted checkout sessions with a redirect URL for each provider.
- Verified, idempotent webhook processing. Every gateway uses its own hardened verification: HMAC for Stripe and Shopify, re-fetch from the API for YooKassa, secret-token header for Telegram.
- Pluggable `IBillingPaymentStore` so you own the payment record in your own database.
- Pluggable `IBillingFulfillment` called exactly once per confirmed payment - grant a plan, credit a wallet, send an email, whatever your domain requires.
- Plan IDs are plain strings (`"plus"`, `"pro"`, `"annual"`) - no enum coupling to your subscription model.

## Install

```bash
dotnet add package TechTeaStudio.Billing
```

## Quick start

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

## The two seams you implement

### IBillingPaymentStore

Persists payment records so the orchestrator can detect duplicate webhook deliveries.

```csharp
public interface IBillingPaymentStore
{
    Task<BillingPaymentStatus?> GetStatusAsync(
        string provider, string providerPaymentId, CancellationToken ct = default);

    Task UpsertAsync(BillingPaymentRecord record, CancellationToken ct = default);
}
```

`UpsertAsync` is called after fulfillment succeeds. Your fulfillment must be idempotent on `ProviderPaymentId` - if the store upsert fails after fulfillment, the provider will retry and the orchestrator will call fulfillment again. Checking `GetStatusAsync` inside fulfillment guards against double-granting.

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
                     IBillingFulfillment, ITelegramBot, models, enums
    Providers/       StripeBillingProvider, YooKassaBillingProvider,
                     ShopifyBillingProvider, TelegramStarsBillingProvider,
                     StripeSignature, ShopifySignature, options
    Services/        BillingService
    AspNetCore/      BillingServiceCollectionExtensions, IBillingBuilder
  TechTeaStudio.Billing.Tests/
    tests for signatures, payload encoding, and orchestrator idempotency
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
