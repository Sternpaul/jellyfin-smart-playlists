using System.Reflection;
using Jellyfin.Plugin.AIRecommender.Services;
using Xunit;

namespace Jellyfin.Plugin.AIRecommender.Tests;

public class PlaylistCleanupTests
{
    private static bool ShouldDelete(Guid ownerId, string name, Guid targetUserId)
    {
        var method = typeof(PlaylistEngine).GetMethod(
            "ShouldDeleteRecommendationPlaylist",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return (bool)method.Invoke(null, new object?[] { ownerId, name, targetUserId })!;
    }

    [Fact]
    public void Personal_playlist_owned_by_target_user_is_preserved()
    {
        var userId = Guid.NewGuid();
        Assert.False(ShouldDelete(userId, "My Friday Movies", userId));
    }

    [Fact]
    public void Plugin_playlist_owned_by_target_user_is_deleted()
    {
        var userId = Guid.NewGuid();
        Assert.True(ShouldDelete(userId, "For You", userId));
    }

    [Fact]
    public void Ownerless_plugin_ghost_is_deleted()
    {
        Assert.True(ShouldDelete(Guid.Empty, "Because You Watched Arrival11", Guid.NewGuid()));
    }

    [Fact]
    public void Another_users_plugin_playlist_is_preserved()
    {
        Assert.False(ShouldDelete(Guid.NewGuid(), "For You", Guid.NewGuid()));
    }
}
