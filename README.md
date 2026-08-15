<p align="center">
  <img src="https://raw.githubusercontent.com/TechTeaStudio/Billing/product/src/TechTeaStudio.Billing/icon.png" alt="TechTeaStudio.Billing logo" width="160" />
</p>

<h1 align="center">TechTeaStudio.Billing</h1>

<p align="center">
  Provider-agnostic payment billing for ASP.NET Core. One seam: Stripe, YooKassa, Shopify, Telegram Stars, Ko-fi, Patreon.
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/TechTeaStudio.Billing"><img alt="NuGet" src="https://img.shields.io/nuget/v/TechTeaStudio.Billing.svg?logo=nuget&label=NuGet" /></a>
  <a href="https://www.nuget.org/packages/TechTeaStudio.Billing"><img alt="Downloads" src="https://img.shields.io/nuget/dt/TechTeaStudio.Billing.svg?logo=nuget&label=Downloads" /></a>
  <a href="https://www.nuget.org/packages/TechTeaStudio.Billing.Abstractions"><img alt="Abstractions" src="https://img.shields.io/nuget/v/TechTeaStudio.Billing.Abstractions.svg?logo=nuget&label=Abstractions" /></a>
  <img alt=".NET" src="https://img.shields.io/badge/.NET-5.0%20%E2%86%92%2010.0-512BD4?logo=dotnet&logoColor=white" />
  <a href="https://github.com/TechTeaStudio/Billing/actions/workflows/dotnet.yml"><img alt="Build" src="https://img.shields.io/github/actions/workflow/status/TechTeaStudio/Billing/dotnet.yml?branch=product&logo=github&label=build" /></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/badge/license-MIT-blue.svg" /></a>
</p>

## What this gives you

A single `IBillingProvider` seam that fronts six payment surfaces behind one orchestrator. Wire up Stripe, YooKassa, Shopify, Telegram Stars, Ko-fi, or Patreon (or all at once) and your business logic never references a gateway directly.

- Hosted checkout sessions with a redirect URL for each gateway provider.
- Verified, idempotent webhook processing. Every provider uses its own hardened verification: HMAC-SHA256 for Stripe and Shopify, re-fetch from the API for YooKassa, secret-token header for Telegram, constant-time verification token for Ko-fi, HMAC-MD5 (Patreon's mandated scheme) for Patreon.
- External purchases: donations and memberships that happen ON the platform (Ko-fi, Patreon, Boosty) are attributed to app users via claim codes and identity links; anything unattributable lands in an unclaimed holding area instead of being lost. See "External purchases" below.
- Refund tracking: a refunded payment is marked `Refunded` and a redelivered success webhook can never re-grant it.
- Pluggable `IBillingPaymentStore` so you own the payment record in your own database.
- Pluggable `IBillingFulfillment` called exactly once per confirmed payment - grant a plan, credit a wallet, send an email, whatever your domain requires.
- Plan/product IDs are plain strings (`"plus"`, `"pro"`, `"credits100"`) - no enum coupling to your subscription model. `BillingPlanGuard` refuses checkout for plans whose provider price is 0/absent (a configured 0 is a money hole).

## How it compares

| Approach | Providers | Webhook verification | Idempotency | Donation platforms |
|---|---|---|---|---|
| Official SDKs (Stripe.net, YooKassa SDK, ShopifySharp) | one each, different shapes | each SDK's own helper | you write it | not covered |
| A hand-rolled webhook controller | whatever you write | you write the HMAC, and the constant-time compare, and the replay window | you write it | you write it |
| Payment-page links only (Ko-fi / Patreon buttons) | none in code | none | none | no attribution - you reconcile by hand |
| **TechTeaStudio.Billing** | six behind one `IBillingProvider` | per-provider, hardened, unit-tested against official payload samples | `TryReserveAsync` claim on `(provider, paymentId)` | claim codes, identity links, unclaimed holding area |

The trade this makes: no SDK means no typed access to the parts of each gateway's API this library does not use. If you need Stripe subscriptions management, invoices or the customer portal, call Stripe.net for those and let this handle checkout plus the webhook.

## Install

```bash
dotnet add package TechTeaStudio.Billing
```

That is all most apps need - it pulls in the contracts package below automatically.

## Packages

| Package | What it holds | Frameworks | References |
|---|---|---|---|
| `TechTeaStudio.Billing` | Providers, orchestrator, DI wiring, webhook endpoints, in-memory dev stores | net5.0 → net10.0 | ASP.NET Core shared framework + `.Abstractions` |
| `TechTeaStudio.Billing.Abstractions` | `IBillingProvider`, `IBillingService`, `IBillingPaymentStore`, `IBillingFulfillment`, `IExternalPurchaseService`, `ITelegramBot`, the attribution stores, the models, `ClaimCode`, `BillingPlanGuard` | netstandard2.0, net472, net5.0 → net10.0 | Nothing at all |

The split exists for one reason: the main package carries `<FrameworkReference Include="Microsoft.AspNetCore.App" />`, and that is contagious. Reference it from a plain domain or application class library just to implement `IBillingFulfillment` and that library is now an ASP.NET Core library too. So the seams you implement live in a package with no dependencies and no framework reference:

```bash
# In the class library that implements the seams - no web stack attached.
dotnet add package TechTeaStudio.Billing.Abstractions
```

```bash
# In the web project that wires it all up.
dotnet add package TechTeaStudio.Billing
```

Namespaces did not move (`TechTeaStudio.Billing.Abstractions`, `TechTeaStudio.Billing`), so this is not a source-breaking change - code written against v0.3.0 compiles unchanged.

It stops at two packages deliberately. A third, holding the providers and services so worker services and console hosts could use them without ASP.NET Core, was considered and rejected for now: `KofiBillingProvider` parses the Ko-fi form body with `Microsoft.AspNetCore.WebUtilities.FormReader`, so that package would mean hand-rolling form parsing in a verified-webhook path, and it would trade zero package dependencies for five pinned `Microsoft.Extensions.*` references. Neither is worth it for 3k lines. Per-provider packages are rejected outright - every provider here is raw `HttpClient` with no SDK to isolate.

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

## Target frameworks

`TechTeaStudio.Billing` ships `net5.0`, `net6.0`, `net7.0`, `net8.0`, `net9.0` and `net10.0`. `TechTeaStudio.Billing.Abstractions` adds `netstandard2.0` and `net472`.

### .NET Framework

The contracts run on **.NET Framework 4.6.2+**; the library itself does not, and cannot. `TechTeaStudio.Billing` carries `FrameworkReference Microsoft.AspNetCore.App`, and ASP.NET Core has not run on .NET Framework since version 3.0 - the endpoint mapping, `IEndpointRouteBuilder` and `Microsoft.AspNetCore.WebUtilities` all have no .NET Framework asset. Trying to target it fails at restore with `NETSDK1073`.

What the netstandard2.0/net472 assets are for is sharing contracts across a mixed estate: a domain assembly multi-targeting `net472;net8.0` can define its `IBillingFulfillment`, hold `BillingNotification`/`BillingPaymentRecord`, issue codes with `ClaimCode` and check prices with `BillingPlanGuard`, while the ASP.NET Core host that actually receives the webhooks references the same contracts from `net8.0`.

One deliberate API difference on those two frameworks: neither runtime supports default interface members, so `IBillingPaymentStore.TryReserveAsync` and `MarkRefundedAsync` are **required** members there rather than defaulted. That is what the documentation asks production stores to do anyway - the defaults only ever existed so pre-0.3.0 stores kept compiling - and it affects no existing consumer, since no .NET Framework build of this package existed before.

The webhook endpoints are mapped as `RequestDelegate` handlers rather than minimal-API lambdas, because `Results.*` and the `Delegate` overload of `MapPost` are .NET 6+ while the `RequestDelegate` overload has existed since ASP.NET Core 3.0. Routes, status codes and behaviour are identical on every framework; there is exactly one `#if` in the whole library, for the `StreamReader.ReadToEndAsync` overload that gained a `CancellationToken` in .NET 7.

Building the older frameworks needs no old SDK installed - the .NET 10 SDK downloads the reference packs from NuGet. Note that **net5.0, net6.0 and net7.0 are out of support from Microsoft**. They are carried so a host pinned to an older runtime can still take fixes from this library; if you get to choose, target net8.0 or net10.0.

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

Both live in `TechTeaStudio.Billing.Abstractions`, so the assembly that implements them needs no web stack - see "Packages" above.

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

One folder per package under `src/`, matching the other Tech Tea Studio repositories.

```
src/
  Directory.Build.props            version, target frameworks, packaging - shared by all projects
  TechTeaStudio.Billing.Abstractions/
    TechTeaStudio.Billing.Abstractions/
                                   the contracts package: IBillingProvider, IBillingService,
                                   IBillingPaymentStore, IBillingFulfillment, ITelegramBot,
                                   IExternalPurchaseService, IClaimCodeStore,
                                   IExternalIdentityLinkStore, IUnclaimedPurchaseStore,
                                   ClaimCode, BillingPlanGuard, models, enums
  TechTeaStudio.Billing/
    TechTeaStudio.Billing.sln
    icon.png                       package icon, packed into both packages
    TechTeaStudio.Billing/
      Providers/     StripeBillingProvider, YooKassaBillingProvider,
                     ShopifyBillingProvider, TelegramStarsBillingProvider,
                     KofiBillingProvider, PatreonBillingProvider,
                     StripeSignature, ShopifySignature, PatreonSignature, options
    Services/        BillingService, ExternalPurchaseService, PaymentPipeline,
                     in-memory dev stores
    AspNetCore/      BillingServiceCollectionExtensions, IBillingBuilder,
                     BillingEndpointRouteBuilderExtensions
  TechTeaStudio.Billing.Tests/
    tests for signatures, payload encoding, provider parsing, attribution,
    refunds, orchestrator idempotency, and endpoint mapping
```

## Build & test

```bash
dotnet build src/TechTeaStudio.Billing/TechTeaStudio.Billing.sln -c Release
```

```bash
dotnet test src/TechTeaStudio.Billing/TechTeaStudio.Billing.sln -c Release
```

```bash
dotnet pack src/TechTeaStudio.Billing/TechTeaStudio.Billing.sln -c Release -o artifacts
```

The library builds all six frameworks; the tests run on net8.0, net9.0 and net10.0, which is what CI has runtimes for. Packing is a separate step on purpose - `GeneratePackageOnBuild` across six frameworks and two projects would repack four packages on every build and every test run.

Explorer's folder icon for this repo comes from `.icon/folder.ico` plus a `desktop.ini`, both gitignored because git cannot carry the hidden+system attribute that makes them work. Run `.\New-FolderIcon.ps1` after a fresh clone to regenerate them.

## Versioning & release

Commit format: `vX.Y.Z Short description` (one line, 72 chars max). Bump `<Version>` in `src/Directory.Build.props` - one place now sets the version of both packages, instead of repeating it in every `.csproj`.

The release branch is `product`. GitHub Actions publish to NuGet on push to `product`.

### CI setup

[`.github/workflows/dotnet.yml`](.github/workflows/dotnet.yml) delegates to the org-wide reusable workflow `TechTeaStudio/.github/.github/workflows/nuget-publish-reusable.yml@main`, which restores, builds Release, runs the tests, packs every project matched by `src/*/*/*.csproj`, and pushes with `--skip-duplicate`. Both packable projects match that glob, so the split needs no workflow change.

**One secret, `NUGET_API_KEY`.** Add it at `Settings → Secrets and variables → Actions → New repository secret` on `TechTeaStudio/Billing`, or as an organization secret on `TechTeaStudio` with access granted to this repo. Generate the value on nuget.org under `Account → API Keys → Create`:

| Field | Value |
|---|---|
| Key name | anything, e.g. `techteastudio-ci` |
| Scopes | **Push** → *Push new packages and package versions* |
| Glob pattern | `TechTeaStudio.*` |

Both fields matter. The narrower *Push new versions of existing packages* scope cannot create `TechTeaStudio.Billing.Abstractions`, which has never been published. And a glob of `TechTeaStudio.Billing` matches only that exact id, so the Abstractions push would 403 - `TechTeaStudio.*` covers both.

Two more things gate the first green run:

- **Actions must be able to read the reusable workflow.** If `TechTeaStudio/.github` is private, enable `Organization → Settings → Actions → General → Allow access to workflows from repositories in this organization`. Otherwise the run fails at "workflow not found" before any dotnet step.
- **`--skip-duplicate` means a re-push of an existing version is a silent no-op**, not an error. If a release seems not to publish, check that the version was bumped.

## License

Licensed under the [MIT License](LICENSE). Copyright &copy; Tech Tea Studio.

<p align="center">
  Built as part of the Hyperion Ecosystem by <a href="https://techteastudio.cc">TechTeaStudio</a>.
</p>
