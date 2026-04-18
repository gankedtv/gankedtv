# Ganked.tv — Combined MVP Plan

## 1. Project Overview

**Name:** Ganked.tv
**Concept:** Web-based (mobile-friendly) platform for sharing short gaming clips.
**USP:** No algorithm, no noise — pure gaming highlights. Upload-agnostic (any recording tool works).
**License:** AGPL-3.0
**Repo:** https://github.com/gankedtv/gankedtv

---

## 2. Competitive Landscape

| Platform | Strength | Gap for Ganked.tv |
|----------|----------|-------------------|
| **Medal.tv** | Instant clip sharing, cloud sync, daily top 10 per game | Tied to their recording app — Ganked.tv is upload-agnostic |
| **Allstar.gg** | Low FPS impact, optimized for competitive titles | Small game library, premium-gated |
| **TikTok / YT Shorts** | Massive reach, algorithm-driven discovery | Not gaming-specific, no game tagging |

**Differentiation:**
- Accept clips from any source (OBS, ShadowPlay, Medal, Xbox Game Bar, console)
- Game-centric social (game tagging, per-game feeds)
- No desktop app required — just upload and share
- Open source (AGPL)

---

## 3. Tech Stack

### Frontend
- **Vue 3** + TypeScript
- **Vite** (build tool)
- **Pinia** (state management)
- **Plyr** or **Video.js** (video player)
- Dark mode first
- Mobile-first responsive design

### Backend
- **C# .NET 10** (Minimal APIs)
- **Entity Framework Core** + Npgsql
- **Auth:** OAuth 2.0 (Discord + Google) with JWT (access + refresh tokens)
- **AWSSDK.S3** (works with MinIO)
- **FFmpeg** (thumbnail generation, future transcoding)

### Infrastructure
- **PostgreSQL** — metadata, users, social data
- **MinIO** — video + thumbnail storage via presigned URLs
- **Docker Compose** — local dev environment
- **Redis** — future: caching, rate limiting, job queues

---

## 4. Architecture

```
┌─────────────────────────────────────────────────┐
│              Vue 3 Frontend (SPA)                │
│  Pinia · Vue Router · Plyr · Dark Mode          │
└────────────────────┬────────────────────────────┘
                     │ REST / JSON + Presigned URLs
┌────────────────────▼────────────────────────────┐
│           ASP.NET Core Minimal API (.NET 10)     │
│  ┌──────────┐  ┌──────────┐  ┌───────────────┐  │
│  │ Auth     │  │ Clips    │  │ Background    │  │
│  │ (OAuth + │  │ Service  │  │ Jobs (FFmpeg  │  │
│  │  JWT)    │  │          │  │ thumbnails)   │  │
│  └──────────┘  └──────────┘  └───────────────┘  │
└──────┬───────────────┬──────────────┬───────────┘
       │               │              │
  ┌────▼────┐    ┌─────▼─────┐  ┌────▼─────┐
  │PostgreSQL│    │   MinIO   │  │  Redis   │
  │(metadata)│    │  (clips)  │  │ (later)  │
  └──────────┘    └───────────┘  └──────────┘
```

**Key design decisions:**
- **Presigned URLs** for both upload and download — video never flows through the API
- **OAuth-first auth** — gamers already have Discord, no need for email/password registration
- **3-step upload flow** — create metadata → get presigned URL → confirm completion
- **Background jobs** for thumbnail extraction (FFmpeg)

---

## 5. Database Schema

```sql
-- Users (populated from OAuth)
CREATE TABLE users (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    username        VARCHAR(30) UNIQUE NOT NULL,
    email           VARCHAR(255) UNIQUE,
    avatar_url      TEXT,
    bio             VARCHAR(500),
    -- OAuth provider info
    discord_id      VARCHAR(50) UNIQUE,
    google_id       VARCHAR(50) UNIQUE,
    --
    created_at      TIMESTAMPTZ DEFAULT now(),
    updated_at      TIMESTAMPTZ DEFAULT now()
);

-- Games (lookup table)
CREATE TABLE games (
    id              SERIAL PRIMARY KEY,
    name            VARCHAR(255) NOT NULL,
    slug            VARCHAR(255) UNIQUE NOT NULL,
    cover_url       TEXT,
    igdb_id         INT
);

-- Clips (core entity)
CREATE TABLE clips (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id         UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    game_id         INT REFERENCES games(id),
    title           VARCHAR(255) NOT NULL,
    description     TEXT,
    -- MinIO references
    video_key       TEXT NOT NULL,
    thumbnail_key   TEXT,
    -- Video metadata
    duration_secs   SMALLINT,
    width           SMALLINT,
    height          SMALLINT,
    file_size_bytes BIGINT,
    -- Social counters (denormalized for performance)
    view_count      INT DEFAULT 0,
    like_count      INT DEFAULT 0,
    -- Status & visibility
    status          VARCHAR(20) DEFAULT 'processing',  -- processing | ready | failed
    visibility      VARCHAR(20) DEFAULT 'public',      -- public | unlisted
    --
    created_at      TIMESTAMPTZ DEFAULT now(),
    updated_at      TIMESTAMPTZ DEFAULT now()
);

CREATE INDEX idx_clips_user_id ON clips(user_id);
CREATE INDEX idx_clips_game_id ON clips(game_id);
CREATE INDEX idx_clips_created_at ON clips(created_at DESC);
CREATE INDEX idx_clips_status ON clips(status) WHERE status = 'ready';

-- Likes
CREATE TABLE likes (
    user_id     UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    clip_id     UUID NOT NULL REFERENCES clips(id) ON DELETE CASCADE,
    created_at  TIMESTAMPTZ DEFAULT now(),
    PRIMARY KEY (user_id, clip_id)
);

-- Refresh tokens (rotated on each /auth/refresh call)
CREATE TABLE refresh_tokens (
    id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id      UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    token_hash   TEXT NOT NULL,                  -- SHA-256 of the raw token; raw token is only sent to the client
    expires_at   TIMESTAMPTZ NOT NULL,
    revoked_at   TIMESTAMPTZ,                    -- set when rotated or explicitly revoked
    created_at   TIMESTAMPTZ DEFAULT now()
);

CREATE INDEX idx_refresh_tokens_user_id ON refresh_tokens(user_id);
CREATE INDEX idx_refresh_tokens_token_hash ON refresh_tokens(token_hash);
```

**Username generation on OAuth signup:** OAuth providers don't guarantee unique usernames (Discord usernames are not globally unique post-handle-migration; Google has none). On signup: take the provider username (or email local-part for Google), lowercase, slugify (`[a-z0-9_-]`, max 24 chars), and append `-{4 hex}` if it collides with an existing row. The user can later change it via `PATCH /me`.

---

## 6. API Design

### Auth
| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/auth/discord/start` | Redirect to Discord OAuth (sets signed `state` cookie) |
| GET | `/auth/discord/callback` | Validate `state`, handle Discord callback, issue JWT + refresh token |
| GET | `/auth/google/start` | Redirect to Google OAuth (sets signed `state` cookie) |
| GET | `/auth/google/callback` | Validate `state`, handle Google callback, issue JWT + refresh token |
| POST | `/auth/refresh` | Rotate access token (consumes one refresh token, issues a new one) |
| GET | `/me` | Get current user profile |
| PATCH | `/me` | Update username, bio, avatar |

**OAuth security note:** Both providers use the OAuth 2.0 `state` parameter to prevent CSRF on the callback. `/start` generates a random nonce, signs it (HMAC) together with an optional `returnTo`, sets it in a short-lived (5 min) HttpOnly cookie, and forwards it as `state` in the redirect. `/callback` validates the signature and matches the cookie before issuing tokens.

### Users
| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/users/{username}` | Public user profile + their public/ready clips |

### Clips
| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/clips/feed` | Paginated chronological feed (public, status=ready) |
| GET | `/clips/{id}` | Get clip metadata + presigned video URL |
| POST | `/clips` | Create clip record (title, game, description, visibility) |
| POST | `/clips/{id}/upload-url` | Get presigned MinIO PUT URL |
| POST | `/clips/{id}/complete` | Mark upload done → trigger thumbnail job |
| PATCH | `/clips/{id}` | Edit title, description, game, visibility (owner only) |
| DELETE | `/clips/{id}` | Delete clip + MinIO object (owner only) |

### Likes
| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/clips/{id}/like` | Like a clip |
| DELETE | `/clips/{id}/like` | Unlike a clip |

### Games (later)
| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/games` | List/search games |
| GET | `/games/{slug}` | Game detail + clips |

---

## 7. Upload Flow (Detailed)

This is the most critical flow in the entire app. The 3-step approach keeps video off your API:

```
Client                          API                         MinIO
  │                              │                            │
  ├─ POST /clips ───────────────►│                            │
  │  {title, game, visibility}   │── creates clip record ──►  │
  │◄─ {clip_id, status:draft} ──┤   (status = "draft")       │
  │                              │                            │
  ├─ POST /clips/{id}/upload-url►│                            │
  │                              │── generates presigned ────►│
  │◄─ {presigned_put_url} ──────┤   PUT URL                  │
  │                              │                            │
  ├─ PUT presigned_url ─────────────────────────────────────►│
  │  (raw video bytes)           │                        ✅ stored
  │◄─ 200 OK ───────────────────────────────────────────────┤
  │                              │                            │
  ├─ POST /clips/{id}/complete ─►│                            │
  │                              │── set status="processing"  │
  │                              │── queue thumbnail job      │
  │◄─ {clip_id, status:processing}                            │
  │                              │                            │
  │        (background)          │── FFmpeg extracts thumb ──►│
  │                              │── set status="ready"       │
```

---

## 8. MVP Scope (What to Build)

### In Scope ✅
- OAuth login (Discord + Google)
- Upload MP4 clips via presigned URLs
- Clip metadata: title, game, description, visibility
- Chronological feed (public clips, newest first)
- Like / unlike clips
- Edit & delete own clips
- Auto-generated thumbnails (FFmpeg)
- User profile page (your clips)
- Video player with proper controls
- Dark mode UI
- Mobile-friendly responsive design

### Out of Scope ❌ (Post-MVP)
- Comments & threaded replies
- Follow system & personalized feed
- Search & discovery (trending, tags)
- Video transcoding / multiple qualities
- HLS/DASH adaptive streaming
- Notifications
- Discord bot integration
- Clip challenges / competitions
- Admin/moderation tools

---

## 9. Development Phases

### Phase 1 — Core Loop (Weeks 1-3) ⭐ START HERE
> Goal: Upload a clip, store it, play it back.

**Week 1: Backend Foundation**
- [ ] EF Core setup + first migration (users, clips, likes tables)
- [ ] MinIO service: bucket creation, presigned URL generation
- [ ] OAuth flow: Discord + Google → JWT tokens
- [ ] `GET /me` endpoint

**Week 2: Clip CRUD**
- [ ] Full 3-step upload flow (create → presign → complete)
- [ ] `GET /clips/feed` with cursor-based pagination
- [ ] `GET /clips/{id}` with presigned video URL
- [ ] `PATCH /clips/{id}` and `DELETE /clips/{id}` (owner only)
- [ ] Like / unlike endpoints
- [ ] File validation (MP4 only, max size, max duration)

**Week 3: Frontend**
- [ ] Vue Router: `/`, `/login`, `/upload`, `/clip/:id`, `/user/:username`
- [ ] OAuth login flow (redirect to Discord/Google, handle callback)
- [ ] Upload page: file picker → 3-step upload → redirect to clip
- [ ] Feed page: chronological grid of clips with thumbnails
- [ ] Clip detail page with video player (Plyr)
- [ ] User profile page showing their clips
- [ ] Like button with optimistic UI updates

**✅ Done when:** You can log in with Discord, upload a clip, see it in the feed, and like it.

---

### Phase 2 — Polish & Social (Weeks 4-6)
> Goal: Make it feel like a real product.

- [ ] Thumbnail generation background job (FFmpeg)
- [ ] Game tagging: add games table, searchable game selector on upload
- [ ] Clip editing: update title, description, game, visibility
- [ ] Upload progress bar (track presigned URL upload progress)
- [ ] OG meta tags for Discord/Twitter embeds (thumbnail + title)
- [ ] Short share URLs: `ganked.tv/c/{shortcode}`
- [ ] View counting (debounced, don't count refreshes)
- [ ] Error handling & loading states throughout the UI
- [ ] Mobile responsive refinements

---

### Phase 3 — Discovery (Weeks 7-9)
> Goal: Help users find content.

- [ ] Game pages: `/game/{slug}` with all clips for that game
- [ ] Comments + threaded replies
- [ ] Follow system + "following" feed tab
- [ ] Search (PostgreSQL full-text search on titles + game names)
- [ ] Trending: time-weighted scoring (likes + views in last 24h)
- [ ] Tags (#clutch, #funny, #fail, etc.)
- [ ] Notification system (likes, comments, follows)

---

### Phase 4 — Scale & Grow (Weeks 10+)
- [ ] Video transcoding (multiple qualities with FFmpeg)
- [ ] Redis caching for hot feeds
- [ ] Vertical-scroll player (TikTok-style, optional)
- [ ] "Clip of the Day" featured clip
- [ ] Game leaderboards (most liked per game per week)
- [ ] Discord bot for auto-posting clips to servers
- [ ] Import from Medal.tv / YouTube URLs
- [ ] Admin dashboard + moderation tools
- [ ] Rate limiting & abuse prevention

---

## 10. Deployment

### Development
```bash
docker compose -f docker-compose.dev.yml up -d  # PostgreSQL + MinIO
cd server && dotnet watch --project src/GankedTV.Api
cd web && bun dev
```

### Production
- **Docker Compose** for all services
- **Reverse proxy:** Caddy (auto HTTPS via Let's Encrypt)
- **MinIO** on your NAS (separate from app server)
- **Daily database backups** (pg_dump cron job → backup location)
- **Domain:** ganked.tv

### Environment Variables
```env
# Database (local dev: docker-compose exposes Postgres on host port 5435)
DATABASE_URL=Host=localhost;Port=5435;Database=gankedtv;Username=gankedtv;Password=gankedtv_dev

# MinIO
MINIO_ENDPOINT=localhost:9000
MINIO_ACCESS_KEY=minioadmin
MINIO_SECRET_KEY=minioadmin
MINIO_BUCKET_CLIPS=clips
MINIO_BUCKET_THUMBNAILS=thumbnails
MINIO_USE_SSL=false
MINIO_PUBLIC_URL=https://cdn.ganked.tv  # public-facing URL for presigned links

# Auth
JWT_SECRET=your-secret-key
JWT_EXPIRY_MINUTES=15
REFRESH_TOKEN_EXPIRY_DAYS=30
DISCORD_CLIENT_ID=...
DISCORD_CLIENT_SECRET=...
DISCORD_REDIRECT_URI=https://ganked.tv/auth/discord/callback
GOOGLE_CLIENT_ID=...
GOOGLE_CLIENT_SECRET=...
GOOGLE_REDIRECT_URI=https://ganked.tv/auth/google/callback

# App
CORS_ORIGINS=https://ganked.tv
MAX_UPLOAD_SIZE_MB=500
MAX_CLIP_DURATION_SECS=120
```

---

## 11. Implementation Tips

**OAuth (Discord):**
- Discord OAuth is very common in gaming apps — make it the primary login method
- Use the `identify` and `email` scopes
- Pull username + avatar from Discord to pre-populate the profile

**Presigned URLs:**
- `AWSSDK.S3` works with MinIO — just set the `ServiceURL` to your MinIO endpoint
- Set presigned URL expiry to ~15 minutes for uploads
- For downloads, you can either use presigned GETs or set the thumbnails bucket to public-read

**MinIO bucket bootstrap:**
- Ensure `clips` and `thumbnails` buckets exist on API startup (idempotent `PutBucket`).
- Run during `IHostedService.StartAsync` so the API fails fast if MinIO is unreachable.

**FFmpeg Thumbnails:**
```bash
ffmpeg -i input.mp4 -ss 00:00:01 -frames:v 1 -q:v 2 thumbnail.jpg
```

**Cursor-based Pagination:**
- Don't use OFFSET/LIMIT (gets slow with large datasets)
- Use `WHERE created_at < @cursor ORDER BY created_at DESC LIMIT 20`
- Return the last item's `created_at` as the next cursor

**DTO layout & mappers (.NET convention):**
- DTOs live in `server/src/GankedTV.Api/Contracts/<Area>/` — one `public sealed record` per file. Namespace: `GankedTV.Api.Contracts.<Area>`.
- Entity → DTO projection happens via **hand-written static extension methods** in a `*Mappings.cs` file next to the DTOs (e.g. `Contracts/Clips/ClipMappings.cs` with `ToFeedItem(this Clip, ...)`). Avoid reflection-based libraries like AutoMapper — modern .NET convention is explicit mappers (compile-time checked, no runtime surprises).
- Endpoint handlers should not construct DTOs inline; call the mapper.
- Caller-scoped values (e.g. `likedByMe`, presigned URLs) are passed into the mapper as parameters — the entity stays ignorant of the caller.

**Discord Embeds (OG Tags):**
```html
<meta property="og:title" content="Insane Valorant Ace — by username" />
<meta property="og:description" content="Watch this clip on Ganked.tv" />
<meta property="og:image" content="https://cdn.ganked.tv/thumbnails/abc.jpg" />
<meta property="og:video" content="https://cdn.ganked.tv/clips/abc.mp4" />
<meta property="og:type" content="video.other" />
```
This makes clips auto-embed beautifully when pasted in Discord — **huge for virality in gaming communities**.

---

**Status: Ready for implementation 🚀**
