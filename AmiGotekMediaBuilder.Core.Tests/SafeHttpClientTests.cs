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
}
