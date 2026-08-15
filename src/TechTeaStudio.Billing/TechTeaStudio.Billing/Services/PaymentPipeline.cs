using Microsoft.Extensions.Logging;
using TechTeaStudio.Billing.Abstractions;

namespace TechTeaStudio.Billing.Services;

/// <summary>What <see cref="PaymentPipeline.ProcessAsync"/> actually did.</summary>
internal enum PaymentOutcome
{
    /// <summary>Fulfillment ran for the first time.</summary>
    Fulfilled,

    /// <summary>The payment was already terminal (succeeded or refunded) or is being
    /// fulfilled concurrently - nothing was granted. Accepted, so providers stop retrying.</summary>
    AlreadyProcessed,

    /// <summary>A non-granting event (cancel, failure, refund, irrelevant) was recorded.</summary>
    Recorded,

    /// <summary>Fulfillment failed; the caller may retry.</summary>
    Failed,
}

/// <summary>
/// The single fulfillment path every confirmed payment goes through, whatever its origin:
/// a provider webhook (<see cref="BillingService"/>), a claimed held purchase, or a manual
/// submission (<see cref="ExternalPurchaseService"/>). Fulfillment is guarded by
/// <see cref="IBillingPaymentStore.TryReserveAsync"/> per (provider, paymentId), and refunds
/// are recorded without ever re-running fulfillment.
/// </summary>
internal sealed class PaymentPipeline
{
    private readonly IBillingPaymentStore _store;
    private readonly IBillingFulfillment _fulfillment;
    private readonly ILogger _log;

    public PaymentPipeline(
        IBillingPaymentStore store, IBillingFulfillment fulfillment, ILogger log)
    {
        _store = store;
        _fulfillment = fulfillment;
        _log = log;
    }

    /// <summary>Processes a verified notification. Anything but
    /// <see cref="PaymentOutcome.Failed"/> means the payload was accepted and the provider
    /// should stop retrying.</summary>
    public async Task<PaymentOutcome> ProcessAsync(
        string providerName, BillingNotification note, CancellationToken ct = default)
    {
        var record = new BillingPaymentRecord(
            note.UserId, providerName, note.ProviderPaymentId, note.PlanId,
            note.AmountMinor, note.Currency, BillingPaymentStatus.Pending);

        if (note.Kind == BillingEventKind.PaymentRefunded)
        {
            // A refund is recorded even when no payment row exists yet: providers redeliver
            // out of order, so a refund can overtake its own success webhook. Without this
            // tombstone the late success would find nothing stored and fulfill money that
            // was already returned.
            if (note.UserId == Guid.Empty)
                _log.LogWarning(
                    "Refund for provider={Provider} paymentId={PaymentId} could not be attributed - " +
                    "recording it anyway so the payment can never be fulfilled.",
                    providerName, note.ProviderPaymentId);

            await _store.MarkRefundedAsync(
                record with { Status = BillingPaymentStatus.Refunded }, ct);
            return PaymentOutcome.Recorded;
        }

        if (note.Kind is BillingEventKind.PaymentCanceled or BillingEventKind.PaymentFailed)
        {
            var existing = await _store.GetStatusAsync(providerName, note.ProviderPaymentId, ct);
            // Never let a late cancel/failure overwrite a terminal outcome.
            if (existing is BillingPaymentStatus.Succeeded or BillingPaymentStatus.Refunded)
                return PaymentOutcome.AlreadyProcessed;

            var terminal = note.Kind == BillingEventKind.PaymentCanceled
                ? BillingPaymentStatus.Canceled
                : BillingPaymentStatus.Failed;
            await _store.UpsertAsync(record with { Status = terminal }, ct);
            return PaymentOutcome.Recorded;
        }

        // Authenticated but irrelevant event - accept so the provider stops retrying.
        if (note.Kind != BillingEventKind.PaymentSucceeded) return PaymentOutcome.Recorded;

        // Reserve first: a store with an atomic claim makes this exactly-once even under
        // concurrent deliveries; the default read-then-check still blocks re-fulfilling a
        // payment that is already succeeded or refunded.
        if (!await _store.TryReserveAsync(record, ct)) return PaymentOutcome.AlreadyProcessed;

        try
        {
            await _fulfillment.OnPaymentSucceededAsync(note, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Fulfillment failed for provider={Provider} paymentId={PaymentId}.",
                providerName, note.ProviderPaymentId);
            return PaymentOutcome.Failed;
        }

        try
        {
            await _store.UpsertAsync(record with { Status = BillingPaymentStatus.Succeeded }, ct);
        }
        catch (Exception ex)
        {
            // Fulfillment already ran - prefer accepting so the provider does not retry and
            // fulfillment is not called a second time. The host's fulfillment must be
            // idempotent on ProviderPaymentId to guard against any race that does cause a retry.
            _log.LogError(ex,
                "Store upsert failed after fulfillment succeeded for provider={Provider} paymentId={PaymentId}.",
                providerName, note.ProviderPaymentId);
        }

        return PaymentOutcome.Fulfilled;
    }
}
