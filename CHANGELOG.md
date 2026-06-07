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
