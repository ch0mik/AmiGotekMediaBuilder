using System.Text;
using AmiGotekMediaBuilder.Core.Export;

namespace AmiGotekMediaBuilder.Core.Tests;

public sealed class GotekNfoRendererTests
{
    [Fact]
    public void EmitsRequiredLabels()
    {
        var text = GotekNfoRenderer.Render("Oil Imperium", "1992", "ECS", "QTX");
        Assert.StartsWith("Title: Oil Imperium\nBlurb: 1992 - ECS - QTX\n", text);
    }

    [Fact]
    public void NeverExceedsFirmwareByteLimit()
    {
        var text = GotekNfoRenderer.Render(new string('Ą', 600), null, null, new string('界', 600));
        Assert.True(Encoding.UTF8.GetByteCount(text) <= GotekNfoRenderer.MaxBytes);
        Assert.Contains("…", text);
    }
}
