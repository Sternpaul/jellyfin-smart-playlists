# Changelog

All notable changes to the Jellyfin AI Recommender plugin.

## v1.5.31
- **Diagnostic build (temporary), continued.** Added `DIAG Unwatched` Warning log to `GetUnwatchedClassifiedMoviesAsync` printing `all` (DB movie count), `watchedIds` (count excluded as watched), `result` (unwatched/eligible count), and `unclassifiedExcluded` (= all − watched − result). This isolates whether the empty generator input is caused by a zero DB read, a watch-state join marking everything played, or an `IsClassified=false` flag. No behavioral change to generation.

## v1.5.30
- **Diagnostic build (temporary).** Added `DIAG ForYou` Warning logs to `GenerateForYouPlaylistAsync` that print `unwatched.Count`, `claimed.Count`, `scoredMovies.Count`, `hasTaste`, `tasteSize`, `exploreSize`, and `finalPicks.Count` at runtime. Purpose: root-cause why playlists were "Skipped … because there were no items" for enabled users even though the debug snapshot reported 1699 eligible, unclassified movies. These log lines will be removed once the cause is confirmed. (No behavioral change to generation.)

## v1.5.29
- **Root cause of the ballooning DB (and stalled playlist generation): Jellyfin re-assigns `ItemId` whenever a movie is re-added/re-scanned, so the indexer treated each re-add as a brand-new movie and inserted another row.** The library of ~1700 films accumulated 3367 rows (same film stored up to 4× under different GUIDs, in mixed casing). Playlist similarity then ran O(n²) over the bloated table, which is what made refresh stall and "fail with nothing created." The previous (v1.5.28) dedup keyed on `ItemId` — but every phantom row already had a *distinct* ItemId, so it deleted nothing.
  - **v1.5.29 fix:** the one-time startup repair now dedups by **ImdbId** (the real stable identity) — keep one row per ImdbId — plus one row per `(Title, ReleaseYear)` for movies with no ImdbId, then normalizes `ItemId` casing across `Movies`/`Affinities`/`SurfaceHistory`/`UserRatings`. The indexer now reuses the existing row by ImdbId and *syncs the row's ItemId* to Jellyfin's current GUID instead of inserting a duplicate. `SaveMoviesAsync` upserts by ImdbId too. Net effect: re-adding a movie updates its row; the DB can no longer grow on every refresh.
  - Verified against the live DB: 3367 → **1699 rows** (1686 titles), 0 remaining duplicate ImdbIds. The deduped file was returned to the user.
- **Fixed the orphan-prune `DbUpdateConcurrencyException`** (the `Orphan pruning failed; continuing` warning). The EF `RemoveRange` path threw against this DB; replaced with raw SQL `DELETE ... WHERE LOWER(ItemId) IN (...)`.

## v1.5.28
- **Fixed the Index & Classify crash ("An item with the same key has already been added").** Your `airecommender.db` had duplicate `ItemId` rows — from a database created before the `PRIMARY KEY (ItemId)` constraint existed (`CREATE TABLE IF NOT EXISTS` is a no-op on pre-existing tables, so upgraded DBs never got the PK, and `SaveMoviesAsync` let duplicates in). `IndexLibraryAsync` blew up at `ToDictionary(ItemId)` in ~0 seconds, so the Classify Library button never ran. Now: (1) startup runs a one-time repair that collapses duplicate rows (keeps the latest) and adds a `UNIQUE` index so dups can't recur; (2) the index no longer throws on dups; (3) `SaveMoviesAsync` is a true upsert so it can't create dups going forward.
- **Fixed the DB being far larger than your real library (the "3328 but I don't have that many" mystery).** The index only ever *added* rows — `OnItemRemoved` was a no-op — so movies you deleted from Jellyfin stayed in the DB as orphans, and TMDB enrichment counted those rows (hence 3328). The index now prunes rows whose `ItemId` isn't in the current Jellyfin library. Next Classify Library run will shrink the DB to match your real movie count.
- **Made the Refresh task stoppable.** v1.5.26's enrichment timeout CTS was not linked to the task's cancellation token, so clicking Stop couldn't abort the enrichment loop. Now it's linked (with a 3-minute hard cap) and a Stop request is logged instead of hanging.

## v1.5.27
- **Reverted the config-page buttons to fire-and-forget scheduled tasks (fixes v1.5.26 regression).** v1.5.26 ran Index/Classify and Playlist Refresh *inside the HTTP request*, which caused a forever loading circle, no way to stop the job, and — when the browser closed the tab — the request's cancellation token aborted every in-flight TMDB HTTP call and EF save at once, producing 2000+ `TaskCanceledException` spam. The buttons now trigger the real Jellyfin scheduled tasks again: progress is visible and the job is stoppable from Dashboard > Scheduled Tasks.
- **Quieted TMDB enrichment logging.** Per-movie TMDB failures now log at Debug (message only, no stack trace) instead of Warning + full exception, so transient TMDB hiccups stop flooding the log.
- **Fixed "classified but never sent to OpenRouter" (real pre-existing bug).** `MovieClassifier.ProcessClassificationResult` marked every batch movie `IsClassified = true` unconditionally — including movies the AI returned with **empty `Subcategories`**. Those movies got flagged classified with no usable tags and were never re-sent for classification. Now a movie is only marked classified when it actually received non-empty subcategories; movies that came back empty stay unclassified and get re-sent on the next run. (Same fix applied to the text-fallback parse path.)

## v1.5.26
- **Manual "Classify Library" / "Generate Playlists" buttons now report real results** (were fire-and-forget "Task Started" with no feedback). The index/classify button runs the index + classification synchronously in the request and returns how many movies were processed and how many remain unclassified; the Generate button already awaited and now surfaces per-run success/errors. The config page shows these messages instead of "Task Started".
- **TMDB keyword enrichment can no longer abort playlist generation.** It ran under the request's cancellation token, so cancelling the button (tab close / timeout) or a slow TMDB call cancelled the EF DB write and killed the whole refresh before any playlist was built. Enrichment now uses its own 3-minute timeout, a 10s per-movie budget, and a non-cancellable DB save, and any failure is logged and the refresh continues.
- **Newly-added movies get a more reliable incremental classify.** The 20s debounced classify after an `ItemAdded` now retries up to 3 times (30s apart) so a transient AI/rate-limit error doesn't leave new movies permanently unclassified.
- **Index now logs library-vs-DB counts** ("Library scan: N movies in Jellyfin, M in recommender DB") so it's visible whether new movies are being detected.

## v1.5.25
- **Fixed three bugs reported from user logs:**
  1. **`no such column: u.EnableRatingsPlaylist` crash.** The `UserWatchlists` table DDL predated v1.5.17 and was missing `RatingsJsonUrl` + `EnableRatingsPlaylist`; no migration added them, so every load/save of the per-user watchlist/ratings config threw and dropped the save. Added an idempotent ALTER migration (and the columns to the fresh-install DDL).
  2. **New movies never got classified.** The incremental `ItemAdded` handler saved metadata but never triggered classification, so freshly-added movies sat unclassified until the next 2am daily task. Added a 20s debounced classification trigger on add.
  3. **"Generate Playlists" button hung on "running" with no logs.** It called `_taskManager.Execute` (fire-and-forget) and returned immediately, with no completion signal and swallowed errors. Now it awaits the refresh per user inside the request and returns success/error, so the UI reflects real progress and failures are visible.

## v1.5.24
- **Fixed a crash on upgraded databases (regression from v1.5.21).** `Popularity` is a non-nullable `double` in the model but the column was added as NULL, so every pre-v1.5.21 row had NULL there. `GetAllMoviesAsync` → `GetDouble` then threw "The data is NULL at ordinal 14", killing the index/classify task on existing installs. Root cause: `MovieStore.MigrateAddMovieKeywordColumns` added `Popularity` nullable and never backfilled. Fix: backfill NULLs to 0 at migration time, and make the column `REAL NOT NULL DEFAULT 0` for fresh installs.

## v1.5.23
- **Hidden Gems description now reflects the dual "hidden" definition.** "Hidden" = high acclaim AND outside your watched subcategories AND genuinely obscure (low TMDB popularity). The prior wording framed popularity only as a "push down"; it is in fact a defining criterion alongside subcategory-unfamiliarity. Code behavior unchanged (v1.5.21 fame-penalty soft-rank still applies). Docs + code comment only.

## v1.5.22
- **README accuracy pass.** Audited every playlist description against the actual generator code. No behavioral change — pure docs. Fixed two false claims about "From Your Watchlist" (it is the raw matched watchlist in match order, *not* re-scored by the taste engine; only "Highly Rated by You" uses smart ranking). Also softened "Letterboxd ratings dominate" → "weighted heavily" on For You to match the scoring math. Verified all other rows (Recently Added, Subcategory For You, Discover, Wild Card, Highly Rated by You, Because You Watched) are accurate.

## v1.5.21
- **Made "Hidden Gems" genuinely hidden.** Previously it only required high acclaim + a subcategory you don't watch much — so famous films (Seven Samurai, Black Panther, 12 Angry Men) qualified as long as they sat in an unfamili使用的 subcategory. Now it fetches each movie's **TMDB popularity** (reused from the existing keyword enrichment, no extra API hit) and applies a log-scaled **fame penalty** so blockbusters sink and obscure-acclaimed films rise to the top 15. Added a `Fame Penalty` config knob (default 0.15; 0 restores the old behavior). Penalty is skipped when popularity is unknown (no TMDB key) so it degrades safely. Existing libraries are backfilled with popularity on the next refresh.

## v1.5.20
- **Fixed "Because You Watched" playlist title not matching its content.** The list is seeded on the 5 most-recently-watched movies and ranked by best similarity across all 5, but it was *titled* after only the single most-recent watch — so when a more recent watch contributed little, the playlist got named after a movie it wasn't about (e.g. "Because You Watched Disclosure Day" whose picks were all Godfather-style films). The title now reflects the seed that actually *dominates* the final picks (most contributions; ties break to the more recent seed), so the label matches the content. Ranking of picks is unchanged.

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
