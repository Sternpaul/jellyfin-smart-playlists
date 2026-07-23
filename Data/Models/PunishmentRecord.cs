using System;

namespace Jellyfin.Plugin.AIRecommender.Data.Models
{
    public class PunishmentRecord
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        
        public Guid UserId { get; set; }
        public Guid MovieId { get; set; }
        
        public DateTime BannedAt { get; set; }
        public int CyclesRemaining { get; set; }
        
        // This is the active penalty weight. It decays over time (e.g. 4 weeks).
        public double PenaltyWeight { get; set; } = 1.0; 
    }
}
