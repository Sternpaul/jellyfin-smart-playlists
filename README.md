# 🎬 Jellyfin AI Movie Recommender

> The Netflix algorithm, from first principles — just better.

A Jellyfin server plugin that solves the "what should I watch" problem for massive movie libraries. It uses **AI to properly classify every movie** (because TMDB's broad "Action/Thriller" tags are useless), then builds **intelligent, self-updating playlists** that learn from your watch history, rotate content, punish rejected movies, and prevent filter bubbles.

**Bring your own AI provider** — supports Google AI, OpenRouter, OpenAI, and Anthropic Claude.

**Works on ALL Jellyfin clients** — including FireTV, Android TV, iOS, and web. No client-side changes needed.

---

## The Problem

You have thousands of movies. Jellyfin (via TMDB) tags a psychological slow-burn character study and a Fast & Furious movie both as "Action/Thriller". Browsing by genre is meaningless. You end up scrolling endlessly or rewatching the same 20 movies.

## The Solution

1. **AI reads every movie's plot summary** and assigns real subcategories, moods, and themes — once, on first run
2. **Smart playlists** use these AI classifications to build per-user, rotating recommendations
3. **The punishment mechanic** keeps playlists fresh — rejected movies get banned temporarily
4. **Anti-bubble protection** ensures your wide taste is respected, not narrowed

---

## 🤖 Supported AI Providers

Pick your provider. Bring your own API key. Switch anytime.

| Provider | Default Model | Best For | Classification Cost (~5K movies) |
|---|---|---|---|
| **Google AI** *(default)* | Gemma 4 31B | Free tier available, fast, great quality | ~$0.03 |
| **OpenRouter** | Any model | Maximum flexibility, access to 200+ models | ~$0.05-0.10 |
| **OpenAI** | GPT-4o Mini | Strict structured output, reliable | ~$0.15-0.30 |
| **Anthropic Claude** | Claude Sonnet 4.5 | Best reasoning quality | ~$0.20-0.40 |

All providers work for both **batch classification** (one-time) and **interactive AI chat** (ongoing). You can even use different models for each — e.g., a cheap model for classification and a premium model for chat.

---

## ✨ Features

### 🧠 AI Movie Classification (One-Time)

On first run, the plugin sends every movie's plot summary to your chosen AI and gets back rich metadata that TMDB never provides:

| AI-Assigned Field | Example for "Se7en" |
|---|---|
| **Subcategories** | Psychological Thriller, Neo-Noir, Crime Thriller |
| **Mood tags** | dark, suspenseful, disturbing, cerebral |
| **Themes** | obsession, morality, serial killer, detective |
| **Narrative style** | mystery-procedural |
| **Accessibility** | mainstream |
| **Intensity** | high |

This is what makes the whole system work. After classification, playlists run with **zero ongoing API costs**. New movies added to the library are classified incrementally (pennies).

### 🎯 Smart Playlists

Dynamic, per-user playlists that update automatically. These appear as regular playlists in your library on **every Jellyfin client**.

| Playlist | What It Does |
|---|---|
| **For You** | Your top 20 personalized picks across all genres. 75% taste-matched, 25% exploration. |
| **Because You Watched [X]** | 10 movies similar to what you just watched. Regenerates after every movie. |
| **Hidden Gems** | High-rated movies (≥ 7.0) that are cult, arthouse, or niche. Discovery-focused. |
| **Recently Added** | All unwatched movies, sorted by date added to library (most recent first). |
| **[Subcategory] For You** | Deep dives into subcategories you love (e.g., "Psychological Thrillers For You"). |
| **Discover: [Subcategory]** | Gateway playlists into subcategories you haven't explored yet. Rotates regularly. |
| **Wild Card** | 100% exploration — picks from subcategories you watch the least. Only high-rated films. |
| **From Your Watchlist** | Your Letterboxd watchlist filtered to movies in your library, with full smart scoring. |

All playlists are **private per user** — each user gets their own set, invisible to other users.

### 🚫 The Punishment Mechanic

When you pick a movie from a playlist, **every other movie in that playlist gets banned** from it temporarily. They had their chance — they lost. The playlist rebuilds with entirely fresh picks.

- After a cooling period (default: 2 refresh cycles), they become eligible again, but with a **lower priority penalty**
- **Penalty Recovery:** The penalty isn't permanent. It fades out over 4 weeks (time-based decay). Additionally, if you watch a closely related movie, the penalty is instantly reduced because your interest in that niche just spiked.
- This forces constant freshness — you never see the same stale playlist twice

### 🌍 Anti-Bubble Protection

Your taste is wide? The plugin respects that.

- **25% of every playlist** is reserved for exploration picks (configurable 10-50%)
- No single subcategory can dominate more than 40% of any playlist
- If multiple playlists start looking too similar, the system forces diversification
- A dedicated **Wild Card** playlist always pulls from your least-explored subcategories
- Discovery playlists rotate to expose you to new areas over time

### 🎭 Dynamic Subcategories

Instead of Jellyfin's flat "Action / Comedy / Drama" genres, the AI reads each movie's plot and assigns meaningful subcategories:

| Genre | Example Subcategories |
|---|---|
| Action | Heist, Martial Arts, War, Superhero, Chase/Pursuit, Revenge |
| Thriller | Psychological, Neo-Noir, Crime, Spy/Espionage, Legal, Techno-Thriller |
| Comedy | Dark Comedy, Rom-Com, Satire, Slapstick, Buddy, Mockumentary |
| Drama | Historical, Courtroom, Family, Sports, Biopic, Coming-of-Age |
| Sci-Fi | Space Opera, Cyberpunk, Time Travel, Hard Sci-Fi, Dystopian, First Contact |
| Horror | Supernatural, Slasher, Psychological, Body Horror, Folk, Found Footage |

These are assigned by AI reading actual plot summaries — not by TMDB's generic tags. Subcategories you know well get bigger playlists ("For You"). Unfamiliar ones get smaller gateway playlists ("Discover:"). Both rotate so you always see something new.

### 🔍 Similarity Engine

Movies are compared using AI-assigned metadata (not TMDB's broad tags):

| Factor | Weight | What It Compares |
|---|---|---|
| Subcategory overlap | 30% | AI-assigned subcategories |
| Mood overlap | 20% | AI-assigned mood tags (dark, cerebral, etc.) |
| Theme overlap | 15% | AI-assigned themes (obsession, revenge, etc.) |
| Director/Cast overlap | 10% | Shared creative talent |
| Narrative style match | 10% | Same storytelling approach |
| Era proximity | 5% | Movies from similar decades |
| Rating proximity | 5% | Similar quality tier |
| Intensity match | 5% | Similar intensity level |

This powers "Because You Watched" playlists and fills exploration slots with adjacent (not random) content.

### 📋 Letterboxd Watchlist Integration

Got a massive Letterboxd watchlist? Import it and get a smart playlist that only picks from movies on your watchlist that are already in your library.

- **Import via JSON URL** *(recommended)* — point to any URL serving a JSON array (e.g., a Radarr-compatible export with `imdb_id`, `title`, `release_year`). Auto-synced on every playlist refresh.
- **Import via CSV** — export from Letterboxd (Settings → Import & Export) and upload
- **Matching by IMDB ID** — most reliable. Falls back to title + year if no IMDB match.
- The **"From Your Watchlist"** playlist applies ALL the same smart logic (scoring, diversity, punishment, rotation) but only from your watchlist movies
- Unmatched titles (on watchlist but not in your library) are listed so you can see what's missing
- Watchlist is re-synced on the same schedule as playlist refresh
- **Per-user** — each Jellyfin user can configure their own watchlist URL or CSV

### ⭐ Review Nudging (Subtle)

Optionally, the plugin gives a scoring nudge based on critical acclaim and your personal Letterboxd ratings. This is:
- **Configurable** — set the weight from 0% (disabled) up to 15% via the settings page
- **Never displayed** to the user — purely a background signal
- **Never dominant** — even at 15%, it can't override strong taste-matching or diversity rules
- Helps surface critically acclaimed films slightly more often, without turning every playlist into an "Oscar winners" list

### 💬 AI Chat (Web Client Only)

Ask for recommendations in natural language:
- *"I want a mind-bending thriller"*
- *"Something light and funny for date night"*
- *"Movies like Interstellar but I haven't seen"*

The AI searches your library using enriched metadata, excludes everything you've watched, and returns personalized picks with explanations. Uses the same provider/API key as classification.

### ⚙️ How Playlists Stay Fresh

```
User watches a movie
    │
    ├── Movie removed from ALL playlists (forever)
    ├── All other movies in the SOURCE playlist get BANNED (cooling period)
    ├── Taste profile updated with the new watch
    ├── Source playlist rebuilt with entirely fresh picks
    └── Other playlists adjusted (watched movie removed, replacement scored)

Every N hours (configurable, default: 12):
    │
    ├── Check for any new watches since last refresh
    ├── Rotate 30% of each playlist's content with fresh picks
    ├── Rotate active subcategory playlists
    ├── Update exploration slots
    └── Enforce diversity constraints
```

---

## 📦 Installation

### Prerequisites
- Jellyfin Server **10.11.x**
- .NET 9.0 SDK (for building from source)
- An API key from one of: [Google AI Studio](https://aistudio.google.com/) (free tier available), [OpenRouter](https://openrouter.ai/), [OpenAI](https://platform.openai.com/), or [Anthropic](https://console.anthropic.com/)

### Install from Release

1. Download the latest `.dll` from [Releases](../../releases)
2. Place it in your Jellyfin plugin directory:
   ```
   # Docker (Depends on your mount, usually one of these)
   /config/plugins/AIRecommender/Jellyfin.Plugin.AIRecommender.dll
   /config/data/plugins/AIRecommender/Jellyfin.Plugin.AIRecommender.dll

   # Windows
   C:\Users\{you}\AppData\Local\jellyfin\plugins\AIRecommender\Jellyfin.Plugin.AIRecommender.dll

   # Linux (Native install)
   /var/lib/jellyfin/plugins/AIRecommender/Jellyfin.Plugin.AIRecommender.dll
   ```
3. Restart Jellyfin
4. Go to **Dashboard → Plugins → AI Recommender**
5. Select your AI provider and enter your API key
6. Click **"Classify Library"** — runs once, takes a few minutes for large libraries
7. Playlists are generated automatically after classification completes

### Build from Source

To compile the plugin yourself, you must first install the **.NET 9.0 SDK**.
- **Windows / macOS**: Download the installer from the [official Microsoft .NET download page](https://dotnet.microsoft.com/download/dotnet/9.0).
- **Linux (Debian/Ubuntu)**: The standard `apt` repositories don't always have the latest .NET versions. The most reliable way is to use Microsoft's official install script:
  ```bash
  wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh
  chmod +x ./dotnet-install.sh
  ./dotnet-install.sh --channel 9.0
  export DOTNET_ROOT=$HOME/.dotnet
  export PATH=$PATH:$HOME/.dotnet
  ```

Once the SDK is installed:

```bash
git clone https://github.com/Sternpaul/jellyfin-smart-playlists.git
cd jellyfin-smart-playlists
dotnet build --configuration Release
```

The compiled DLL will be in `bin/Release/net9.0/`.

### Docker Volume Note

The plugin stores its SQLite database in the Jellyfin config directory. This works automatically with standard Docker setups where `/config` is a mounted volume. No additional volume mounts needed.

---

## ⚙️ Configuration

Access via **Dashboard → Plugins → AI Recommender**.

### AI Provider Settings

| Setting | Default | Description |
|---|---|---|
| **AI Provider** | Google AI | Choose: Google AI, OpenRouter, OpenAI, or Anthropic Claude |
| **API Key** | *(required)* | Your API key for the selected provider |
| **Classification Model** | `gemma-4-31b-it` | Model for batch movie classification (provider-specific) |
| **Chat Model** | *(same as above)* | Model for interactive chat (can differ from classification) |
| **Custom Endpoint** | *(empty)* | Optional custom API URL for self-hosted or proxy setups |

### Playlist Settings

| Setting | Default | Description |
|---|---|---|
| **Playlist Refresh Interval** | 12 hours | How often playlists auto-refresh |
| **Max Movies Per Playlist** | 20 | Maximum movies in each playlist |
| **Playlist Rotation %** | 30% | Percentage of movies swapped each refresh cycle |
| **Diversity Weight** | 25% | Playlist slots reserved for exploration (10-50%) |
| **Cooling Period** | 2 cycles | How long banned movies stay out after rejection |
| **Enabled Playlist Types** | All | Toggle individual playlist types on/off |
| **Review Nudging Weight** | 0% | How much critical acclaim boosts a movie's score (0-15%) |

### Per-User Settings

Each Jellyfin user can configure their own Letterboxd integration via the plugin page:

| Setting | Default | Description |
|---|---|---|
| **Letterboxd Username** | *(empty)* | Public username for automatic watchlist scraping |
| **Watchlist JSON URL** | *(empty)* | URL to a JSON watchlist file (Radarr-compatible format) |
| **Watchlist CSV** | *(empty)* | Upload a Letterboxd CSV export instead |
| **Enable Watchlist Playlist** | Off | Generate the "From Your Watchlist" playlist |

### Provider-Specific Model IDs

| Provider | Classification Model | Chat Model |
|---|---|---|
| Google AI | `gemma-4-31b-it` | `gemma-4-31b-it` |
| OpenRouter | `google/gemma-4-31b-it` | `google/gemma-4-31b-it` |
| OpenAI | `gpt-4o-mini` | `gpt-4o` |
| Anthropic | `claude-sonnet-4-5` | `claude-sonnet-4-5` |

---

## 🔌 API Endpoints

The plugin exposes a REST API for custom integrations:

| Method | Endpoint | Description |
|---|---|---|
| `POST` | `/api/airecommender/chat` | Natural language AI recommendations |
| `GET` | `/api/airecommender/playlists/{userId}` | Get all dynamic playlists for a user |
| `POST` | `/api/airecommender/refresh/{userId}` | Force-refresh playlists for a user |
| `GET` | `/api/airecommender/taste-profile/{userId}` | View computed taste profile |
| `GET` | `/api/airecommender/history/{userId}` | Export full watch history |
| `POST` | `/api/airecommender/classify` | Trigger library classification |
| `POST` | `/api/airecommender/reindex` | Trigger library reindex |
| `GET` | `/api/airecommender/status` | Plugin status, classification progress, stats |

All endpoints require Jellyfin authentication via the `Authorization` header.

---

## 🏗️ How It Works (Technical)

### First Run
1. **Library Indexing** — Scans your entire movie library and caches metadata (title, year, overview/plot, cast, director, rating) into SQLite
2. **AI Classification** — Sends plot summaries in batches to your chosen AI provider. The model assigns subcategories, moods, themes, narrative style, accessibility, and intensity for each movie
3. **Similarity Computation** — Calculates movie-to-movie similarity using AI-assigned metadata and stores top-50 similar movies per film
4. **Watch History Export** — Scans all user accounts for watched movies and builds the exclusion list
5. **Taste Profiling** — Computes subcategory weights, mood preferences, era preferences per user
6. **Playlist Generation** — Creates all dynamic playlists for each user

### Ongoing
- **Incremental classification** — New library additions are automatically sent to the AI for classification
- **Real-time watch tracking** — Hooks into Jellyfin's playback events to catch watches as they happen
- **Scheduled refresh** — Playlists regenerate on a configurable interval (zero API cost)
- **Immediate refresh** — When a user finishes a movie, their playlists update (debounced)

### Client Compatibility

| Client | Playlists | AI Chat |
|---|---|---|
| Web Browser | ✅ | ✅ |
| FireTV / Android TV | ✅ | ❌ |
| iOS / Android Mobile | ✅ | ❌ |
| Kodi + Jellyfin Plugin | ✅ | ❌ |

Playlists are standard Jellyfin playlists — they work everywhere. The AI chat UI is a web-only plugin page.

---

## 💰 Cost

| Operation | When | Google AI | OpenRouter | OpenAI | Anthropic |
|---|---|---|---|---|---|
| **Classification** | Once | ~$0.03 | ~$0.05-0.10 | ~$0.15-0.30 | ~$0.20-0.40 |
| **New movie** | On add | < $0.001 | < $0.001 | < $0.01 | < $0.01 |
| **Playlist refresh** | Every 12h | Free | Free | Free | Free |
| **AI chat query** | Each ask | ~$0.001 | ~$0.001-0.01 | ~$0.005-0.02 | ~$0.005-0.02 |

After initial classification, smart playlists run at **zero cost**.

---

## 🗺️ Roadmap

- [ ] Plugin scaffolding and configuration
- [ ] AI provider abstraction (Google AI, OpenRouter, OpenAI, Anthropic)
- [ ] Movie indexer (batch + incremental)
- [ ] AI movie classifier (batch classification + critical acclaim scoring)
- [ ] Similarity engine (AI-assigned tags)
- [ ] Watch history export and real-time tracking
- [ ] Taste profiler
- [ ] Playlist engine with punishment mechanic
- [ ] Scheduled tasks and auto-refresh
- [ ] Letterboxd watchlist import (CSV + scraping) + watchlist playlist
- [ ] Review nudging (critical acclaim + personal ratings)
- [ ] Admin configuration page (provider picker, model selector, per-user Letterboxd)
- [ ] REST API endpoints
- [ ] AI chat recommendation engine
- [ ] Chat UI page

---

## 📄 License

MIT License — see [LICENSE](LICENSE) for details.
