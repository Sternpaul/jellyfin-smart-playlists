# 🎬 Jellyfin AI Movie Recommender

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

> The Netflix algorithm, from first principles — just better.

A Jellyfin plugin that solves "what should I watch" for massive libraries. It **uses AI to properly classify every movie** (TMDB's broad "Action/Thriller" tags are useless), then builds **intelligent, self-updating playlists** that learn from your watch history, rotate content, punish rejected movies, and avoid filter bubbles.

**Bring your own AI provider** — Google AI, OpenRouter, OpenAI, Anthropic. Works on **all Jellyfin clients** (FireTV, Android TV, iOS, web) — no client changes.

---

## How it works (the short version)

1. **AI classifies every movie once** — reads the plot and assigns real subcategories, moods, themes, narrative style, and intensity. This is the foundation; everything else builds on it.
2. **TMDB keywords sharpen it** — curated, objective tags (e.g. `serial killer`, `neo-noir`) are pulled per movie and used as a precision signal in taste-matching and similarity.
3. **It learns from what you watch** — picks, rejections, and ratings shape per-user taste profiles and a small "affinity" nudge per movie.
4. **Imported ratings are the dominant signal** — point a user at a ratings JSON export and each valid rating contributes a non-negative boost up to the configured `RatingWeight`. **No ratings URL → zero ratings weight**; recommendations fall back to taste + learning.
5. **Playlists regenerate on a schedule** (default 12h) and after a verified watch — fresh picks, no staleness.

### The playlists you get (per user, private)
| Playlist | What it does |
|---|---|
| **For You** | Top personalized picks. 75% taste-matched + 25% exploration. **Letterboxd ratings dominate this list when set.** |
| **Because You Watched [X]** | Ranks against up to five recent verified watches; the title names the seed contributing the most selected films. |
| **Hidden Gems** | High-acclaim films from subcategories you *don't* already watch much. |
| **Recently Added** | Unwatched movies, newest first. |
| **[Subcategory] For You** | Deep dive into a subcategory you love (e.g. "Psychological Thrillers For You"). |
| **Discover: Hidden World** | Gateway into your least-explored subcategories, bridged to your taste. |
| **Wild Card** | 100% exploration — least-explored subcategory, high-acclaim only. |
| **From Your Watchlist** | An imported watchlist JSON, filtered to movies in your library. |
| **More Like Your Favorites** | Unwatched, unrated local films ranked by similarity to your 4★+ Letterboxd favorites. Rated films are anchors, never results. |

A movie appears in **at most one** discovery playlist (For You / Hidden Gems / Discover / Wild Card / Subcategory) so it never shows twice; *Because You Watched* is exempt.

### Learning loop (the punishment mechanic)
When you **watch a movie from one of your playlists**, the plugin learns from that one action. Administrator-assigned persistent collections are deliberately excluded from this learning loop:
- **Sibling penalty** — the other movies in that playlist get a rejection penalty and a temporary ban.
- **Similar-movie reward** — the watched movie's nearest neighbours get a small affinity boost.
- **Time decay** — penalties/rewards fade exponentially (default 28-day half-life).
- Only actual Jellyfin playback stops strictly above 50% create recency, taste, or learning signals. Exactly 50%, short playback, unknown/reset positions, and manually toggling Played do not count.

### Anti-bubble protection
**For You** reserves 25% by default for exploration, while a Diversity Cap (default 60%) limits subcategory dominance. Dedicated Discover and Wild Card playlists add separate exploration surfaces.

---

## 📦 Installation

### Method 1 — Self-hosted repository (recommended, auto-updates)
1. **Dashboard → Plugins → Repositories → Add**
2. URL: `https://sternpaul.github.io/jellyfin-smart-playlists/repo/manifest.json`
3. Save, then open **Catalog** (or restart). "AI Recommender" appears under *Available* (Developer **Sternpaul**).
4. Install. Future releases show an **Update** button automatically.
5. **Dashboard → Plugins → AI Recommender** → pick your provider, enter your API key, run **Index & Classify Library**, then **Refresh Playlists**.

### Method 2 — Manual (sideload the DLL)
Download the latest ZIP from [GitHub Releases](https://github.com/Sternpaul/jellyfin-smart-playlists/releases), extract its single `Jellyfin.Plugin.AIRecommender.dll` into a dedicated folder under `/config/data/plugins/`, and restart Jellyfin. Do not copy build dependencies; Jellyfin supplies them.

> Identity is the fixed GUID `3D3D8BE7-67AB-4F65-9F31-3EAE8764BBA3`. `targetAbi` is `10.11.0.0` (the minimum 10.11 ABI — pinning to the exact server build makes Jellyfin silently drop the plugin).

Prerequisites: Jellyfin **10.11.x**; an API key from [Google AI Studio](https://aistudio.google.com/), [OpenRouter](https://openrouter.ai/), [OpenAI](https://platform.openai.com/), or [Anthropic](https://console.anthropic.com/).

> **Docker / NAS users:** The SQLite database lives in the plugin configurations directory. Current releases automatically use DELETE journal mode and checkpoint legacy WAL data at startup, so bind-mounted storage needs no manual SQLite workaround. After upgrading from a much older release, restart Jellyfin and run **Index & Classify Library** followed by **Refresh Playlists**.

---

## Letterboxd ratings (the dominant signal)
Your **star ratings are the strongest recommendation signal**. Per user, in the plugin config → *Per-User Watchlist Settings*, set **Ratings JSON URL** to a URL that serves a JSON file of your ratings. On each refresh the plugin fetches that JSON and matches films to your library by IMDb id, then stores them. A 5-star rating contributes up to `RatingWeight` (default **0.50**) to the For You score — far above the smaller taste/affinity nudges — so films you loved rise to the top.

**Expected JSON format** (a JSON array of objects; `imdb_id` and `rating` are required, the rest optional):
```json
[
  { "title": "Project Hail Mary", "imdb_id": "tt12042730", "rating": 4.0 },
  { "title": "12 Angry Men",      "imdb_id": "tt0050083",  "rating": 5.0 },
  { "title": "Vertigo",           "imdb_id": "tt0052357",  "rating": 3.5 }
]
```
- `imdb_id` — used for **exact** matching to your library. Entries without a valid `imdb_id` (or that don't match a library item) are skipped.
- `rating` — any numeric value greater than `0` and at most `5`. Values outside that range are ignored; half-star increments are not required.
- `title` — only used for logging/debugging; it is **not** used for matching.
- Any other fields in each object are ignored.
- The file must be served with `Content-Type: application/json` (a raw GitHub `/raw/` URL or any static host works).

This is the only supported ratings source — the plugin does **not** scrape Letterboxd. You can generate this file yourself (e.g. from a Letterboxd export, or any tool that emits the shape above).

- **No URL → no ratings weight.** Ratings are cleared for that user; recommendations use taste + learning only.
- **"More Like Your Favorites"** is generated when enabled. Rated 4★+ films are similarity anchors; all rated films are excluded from the recommendation results.
- Fetching is **fail-safe**: a bad/empty URL or an unreachable file is logged and never breaks the rest of the refresh. Re-publish the JSON whenever your ratings change; the next refresh picks it up.

---

## ⚙️ Configuration (summary)
**Dashboard → Plugins → AI Recommender.** All plugin controls and API operations are administrator-only. The administrator configures the AI provider, playlist behavior, and the watchlist/ratings sources and bonus playlists assigned to each Jellyfin user. Non-admin users only consume the generated playlists; they cannot access or change plugin settings.

The full settings tables, technical internals, REST API, cost breakdown, and roadmap are in **[DETAILS](#details)** below.

---

# DETAILS

## ✨ Features in depth

### AI Movie Classification
Sends each movie's plot to your AI and returns subcategories, moods, themes, narrative style, accessibility, and intensity. Playlist generation itself makes no AI calls; only new or explicitly reclassified movies incur later classification requests.

### Smart Playlists
Dynamic, per-user, auto-updating. See the table above. **From Your Watchlist** preserves matched source order before bounded rotation, while **More Like Your Favorites** uses 4★+ imported ratings as weighted similarity anchors and recommends only unwatched, unrated local films.

### What's Happening (transparency)
The config page shows, live per user: top taste weights plus bounded samples (up to 50 each) of currently penalized movies, novelty boosts, last-refresh exclusions and their reasons, taste drift, and recently surfaced history. Observability only — changes no behavior.

### Dynamic Subcategories
AI reads each plot and assigns meaningful subcategories (Heist, Neo-Noir, Folk Horror, …) instead of TMDB's flat genres.

### Similarity Engine
Movies are compared via AI metadata: Subcategory 30% · Mood 20% · Theme 15% · Director/Cast 10% · Narrative style 10% · Era 5% · critical-acclaim proximity 5% · Intensity 5%, plus configurable TMDB keyword overlap; the total is capped at 1.0. This powers *Because You Watched* and exploration slots.

### TMDB Keywords (v1.5.14, precision signal)
Curated, objective tags from TMDB (e.g. `serial killer`, `neo-noir`, `self-fulfilling prophecy`) are fetched per movie (resolving the TMDB id from the IMDb id already stored; cached in `tmdb_keywords_cache.json`) and added as a configurable overlap term in **For You** taste-matching (`KeywordWeight`, default 0.10) and the **Because You Watched** similarity engine. Keywords are more reliable than the LLM's subjective "themes" and need no re-classification — they're pulled at refresh time. Set a TMDB v3 API key in the config to enable; leave it blank to disable.

### Watchlist JSON Integration
Per user, in *Per-User Watchlist Settings*, enable **"From Your Watchlist"** and provide a **Watchlist JSON URL**. IMDb ID is attempted first; any unmatched entry can fall back to normalized title and optional year.

The endpoint must return an array of objects. Recognized fields:
```json
[
  { "imdb_id": "tt12042730", "title": "Project Hail Mary", "release_year": 2026 },
  { "imdb_id": "tt0050083",  "title": "12 Angry Men",      "release_year": 1957 }
]
```
- `imdb_id` — preferred; exact match to your library.
- `title` — used for the IMDb-id fallback and for title-based matching.
- `release_year` — used only to disambiguate title matches.

The **From Your Watchlist** playlist contains unwatched matched items in source order, subject to the configured playlist-size and rotation policies; it is not taste-scored.

### Anti-Bubble Protection
**For You** reserves 25% of its slots for exploration (configurable 10–50%) and applies the configurable 60% subcategory cap. Dedicated Discover and Wild Card playlists use their own ranking rules.

### AI Chat (web client only)
Natural-language recommendations using your enriched metadata.

### How Playlists Stay Fresh
```
User watches a movie
    ├── Movie excluded from generated recommendation/watchlist results
    │   (persistent collections and personal playlists remain untouched)
    ├── Other movies in owner-scoped source playlists get penalized (persistent collections excluded)
    ├── Taste profile updated
    └── Rotating recommendation playlists rebuilt with fresh picks
Every N hours (default 12): apply configured rotation, re-check watches, enforce diversity,
and reconcile assigned persistent collections without rotating them.
```

## Configuration reference

### AI Provider Settings
| Setting | Default | Description |
|---|---|---|
| AI Provider | OpenRouter | Google AI / OpenRouter / OpenAI / Anthropic |
| API Key | *(required)* | Key for the selected provider |
| Classification Model | `nvidia/nemotron-3-super-120b-a12b:free` | Batch classification model; use an ID supported by the selected provider |
| Chat Model | `nvidia/nemotron-3-super-120b-a12b:free` | May differ from the classification model |
| Custom Endpoint | *(empty)* | Optional self-hosted/proxy URL |
| TMDB API Key | *(empty)* | Optional v3 key for keyword overlap and popularity signals |

### Playlist Settings
| Setting | Default | Description |
|---|---|---|
| Playlist Refresh Interval | 12h | Auto-refresh cadence |
| Max Movies Per Playlist | 20 | Cap for generated recommendation playlists (persistent collections have a separate 100-member safety limit) |
| Playlist Rotation % | 30% | Swapped each cycle |
| Diversity Weight | 25% | Exploration slots (10–50%) |
| Cooling Period | 2 cycles | Ban duration after rejection |
| Enabled Playlist Types | All | Toggle types |
| Rating Weight | 0.50 | **Max contribution of a 5★ Letterboxd rating to For You** (0 disables) |
| TMDB Keyword Weight | 0.10 | Keyword-overlap contribution; requires a TMDB key |
| Hidden Gems Fame Penalty | 0.15 | Pushes popular blockbusters down; requires TMDB popularity data |

### Per-User Settings
| Setting | Default | Description |
|---|---|---|
| Watchlist JSON URL | *(empty)* | Public JSON array matched to the local library |
| Ratings JSON URL | *(empty)* | Public JSON ratings export matched by IMDb ID; no scraping |
| Enable Watchlist Playlist | Off | "From Your Watchlist" |
| Enable Ratings Playlist | Off | "More Like Your Favorites" |
| Exclude user from recommendations | Off | Removes only exact registered rotating recommendations; assigned persistent collections remain |

### Automatic contextual playlist artwork (v1.7.10+)
Dynamic recommendation playlists automatically receive personalized Primary and Backdrop images with no administrator settings required. The plugin selects the highest-ranked representative movie from that user's playlist ranking (`Because You Watched` uses its verified watched anchor), prefers a local Jellyfin Backdrop, falls back to Primary and then later ranked movies, and composites the playlist title over a darkened cover crop with the blue-purple theme accent. If no source image can be read safely, the embedded v1.7.9 artwork remains the deterministic fallback.

Generated-image and source-image SHA-256 fingerprints are stored separately for Primary and Backdrop. The plugin refreshes an image only while its current bytes still match the last plugin-generated bytes. Uploading custom artwork changes those bytes and opts that image type out of automatic replacement for as long as those custom bytes remain; deleting the custom image allows the plugin to generate artwork again. A custom Primary does not unlock or disable the Backdrop, and vice versa. Persistent collections remain outside this automatic recommendation-artwork path.

Starting with v1.7.11, the plugin replaces Jellyfin's automatic four-poster Primary collage only when it has just created that exact recommendation playlist in the current refresh. Unknown artwork on an already-existing playlist remains protected as custom artwork.

v1.7.12 creates each new managed playlist as an empty video playlist, applies contextual artwork, and only then adds members. Jellyfin's queued member refresh therefore observes existing artwork and does not race in a four-poster collage.

v1.7.14 also repairs registered rotating playlists created before that ordering fix. On refresh, an unowned Primary is migrated only when it is Jellyfin's exact playlist-ID-scoped 600×600 PNG in the dynamic-image metadata cache. Playlist-directory images, other dimensions and paths, prior plugin ownership mismatches, persistent collections, and administrator-customized Primary/Backdrop images remain untouched.

### Persistent Collections (v1.7.8+)
Administrators manage persistent definitions in **Dashboard → Plugins → AI Recommender → Persistent Collections**. Choose **Explicit movie list** or **Curated/composite universe**, give the definition a unique name, and enter TMDB movie IDs and/or IMDb IDs. These identifiers are resolved only against movies already indexed in the local Jellyfin library; the plugin does not download media or expand a TMDB collection automatically.

Definitions persist independently from users. Administrators explicitly assign each definition to selected Jellyfin users, creating one private native playlist per assignment.

- Membership may include watched and unwatched local movies and is ordered deterministically by release year, then title.
- Renaming or editing a definition updates each exact registered playlist in place and preserves its Jellyfin playlist ID.
- If an edit resolves to no local movies, the plugin preserves an existing valid playlist instead of replacing it with an empty one.
- Collections do not rotate, enter recommendation novelty history, receive generic recommendation artwork, or affect watch-event sibling learning.
- Disabling recommendations for a user does not remove administrator-assigned collections.
- Unassignment or deletion removes only exact plugin-registered persistent playlists. Personal, legacy, and otherwise unregistered playlists remain untouched even if their names or members overlap.
- Each definition accepts at most 100 combined TMDB/IMDb identifiers; oversized definitions are rejected rather than silently truncated.
- TMDB collection expansion and read-only Radarr catalog/completeness data remain separate follow-up work.

### Dynamic Rating / Learning (v1.3.0+)
| Setting | Default | Description |
|---|---|---|
| Taste Memory Half-Life | 120 days | Fade rate for older verified watches in the taste profile |
| Critical Acclaim Nudge | 0% | Optional small critical-rating contribution |
| Affinity Decay Half-Life | 28 days | Fade rate for learned rewards/penalties |
| Rejection Penalty | -0.30 | Affinity drop for siblings of a watched movie |
| Similar-Movie Reward | 0.10 | Affinity rise for similar titles |
| Affinity Rank Weight | 0.15 | Max contribution of learned affinity (small nudge) |
| New-Movie Boost Window | 30 days | Recency nudge duration |
| New-Movie Boost Weight | 0.10 | Recency nudge size |
| Diversity Cap | 60% | Max % any subcategory in a playlist |
| Director Affinity Bonus | 0.05 | Nudge for recurring directors |
| Soft Penalty Strength | 0.50 | 0 = hard ban; 1 = no penalty |
| New-Movie Min Taste-Fit | 0.30 | For You boosts new films only if they fit |
| Novelty Bonus | 0.05 | Nudge for un-recently-surfaced films |
| Novelty Half-Life | 30 days | Novelty fade |
| Verified Playback Threshold | Strictly >50% | Fixed policy; actual playback stop only, not manual Played |
| Decay Reference | 3/week | Watch-rate scaling for half-lives (0.3x–3x) |

Model availability and IDs change over time. Enter model IDs accepted by the selected provider; the defaults above are OpenRouter model IDs and are not portable to the Google AI, OpenAI, or Anthropic APIs.

## REST API
All endpoints require an authenticated Jellyfin administrator (`RequiresElevation`). Non-admin accounts cannot call plugin APIs or modify plugin-managed per-user settings.
| Method | Endpoint | Description |
|---|---|---|
| POST | `/AIRecommender/Chat` | Natural-language recommendations |
| POST | `/AIRecommender/ClassifyLibrary` | Start the index/classification task |
| POST | `/AIRecommender/RefreshPlaylists` | Start the all-user playlist refresh task |
| POST | `/AIRecommender/Playlists/Refresh?userId=` | Refresh one user's recommendations and assigned collections |
| GET | `/AIRecommender/UserWatchlistConfig?userId=` | Read one user's watchlist/ratings configuration |
| POST | `/AIRecommender/UserWatchlistConfig` | Save one user's watchlist/ratings configuration |
| POST | `/AIRecommender/UserConfig/SyncLetterboxd?userId=` | Sync one user's watchlist source |
| POST | `/AIRecommender/UserConfig/SyncRatings?userId=` | Sync one user's ratings JSON source |
| GET | `/AIRecommender/Debug/{userId}` | Live "what's happening" snapshot |
| GET/POST | `/AIRecommender/Collections` | List or create/update persistent collection definitions |
| POST | `/AIRecommender/Collections/Assignment` | Assign or unassign a definition for one user |
| DELETE | `/AIRecommender/Collections/{definitionId}` | Delete a definition and reconcile its exact managed playlists |
| POST | `/AIRecommender/Collections/Refresh?userId=` | Reconcile persistent collections for one user |

## How it works (technical)
**First run:** index the local movie library → AI-classify unclassified movies → read Jellyfin Played flags for broad recommendation exclusion → generate playlists. Taste, recency, and learning begin only with playback stops the plugin verifies strictly above 50%. **Ongoing:** new movies are indexed/classified incrementally, verified playback updates persisted state, and scheduled or watch-triggered refreshes rebuild recommendations without calling the classifier. A manual Jellyfin **Mark Played** remains an exclusion signal only.

**Client compatibility:** Playlists work on every client. AI Chat is web-only.

## Cost
Initial classification cost depends on provider, model, and library size; later classification calls are needed only for new or explicitly reclassified movies. Playlist refresh and local scoring make no AI calls, although refresh may fetch configured ratings/watchlist JSON and TMDB metadata. AI Chat calls the configured provider and therefore has a per-use provider cost.

## Build from source
Requires .NET 9.0 SDK:
```bash
git clone https://github.com/Sternpaul/jellyfin-smart-playlists.git
cd jellyfin-smart-playlists
dotnet test Jellyfin.Plugin.AIRecommender.sln --configuration Release
dotnet build Jellyfin.Plugin.AIRecommender.csproj --configuration Release
```
Root `dotnet test` discovers and runs the xUnit suite through `Jellyfin.Plugin.AIRecommender.sln`. The DLL lands in `bin/Release/net9.0/`. The SQLite DB lives in the Jellyfin config dir (`/config/data/plugins/configurations/airecommender.db`) — persists across updates.

Releases are tag-only and immutable. The release workflow requires the tag and project version to match, runs the full test suite, requires a version-specific changelog, packages only the plugin DLL, publishes the manifest to `gh-pages`, and verifies the public ABI, source URL, changelog, MD5 checksum, and ZIP contents before succeeding.

## Roadmap

### In progress — one feature per release
- [x] AI classification, similarity engine, taste profiling
- [x] Playlist engine + punishment mechanic + scheduled refresh
- [x] External watchlist and per-user ratings imports
- [x] Self-hosted plugin repository with auto-update
- [x] Transparency / "what's happening" panel
- [x] **v1.6.7:** remove cross-user scoring state and serialize refresh-affecting operations
- [x] **v1.6.8:** make the configured refresh interval control the scheduled task
- [x] **v1.6.9:** implement deterministic playlist rotation
- [x] **v1.7.0:** align playlist size limits across generators
- [x] **v1.7.1:** align completion settings with strict verified-playback semantics
- [x] **v1.7.2:** harden test discovery, releases, publication checks, and documentation
- [x] **v1.7.3:** add durable managed-playlist provenance registry
- [x] **v1.7.4:** update playlists in place while preserving IDs and metadata
- [x] **v1.7.5:** native Jellyfin playlist descriptions and explanations
- [x] **v1.7.6:** deterministic primary/backdrop artwork with manual-art preservation
- [x] **v1.7.7:** replace contradictory rated-title playlist with unseen recommendations similar to 4★+ Letterboxd favorites
- [x] **v1.7.8:** persistent per-user composite and curated collections
- [x] **v1.7.9:** static Jellyfin-themed playlist artwork with one-time legacy-art migration and custom-art preservation
- [x] **v1.7.10:** personalized representative-movie playlist artwork with locally composited themed titles and custom-art preservation
- [x] **v1.7.11:** replace Jellyfin's initial four-poster collage on newly created recommendation playlists without weakening existing custom-art protection
- [x] **v1.7.12:** apply contextual artwork before adding members can queue Jellyfin's four-poster collage refresh
- [x] **v1.7.13:** exclude every locally matched ratings-JSON title from all recommendation playlists
- [x] **v1.7.14:** safely migrate existing registered playlists from Jellyfin's legacy four-poster Primary collage
- [ ] **v1.7.15:** optional read-only Radarr collection catalog and completeness data
- [ ] **v1.7.16:** administrator-approved collection suggestions per user
- [ ] **v1.7.17:** index and classify each TV series once and aggregate verified episode playback
- [ ] **v1.7.18:** blend one bounded series signal into movie taste without episode multiplication

### Planned features after the series-aware taste work
- **New For You:** recently added movies filtered by taste instead of a generic date-only list.
- **Continue the Vibe:** a short-lived playlist driven by the last few verified watches.
- **Rewatch Rediscovery:** older verified favorites that have not been watched through Jellyfin recently.
- **Seasonal collections:** configurable Halloween, Christmas, awards-season, and similar date-window lists.
- **Collection completion dashboard:** available, missing, and excluded franchise titles; any Radarr acquisition action would require a separate explicit opt-in design.
- **Pin / never recommend controls:** preserve a playlist or permanently suppress a movie, franchise, or collection.
- **Freshness modes:** stable, balanced, and fresh rotation policies.
- **Shared household playlists:** intersection-first recommendations for selected users without exposing either person's private history.
- **Taste timeline:** show how verified movie and series signals changed subcategories and moods over time.
- **Runtime-aware evening mix:** choose a movie or compact episode set for an available time budget.
- **New Season Radar:** surface newly added seasons for series the user has verified watching.
- **Continue or resume a series:** next episodes and verified-started shows that have gone inactive.
- **Completed-series discovery:** favor finished stories for users who prefer completed shows.
- **Binge-balance mode:** offset a long episode binge with shorter or deliberately different recommendations.
- **Cross-media explanations:** explain movie recommendations using both verified films and series, without creating mixed TV playlists.
- **Optional user self-service settings:** strict same-user authorization; plugin management remains administrator-only until deliberately changed.

Separate TV recommendation playlists are intentionally not in the current implementation plan.

## License
MIT — see [LICENSE](LICENSE).
