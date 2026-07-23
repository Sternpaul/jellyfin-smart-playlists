using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AIRecommender.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.AIRecommender.Data
{
    public class MovieStore
    {
        private readonly string _dbPath;

        public MovieStore(MediaBrowser.Common.Configuration.IApplicationPaths applicationPaths)
        {
            _dbPath = Path.Combine(applicationPaths.PluginConfigurationsPath, "airecommender.db");
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            using var db = GetContext();
            db.Database.EnsureCreated();
        }

        private AiDbContext GetContext()
        {
            return new AiDbContext(_dbPath);
        }

        public async Task<List<MovieMetadata>> GetAllMoviesAsync(CancellationToken cancellationToken = default)
        {
            using var db = GetContext();
            return await db.Movies.ToListAsync(cancellationToken);
        }

        public async Task<List<MovieMetadata>> GetUnclassifiedMoviesAsync(CancellationToken cancellationToken = default)
        {
            using var db = GetContext();
            return await db.Movies.Where(m => !m.IsClassified).ToListAsync(cancellationToken);
        }

        public async Task SaveMoviesAsync(IEnumerable<MovieMetadata> movies, CancellationToken cancellationToken = default)
        {
            using var db = GetContext();
            foreach (var movie in movies)
            {
                var existing = await db.Movies.FindAsync(new object[] { movie.ItemId }, cancellationToken);
                if (existing == null)
                {
                    db.Movies.Add(movie);
                }
                else
                {
                    db.Entry(existing).CurrentValues.SetValues(movie);
                }
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        public async Task<UserWatchlistConfig?> GetUserWatchlistConfigAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            using var db = GetContext();
            return await db.UserWatchlists.FindAsync(new object[] { userId }, cancellationToken);
        }

        public async Task SaveUserWatchlistConfigAsync(UserWatchlistConfig config, CancellationToken cancellationToken = default)
        {
            using var db = GetContext();
            var existing = await db.UserWatchlists.FindAsync(new object[] { config.UserId }, cancellationToken);
            if (existing == null)
            {
                db.UserWatchlists.Add(config);
            }
            else
            {
                db.Entry(existing).CurrentValues.SetValues(config);
            }
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
