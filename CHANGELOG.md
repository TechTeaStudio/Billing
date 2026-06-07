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
