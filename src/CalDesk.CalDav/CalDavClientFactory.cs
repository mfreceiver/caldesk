using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;
using CalDesk.CalDav.Dav;
using CalDesk.Core.Models;
using CalDesk.Core.Services;

namespace CalDesk.CalDav;

/// <summary>Creates per-account CalDAV clients backed by <see cref="HttpClient"/> with Basic auth.</summary>
public sealed class CalDavClientFactory : ICalDavClientFactory
{
    private readonly ICredentialStore _credentials;
    private readonly bool _trustAllCertificates;

    public CalDavClientFactory(ICredentialStore credentials, bool trustAllCertificates = false)
    {
        _credentials = credentials;
        _trustAllCertificates = trustAllCertificates;
    }

    public async Task<ICalDavClient> CreateAsync(Account account, CancellationToken ct = default)
    {
        var password = await _credentials.GetPasswordAsync(account.CredentialTarget, ct).ConfigureAwait(false) ?? string.Empty;
        var origin = new Uri(new Uri(account.ServerUrl).GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);

        var handler = new SocketsHttpHandler();
        if (_trustAllCertificates)
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

        return new CalDavClient(account, http, origin);
    }

    internal static Uri Combine(Uri origin, string path) => new(origin, path.TrimStart('/'));
}
