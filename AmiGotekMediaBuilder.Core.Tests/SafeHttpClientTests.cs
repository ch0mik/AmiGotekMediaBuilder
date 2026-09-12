using System.Net;
using System.Net.Http;
using AmiGotekMediaBuilder.Core.Networking;

namespace AmiGotekMediaBuilder.Core.Tests;

public sealed class SafeHttpClientTests
{
    [Theory]
    [InlineData("file:///secret")]
    [InlineData("http://127.0.0.1/private")]
    [InlineData("http://192.168.1.1/private")]
    public void RejectsUnsafeUrls(string url) =>
        Assert.Throws<UnsafeUrlException>(() => SafeHttpClient.GuardUrl(url));

    [Fact]
    public async Task EnforcesResponseLimit()
    {
        using var client = new SafeHttpClient(handler: new StaticHandler(new byte[9]));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetBytesAsync("https://example.test/data", maxBytes: 8));
    }

    [Fact]
    public async Task FollowsOnlyValidatedRedirectsForDemosceneDownloads()
    {
        using var client = new SafeHttpClient(handler: new RedirectHandler());
        var bytes = await client.GetBytesFollowingRedirectsAsync("https://example.test/start");
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
    }

    [Fact]
    public async Task RetriesTransientServerFailureBeforeReturningPayload()
    {
        var progress = new List<HttpRetryProgress>();
        using var client = new SafeHttpClient(handler: new TransientFailureHandler(),
            retryProgress: new Progress<HttpRetryProgress>(progress.Add));

        var bytes = await client.GetBytesAsync("https://retry.example.test/data");

        Assert.Equal([7, 8, 9], bytes);
        var retry = Assert.Single(progress);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, retry.StatusCode);
        Assert.Equal(1, retry.Attempt);
    }

    private sealed class StaticHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
    }

    private sealed class RedirectHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/start", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("https://example.test/final") }
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            });
        }
    }

    private sealed class TransientFailureHandler : HttpMessageHandler
    {
        private int _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = Interlocked.Increment(ref _requests) == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([7, 8, 9]) };
            return Task.FromResult(response);
        }
    }
}
