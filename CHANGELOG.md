## v0.3.0

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

## v0.2.0

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

## v0.1.0

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
