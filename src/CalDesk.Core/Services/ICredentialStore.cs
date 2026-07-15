namespace CalDesk.Core.Services;

/// <summary>OS-backed secret storage (Windows Credential Locker on Windows).
/// Only opaque blobs keyed by <c>target</c> are stored; the DB keeps only the reference.</summary>
public interface ICredentialStore
{
    Task<string?> GetPasswordAsync(string target, CancellationToken ct = default);

    Task SetPasswordAsync(string target, string password, CancellationToken ct = default);

    Task DeleteAsync(string target, CancellationToken ct = default);
}
