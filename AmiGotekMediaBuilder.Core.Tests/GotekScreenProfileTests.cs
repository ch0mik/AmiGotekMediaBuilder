using AmiGotekMediaBuilder.Core.Export;

namespace AmiGotekMediaBuilder.Core.Tests;

public sealed class GotekScreenProfileTests
{
    [Fact]
    public void DefaultProfileIsTheRecommended480x320Screen()
    {
        Assert.Equal("guition-jc3248w535c", GotekScreenProfile.Default.Id);
        Assert.Equal(480, GotekScreenProfile.Default.Width);
        Assert.Equal(320, GotekScreenProfile.Default.Height);
        Assert.Equal("recommended", GotekScreenProfile.Default.Status);
    }

    [Fact]
    public void SupportedProfilesHaveUniqueIdsAndPositiveDimensions()
    {
        var profiles = GotekScreenProfile.Supported;

        Assert.Equal(profiles.Count, profiles.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(profiles, profile =>
        {
            Assert.False(string.IsNullOrWhiteSpace(profile.DisplayName));
            Assert.DoesNotContain('(', profile.DisplayName);
            Assert.DoesNotContain(')', profile.DisplayName);
            Assert.True(profile.Width > 0);
            Assert.True(profile.Height > 0);
        });
    }
}
