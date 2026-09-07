using System.Net;
using AmiGotekMediaBuilder.Core.Metadata;
using AmiGotekMediaBuilder.Core.Models;
using AmiGotekMediaBuilder.Core.Networking;

namespace AmiGotekMediaBuilder.Core.Tests;

public sealed class PouetProviderTests
{
    [Fact]
    public async Task FindsAmigaProductionAndArtworkFromPublicHtml()
    {
        using var client = new SafeHttpClient(handler: new PouetHandler());
        var provider = new PouetProvider(new OnlineProviderOptions("pouet", "https://www.pouet.net"), client);
        var group = new ReleaseGroup { ReleaseKey = "starstruck|", Title = "Starstruck", Extension = "adf" };

        var result = await provider.ResolveAsync(group);

        Assert.NotNull(result);
        Assert.Equal("Starstruck", result!.Title);
        Assert.Equal("2006", result.Year);
        Assert.Equal("https://content.pouet.net/shot.jpg", result.ArtworkUrl);
        Assert.Equal("pouet", result.Provider);
    }

    private sealed class PouetHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.RequestUri!.AbsolutePath.Contains("search", StringComparison.OrdinalIgnoreCase)
                ? "<a href=\"/prod.php?which=25778\">Starstruck</a>"
                : "<html><head><title>Starstruck by The Black Lotus :: pouët.net</title><meta property=\"og:image\" content=\"https://content.pouet.net/shot.jpg\" /></head><body>Amiga AGA demo release date : august 2006</body></html>";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) });
        }
    }
}
