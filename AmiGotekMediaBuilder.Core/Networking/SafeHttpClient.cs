using System.Net;
using System.Net.Http.Headers;
using System.Collections.Concurrent;

namespace AmiGotekMediaBuilder.Core.Networking;

/// <summary>
/// Bounded, no-redirect HTTP transport shared by online providers.
/// </summary>
public sealed record HttpRetryProgress(string Host, HttpStatusCode? StatusCode, int Attempt, int MaxAttempts, TimeSpan Delay);

public sealed class SafeHttpClient : IDisposable
{
    private const int MaxAttempts = 5;
    private static readonly ConcurrentDictionary<string, HostRateGate> HostGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _client;
    private readonly bool _resolveDns;
    private readonly IProgress<HttpRetryProgress>? _retryProgress;
    private readonly TimeSpan _minimumRequestInterval;

    public SafeHttpClient(TimeSpan? timeout = null, HttpMessageHandler? handler = null,
        IProgress<HttpRetryProgress>? retryProgress = null, TimeSpan? minimumRequestInterval = null)
    {
        _resolveDns = handler is null;
        _retryProgress = retryProgress;
        _minimumRequestInterval = minimumRequestInterval is { } interval && interval > TimeSpan.Zero
            ? interval : TimeSpan.Zero;
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
        var uri = new Uri(url);
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, uri), uri, cancellationToken);
        return await EnsureAndReadAsync(response, maxBytes, cancellationToken);
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
            var currentUri = new Uri(current);
            using var response = await SendWithRetryAsync(
                () => new HttpRequestMessage(HttpMethod.Get, currentUri), currentUri, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                if (hop == 3 || response.Headers.Location is null)
                    throw new HttpRequestException("redirect limit exceeded or Location header missing");
                current = new Uri(new Uri(current), response.Headers.Location).ToString();
                continue;
            }
            return await EnsureAndReadAsync(response, maxBytes, cancellationToken);
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
        var uri = new Uri(url);
        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new ByteArrayContent(content)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            if (headers is not null)
                foreach (var pair in headers) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            return request;
        }, uri, cancellationToken);
        return await EnsureAndReadAsync(response, maxBytes, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory, Uri uri, CancellationToken cancellationToken)
    {
        var gate = HostGates.GetOrAdd(uri.Authority, _ => new HostRateGate());
        for (var attempt = 1; ; attempt++)
        {
            await WaitForCooldownAsync(gate, cancellationToken);
            HttpResponseMessage? response = null;
            try
            {
                using var request = requestFactory();
                response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (_minimumRequestInterval > TimeSpan.Zero)
                    SetCooldown(gate, _minimumRequestInterval);
                if (!IsTransient(response.StatusCode) || attempt >= MaxAttempts)
                    return response;

                var delay = RetryDelay(response, attempt);
                SetCooldown(gate, delay);
                _retryProgress?.Report(new HttpRetryProgress(uri.Host, response.StatusCode, attempt, MaxAttempts, delay));
                response.Dispose();
            }
            catch (HttpRequestException) when (!cancellationToken.IsCancellationRequested && attempt < MaxAttempts)
            {
                var delay = RetryDelay(response: null, attempt);
                SetCooldown(gate, delay);
                _retryProgress?.Report(new HttpRetryProgress(uri.Host, null, attempt, MaxAttempts, delay));
                response?.Dispose();
            }

            await WaitForCooldownAsync(gate, cancellationToken);
        }
    }

    private static async Task<byte[]> EnsureAndReadAsync(HttpResponseMessage response, long maxBytes, CancellationToken cancellationToken)
    {
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new HttpRequestException($"redirect refused: {(int)response.StatusCode}");
        response.EnsureSuccessStatusCode();
        return await ReadResponseBytesAsync(response, maxBytes, cancellationToken);
    }

    private static bool IsTransient(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.RequestTimeout or
        (HttpStatusCode)425 or
        HttpStatusCode.TooManyRequests or
        HttpStatusCode.InternalServerError or
        HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or
        HttpStatusCode.GatewayTimeout;

    private static TimeSpan RetryDelay(HttpResponseMessage? response, int attempt)
    {
        var retryAfter = response?.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            return LimitDelay(delta);
        if (retryAfter?.Date is { } date)
        {
            var until = date - DateTimeOffset.UtcNow;
            if (until > TimeSpan.Zero) return LimitDelay(until);
        }

        var baseSeconds = response?.StatusCode == HttpStatusCode.TooManyRequests ? 15 : 2;
        var seconds = Math.Min(120, baseSeconds * Math.Pow(2, attempt - 1));
        // A small jitter prevents several clients from retrying in lockstep.
        return TimeSpan.FromMilliseconds(seconds * 1000 * (0.85 + Random.Shared.NextDouble() * 0.30));
    }

    private static TimeSpan LimitDelay(TimeSpan delay) => delay > TimeSpan.FromMinutes(2)
        ? TimeSpan.FromMinutes(2) : delay;

    private static void SetCooldown(HostRateGate gate, TimeSpan delay)
    {
        lock (gate.Sync)
        {
            var next = DateTimeOffset.UtcNow + delay;
            if (next > gate.NextAllowedUtc) gate.NextAllowedUtc = next;
        }
    }

    private static async Task WaitForCooldownAsync(HostRateGate gate, CancellationToken cancellationToken)
    {
        TimeSpan delay;
        lock (gate.Sync) delay = gate.NextAllowedUtc - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
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

    private sealed class HostRateGate
    {
        public object Sync { get; } = new();
        public DateTimeOffset NextAllowedUtc { get; set; }
    }
}
