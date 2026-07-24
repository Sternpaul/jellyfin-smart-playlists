# Changelog

All notable changes to the Jellyfin AI Recommender plugin.

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
