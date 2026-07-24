# 🎬 Jellyfin AI Movie Recommender

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
- Only watches that reach `Min Watch % to Learn` (default 50%) count; a glance is ignored.

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

---

## Letterboxd ratings (the big one)
Your **star ratings are the strongest recommendation signal**. Per user, in the plugin config → *Per-User Watchlist Settings*, enter your **public Letterboxd username**. On each refresh the plugin scrapes `letterboxd.com/<you>/films/ratings/`, matches films to your library, and stores them. A 5-star rating contributes up to `RatingWeight` (default **0.50**) to the For You score — far above the smaller taste/affinity nudges — so films you loved rise to the top.

- **No username → no ratings weight.** Ratings are cleared for that user; recommendations use taste + learning only.
- **"Highly Rated by You" playlist** is generated when enabled.
- Scraping is **fail-safe**: a bad/empty username or a Letterboxd markup change is logged and never breaks the rest of the refresh.
- Honest caveat: this scrapes a public page (no open ratings API), so it's ToS-gray and could break if Letterboxd changes its markup. The watchlist CSV/JSON import path remains the robust alternative.

---

## ⚙️ Configuration (summary)
**Dashboard → Plugins → AI Recommender.** AI provider + key, refresh interval, playlist sizes, diversity, learning rates — all configurable. **Per-user**: Letterboxd username, watchlist URL/CSV, and which bonus playlists to enable.

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
Import via JSON URL (Radarr-compatible: `imdb_id`, `title`, `release_year`) or CSV export. Matched by IMDB ID, falling back to title + year. Per-user.

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
| Min Watch % to Learn | 50% | Watch must reach this % to count as a signal |
| Decay Reference | 3/week | Watch-rate scaling for half-lives (0.3x–3x) |

### Provider-specific model IDs
| Provider | Classification | Chat |
|---|---|---|
| Google AI | `gemma-4-31b-it` | `gemma-4-31b-it` |
| OpenRouter | `google/gemma-4-31b-it` | `google/gemma-4-31b-it` |
| OpenAI | `gpt-4o-mini` | `gpt-4o` |
| Anthropic | `claude-sonnet-4-5` | `claude-sonnet-4-5` |

## REST API
All endpoints require Jellyfin auth (`Authorization` header).
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
**First run:** index library → AI-classify → compute similarity → export watch history → taste-profile → generate playlists. **Ongoing:** incremental classification of new movies, real-time watch hooks, scheduled refresh (zero API cost), debounced immediate refresh after a watch.

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
- [x] AI classification, similarity engine, taste profiling
- [x] Playlist engine + punishment mechanic + scheduled refresh
- [x] Letterboxd watchlist import (CSV/JSON) + watchlist playlist
- [x] Per-user Letterboxd **ratings** as dominant signal + "Highly Rated by You"
- [x] Self-hosted plugin repository with auto-update
- [x] Transparency / "what's happening" panel
- [ ] Richer ratings coverage (CSV export import as robust alternative to scraping)

## License
MIT — see [LICENSE](LICENSE).
