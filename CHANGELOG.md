# Changelog

All notable changes to this package are documented here.
Format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.5.2] - 2026-08-15

Version bump only, to give the publish pipeline a version it has not seen. No source, package or metadata changes. `dotnet nuget push` runs with `--skip-duplicate`, so re-pushing an existing version is a silent no-op - a new number is the only way to tell "published" from "skipped".

## [0.5.1] - 2026-08-15

Packaging fix: v0.4.0 and v0.5.0 could never publish. No library code changed.

### Fixed

- **`<Version>` is back in each packable `.csproj`.** Moving it into `Directory.Build.props` in v0.4.0 looked like de-duplication, but the org publish workflow reads the version by grepping the project file text (`grep -Po '(?<=<Version>)[^<]+' "$proj"`) and skips any project without a literal tag. Both packages were skipped, `artifacts/` came out empty, and the push step then iterated a glob that matched nothing. Every other setting stays centralised; only the version is duplicated, and `Directory.Build.props` now carries a comment saying why it must not move back.

## [0.5.0] - 2026-08-15

.NET Framework reach for the contracts. `TechTeaStudio.Billing.Abstractions` now also targets `netstandard2.0` and `net472`, so a .NET Framework 4.6.2+ assembly can share the billing contracts with a modern ASP.NET Core host. The main package is unchanged and stays .NET 5+.

### Added

- `TechTeaStudio.Billing.Abstractions` targets `netstandard2.0` and `net472` alongside `net5.0`-`net10.0`. Still zero package dependencies and no framework reference: `Microsoft.NETFramework.ReferenceAssemblies` is referenced with `PrivateAssets="all"` purely so the `net472` leg builds on the Linux CI runner, and it does not appear in the nuspec.
- `Polyfills.cs` - an internal `IsExternalInit`, which is what lets the positional records compile below .NET 5.
- A rejection-sampling `ClaimCode` RNG for the downlevel frameworks, since `RandomNumberGenerator.GetInt32` only exists from .NET Core 3.0. Reducing a raw 32-bit draw with `%` would bias the first `2^32 % 30` characters of the alphabet; the short trailing block is discarded instead. Verified on .NET Framework 4.8 over 160k characters - worst per-character deviation 3.3% against an expected 5333.

### Changed

- **`IBillingPaymentStore.TryReserveAsync` and `MarkRefundedAsync` are required members on `netstandard2.0`/`net472`**, because neither runtime supports default interface members. They keep their default implementations on `net5.0` and up, so no existing consumer is affected - and there was no .NET Framework build before this release. Implementing both is what the documentation asks production stores to do regardless.
- `ClaimCode.Generate` uses `stackalloc` on .NET 5+ as before, and a `char[8]` below it - avoiding a `System.Memory` dependency was worth more than the allocation, given the method runs once per user.

### Notes

- The main `TechTeaStudio.Billing` package **cannot** target .NET Framework and this is not a gap that can be closed: it needs `Microsoft.AspNetCore.App`, and ASP.NET Core dropped .NET Framework at 3.0. `dotnet restore` on a `net48` target fails with `NETSDK1073`.
- The downlevel code paths are not exercised by CI - the runner is `ubuntu-latest` and the test project targets net8.0/9.0/10.0. They were validated by hand against .NET Framework 4.8; re-run that check when touching `ClaimCode` or the polyfills.

## [0.4.0] - 2026-08-15

Reach and packaging. The library now runs on net5.0 through net10.0 instead of net8.0+, and the contracts ship as their own dependency-free package so a domain assembly can implement the seams without being pulled onto the ASP.NET Core shared framework. No behaviour changed and no namespace moved - existing code compiles untouched.

### Added

- `TechTeaStudio.Billing.Abstractions` - a second package holding the contracts (`IBillingProvider`, `IBillingService`, `IBillingPaymentStore`, `IBillingFulfillment`, `IExternalPurchaseService`, `ITelegramBot`, the three attribution stores), the models, `ClaimCode` and `BillingPlanGuard`. Zero package references and no `FrameworkReference`. `TechTeaStudio.Billing` depends on it, so `dotnet add package TechTeaStudio.Billing` still brings everything and nothing needs re-importing.
- Symbol packages (`.snupkg`) and SourceLink on both packages, so consumers can step into this code from their debugger. SourceLink needs no `PackageReference` - it has shipped inside the SDK since 8.0.100.
- `EnablePackageValidation` - `dotnet pack` now fails if the six target frameworks stop exposing the same public API, which is the mistake a `#if` most easily introduces.
- `Directory.Build.props` holding version, target frameworks and packaging settings once, so the two packable projects cannot drift apart.
- `BillingEndpointMappingTests` - the mapped endpoints had no coverage at all. Five tests pull the real `RequestDelegate` off the endpoint and invoke it: `{provider}` route-segment binding, raw body pass-through, case-insensitive header lookup, 400 on a rejected payload, 404 when no `ITelegramBot` is registered, and the `basePath` argument. 121 tests total.
- `New-FolderIcon.ps1` - rebuilds the repository's Windows folder icon (`.icon/folder.ico`, 16-256 px frames, plus the UTF-16 `desktop.ini` and the attributes the shell needs) from the package icon. Same `.icon/folder.ico` layout as the HyperionProtocol, Auth and ConfigBase repositories; the generated files are gitignored, since git cannot carry the hidden+system attribute that makes them work.

### Changed

- **Target frameworks: `net5.0;net6.0;net7.0;net8.0;net9.0;net10.0`** (was `net8.0;net9.0;net10.0`). Building the older three needs no old SDK installed - the .NET 10 SDK downloads the reference packs from NuGet. Be aware that net5.0, net6.0 and net7.0 are out of support upstream; they are here so a host stuck on an older runtime can still take fixes, not as an endorsement.
- `MapTechTeaStudioBilling` now maps `RequestDelegate` handlers instead of minimal-API lambdas. That overload has existed since ASP.NET Core 3.0, whereas the `Results` helpers and the `Delegate` overload of `MapPost` are .NET 6+, so this is what makes net5.0 reachable from one source file. Routes, status codes and behaviour are identical; the `{provider}` segment is now read from `HttpRequest.RouteValues` and the status set directly on the response.
- Test project targets `net8.0;net9.0;net10.0` (added net10.0). It deliberately does not target net5.0-net7.0: those runtimes are not installed in CI, and the only framework-conditional code path in the library is the `StreamReader.ReadToEndAsync` overload, which net8.0 and net10.0 already straddle.
- Package descriptions and release notes state the supported framework range.
- `GeneratePackageOnBuild` removed. Six frameworks times two packable projects meant four `.nupkg`/`.snupkg` were repacked on every build, including Debug and every `dotnet test` run, leaving Debug-configuration packages in `bin/Debug`. Packing is now one explicit `dotnet pack -c Release` step, which is what CI already did.
- Warnings are errors on the build server only (`TreatWarningsAsErrors` gated on `GITHUB_ACTIONS`). With the same source compiling six times, a warning that fires on one old framework is easy to scroll past; local builds still just warn.
- README logo pointed at `/product/icon.png`, but the icon lives at `src/TechTeaStudio.Billing/icon.png` - the image was broken. Path corrected.
- Project layout now follows the same convention as the sibling repositories: each package gets its own `src/<PackageId>/<PackageId>/` folder, so `TechTeaStudio.Billing.Abstractions` sits beside `TechTeaStudio.Billing` rather than inside it, and the solution references it with `..\` exactly as `TechTeaStudio.Auth.sln` references its sub-packages. `Directory.Build.props` moved up to `src/` so sibling package folders inherit it. The CI glob `src/*/*/*.csproj` matches either way.

### Fixed

- `StreamReader.ReadToEndAsync(CancellationToken)` in the webhook endpoints only exists from .NET 7 on; the pre-net7.0 builds use the parameterless overload. The request body stream still observes `RequestAborted` underneath, so an aborted request faults the read rather than hanging.
- A nullability warning on net5.0 only: `FormUrlEncodedContent` takes `IEnumerable<KeyValuePair<string?, string?>>` there and was tightened to non-nullable in net6.0, so the Stripe checkout form warned on exactly one of the six frameworks. The build is warning-free on all of them now.

## [0.3.0] - 2026-08-15

External purchases: donation-platform payments (Ko-fi, Patreon, manual Boosty) attributed to app users via claim codes, with an unclaimed holding area so received money is never dropped. New package icon.

### Added

- `KofiBillingProvider` ("kofi") - official Ko-fi webhook: form-urlencoded body with a `data` JSON field, static `verification_token` compared constant-time (Ko-fi offers no HMAC), event types Donation / Subscription / Shop Order / Commission, idempotency on `kofi_transaction_id`. Ko-fi has no refund or membership-ended webhooks - documented as time-boxed-entitlement territory.
- `PatreonBillingProvider` ("patreon") - APIv2 `members:*` webhooks: `X-Patreon-Signature` = lowercase hex HMAC-MD5 of the RAW body keyed with the per-webhook secret (Patreon's mandated scheme), `X-Patreon-Event` routing, purchase rule `patron_status == active_patron && last_charge_status == Paid` (optional `HonorFreeTrial`), tier-ID product mapping, campaign filter, refund statuses recorded as `PaymentRefunded`.
- `PatreonSignature` - pure static verifier (constant-time hex compare) alongside the existing Stripe/Shopify ones.
- `IExternalPurchaseService` / `ExternalPurchaseService` - claim-code issue (`GetClaimCodeAsync`), buyer resolution (code in message, then (provider, email) identity link), holding (`HoldAsync`), claims (`ClaimByEmailAsync` for verified emails, targeted `ClaimAsync` with product override for admins, `ListUnclaimedAsync`), and `SubmitAsync` - the manual path for Boosty, which has NO official API or webhooks (everything public is reverse-engineered and unstable; deliberately not integrated).
- `ClaimCode` - crypto-RNG codes over an ambiguity-free alphabet ("CHR-K7M2P9XA"), case-insensitive extraction from free text; prefix from `Billing:External:ClaimCodePrefix`.
- `IClaimCodeStore`, `IExternalIdentityLinkStore`, `IUnclaimedPurchaseStore` host seams with in-memory dev defaults and `UseClaimCodeStore<T>` / `UseExternalIdentityLinkStore<T>` / `UseUnclaimedPurchaseStore<T>` builder methods.
- `BillingEventKind.PaymentRefunded` + `BillingPaymentStatus.Refunded` - a refunded payment is terminal: a redelivered success webhook can never re-fulfill it.
- `PaymentPipeline` (internal) - the single fulfillment path shared by webhooks, claims, and manual submissions, so every origin gets the same exactly-once guarantee.
- `BillingPlanGuard` moved into the package from Chronos - it always encoded the package's own config-key schema (`PriceKeyFor`), so it belongs here; external platforms deliberately have no price keys and fail open.
- `AddKofi` / `AddPatreon` builder methods; config overload now binds `Billing:Kofi`, `Billing:Patreon`, `Billing:External`.
- Tests: Ko-fi wire-format parsing (canonical payloads from Ko-fi docs), Patreon signature vectors incl. the NO-BREAK-SPACE raw-body trap, provider purchase/hold/refund flows, attribution and claim idempotency, plan-guard port - 104 tests total.

### Changed

- `IBillingPaymentStore` gained two default-implemented members, so existing stores keep compiling: `TryReserveAsync` (the fulfillment claim - override with an atomic insert-first for real exactly-once) and `MarkRefundedAsync` (override to flip only the status so a refund payload's empty plan / zero amount cannot erase the original record).
- `IBillingBuilder` gained `AddKofi`, `AddPatreon`, `UseClaimCodeStore<T>`, `UseExternalIdentityLinkStore<T>`, `UseUnclaimedPurchaseStore<T>`. Source-breaking for anyone who implements the interface themselves; consumers who only call it are unaffected.
- `BillingService.StartCheckoutAsync` catches a provider's `InvalidOperationException` (unpriced plan, missing PageUrl) and returns null instead of surfacing a 500.
- `InMemoryBillingPaymentStore` now keeps the whole record and reserves atomically, so the dev default behaves like a correct production store.
- Package icon replaced with the TTS billing receipt icon.

### Fixed

Found by an adversarial review of this release before it shipped; each one is now covered by a regression test.

- **A refund that overtook its own success webhook was dropped**, leaving no record - a retried success then fulfilled money that had already been returned. Refunds are now always recorded, including as a tombstone for a payment the store has never seen.
- **Concurrent first deliveries of the same payment could both fulfill**: the pipeline read the status, fulfilled, then wrote, with no claim in between. Fulfillment now happens only after `TryReserveAsync` grants the claim.
- **A held purchase could vanish**: the claim path removed it from the unclaimed store and only put it back when the pipeline returned false, so a throwing payment store, or a canceled request, destroyed received money. The claim is now wrapped, the re-hold is uncancellable, and a failed re-hold logs at critical level.
- **Admin claims never persisted the identity link**, so the next renewal and any later refund detached from the account. The link is now written on every successful claim.
- **A late cancel/failure could overwrite a succeeded payment.** Terminal statuses are now respected.
- **Ko-fi Shop Orders ignored quantity** - three units became a single grant. Multi-unit and multi-item orders are held for an admin instead.
- **A Ko-fi donation carrying a valid claim code was silently discarded** when no donation product was configured; it is now held so an admin sees a buyer who asked for something.
- **A Ko-fi `data` field that is valid JSON but not an object** (a bare string or array) threw, turning an unauthenticated payload into a 500 that Ko-fi would retry forever. It is now rejected.
- **Patreon `members:pledge:update` charges were dropped** - a tier upgrade that bills immediately now grants.
- **Product mappings whose key contains spaces** (a Ko-fi tier is literally "Gold Tier") were unreachable on env-only deployments, because no environment variable can carry that name. Lookups now ignore case, spaces and punctuation, so `Billing__Kofi__TierProducts__GoldTier` works.
- **`ClaimCode.TryExtract` used `IgnoreCase` without `CultureInvariant`**, so a Turkish culture could stop a prefix from matching.
- **Registering billing twice made every webhook throw** on a duplicate provider key; duplicates are now collapsed.
- `ClaimByEmailAsync` counts only first-time grants, and `ClaimAsync` reports false when the payment was already processed, instead of reporting a grant that did not happen.

## [0.2.0] - 2026-06-07

Turnkey config-driven setup: a consumer sets provider config values in appsettings and billing works with no boilerplate.

### Added

- `AddTechTeaStudioBilling(IConfiguration config, string sectionName)` overload - binds all four provider option classes from config sub-sections automatically. Providers whose section is absent stay dormant (`IsConfigured == false`).
- `InMemoryBillingPaymentStore` - thread-safe dev default for `IBillingPaymentStore` registered via `TryAddSingleton`. Swap with `UsePaymentStore<T>()` for production.
- `NoOpBillingFulfillment` - dev default for `IBillingFulfillment` registered via `TryAddScoped`. Logs a one-time warning so developers notice unplugged fulfillment.
- `IBillingBuilder.OnPaymentSucceeded(Func<IServiceProvider, BillingNotification, CancellationToken, Task>)` - one-line delegate fulfillment; replaces the no-op via `RemoveAll` + re-add.
- `DelegateBillingFulfillment` - internal fulfillment implementation that wraps the delegate.
- `UsePaymentStore<T>()` and `UseFulfillment<T>()` now call `RemoveAll` before re-adding so they cleanly replace the defaults instead of stacking registrations.
- `MapTechTeaStudioBilling(IEndpointRouteBuilder, string basePath)` - maps `POST /billing/webhook/{provider}` and `POST /billing/telegram/bot` endpoints. Ports the provider-agnostic webhook and Telegram Stars bot lifecycle from Chronos into the package itself.
- Tests: `InMemoryBillingPaymentStoreTests`, `OnPaymentSucceededDelegateTests`, `ConfigOverloadTests`.

## [0.1.0] - 2026-06-07

Initial release. Provider-agnostic payment billing for ASP.NET Core.

### Added

- `IBillingProvider` seam with four built-in implementations: Stripe, YooKassa, Shopify, Telegram Stars.
- `IBillingService` / `BillingService` orchestrator: hosted checkout, idempotent webhook processing, pluggable store and fulfillment.
- `IBillingPaymentStore` - host-implemented payment persistence seam.
- `IBillingFulfillment` - host-implemented fulfillment callback seam (called exactly once per successful payment).
- `StripeSignature.Verify` - HMAC-SHA256 with timestamp replay guard (5-minute window).
- `ShopifySignature.Verify` - HMAC-SHA256 constant-time Base64 header comparison.
- Telegram Stars provider with deep-link payload encoding (`pay-{guidN}-{planId}`) and `X-Telegram-Bot-Api-Secret-Token` verification.
- YooKassa provider with re-fetch authentication (the webhook body is unsigned - the API is the source of truth).
- Plan-keyed option dictionaries on all providers (`PriceIds`, `Amounts`, `Plans`) replacing the hardcoded Plus/Pro fields.
- `IBillingBuilder` DI fluent interface: `AddTechTeaStudioBilling()`, `AddStripe()`, `AddYooKassa()`, `AddShopify()`, `AddTelegramStars()`, `UsePaymentStore<T>()`, `UseFulfillment<T>()`.
- xUnit tests: `StripeSignature`, `ShopifySignature`, Telegram payload round-trip, `BillingService` idempotency.
