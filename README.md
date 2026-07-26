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
3. **Letterboxd ratings are the dominant signal** — if you set your Letterboxd username, your star ratings pull strongly toward (or away from) films. **No username → zero ratings weight**, your recommendations fall back to taste + learning only.
4. **Playlists regenerate on a schedule** (default 12h) and after you finish a movie — fresh picks, no staleness.

### The playlists you get (per user, private)
| Playlist | What it does |
|---|---|
| **For You** | Top personalized picks. 75% taste-matched + 25% exploration. **Letterboxd ratings dominate this list when set.** |
| **Because You Watched [X]** | Movies similar to what you just watched (regenerates after each watch). |
| **Hidden Gems** | High-acclaim films from subcategories you *don't* already watch much. |
| **Recently Added** | Unwatched movies, newest first. |
| **[Subcategory] For You** | Deep dive into a subcategory you love (e.g. "Psychological Thrillers For You"). |
| **Discover: Hidden World** | Gateway into your least-explored subcategories, bridged to your taste. |
| **Wild Card** | 100% exploration — least-explored subcategory, high-acclaim only. |
| **From Your Watchlist** | Your Letterboxd watchlist, filtered to movies in your library. |
| **Highly Rated by You** | Your top-rated Letterboxd films that are in the library (unwatched preferred). |

A movie appears in **at most one** discovery playlist (For You / Hidden Gems / Discover / Wild Card / Subcategory) so it never shows twice; *Because You Watched* is exempt.

### Learning loop (the punishment mechanic)
When you **watch a movie from a playlist**, the plugin learns from that one action:
- **Sibling penalty** — the other movies in that playlist get a rejection penalty and a temporary ban.
- **Similar-movie reward** — the watched movie's nearest neighbours get a small affinity boost.
- **Time decay** — penalties/rewards fade exponentially (default 28-day half-life).
- Only actual Jellyfin playback stops strictly above 50% create recency, taste, or learning signals. Exactly 50%, short playback, unknown/reset positions, and manually toggling Played do not count.

### Anti-bubble protection
25% of every playlist is reserved for exploration; a Diversity Cap (default 60%) stops any one subcategory from dominating.

---

## 📦 Installation

### Method 1 — Self-hosted repository (recommended, auto-updates)
1. **Dashboard → Plugins → Repositories → Add**
2. URL: `https://sternpaul.github.io/jellyfin-smart-playlists/repo/manifest.json`
3. Save, then open **Catalog** (or restart). "AI Recommender" appears under *Available* (Developer **Sternpaul**).
4. Install. Future releases show an **Update** button automatically.
5. **Dashboard → Plugins → AI Recommender** → pick your provider, enter your API key, click **Classify Library** (runs once). Playlists generate automatically.

### Method 2 — Manual (sideload the DLL)
Download the latest `.dll` from [Releases](../../releases), drop it in your plugin folder (`/config/data/plugins/AIRecommender/`), restart Jellyfin, then classify.

> Identity is the fixed GUID `3D3D8BE7-67AB-4F65-9F31-3EAE8764BBA3`. `targetAbi` is `10.11.0.0` (the minimum 10.11 ABI — pinning to the exact server build makes Jellyfin silently drop the plugin).

Prerequisites: Jellyfin **10.11.x**; an API key from [Google AI Studio](https://aistudio.google.com/), [OpenRouter](https://openrouter.ai/), [OpenAI](https://platform.openai.com/), or [Anthropic](https://console.anthropic.com/).

> **Docker / NAS users (v1.5.32+):** The plugin's SQLite database is stored in your plugin configurations directory. On Docker bind-mounts, the default SQLite WAL (Write-Ahead Logging) mode can cause data to be invisible across connections. Since v1.5.32, the plugin automatically forces DELETE journal mode and checkpoints any existing WAL data at startup — no manual action needed. If you're upgrading from an older version that had empty playlists, simply install v1.5.32, restart Jellyfin, and run **Index & Classify Library** followed by **Refresh Playlists**. Check your Jellyfin log for `SQLite journal mode set to: delete` to confirm the fix is active.

---

## Letterboxd ratings (the dominant signal)
Your **star ratings are the strongest recommendation signal**. Per user, in the plugin config → *Per-User Watchlist Settings*, set **Ratings JSON URL** to a URL that serves a JSON file of your ratings. On each refresh the plugin fetches that JSON and matches films to your library by IMDB id, then stores them. A 5-star rating contributes up to `RatingWeight` (default **0.50**) to the For You score — far above the smaller taste/affinity nudges — so films you loved rise to the top.

**Expected JSON format** (a JSON array of objects; `imdb_id` and `rating` are required, the rest optional):
```json
[
  { "title": "Project Hail Mary", "imdb_id": "tt12042730", "rating": 4.0 },
  { "title": "12 Angry Men",      "imdb_id": "tt0050083",  "rating": 5.0 },
  { "title": "Vertigo",           "imdb_id": "tt0052357",  "rating": 3.5 }
]
```
- `imdb_id` — used for **exact** matching to your library. Entries without a valid `imdb_id` (or that don't match a library item) are skipped.
- `rating` — a number from `0.5` to `5.0` (half-star granularity). Values outside that range are ignored.
- `title` — only used for logging/debugging; it is **not** used for matching.
- Any other fields in each object are ignored.
- The file must be served with `Content-Type: application/json` (a raw GitHub `/raw/` URL or any static host works).

This is the only supported ratings source — the plugin does **not** scrape Letterboxd. You can generate this file yourself (e.g. from a Letterboxd export, or any tool that emits the shape above).

- **No URL → no ratings weight.** Ratings are cleared for that user; recommendations use taste + learning only.
- **"Highly Rated by You" playlist** is generated when enabled.
- Fetching is **fail-safe**: a bad/empty URL or an unreachable file is logged and never breaks the rest of the refresh. Re-publish the JSON whenever your ratings change; the next refresh picks it up.

---

## ⚙️ Configuration (summary)
**Dashboard → Plugins → AI Recommender.** All plugin controls and API operations are administrator-only. The administrator configures the AI provider, playlist behavior, and the watchlist/ratings sources and bonus playlists assigned to each Jellyfin user. Non-admin users only consume the generated playlists; they cannot access or change plugin settings.

The full settings tables, technical internals, REST API, cost breakdown, and roadmap are in **[DETAILS](#details)** below.

---

# DETAILS

## ✨ Features in depth

### AI Movie Classification (one-time)
Sends each movie's plot to your AI and returns subcategories, moods, themes, narrative style, accessibility, intensity. After classification, playlists run at **zero ongoing API cost**; new movies are classified incrementally.

### Smart Playlists
Dynamic, per-user, auto-updating. See the table above. "From Your Watchlist" and "Highly Rated by You" apply the same smart scoring but only from your Letterboxd data.

### What's Happening (transparency)
The config page shows, live per user: top taste weights, currently-penalized movies (with cooling time), active novelty boosts, every movie excluded on the last refresh and *why*, taste drift since your oldest snapshot, and recently-surfaced history. Observability only — changes no behavior.

### Dynamic Subcategories
AI reads each plot and assigns meaningful subcategories (Heist, Neo-Noir, Folk Horror, …) instead of TMDB's flat genres.

### Similarity Engine
Movies compared via AI metadata: Subcategory 30% · Mood 20% · Theme 15% · Director/Cast 10% · Narrative style 10% · Era 5% · Rating 5% · Intensity 5%. Powers *Because You Watched* and exploration slots.

### TMDB Keywords (v1.5.14, precision signal)
Curated, objective tags from TMDB (e.g. `serial killer`, `neo-noir`, `self-fulfilling prophecy`) are fetched per movie (resolving the TMDB id from the IMDB id already stored; cached in `tmdb_keywords_cache.json`) and added as a configurable overlap term in **For You** taste-matching (`KeywordWeight`, default 0.10) and the **Because You Watched** similarity engine. Keywords are more reliable than the LLM's subjective "themes" and need no re-classification — they're pulled at refresh time. Set a TMDB v3 API key in the config to enable; leave it blank to disable.

### Letterboxd Watchlist Integration
Per user, in *Per-User Watchlist Settings*, enable **"From Your Watchlist"** and supply your list either as a **JSON URL** or a **CSV** paste (upload). Both are matched to your library by IMDB id (exact), falling back to title + year when no IMDB id is present.

**JSON** — an array of objects. Recognised fields:
```json
[
  { "imdb_id": "tt12042730", "title": "Project Hail Mary", "release_year": 2026 },
  { "imdb_id": "tt0050083",  "title": "12 Angry Men",      "release_year": 1957 }
]
```
- `imdb_id` — preferred; exact match to your library.
- `title` — used for the IMDB-id fallback and for title-based matching.
- `release_year` — used only to disambiguate title matches.

**CSV** — a header row followed by one film per line. The parser looks for columns named `name` (required) and `year` (optional); other columns are ignored. Example:
```csv
name,year,Letterboxd URI
Project Hail Mary,2026,/film/project-hail-mary/
12 Angry Men,1957,/film/12-angry-men/
```
Matching is by title (+ year when available); a CSV without IMDB ids is less precise than the JSON path, so JSON is recommended where you have IMDB ids.

The "From Your Watchlist" playlist is built from the matched items using the same smart scoring as the other playlists.

### Anti-Bubble Protection
25% exploration reserve (configurable 10–50%); Diversity Cap (default 60%); dedicated Wild Card; rotating discovery.

### AI Chat (web client only)
Natural-language recommendations using your enriched metadata.

### How Playlists Stay Fresh
```
User watches a movie
    ├── Movie removed from ALL playlists
    ├── Other movies in the SOURCE playlist get BANNED (cooling period)
    ├── Taste profile updated
    ├── Source playlist rebuilt with fresh picks
    └── Other playlists adjusted
Every N hours (default 12): rotate 30%, re-check watches, enforce diversity.
```

## Configuration reference

### AI Provider Settings
| Setting | Default | Description |
|---|---|---|
| AI Provider | Google AI | Google AI / OpenRouter / OpenAI / Anthropic |
| API Key | *(required)* | Key for the selected provider |
| Classification Model | `gemma-4-31b-it` | Batch classification model |
| Chat Model | *(same)* | Can differ from classification |
| Custom Endpoint | *(empty)* | Optional self-hosted/proxy URL |

### Playlist Settings
| Setting | Default | Description |
|---|---|---|
| Playlist Refresh Interval | 12h | Auto-refresh cadence |
| Max Movies Per Playlist | 20 | Cap per playlist |
| Playlist Rotation % | 30% | Swapped each cycle |
| Diversity Weight | 25% | Exploration slots (10–50%) |
| Cooling Period | 2 cycles | Ban duration after rejection |
| Enabled Playlist Types | All | Toggle types |
| Rating Weight | 0.50 | **Max contribution of a 5★ Letterboxd rating to For You** (0 disables) |

### Per-User Settings
| Setting | Default | Description |
|---|---|---|
| Letterboxd Username | *(empty)* | Public handle for ratings scrape |
| Watchlist JSON URL | *(empty)* | Radarr-compatible watchlist JSON |
| Watchlist CSV | *(empty)* | Letterboxd CSV export |
| Enable Watchlist Playlist | Off | "From Your Watchlist" |
| Enable Ratings Playlist | Off | "Highly Rated by You" |

### Dynamic Rating / Learning (v1.3.0+)
| Setting | Default | Description |
|---|---|---|
| Affinity Decay Half-Life | 28 days | Fade rate for learned ratings/penalties |
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

### Provider-specific model IDs
| Provider | Classification | Chat |
|---|---|---|
| Google AI | `gemma-4-31b-it` | `gemma-4-31b-it` |
| OpenRouter | `google/gemma-4-31b-it` | `google/gemma-4-31b-it` |
| OpenAI | `gpt-4o-mini` | `gpt-4o` |
| Anthropic | `claude-sonnet-4-5` | `claude-sonnet-4-5` |

## REST API
All endpoints require an authenticated Jellyfin administrator (`RequiresElevation`). Non-admin accounts cannot call plugin APIs or modify plugin-managed per-user settings.
| Method | Endpoint | Description |
|---|---|---|
| POST | `/api/airecommender/chat` | NL recommendations |
| GET | `/api/airecommender/playlists/{userId}` | User's dynamic playlists |
| POST | `/api/airecommender/refresh/{userId}` | Force refresh |
| GET | `/api/airecommender/taste-profile/{userId}` | Taste profile |
| GET | `/api/airecommender/debug/{userId}` | Live "what's happening" snapshot |
| POST | `/api/airecommender/classify` | Trigger classification |
| POST | `/api/airecommender/UserConfig/SyncRatings?userId=` | Re-scrape a user's ratings |

## How it works (technical)
**First run:** index library → AI-classify → compute similarity → export watch history → taste-profile → generate playlists. **Ongoing:** incremental classification of new movies, verified playback hooks, scheduled refresh (zero API cost), debounced immediate refresh after a watch. A manual Jellyfin **Mark Played** remains a strict exclusion signal only; recent-watch ordering, taste recency, and learning use actual Jellyfin playback sessions stopped above 50% completion.

**Client compatibility:** Playlists work on every client. AI Chat is web-only.

## Cost
Classification is a one-time ~$0.03–0.40 (by provider); new movies <$0.01; refresh and chat are pennies. After classification, smart playlists are **free**.

## Build from source
Requires .NET 9.0 SDK:
```bash
git clone https://github.com/Sternpaul/jellyfin-smart-playlists.git
cd jellyfin-smart-playlists
dotnet build --configuration Release
```
DLL lands in `bin/Release/net9.0/`. The SQLite DB lives in the Jellyfin config dir (`/config/data/plugins/configurations/airecommender.db`) — persists across updates.

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
- [ ] **v1.7.2:** harden test discovery, releases, publication checks, and documentation
- [ ] **v1.7.3:** durable managed-playlist provenance registry
- [ ] **v1.7.4:** update playlists in place while preserving IDs and metadata
- [ ] **v1.7.5:** native Jellyfin playlist descriptions and explanations
- [ ] **v1.7.6:** deterministic primary/backdrop artwork with manual-art preservation
- [ ] **v1.7.7:** persistent per-user composite and curated collections
- [ ] **v1.7.8:** optional read-only Radarr collection catalog and completeness data
- [ ] **v1.7.9:** administrator-approved collection suggestions per user
- [ ] **v1.7.10:** index and classify each TV series once (never every episode)
- [ ] **v1.7.11:** store verified episode playback strictly above 50% and aggregate it by series
- [ ] **v1.7.12:** blend one bounded series signal into movie taste without episode multiplication

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
