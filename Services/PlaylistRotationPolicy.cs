using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AIRecommender.Services;

public static class PlaylistRotationPolicy
{
    public static IReadOnlyList<Guid> Select(
        IEnumerable<Guid> previousMembers,
        IEnumerable<Guid> rankedEligibleCandidates,
        int targetSize,
        int rotationPercent)
    {
        ArgumentNullException.ThrowIfNull(previousMembers);
        ArgumentNullException.ThrowIfNull(rankedEligibleCandidates);

        if (targetSize <= 0)
        {
            return Array.Empty<Guid>();
        }

        var ranked = rankedEligibleCandidates
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ranked.Count == 0)
        {
            return Array.Empty<Guid>();
        }

        targetSize = Math.Min(targetSize, ranked.Count);
        rotationPercent = Math.Clamp(rotationPercent, 0, 100);
        var replacementCount = (int)Math.Ceiling(targetSize * (rotationPercent / 100.0));
        var retentionTarget = targetSize - replacementCount;
        var eligible = ranked.ToHashSet();
        var previous = previousMembers
            .Where(eligible.Contains)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var previousSet = previous.ToHashSet();

        var result = previous.Take(retentionTarget).ToList();
        var selected = result.ToHashSet();

        // Prefer genuinely new candidates for the configured replacement slots.
        foreach (var candidate in ranked.Where(id => !previousSet.Contains(id)))
        {
            if (result.Count >= targetSize)
            {
                break;
            }

            if (selected.Add(candidate))
            {
                result.Add(candidate);
            }
        }

        // A small library may not have enough genuinely new candidates. Fill the
        // remainder deterministically rather than returning an undersized playlist.
        foreach (var candidate in ranked)
        {
            if (result.Count >= targetSize)
            {
                break;
            }

            if (selected.Add(candidate))
            {
                result.Add(candidate);
            }
        }

        return result;
    }
}
