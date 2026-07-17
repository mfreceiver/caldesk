namespace CalDesk.Core.Models;

/// <summary>A configured CalDAV account. Passwords are never stored here — only a
/// reference (<see cref="CredentialTarget"/>) into the OS credential locker.</summary>
public sealed class Account
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Server base URL as entered by the user, e.g. <c>https://your-caldav-server.example</c>.</summary>
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>CalDAV user name.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Reference into the OS credential locker, e.g. <c>CalDesk:account:&lt;id&gt;</c>.</summary>
    public string CredentialTarget { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
