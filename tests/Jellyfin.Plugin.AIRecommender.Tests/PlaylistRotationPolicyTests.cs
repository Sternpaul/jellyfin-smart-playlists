using System;
using System.Linq;
using Jellyfin.Plugin.AIRecommender.Services;
using Xunit;

namespace Jellyfin.Plugin.AIRecommender.Tests;

public class PlaylistRotationPolicyTests
{
    private static readonly Guid[] Candidates = Enumerable.Range(1, 20)
        .Select(i => Guid.Parse($"00000000-0000-0000-0000-{i:000000000000}"))
        .ToArray();

    [Fact]
    public void Thirty_percent_replaces_exactly_three_of_ten_when_pool_allows()
    {
        var previous = Candidates.Take(10).ToArray();
        var ranking = Candidates.ToArray();

        var result = PlaylistRotationPolicy.Select(previous, ranking, 10, 30);

        Assert.Equal(previous.Take(7), result.Take(7));
        Assert.Equal(Candidates.Skip(10).Take(3), result.Skip(7));
        Assert.Equal(7, result.Intersect(previous).Count());
    }

    [Fact]
    public void Zero_percent_keeps_every_still_eligible_member()
    {
        var previous = Candidates.Take(10).ToArray();

        var result = PlaylistRotationPolicy.Select(previous, Candidates, 10, 0);

        Assert.Equal(previous, result);
    }

    [Fact]
    public void One_hundred_percent_uses_only_new_members_when_pool_allows()
    {
        var previous = Candidates.Take(10).ToArray();

        var result = PlaylistRotationPolicy.Select(previous, Candidates, 10, 100);

        Assert.Equal(Candidates.Skip(10).Take(10), result);
        Assert.Empty(result.Intersect(previous));
    }

    [Fact]
    public void Ineligible_previous_members_are_removed_even_at_zero_percent()
    {
        var previous = Candidates.Take(10).ToArray();
        var ranking = Candidates.Skip(2).ToArray();

        var result = PlaylistRotationPolicy.Select(previous, ranking, 10, 0);

        Assert.DoesNotContain(previous[0], result);
        Assert.DoesNotContain(previous[1], result);
        Assert.Equal(10, result.Count);
    }

    [Fact]
    public void First_generation_uses_top_ranked_candidates()
    {
        var result = PlaylistRotationPolicy.Select(Array.Empty<Guid>(), Candidates, 10, 30);

        Assert.Equal(Candidates.Take(10), result);
    }

    [Fact]
    public void Rotation_reuses_old_candidates_when_no_new_eligible_pool_exists()
    {
        var previous = Candidates.Take(10).ToArray();

        var result = PlaylistRotationPolicy.Select(previous, previous, 10, 100);

        Assert.Equal(previous, result);
    }
}
