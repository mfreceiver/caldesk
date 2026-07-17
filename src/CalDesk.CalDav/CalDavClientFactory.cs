using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;
using CalDesk.Core.Models;
using CalDesk.Core.Services;

namespace CalDesk.CalDav;

/// <summary>Creates per-account CalDAV clients backed by <see cref="HttpClient"/> with Basic auth.
/// The “trust all certificates” flag is read via a provider so the user can toggle it in settings
/// without restarting the app.</summary>
public sealed class CalDavClientFactory : ICalDavClientFactory
{
    private readonly ICredentialStore _credentials;
    private readonly Func<bool>? _trustAllCertificates;
    private readonly Action<string>? _log;

    public CalDavClientFactory(
        ICredentialStore credentials,
        Func<bool>? trustAllCertificates = null,
        Action<string>? log = null)
    {
        _credentials = credentials;
        _trustAllCertificates = trustAllCertificates;
        _log = log;
    }

    public Task<ICalDavClient> CreateAsync(Account account, CancellationToken ct = default)
        => CreateAsync(account, log: _log, ct);

    /// <summary>Create a client with an optional per-call discovery/HTTP step logger.
    /// Does not change <see cref="ICalDavClientFactory"/>; app code can call this overload
    /// when it holds a concrete <see cref="CalDavClientFactory"/>.</summary>
    public async Task<ICalDavClient> CreateAsync(Account account, Action<string>? log, CancellationToken ct = default)
    {
        var password = await _credentials.GetPasswordAsync(account.CredentialTarget, ct).ConfigureAwait(false) ?? string.Empty;
        return CreateWithPassword(account, password, log);
    }

    /// <summary>Create a client using an explicit password (does not read or write the credential store).
    /// Used by settings "测试连接" so a probe can use the form password without persisting it.</summary>
    public ICalDavClient CreateWithPassword(Account account, string password, Action<string>? log = null)
    {
        var origin = new Uri(new Uri(account.ServerUrl).GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);

        var handler = new SocketsHttpHandler
        {
            // PROPFIND must not be auto-downgraded to GET on 301/302; CalDavClient re-issues
            // the same method+body when following redirects.
            AllowAutoRedirect = false,
        };
        if (_trustAllCertificates?.Invoke() ?? false)
        {
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            };
        }

        var http = new HttpClient(handler)
        {
            BaseAddress = origin,
            Timeout = TimeSpan.FromSeconds(30),
        };

        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{account.Username}:{password}"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CalDesk/0.1");

        return new CalDavClient(account, http, origin, log ?? _log);
    }

    internal static Uri Combine(Uri origin, string path) => new(origin, path.TrimStart('/'));
}
