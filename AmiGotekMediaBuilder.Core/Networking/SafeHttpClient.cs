using System.Net;
using System.Net.Http.Headers;

namespace AmiGotekMediaBuilder.Core.Networking;

/// <summary>
/// Bounded, no-redirect HTTP transport shared by online providers.
/// </summary>
public sealed class SafeHttpClient : IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _resolveDns;

    public SafeHttpClient(TimeSpan? timeout = null, HttpMessageHandler? handler = null)
    {
        _resolveDns = handler is null;
        // Keep redirects under explicit validation in GetBytesFollowingRedirectsAsync,
        // but honor the operating system's proxy configuration. Disabling the
        // proxy made all public providers unreachable on common Windows and
        // corporate networks.
        handler ??= new HttpClientHandler { AllowAutoRedirect = false, UseProxy = true };
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(20)
        };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AmiGotekMediaBuilder", "0.1"));
    }

    public async Task<byte[]> GetBytesAsync(string url, long maxBytes = 3_000_000, CancellationToken cancellationToken = default)
    {
        GuardUrl(url, _resolveDns);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendBytesAsync(request, maxBytes, cancellationToken);
    }

    /// <summary>
    /// GET with a small, validated redirect budget. Pouët download links often
    /// point at a mirror that redirects to the actual file; every hop is
    /// rechecked against the SSRF policy and no more than three hops are
    /// followed.
    /// </summary>
    public async Task<byte[]> GetBytesFollowingRedirectsAsync(
        string url,
        long maxBytes = 3_000_000,
        CancellationToken cancellationToken = default)
    {
        var current = url;
        for (var hop = 0; hop <= 3; hop++)
        {
            GuardUrl(current, _resolveDns);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                if (hop == 3 || response.Headers.Location is null)
                    throw new HttpRequestException("redirect limit exceeded or Location header missing");
                current = new Uri(new Uri(current), response.Headers.Location).ToString();
                continue;
            }
            response.EnsureSuccessStatusCode();
            return await ReadResponseBytesAsync(response, maxBytes, cancellationToken);
        }
        throw new HttpRequestException("redirect limit exceeded");
    }

    public async Task<byte[]> PostBytesAsync(
        string url,
        byte[] content,
        string contentType = "application/octet-stream",
        long maxBytes = 3_000_000,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        GuardUrl(url, _resolveDns);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(content)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        if (headers is not null)
            foreach (var pair in headers) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        return await SendBytesAsync(request, maxBytes, cancellationToken);
    }

    private async Task<byte[]> SendBytesAsync(HttpRequestMessage request, long maxBytes, CancellationToken cancellationToken)
    {
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new HttpRequestException($"redirect refused: {(int)response.StatusCode}");
        response.EnsureSuccessStatusCode();
        return await ReadResponseBytesAsync(response, maxBytes, cancellationToken);
    }

    private static async Task<byte[]> ReadResponseBytesAsync(HttpResponseMessage response, long maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > maxBytes)
                throw new InvalidOperationException($"HTTP response exceeds {maxBytes} byte safety limit");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    public static void GuardUrl(string url, bool resolveDns = false)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host))
            throw new UnsafeUrlException($"refusing to fetch non-http(s) URL: {url}");
        if (IPAddress.TryParse(uri.Host, out var literal))
        {
            if (IsPrivate(literal)) throw new UnsafeUrlException($"refusing private address: {url}");
            return;
        }
        if (!resolveDns) return;
        IPAddress[] addresses;
        try { addresses = Dns.GetHostAddresses(uri.Host); }
        catch (Exception ex) { throw new UnsafeUrlException($"could not resolve host '{uri.Host}': {ex.Message}"); }
        if (addresses.Length == 0 || addresses.Any(IsPrivate))
            throw new UnsafeUrlException($"refusing host resolving to a non-public address: {uri.Host}");
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return bytes[0] == 10 || bytes[0] == 127 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 169 && bytes[1] == 254);
        return (bytes[0] & 0xFE) == 0xFC || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80);
    }

    public void Dispose() => _client.Dispose();
}
