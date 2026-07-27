namespace Jellyfin.Plugin.AIRecommender.Data.Models;

public sealed class UserCollectionSubscription
{
    public Guid UserId { get; set; }
    public Guid CollectionDefinitionId { get; set; }
    public DateTime CreatedAt { get; set; }
}
