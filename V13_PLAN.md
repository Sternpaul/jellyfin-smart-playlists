# v1.3 Implementation Plan — Dynamic Affinity, Real Punishment, Smarter Playlists

Status: DESIGN LOCKED (pending user sign-off before coding).

## Goal
Replace the mocked/random parts of the recommender with a real, configurable,
*dynamic* rating ("affinity") per (user, movie) that learns from watch behavior,
with gentle time-decay, and make the 4 random/mocked playlists actually smart.
Everything is configurable; every new signal is a *small nudge* on top of the
existing fixed-weight similarity/taste scoring (never dominant).

## Current gaps being fixed (evidence)
- Punishment mechanic is MOCKED: `HandlePunishmentAndRebuildAsync` just calls
  `RefreshUserPlaylistsAsync` (PlaylistEngine.cs:59-72). `PunishmentRecord.cs`
  model exists but is never used (dead).
- Discover (line 257), Wild Card (line 264), Hidden Gems (line 218) use
  `OrderBy(Guid.NewGuid())` = pure random, despite README claims.
- "Because You Watched" seeded on index time (fixed in v1.2.8 — keep).
- No per-movie rating exists at all. Nothing learns from "picked vs ignored".
- `CoolingPeriodCycles` config is read but NEVER used in code (dead).
- New movies compete equally; no recency boost outside "Recently Added".

## Design principles (from user)
1. Affinity is written ONLY on a watch event (from another playlist).
   Refresh READS affinity; it does not write interaction signals.
2. Time-decay is applied (lazy, at read time) so penalties/rewards fade even
   without a new watch — but the *decay itself* is passive housekeeping, not an
   interaction write.
3. All new knobs are CONFIGURABLE and default to SMALL nudges.
4. Affinity modulates ranking; it never overrides strong taste/similarity.

## Part 1 — Data model (additive; Movies table untouched)
New SQLite table (composite PK):
```
MovieAffinity (
  UserId    TEXT NOT NULL,
  ItemId    TEXT NOT NULL,
  Affinity  REAL NOT NULL DEFAULT 0,      -- learned score, can be negative
  PenaltyUntil TEXT NULL,                 -- ISO datetime; if > now, movie banned/excluded
  LastUpdated TEXT NOT NULL,
  PRIMARY KEY (UserId, ItemId)
)
```
- Decision: consolidate penalty into `MovieAffinity.PenaltyUntil` (drop use of
  the dead `PunishmentRecord` model to avoid two sources of truth). Remove
  `PunishmentRecord.cs` or leave unused — recommend removing its usage.
- `AiDbContext`: add `public DbSet<MovieAffinity> Affinities { get; set; }` and
  configure composite key in `OnModelCreating`.
- OFFLINE DB: add `CREATE TABLE MovieAffinity` to `validate_build.py` so the
  delivered `airecommender.db` already has it (EnsureCreated won't alter an
  existing DB, same lesson as the UserWatchlists bug).

## Part 2 — Config additions (PluginConfiguration.cs + configPage.html)
All with sane SMALL defaults:
- `AffinityDecayHalfLifeDays` (int, default 28): half-life for affinity/penalty
  decay. Used lazily at read: `effective = Affinity * exp(-ageDays / halfLife)`.
- `PunishmentPenalty` (double, default -0.30): affinity drop applied to siblings
  of a watched movie in its source playlist(s).
- `RewardBoost` (double, default +0.10): affinity rise for movies similar to a
  watched movie.
- `AffinityRankWeight` (double, default 0.15): max contribution of affinity to a
  0..1 ranking score (caps the nudge so it can't dominate).
- `NewMovieBoostDays` (int, default 30): window where a freshly-added movie gets
  the recency nudge.
- `NewMovieBoostWeight` (double, default 0.10): size of that nudge.
- `CoolingPeriodCycles` ALREADY EXISTS (default 2) — now actually wired:
  `PenaltyUntil = now + CoolingPeriodCycles * PlaylistRefreshHours`.
Config page: add a "Dynamic Rating / Learning" section with these inputs +
load/save bindings.

## Part 3 — Watch-event engine (replaces mock punishment)
`OnMovieWatched` (already fires on real watch) -> `HandlePunishmentAndRebuildAsync`:
1. Load affinity rows for user (or start empty).
2. Find which playlists the watched movie currently lives in (query user's
   Jellyfin playlists owned by user, check members). For each SOURCE playlist:
   - For every OTHER movie in that playlist: apply penalty ->
     `Affinity -= PunishmentPenalty` (clamp [-1,1]), set
     `PenaltyUntil = now + CoolingPeriodCycles * RefreshHours`.
3. Reward: take top-N (e.g. 25) movies by `SimilarityEngine.CalculateSimilarity`
   to the watched movie; for each, `Affinity += RewardBoost` (clamp), and if
   `PenaltyUntil` set, pull it forward (reduce penalty) — implements README's
   "watch a related movie -> penalty reduced".
4. Persist all changed `MovieAffinity` rows (upsert).
5. Then call `RefreshUserPlaylistsAsync` (rebuild reads new affinity).
This is the ONLY place affinity is written from interaction. Honors user rule.

## Part 4 — Refresh ranking (reads affinity + lazy decay, no interaction write)
Add a helper `GetEffectiveAffinity(userId, movie)`:
```
ageDays = (now - LastUpdated).TotalDays
decayed = Affinity * exp(-ageDays / AffinityDecayHalfLifeDays)
return decayed
```
Per-playlist scoring now adds (capped by `AffinityRankWeight`):
- affinity bonus = clamp(effectiveAffinity, -1, 1) * AffinityRankWeight
- new-movie boost = (DateAdded within NewMovieBoostDays) ? NewMovieBoostWeight : 0
- penalty: if PenaltyUntil > now -> exclude movie from recommendation pools
  (or score = -inf). After PenaltyUntil passes, movie re-eligible (penalty expired).
Applied in: For You (taste score + affinity), Discover, Wild Card, Hidden Gems,
and as a tiebreaker elsewhere. Similarity-based playlists (BYW) keep similarity
as primary but get the affinity/penalty filter too.

## Part 5 — Fix the 3 random playlists (no more Guid.NewGuid())
- Hidden Gems: `CriticalAcclaimScore >= 7` AND subcategory NOT in user's top-K
  profile subcats (low familiarity) -> "hidden" = acclaimed but not your usual.
  Rank by acclaim desc, affinity tiebreak.
- Discover: pick the user's LOWEST-weight subcats (unfamiliar); surface movies
  from those, similarity-bridged to the user's taste (so it's adjacent, not
  random). Take 8.
- Wild Card: pick the single least-explored subcat (min profile weight), high
  acclaim (>=7), similarity-gated. Take 10.
All three still respect penalty exclusion + new-movie boost.

## Part 6 — New movies surface in taste playlists
Recency nudge (Part 4) ensures a movie added in the last `NewMovieBoostDays`
gets +`NewMovieBoostWeight` in For You / Discover / Wild Card ranking, so fresh
library additions appear there too — not only in "Recently Added". Configurable,
small.

## Part 7 — Build / deploy
- Plugin: add `MovieAffinity` DbSet + composite key; EnsureCreated creates the
  table for fresh DBs.
- OFFLINE `airecommender.db`: add `MovieAffinity` table via validate_build.py;
  rebuild and re-deliver (no Movies schema change, additive only).
- Bump to v1.3.0 (affinity + punishment + config) and v1.3.1 (3 playlist fixes)
  — or one v1.3.0 if done together. Tag per memory rule (main + tag, no force).
- No DB migration needed for existing users because we add the table offline and
  the plugin's EnsureCreated also creates it; existing Movies rows untouched.

## Out of scope for v1.3 (already real / not requested)
- For You, Because You Watched, Recently Added, [Subcategory] For You, From Your
  Watchlist: keep as-is (sound). BYW already uses 5 recent real watches (v1.2.8).
- AI chat: unchanged.

## Open confirmations
- OK to drop `PunishmentRecord.cs` usage in favor of MovieAffinity.PenaltyUntil?
- v1.3.0 + v1.3.1 split, or single v1.3.0?
