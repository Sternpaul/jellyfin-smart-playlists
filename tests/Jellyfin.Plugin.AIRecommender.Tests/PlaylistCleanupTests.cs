using System.Reflection;
using Jellyfin.Plugin.AIRecommender.Services;
using Xunit;

namespace Jellyfin.Plugin.AIRecommender.Tests;

public class PlaylistCleanupTests
{
    private static bool ShouldDelete(Guid playlistId, params Guid[] registeredRotatingPlaylistIds)
    {
        var method = typeof(PlaylistEngine).GetMethod(
            "ShouldDeleteRegisteredPlaylist",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return (bool)method!.Invoke(null, new object?[]
        {
            playlistId,
            registeredRotatingPlaylistIds.ToHashSet()
        })!;
    }

    private static string LogicalKey(string displayName)
    {
        var method = typeof(PlaylistEngine).GetMethod(
            "GetManagedPlaylistLogicalKey",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, new object?[] { displayName })!;
    }

    private static IReadOnlyList<Guid> PreviousMembers(
        IReadOnlyDictionary<string, IReadOnlyList<Guid>> previousByLogicalKey,
        string displayName)
    {
        var method = typeof(PlaylistEngine).GetMethod(
            "GetPreviousPlaylistMembers",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (IReadOnlyList<Guid>)method!.Invoke(null, new object?[] { previousByLogicalKey, displayName })!;
    }

    [Fact]
    public void Because_you_watched_anchor_changes_keep_one_stable_logical_key()
    {
        Assert.Equal(LogicalKey("Because You Watched Arrival"), LogicalKey("Because You Watched Alien"));
    }

    [Fact]
    public void Distinct_fixed_playlist_slots_have_distinct_logical_keys()
    {
        Assert.NotEqual(LogicalKey("For You"), LogicalKey("Recently Added"));
    }

    [Fact]
    public void Changed_because_you_watched_anchor_reuses_prior_logical_slot_members()
    {
        var previousMembers = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var previous = new Dictionary<string, IReadOnlyList<Guid>>(StringComparer.OrdinalIgnoreCase)
        {
            [LogicalKey("Because You Watched Arrival")] = previousMembers
        };

        Assert.Equal(previousMembers, PreviousMembers(previous, "Because You Watched Alien"));
    }

    [Fact]
    public void Registered_rotating_playlist_is_deleted_by_jellyfin_id()
    {
        var playlistId = Guid.NewGuid();
        Assert.True(ShouldDelete(playlistId, playlistId));
    }

    [Fact]
    public void Unregistered_personal_or_legacy_playlist_is_preserved_even_if_its_name_matches_plugin_wording()
    {
        Assert.False(ShouldDelete(Guid.NewGuid()));
    }

    [Fact]
    public void Another_registered_playlist_does_not_authorize_deletion()
    {
        Assert.False(ShouldDelete(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void Persistent_collection_is_preserved_when_only_rotating_ids_are_supplied()
    {
        var persistentCollectionId = Guid.NewGuid();
        var rotatingId = Guid.NewGuid();
        Assert.False(ShouldDelete(persistentCollectionId, rotatingId));
    }
}
