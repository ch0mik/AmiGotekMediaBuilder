using System.Net;
using AmiGotekMediaBuilder.Core.Metadata;
using AmiGotekMediaBuilder.Core.Models;
using AmiGotekMediaBuilder.Core.Networking;

namespace AmiGotekMediaBuilder.Core.Tests;

public sealed class HallOfLightProviderTests
{
    [Fact]
    public async Task ResolvesGameMetadataAndPrefersFrontCoverArtwork()
    {
        using var client = new SafeHttpClient(handler: new HallOfLightHandler());
        using var provider = new HallOfLightProvider(new HallOfLightProviderOptions(
            BaseUrl: "https://example.test", RequestDelayMilliseconds: 0), client);
        var group = new ReleaseGroup
        {
            ReleaseKey = "indiana-jones-atlantis",
            Title = "Indiana Jones and the Fate of Atlantis",
            Extension = "adf"
        };

        var result = await provider.ResolveAsync(group);

        Assert.NotNull(result);
        Assert.Equal("Indiana Jones and the Fate of Atlantis", result!.Title);
        Assert.Equal("1992", result.Year);
        Assert.Equal("Lucasfilm Games", result.Publisher);
        Assert.Equal("hall-of-light", result.Provider);
        Assert.Equal("https://example.test/images/fate-front.jpg", result.ArtworkUrl);
        Assert.Equal("https://example.test/games/view/fate-of-atlantis", result.ArtworkSourceUrl);
    }

    [Fact]
    public async Task DoesNotQueryHallOfLightForDemosceneGroups()
    {
        var handler = new HallOfLightHandler();
        using var client = new SafeHttpClient(handler: handler);
        using var provider = new HallOfLightProvider(new HallOfLightProviderOptions(
            BaseUrl: "https://example.test", RequestDelayMilliseconds: 0), client);
        var group = new ReleaseGroup
        {
            ReleaseKey = "demo",
            Title = "A demo production",
            Extension = "adf",
            IsDemoscene = true
        };

        var result = await provider.ResolveAsync(group);

        Assert.Null(result);
        Assert.Empty(handler.RequestedPaths);
    }

    [Fact]
    public async Task TreatsAnubisChallengeAsAnUnavailableProvider()
    {
        using var client = new SafeHttpClient(handler: new ChallengeHandler());
        using var provider = new HallOfLightProvider(new HallOfLightProviderOptions(
            BaseUrl: "https://example.test", RequestDelayMilliseconds: 0), client);
        var group = new ReleaseGroup { ReleaseKey = "game", Title = "Example Game", Extension = "adf" };

        var result = await provider.ResolveAsync(group);

        Assert.Null(result);
    }

    private sealed class HallOfLightHandler : HttpMessageHandler
    {
        public List<string> RequestedPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedPaths.Add(request.RequestUri!.PathAndQuery);
            var body = request.RequestUri.AbsolutePath.Contains("/games/view/", StringComparison.OrdinalIgnoreCase)
                ? """
                  <html><head>
                    <meta property="og:title" content="Indiana Jones and the Fate of Atlantis | Hall of Light" />
                    <meta name="description" content="An adventure game by Lucasfilm Games." />
                  </head><body>
                    <table>
                      <tr><th>Year of the first release</th><td>1992</td></tr>
                      <tr><th>Publisher</th><td>Lucasfilm Games</td></tr>
                    </table>
                    <div class="box-cover"><img alt="Front cover" src="/images/fate-front.jpg" /></div>
                    <div class="screenshots"><img alt="Screenshot" src="/images/fate-screen.jpg" /></div>
                  </body></html>
                  """
                : """
                  <html><body>
                    <a href="/games/view/fate-of-atlantis">Indiana Jones and the Fate of Atlantis</a>
                    <a href="/games/view/another-game">Another Game</a>
                  </body></html>
                  """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }
    }

    private sealed class ChallengeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<title>Making sure you're not a bot!</title><script id=\"anubis-main\"></script>")
            });
    }
}
