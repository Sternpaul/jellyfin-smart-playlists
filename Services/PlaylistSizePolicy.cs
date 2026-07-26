using System;

namespace Jellyfin.Plugin.AIRecommender.Services;

public static class PlaylistSizePolicy
{
    public const int DefaultMaximum = 20;
    public const int MinimumMaximum = 5;
    public const int MaximumMaximum = 100;

    public static int Resolve(int configuredMaximum, int? sourceDefault, int availableCount)
    {
        if (availableCount <= 0)
        {
            return 0;
        }

        var maximum = configuredMaximum is >= MinimumMaximum and <= MaximumMaximum
            ? configuredMaximum
            : DefaultMaximum;
        var desired = sourceDefault.HasValue && sourceDefault.Value > 0
            ? sourceDefault.Value
            : maximum;

        return Math.Min(availableCount, Math.Min(maximum, desired));
    }
}
