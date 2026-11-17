namespace TechTeaStudio.Billing.Providers;

/// <summary>
/// A provider that must answer an accepted webhook with a body of its own. Robokassa takes a
/// notification as delivered only when the answer is "OK" followed by the invoice number; any
/// other answer makes it retry and report the shop's Result URL as failing.
/// </summary>
public interface IBillingWebhookReply
{
    /// <summary>The text/plain body for an accepted webhook, or null to answer with no body.</summary>
    string? ReplyTo(string rawBody);
}
