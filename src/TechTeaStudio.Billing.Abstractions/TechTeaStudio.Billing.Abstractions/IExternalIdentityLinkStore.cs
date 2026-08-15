namespace TechTeaStudio.Billing.Abstractions;

/// <summary>
/// Persists "this platform identity belongs to this app user" links, keyed by
/// (provider, buyer email). The link is what lets message-less payments - Ko-fi
/// subscription renewals, Patreon re-bills - attach to the right account after the
/// first successful claim. Keys arrive pre-normalized (provider lowercase, email
/// trimmed + lowercase); implementations store them as given.
/// </summary>
public interface IExternalIdentityLinkStore
{
    /// <summary>Returns the linked app user for (provider, email), or null.</summary>
    Task<Guid?> FindUserAsync(string provider, string email, CancellationToken ct = default);

    /// <summary>Creates or overwrites the link. Last write wins - re-linking an email
    /// to a different account is a deliberate user action, not a conflict.</summary>
    Task LinkAsync(string provider, string email, Guid userId, CancellationToken ct = default);
}
