using System;
using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.AIRecommender.Data.Models
{
    public class VerifiedWatch
    {
        public Guid UserId { get; set; }

        public Guid ItemId { get; set; }

        public DateTime WatchedAt { get; set; }

        public double PlaybackPercentage { get; set; }
    }
}
