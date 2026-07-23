using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AIRecommender.Data;
using Jellyfin.Plugin.AIRecommender.Data.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AIRecommender.Services
{
    public class MovieIndexer
    {
        private readonly ILibraryManager _libraryManager;
        private readonly MovieStore _movieStore;
        private readonly ILogger<MovieIndexer> _logger;

        public MovieIndexer(
            ILibraryManager libraryManager,
            MovieStore movieStore,
            ILogger<MovieIndexer> logger)
        {
            _libraryManager = libraryManager;
            _movieStore = movieStore;
            _logger = logger;
            
            // Hook into library events for incremental indexing
            _libraryManager.ItemAdded += OnItemAdded;
            _libraryManager.ItemUpdated += OnItemUpdated;
            _libraryManager.ItemRemoved += OnItemRemoved;
        }

        public async Task IndexLibraryAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Starting full library index for AI Recommender...");
            
            var allMovies = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Movie) },
                IsVirtualItem = false,
                Recursive = true
            }).OfType<Movie>().ToList();

            var newOrUpdatedMovies = new List<MovieMetadata>();
            var existingMovies = await _movieStore.GetAllMoviesAsync(cancellationToken);
            var existingDict = existingMovies.ToDictionary(m => m.ItemId);

            foreach (var movie in allMovies)
            {
                if (cancellationToken.IsCancellationRequested) break;

                // Create or update metadata
                if (!existingDict.TryGetValue(movie.Id, out var metadata))
                {
                    metadata = new MovieMetadata 
                    { 
                        ItemId = movie.Id,
                        DateAdded = DateTime.UtcNow
                    };
                    newOrUpdatedMovies.Add(metadata);
                }
                
                // Always sync basic metadata in case it changed in Jellyfin
                UpdateMetadataFromJellyfinItem(movie, metadata);
            }

            if (newOrUpdatedMovies.Any())
            {
                await _movieStore.SaveMoviesAsync(newOrUpdatedMovies, cancellationToken);
                _logger.LogInformation("Indexed {Count} new/updated movies.", newOrUpdatedMovies.Count);
            }
            
            _logger.LogInformation("Library indexing complete.");
        }

        private void UpdateMetadataFromJellyfinItem(Movie jellyfinMovie, MovieMetadata metadata)
        {
            metadata.Title = jellyfinMovie.Name;
            metadata.ReleaseYear = jellyfinMovie.ProductionYear;
            metadata.Plot = jellyfinMovie.Overview;
            
            // Try to extract IMDB ID
            jellyfinMovie.ProviderIds.TryGetValue(MetadataProvider.Imdb.ToString(), out var imdbId);
            metadata.ImdbId = imdbId;

            // Extract director and top cast
            var people = _libraryManager.GetItemPeople(new InternalItemsQuery { ItemIds = new[] { jellyfinMovie.Id } });
            
            metadata.Director = string.Join(", ", people
                .Where(p => p.Type == PersonType.Director)
                .Select(p => p.Name));
                
            metadata.Cast = string.Join(", ", people
                .Where(p => p.Type == PersonType.Actor)
                .Take(5)
                .Select(p => p.Name));

            metadata.LastUpdated = DateTime.UtcNow;
            
            // Note: We don't reset IsClassified to false unless the Plot significantly changes.
            // For now, if it's already classified, it remains classified.
        }

        // --- Incremental Event Handlers ---

        private void OnItemAdded(object? sender, ItemChangeEventArgs e)
        {
            if (e.Item is Movie movie)
            {
                _logger.LogInformation("New movie detected: {Title}. Indexing for AI...", movie.Name);
                var metadata = new MovieMetadata { ItemId = movie.Id, DateAdded = DateTime.UtcNow };
                UpdateMetadataFromJellyfinItem(movie, metadata);
                
                // Fire and forget save
                Task.Run(() => _movieStore.SaveMoviesAsync(new[] { metadata }));
            }
        }

        private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
        {
            if (e.Item is Movie movie)
            {
                Task.Run(async () => 
                {
                    var existing = (await _movieStore.GetAllMoviesAsync()).FirstOrDefault(m => m.ItemId == movie.Id);
                    if (existing != null)
                    {
                        UpdateMetadataFromJellyfinItem(movie, existing);
                        await _movieStore.SaveMoviesAsync(new[] { existing });
                    }
                });
            }
        }

        private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
        {
            if (e.Item is Movie movie)
            {
                // In a production scenario we might delete it from the SQLite DB,
                // but for now we can leave it (or implement a delete method in MovieStore).
            }
        }
    }
}
