using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AIRecommender.Data.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CollectionDefinitionType
{
    ExplicitMovies = 0,
    CuratedUniverse = 1
}

public sealed class CollectionDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public CollectionDefinitionType Type { get; set; }
    public string TmdbMovieIdsJson { get; set; } = "[]";
    public string ImdbIdsJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
