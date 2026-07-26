using Jellyfin.Plugin.AIRecommender.Services;
using Xunit;

namespace Jellyfin.Plugin.AIRecommender.Tests;

public class PlaylistSizePolicyTests
{
    [Theory]
    [InlineData(5, 15, 100, 5)]
    [InlineData(20, 8, 100, 8)]
    [InlineData(50, 15, 100, 15)]
    [InlineData(7, 15, 4, 4)]
    public void Source_specific_size_is_capped_by_global_maximum_and_availability(
        int configuredMaximum,
        int sourceDefault,
        int available,
        int expected)
    {
        Assert.Equal(expected, PlaylistSizePolicy.Resolve(configuredMaximum, sourceDefault, available));
    }

    [Theory]
    [InlineData(5, 100, 5)]
    [InlineData(20, 100, 20)]
    [InlineData(100, 120, 100)]
    [InlineData(20, 3, 3)]
    public void Global_sized_playlist_uses_configured_maximum(
        int configuredMaximum,
        int available,
        int expected)
    {
        Assert.Equal(expected, PlaylistSizePolicy.Resolve(configuredMaximum, null, available));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(101)]
    [InlineData(-1)]
    public void Invalid_configured_maximum_falls_back_to_twenty(int invalidMaximum)
    {
        Assert.Equal(20, PlaylistSizePolicy.Resolve(invalidMaximum, null, 100));
    }
}
