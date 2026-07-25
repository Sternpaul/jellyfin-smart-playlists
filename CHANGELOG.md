# Changelog

All notable changes to the Jellyfin AI Recommender plugin.

## v1.5.35
- **FIXED Index crash + TMDB enrichment concurrency crash (v1.5.34 regression).** `MovieStore.SaveMoviesAsync` used `CurrentValues.SetValues(movie)`, which tried to overwrite the primary key `ItemId` and threw `InvalidOperationException` / `DbUpdateConcurrencyException`. Now normalizes `ItemId` to lowercase and never modifies the key on update (sets `movie.ItemId = existing.ItemId` before `SetValues`; normalizes on insert). This unblocks "Index & Classify Library" and stops the per-user enrichment exceptions.
- **FIXED 14-minute refresh.** TMDB keyword enrichment was inside `RefreshUserPlaylistsAsync`, so it ran once PER USER (loading all 1699 rows + hitting TMDB each time). Moved it to `EnrichKeywordsOnceAsync`, called once per refresh before the user loop. Refresh now enriches the shared DB a single time.

## v1.5.34
- **FIXED the root cause of empty playlists (the real one).** `MovieIndexer.IndexLibraryAsync` built a `metadata` object for every library movie but never added it to the `newOrUpdatedMovies` list, so `SaveMoviesAsync` was never called — the full Index logged "Library indexing complete" having written 0 rows. The recommender DB stayed empty, so every playlist generation skipped with "no items." Fix: `newOrUpdatedMovies.Add(metadata);` inside the index loop (MovieIndexer.cs). This is the actual bug behind "0 movies in DB"; the v1.5.32/v1.5.33 WAL/connection-string work was necessary plumbing but downstream of this. After installing, run Index & Classify Library then Refresh Playlists — the log should show `Indexed 1735 new/updated movies.` and `SaveMoviesAsync committed. DB now has 1735 movie rows.`

## v1.5.33
- **CRITICAL FIX for v1.5.32 (plugin failed to start).** v1.5.32 set `Journal Mode=Delete` in the SQLite connection string (`Data Source=...;Journal Mode=Delete`). Microsoft.Data.Sqlite does NOT support journal-mode keywords in the connection string — it throws `System.ArgumentException: Connection string keyword 'journal mode' is not supported` when the connection opens. Because `MovieStore` opens a connection during construction (`EnsureCreated()`), this crashed both scheduled tasks (AutoRefreshPlaylistsTask, LibraryIndexingTask) at Jellyfin startup, so the plugin never worked. Fix: removed the keyword from the connection string (now just `Data Source={path}`). The journal mode is still switched to DELETE the correct way — via `PRAGMA journal_mode=DELETE` + `PRAGMA wal_checkpoint(TRUNCATE)` in `MovieStore.InitializeDatabase()`, which was already present and correct in v1.5.32 but never executed because the bad connection string threw first. (Corrected the v1.5.32 CHANGELOG note that claimed the keyword was valid.) Build verified clean.

## v1.5.32
- **Fixed the root cause of empty playlists on Docker (bind-mount SQLite WAL durability bug).** EF Core's SQLite provider defaults to WAL (Write-Ahead Logging), which writes to `-wal`/`-shm` sidecar files. On Docker bind-mounts these sidecar files can be invisible to new connections, so the Index task would write 1735 movies to the WAL while the Refresh task (opening a fresh connection) read 0 from the base `.db` file. Fix: (1) on startup, `PRAGMA wal_checkpoint(TRUNCATE)` flushes any data stuck in an existing WAL file into the main DB (rescuing rows that would otherwise be lost); (2) `PRAGMA journal_mode=DELETE` switches to a traditional rollback journal so WAL sidecar files are never created again; (3) the connection string now includes `Journal Mode=Delete` so every new EF context opens in DELETE mode. The Jellyfin log now shows `SQLite journal mode set to: delete` at startup, and `SaveMoviesAsync committed. DB now has N movie rows.` after every index write.
- **Consolidated v1.5.20–v1.5.29 code changes** (these were shipped via tag-only pushes but never landed on main):
  - v1.5.20: "Because You Watched" playlist title now matches the dominant seed (the movie that contributed the most picks), not just the most-recently watched.
  - v1.5.21: Hidden Gems fame penalty — TMDB popularity pushes blockbusters down so genuinely obscure-acclaimed films rise. New `FamePenaltyWeight` config knob (default 0.15, 0 = off).
  - v1.5.24: Fixed NULL Popularity crash on upgraded DBs (backfill NULLs to 0).
  - v1.5.25: Fixed `no such column: EnableRatingsPlaylist` crash; new-movie classify trigger (20s debounced); Generate button feedback.
  - v1.5.26: Index/classify button reports real results; TMDB enrichment can't abort playlist generation (3-minute timeout, non-cancellable DB save); library-vs-DB count logging.
  - v1.5.27: Reverted buttons to scheduled tasks (fixes loading-circle/cancel spam); quieted TMDB per-movie logging; fixed classified-but-empty subcategories never re-sent to AI.
  - v1.5.28: Fixed index crash on duplicate ItemId rows (one-time DB repair + UNIQUE index); orphan pruning for deleted movies; stoppable refresh task.
  - v1.5.29: Fixed DB ballooning (Jellyfin re-assigns ItemId on re-scan; indexer now deduplicates by ImdbId and syncs the row's ItemId to the current GUID; `SaveMoviesAsync` upserts by ImdbId). Fixed orphan-prune `DbUpdateConcurrencyException` (replaced EF RemoveRange with raw SQL).
- **Removed diagnostic Warning logs** from v1.5.30/v1.5.31 (DIAG ForYou, DIAG Unwatched). Replaced with a permanent Info-level log: `Playlist input for {UserId}: N movies in DB, M watched, K eligible`.
- **Added startup diagnostics**: `AI Recommender DB path: ...` logged at plugin load, confirming which database file is in use.

## v1.5.19
- **Fixed the release workflow's manifest auto-update (the part that kept failing to land).** Root cause: the checkout step ran on the release *tag* (detached HEAD), and the manifest push used `git push --force-with-lease origin HEAD:main` from that detached HEAD — the lease can't verify a remote it never fetched, so every retry failed silently and the manifest never updated. It also based the edit on the tag's `repo/manifest.json`, which would have dropped prior versions. The step now checks out `main` first and does a clean fast-forward push. No plugin code change — this is a CI/release fix. The previous 1.5.17/1.5.18 manifest entries were added by hand in the interim.

## v1.5.18
- **Documentation: external-user data formats.** README now documents the exact JSON/CSV shapes for both the ratings feed (`Ratings JSON URL`: array of `{imdb_id, rating, title?}`) and the watchlist import (JSON array of `{imdb_id, title, release_year}` or CSV with `name`/`year` columns), plus the matching rules. CHANGELOG reworded to be vendor-neutral (no "your own repo" / first-person) ahead of other users adopting the plugin.

## v1.5.17
- **Ratings now load from a JSON export instead of scraping Letterboxd.** Replaced the HTML scraper (spoofed user-agent, fragile DOM parsing, title-guess matching) with `FetchRatingsFromJsonAsync`, which fetches a user-supplied URL to a JSON file of ratings. Each entry carries an `imdb_id` and a `0.5`–`5.0` rating, so library matching is now **exact by IMDB id** (previously title-only and error-prone). The per-user config field changed from "Letterboxd Username" to "Ratings JSON URL"; blank = no ratings weight (unchanged guarantee). ToS-clean, no periodic HTML requests, fail-safe on fetch errors. See README for the expected JSON format.

## v1.5.16
- **Fixed DI crash on the per-user config page.** `WatchHistoryService` depends on `TasteProfiler`, which was never registered in the dependency-injection container, so opening a user's watchlist/ratings config (and the `UserWatchlistConfig` endpoints) threw `Unable to resolve service for type 'TasteProfiler'`. Registered it. This was introduced in the v1.5.14 keyword work (TasteProfiler gained use by WatchHistoryService). No other unregistered services were found.

## v1.5.15
- **Config page overhaul for readability.** Reorganised into clear sections (AI Provider, Playlist Behavior, Scoring & Weighting, Learning from Watches, Freshness Nudges, Enabled Playlists, Per-User Watchlist & Ratings) with consistent label + input + plain-English description (and the default value shown on every field). Added a short "How a movie's score is built" explainer at the top of the weighting section so the numbers stop being mysterious. Every weight now says "0 = off". Also surfaced three controls that existed in config but were missing from the UI: **Letterboxd Ratings Weight**, **TMDB Keyword Weight**, and **Custom Endpoint**.

## v1.5.14
- **TMDB keywords as a precision signal.** Curated, objective tags (e.g. `serial killer`, `neo-noir`, `self-fulfilling prophecy`) are fetched per movie (TMDB id resolved from the already-stored IMDB id; cached in `tmdb_keywords_cache.json`) and added as a configurable overlap term in **For You** taste-matching (`KeywordWeight`, default 0.10) and the **Because You Watched** similarity engine. More reliable than the LLM's subjective themes, and fetched at refresh time — no re-classification needed. Add a TMDB v3 API key in the plugin config to enable; blank disables it.

## v1.5.13
- **No Letterboxd username = zero ratings weight (guaranteed).** On every refresh, if a user has no `RatingsUsername` set, any previously-stored ratings are cleared so the dominant ratings signal can never leak into their recommendations. (Ratings only apply when a username is configured.)
- README overhaul: tight overview up top (how it works, playlist types, ratings-as-dominant, install), with the full config tables, technical internals, API, cost, and roadmap moved to a DETAILS section at the bottom. Corrected the stale "Review Nudging" description (ratings are now the dominant signal, not a 0–15% nudge).

## v1.5.12
- **Letterboxd ratings are now the dominant recommendation signal.** Each user can enter their public Letterboxd username in the per-user config; the plugin scrapes their ratings page (`letterboxd.com/<user>/films/ratings/`), matches films to the library (reusing the lenient matcher), and stores per-user ratings. A 5-star rating contributes up to `RatingWeight` (default 0.50) to the For You score, dwarfing the smaller taste/affinity nudges. Added a "Highly Rated by You" playlist (top-rated library films, unwatched preferred). Scraping is fail-safe (errors logged, never break refresh) and cached wholesale per refresh. Note: scraping is ToS-gray (no open ratings API) and may break if Letterboxd changes its page markup.

## v1.5.11
- **"From Your Watchlist" playlist now generates.** Root cause: the config page saved the per-user watchlist config without `ImportMethod`, so it defaulted to `None` and `SyncWatchlistAsync` returned immediately without fetching the URL — the playlist was silently skipped. `ImportMethod` is now derived from the data on save (and inferred at sync time for already-saved configs), so existing setups start working on the next refresh with no re-save needed.
- **Lenient watchlist title matching.** Matching was exact (`string.Equals`), so minor title/year/punctuation differences caused 0 matches. Now normalizes titles (case/whitespace/year/separators) and adds a substring fallback, so real movies actually match.

## v1.5.10
- **Discover and Wild Card playlists now always appear.** Both generators silently produced nothing when their strict filters yielded zero candidates (e.g. least-familiar subcategories already exhausted by other playlists), and `CreateOrUpdateJellyfinPlaylistAsync` skips empty playlists — so they vanished. Added fallbacks: Discover falls back to top-acclaim unwatched films; Wild Card falls back to any high-acclaim unwatched film. Fixes "admin missing some playlists."

## v1.5.9
- **User exclusions now take effect immediately.** Saving config with excluded users deletes their playlists right away (previously you had to wait for the next 12h refresh). Exclusion matching also normalized via `Guid.Parse` (handles case/braces).
- **Debug panel no longer fails on large libraries.** `Active Penalties` and `Active Boosts` are now capped at 50 entries each (Exclusions already capped); the panel shows counts (`PenaltyCount`, `BoostCount`, `ExclusionCount`) and a sample. Fixes the panel breaking when a user has hundreds of watched movies.

## v1.5.8
- **Fixed `no such table: Affinities` crash on existing databases.** `EnsureCreated()` only creates tables when the DB file is new, so a database written by an older version (missing the `Affinities` table) stayed broken and the Debug panel / playlist refresh crashed. Startup now also runs idempotent `CREATE TABLE IF NOT EXISTS` for all five tables, upgrading existing DBs in place without data loss.

## v1.5.7
- **Build is now warning-clean (0 warnings).** Turned off XML doc generation (a Jellyfin plugin doesn't ship/consume a `.xml` doc file, so the 428 CS1591 "missing XML comment" warnings were pure noise) and genuinely fixed the 12 nullable-reference warnings (CS8600/8602/8604/8605) in `AutoRefreshPlaylistsTask.cs` and `MovieClassifier.cs` with proper null-guards. No behavior change.

## v1.5.6
- **User exclusions are now name-based checkboxes:** the config page lists every Jellyfin user with a tick-box; ticking a user disables playlist generation for them. No more pasting GUIDs. Backend still stores GUIDs (unchanged), so existing exclusions keep working.

## v1.5.5
- **Recently-surfaced history:** every time a movie is placed in a playlist, it's logged in a new `SurfaceHistory` table (user, movie, playlist, time). The "What's Happening" debug panel now lists your recently surfaced movies with which playlist and when — so you can see exactly what the engine put in front of you and when.

## v1.5.4
- **Taste-drift view:** the engine now snapshots each user's taste profile weekly (new `TasteSnapshots` table). The "What's Happening" debug panel shows which subcategories you've **gained**, **lost**, or **shifted** since your oldest snapshot — so you can watch your taste evolve over time.

## v1.5.3
- **Consumption-rate-tuned decay:** a user's effective affinity + novelty half-lives now scale by how fast they actually watch (recent weekly rate vs `Decay Reference`). Fast watchers decay quicker (fresher playlists); slow watchers slower. Clamped 0.3x–3x. Default reference 3/week (your stated rate).

## v1.5.2
- **No cross-playlist duplicates:** a movie now appears in at most one of a user's discovery playlists (For You, Hidden Gems, Discover, Wild Card, Subcategory). Once placed, it's excluded from the others so the same film never shows up twice. "Because You Watched" is exempt (it's an intentional similarity list) but still excludes already-watched movies; Recently Added and the Watchlist playlist are their own sources.

## v1.5.1
- **Completion-weighted learning:** a watch only counts as a real signal (sibling penalty + similar-movie reward) if playback reached `Min Watch % to Learn` (default 50%). A quick glance or test below the threshold is ignored — no penalty, no reward. Manual "mark played" without position info is treated as 0% and ignored. 0 = any "played" counts (old behaviour); 100 = must finish.

## v1.5.0
- **Config-page transparency:** every learning knob now has a plain-language explanation. The key clarification — *Affinity Rank Weight* is a small **additive** nudge on top of the fixed `0.7×subcategory + 0.3×mood` base For-You score; it does **not** renormalize the other weights toward 1.
- **"What's Happening Right Now" panel:** new read-only, per-user live view on the config page showing top taste weights, currently penalized movies (with cooling time left), active novelty boosts, and — for the last refresh — every **excluded** movie and *why* (already watched / not yet AI-classified / over the diversity cap). Observability only; no behavior change.
- New API: `GET AIRecommender/Debug/{userId}` (read-only snapshot).

## v1.4.3
- **Novelty tracking:** movies that haven't been surfaced in a playlist recently get a small bonus that decays over `Novelty Half-Life` (default 30d), so the same films stop recycling to the top every refresh.
- **Taste-fit-gated new-movie boost:** in "For You", the new-movie recency boost now only applies when a film's taste-fit score is ≥ `NewMovieBoostMinFit` (default 0.30) — fresh additions surface *because they fit*, not just because they're new.
- New config: `NewMovieBoostMinFit`, `NoveltyBonus`, `NoveltyHalfLifeDays`.

## v1.4.2
- **Director affinity:** learned per-director weights (decay-weighted from watch history) nudge movies by filmmakers the user returns to. Adds the `DirectorPreferences` signal to the taste profile.
- **Configurable Diversity Cap** (gentle default 60%): no single subcategory may occupy more than this share of a playlist (anti-bubble). Set lower (e.g. 40) for stricter diversification.
- **Soft penalty:** rejected movies now gracefully sink in ranking during the cooling window (`SoftPenaltyStrength`, 0 = hard ban … 1 = none; default 0.50) instead of being hard-excluded.
- New config: `DiversityCapPercent`, `DirectorAffinityBonus`, `SoftPenaltyStrength`.

## v1.4.1 (yanked — broken build)
## v1.4.0 (yanked — broken build)
> Superseded by v1.4.2 / v1.4.3 (compile fixes). Do not use.

## v1.3.2
- **Real learning engine:** per-(user, movie) `MovieAffinity` rating table.
- **Punishment / reward written only on watch events:** siblings of a picked movie get a rejection penalty + temporary ban; similar movies get a reward; active penalties are pulled forward. Read with lazy exponential time-decay (no refresh-time writes).
- **Hidden Gems / Discover / Wild Card made real** — similarity/diversity-driven, no `Guid.NewGuid()` randomness.
- **New-movie recency boost** so fresh additions surface beyond "Recently Added".
- 6 configurable learning knobs in a new "Dynamic Rating / Learning" config section.

## v1.3.1 (yanked — broken build)
## v1.3.0 (yanked — broken build)
> Superseded by v1.3.2 (compile fixes). Do not use.

## v1.2.13
- Playlist generation now starts from a **clean slate**: all playlists owned by a user are wiped before regenerating.

## v1.2.11 – v1.2.12
- Playlists belonging to a disabled user are deleted; enabled users get a full wipe before regen.

## v1.2.10
- **Per-user exclusion:** `DisabledUserIds` config skips playlist generation for chosen users.

## v1.2.9
- **Watchlist playlist** now reads the user's real Letterboxd/CSV watchlist (`MatchedItemIds`), not a mocked top-10.

## v1.2.8
- **Because You Watched** seeds on the 5 most-recently *watched* movies and is named after the most recent.

## v1.2.7
- Removed emojis from all playlist names.

## v1.2.6
- Fixed per-user playlist scoping (each user sees only their own recommendation playlists).
