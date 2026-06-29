# GankedTV Design System — Arena

**Status:** Spec approved, pending implementation.
**Replaces:** Newsprint ("Broadcast Almanac") v2

---

## 1 — Concept

A premium gaming clip platform that beats Medal.tv on every dimension. The UI is confident but invisible — one accent color, clean surfaces, no decorative metaphors. Content (clips, game covers) does the talking.

Two modes with genuinely different personalities:
- **Dark (primary):** Late-night LAN. Near-black, Neon Mint accent, high contrast.
- **Light (secondary):** Game day. Warm cream page, white cards, deeper mint for contrast. Not an inversion — a different mood entirely.

The platform is **sleek and exclusive** (nothing feels cheap, nothing feels template) and **community-forward** (social signals are visible everywhere — follower activity, online counts, ranked clips).

Reference: Medal.tv as the floor. Beat it in aesthetics, navigation clarity, discovery, and search.

---

## 2 — The rules (load-bearing)

Dropping any of these lets the system drift back toward generic:

1. **One accent, everywhere.** Mint owns every interactive state: active nav links, CTA buttons, game tags, author handles, section labels, active tab underlines, filter pill borders. No second accent color.
2. **Borders, not shadows.** Depth is created by surface color difference (`surface-base` vs `surface-raised`). No `box-shadow`, no `drop-shadow`, no `shadow-*` utilities — ever. The one exception: Plyr player overrides that *remove* its native shadow.
3. **No hover transforms.** Hover = border color shift to mint + text color shift. Cards stay put. The sole exception: game cover tiles get `translateY(-2px)` — earned because the grid is a browsable catalogue, not a feed.
4. **Plain section names.** Section headers say what they are: "Top Games", "Trending", "Recent Clips". No editorial kicker copy ("What climbed the chart overnight"), no roman numerals, no issue numbers.
5. **No issue numbers anywhere.** No "No. 042", no volume/issue framing. Ranking is expressed as plain numerals (01, 02, 03) in muted condensed type — not a publishing metaphor.
6. **8px card radius.** Cards are `rounded-lg` (8px). Inputs are `rounded-md` (6px). Nav/popovers are `rounded-lg`. No sharp corners (`rounded-none`) and nothing above `rounded-xl` except avatars (`rounded-full`).
7. **No UI gradients.** Thumbnails and game covers supply all visual richness. The only sanctioned gradient is the legibility overlay on video thumbnails (`linear-gradient(transparent, rgba(0,0,0,0.85))`).
8. **Backdrop blur on nav only.** `backdrop-filter: blur(12px)` is used on the sticky nav and nowhere else.
9. **Tabs as underlines, not pills.** Feed tabs (For You / Following / Trending / Top Rated) use a 2px mint bottom border on active. Filter pills (game filters) are the only pill shape in the system.
10. **Mint in light mode deepens.** Light mode accent is `#00b87d` (not `#00e5a0`) for WCAG AA contrast on cream backgrounds. Never use `#00e5a0` on light surfaces.

---

## 3 — Color tokens

One palette, dark (default) + light via `.light` on `<html>`. Defined in the `@theme` block of `src/assets/base.css`.

### Dark mode

| Token | Value | Role |
|---|---|---|
| `--color-surface-base` | `#0b0b0f` | Page background |
| `--color-surface-raised` | `#111116` | Cards, nav background |
| `--color-surface-high` | `#18181f` | Hover backgrounds, inputs, dropdowns |
| `--color-text-primary` | `#f0f0f4` | Headings, body copy, card titles |
| `--color-text-secondary` | `rgba(255,255,255,0.50)` | Bylines, meta, captions |
| `--color-text-muted` | `rgba(255,255,255,0.28)` | Timestamps, placeholders, disabled states, rank numbers |
| `--color-border` | `rgba(255,255,255,0.07)` | Default borders, dividers |
| `--color-border-strong` | `rgba(255,255,255,0.12)` | Focused inputs, popovers, modals |
| `--color-accent` | `#00e5a0` | Mint — all interactive states |
| `--color-accent-bg` | `rgba(0,229,160,0.08)` | Tag fills, active pill background |
| `--color-accent-border` | `rgba(0,229,160,0.25)` | Tag borders, focus rings |

### Light mode (`.light` on `<html>`)

| Token | Value | Role |
|---|---|---|
| `--color-surface-base` | `#f7f5f0` | Warm cream page background |
| `--color-surface-raised` | `#ffffff` | Cards, nav background |
| `--color-surface-high` | `#f0ece3` | Hover backgrounds, inputs |
| `--color-text-primary` | `#1a1a22` | Headings, body copy |
| `--color-text-secondary` | `#888070` | Bylines, meta, captions |
| `--color-text-muted` | `#b0a898` | Timestamps, placeholders, disabled |
| `--color-border` | `#e8e4dc` | Default borders, dividers |
| `--color-border-strong` | `#d0ccc0` | Focused inputs, popovers |
| `--color-accent` | `#00b87d` | Deeper mint — WCAG AA on cream |
| `--color-accent-bg` | `#e8faf4` | Tag fills, active pill background |
| `--color-accent-border` | `#b3ead7` | Tag borders, focus rings |

Usage rules:

- **Mint is the only accent.** Don't introduce a second color for "danger", "warning", or "new" states — use text weight, border style, or copy to signal state differences.
- **Text over video** (duration badges, thumbnail overlays) uses literal `#f4f1e8` and `rgba(0,0,0,0.85)` — not tokens — because it must stay light in both modes.
- **Vendor colors** (`--color-discord`, `--color-google`) are identity-locked.

---

## 4 — Typography

Two fonts. No others.

| Role | Font | Weights | Used for |
|---|---|---|---|
| `font-condensed` | Barlow Condensed | 700, 800, 900 | Hero titles, section titles, wordmark, rank numbers, trending numerals |
| `font-body` | Inter | 400, 500, 600, 700 | Nav links, body copy, card titles, button labels, meta, filter pills |

**Scale:**

| Use | Size | Font | Weight | Case |
|---|---|---|---|---|
| Wordmark | 18px | Barlow Condensed | 900 | Uppercase |
| Hero clip title | 22–28px | Barlow Condensed | 800 | Uppercase |
| Section title | 18–22px | Barlow Condensed | 800 | Uppercase |
| Rank numeral (sidebar) | 22px | Barlow Condensed | 900 | — |
| Card title | 12–13px | Inter | 600 | Sentence |
| Nav link | 12px | Inter | 600 | Sentence |
| Section label (kicker) | 10px | Inter | 700 | Uppercase, 0.14em tracking |
| Game tag / badge | 9–10px | Inter | 700 | Uppercase, 0.07em tracking |
| Meta / byline | 10–11px | Inter | 400–500 | Sentence |
| Button label | 12px | Inter | 700 | Sentence |

Line-height: ~1.15 on condensed display, 1.5 on body, 1.3 on card titles. Don't use font-weight as the primary hierarchy lever — use size + position.

---

## 5 — Layout rhythm

- **Nav:** 56px sticky, `backdrop-filter: blur(12px)`, `bg-surface-raised/90`. Left: logo + nav links. Center: search bar (flex-1, max ~300px) with `⌘K` shortcut hint. Right: live online count + Upload CTA + avatar.
- **Page shell:** `max-w-[1200px] mx-auto px-7 pt-7 pb-16`.
- **Feed controls:** Tabs row + filter pills in the same flex row, separated by `ml-auto`. Border-bottom hairline.
- **Home band order:**
  1. Feed controls (tabs + game filters)
  2. Hero band — `grid-cols-[1fr_300px] gap-5`
  3. Game catalogue — `grid-cols-5 gap-3`
  4. Trending band — `grid-cols-[1fr_280px] gap-5`
  5. Clip grid — `grid-cols-4 gap-3.5`
  6. Load more button
- **Section header:** `flex items-baseline gap-3`, section label (mint, 10px caps) + section title (Barlow Condensed 800 uppercase) + `ml-auto` see-all link. Top border-hairline + `pt-8` between bands.
- **Grid gaps:** `gap-3` (12px) for game tiles, `gap-3.5` (14px) for clip cards.
- **Mobile:** `MobileTabBar` fixed bottom (Feed / Games / Upload / Reels / You). Nav collapses to logo + upload + avatar. Feed becomes single-column.

---

## 6 — Component patterns

### Clip card

Chrome-less card. `rounded-lg border border-border bg-surface-raised`. Thumbnail (`aspect-[16/9]`) with:
- Game tag: top-left, `absolute`, mint bg/border, 9px Inter 700 uppercase
- Duration: bottom-right, `absolute`, `bg-black/75 text-white`, 10px Inter 600
- `hover:border-border-strong` (no transform)

Below thumb: card title (Inter 600 12px, 2-line clamp), then meta row: `@author` in mint + `·` + view count in text-muted.

### Hero clip

Full bleed thumbnail with play button overlay (`size-13 rounded-full bg-black/55 border border-white/30`). Below: game tag + "Top clip today" label in text-muted, then Barlow Condensed 800 uppercase title (22–28px), then meta row (avatar chip + author in mint + views + likes).

### Ranked sidebar

Paired with the hero. Header: "Also trending now" in 10px caps muted. Each item: `grid-cols-[36px_56px_1fr]` — rank numeral (Barlow Condensed 900, muted, 22px; #1 gets mint color) + 16:9 thumbnail + title/meta stack. Bottom border hairline between items.

### Game tile (catalogue)

`aspect-[3/4]` cover image (game art), `rounded-lg border border-border`. Rank numeral top-left in muted condensed. Below: game name (Inter 700 11px) + clip count (Inter 400 10px muted). `hover:border-accent-border hover:-translate-y-0.5`.

### Trending band

Same layout as hero band: featured clip left (with "#49 · up 38 spots" rank label), numbered list right (rank + title + meta + view count).

### Feed tabs

`flex border-b border-border`. Each tab: `text-xs font-semibold px-4 py-2.5 border-b-2 border-transparent text-text-muted`. Active: `border-accent text-text-primary`.

### Filter pills

`flex gap-1.5`. Each pill: `text-[11px] font-semibold px-3 py-1 rounded-full border border-border text-text-muted`. Active: `bg-accent-bg border-accent-border text-accent`.

### Buttons

- **Primary (CTA):** `bg-accent text-[#080f0d] font-bold rounded-lg px-4 py-1.5 hover:brightness-105`
- **Secondary:** `border border-border-strong bg-transparent text-text-secondary rounded-lg hover:border-accent hover:text-accent`
- **Ghost/link:** `text-accent hover:underline`
- **Icon button:** `size-8 rounded-lg border border-border bg-transparent hover:border-border-strong`

### Inputs

`h-10 rounded-md border border-border bg-surface-high px-3 text-sm focus:border-accent focus:outline-none`. Labels: Inter 10px 700 uppercase `tracking-[0.1em] text-text-secondary`.

### Section header

```
<div class="flex items-baseline gap-3 pt-8 border-t border-border mb-4">
  <span class="text-[10px] font-bold tracking-[0.14em] uppercase text-accent">Browse</span>
  <span class="font-condensed text-xl font-black uppercase tracking-wide text-text-primary">Top Games</span>
  <a class="ml-auto text-[11px] font-semibold text-accent">See all →</a>
</div>
```

### Nav search

`flex items-center gap-2 bg-surface-raised border border-border rounded-lg px-3.5 py-1.5 min-w-[220px] cursor-text hover:border-border-strong`. Contains search icon (muted) + placeholder text (muted) + `⌘K` kbd chip (right-aligned).

---

## 7 — Motion

- Page transitions: 150ms opacity fade.
- Hover on borders/colors: 150ms `transition-colors`.
- Game tile lift: 150ms `transition-transform`.
- Upload button: `hover:brightness-105` on `[filter]`, 150ms.
- Toasts: 250ms `slideUp` in, `slideDown` out.
- No `transition-transform` on clip cards — they stay put on hover.

---

## 8 — Beats Medal checklist

Every one of these must be present and working:

- [ ] Prominent search bar in nav (not hidden behind an icon)
- [ ] `⌘K` search shortcut
- [ ] Game filter pills always visible below feed tabs (no extra click to filter)
- [ ] Feed tabs: For You / Following / Trending / Top Rated
- [ ] Ranked sidebar list next to hero (01–05 top clips)
- [ ] Live online user count in nav
- [ ] Clip count on game tiles
- [ ] Author handle in mint (clickable, goes to profile)
- [ ] Duration badge on every thumbnail
- [ ] Load more button (not infinite scroll jank)

---

## 9 — Banned list + sweep

```bash
cd web/src
grep -rnE "shadow-\[|shadow-(sm|md|lg|xl|2xl)" . --include="*.vue"
grep -rn  "backdrop-blur" . --include="*.vue"   # nav only is ok
grep -rnE "hover:(-)?translate|hover:scale" . --include="*.vue"   # game-tile exception ok
grep -rnE "rounded-(xl|2xl|3xl)" . --include="*.vue"
grep -rn  "gradient" . --include="*.vue" --include="*.css"   # thumb overlays only
grep -rnE "No\.\s*\d{3}|issue.number|vol.*iss" . --include="*.vue" --include="*.ts"
grep -rnE "font-display|font-mono|Rajdhani|DM Mono|DM Sans" . --include="*.vue" --include="*.css"
```

---

## 10 — Component conventions

- Tailwind utility classes in templates; tokens via `@theme` block. Scoped CSS only when utilities can't express it (e.g., custom keyframe animations).
- Dynamic values (user accent, avatar fills) via inline `:style`.
- Icons: stroke-based SVG components inheriting `currentColor`.
- Images from user input: `<img>`, never CSS `background-image`.
- Game cover art: always `<img>` with `object-cover`, `aspect-[3/4]`.
- Clip thumbnails: always `<img>` with `object-cover`, `aspect-[16/9]`.

---

## 11 — Page inventory and user flow

Every route in `src/router/index.ts` is covered here. Each entry describes what the page contains and any Arena-specific design notes.

---

### Home (`/`)

The platform's front door. Primary discovery surface.

**Layout (top → bottom):**

1. Feed controls — tabs (For You / Following) + game filter pills (All / CS2 / Valorant / Apex / …)
2. Hero band — featured "Clip of the Day" (left, ~2/3) + ranked sidebar list 01–05 (right, 300px)
3. Game catalogue — section header "Top Games" + 5-up `GameCoverTile` grid
4. Trending band — section header "Trending" + featured trending clip (left) + ranked list 02–05 (right)
5. Clip grid — section header "Recent Clips" + 4-col `ClipCard` grid
6. Load more button

**Notes:**

- Hero clip shows game tag, title (Barlow Condensed 800 uppercase, 22–28px), author in mint, views + likes.
- Hero falls back to `items[0]` when the featured fetch fails — never blank.
- Ranked sidebar numbers: #1 in mint, #2–5 in muted. Barlow Condensed 900, 22px.
- Game filter pills are always visible — no extra click to filter. Active pill: mint bg/border/text.

---

### Clip Detail (`/clip/:id`, `/c/:code`)

The primary watch surface.

**Layout:**

1. Full-width video player (Plyr) inside a plain dark container — no BroadcastFrame corner brackets in the new system
2. Below player: game tag + title (Barlow Condensed 800 uppercase, 22px) + author handle (mint)
3. Action row: Like (heart, count), Share, Copy link, kebab menu (Edit / Delete / Report — visibility gated)
4. Stats strip: views · duration · upload date
5. Description (if present) — Inter 400 13px, text-secondary
6. Tags — `TagChip` row (mint border, mint text)
7. Comments section
8. Related clips — section header "More Clips" + 4-col `ClipCard` grid

**Notes:**

- Processing state: clip not yet ready shows a status panel ("Still processing…"), not a broken player.
- JIT transcode pending: distinct message ("Preparing for your device — try again in a moment").
- Like button: filled mint heart when liked, outline when not. Count inline.
- Share/copy toasts slide up from bottom, 250ms.

---

### Games Catalogue (`/games`)

The game browser — where users pick a game to explore.

**Layout:**

1. Page header — "Games" (Barlow Condensed 900 large) + subtitle with game count
2. 5-col `GameCoverTile` grid (all games with clips, sorted by clip count)
3. Below: section header "Latest Clips" + 4-col clip grid pulling from the cross-game feed

**Notes:**

- Clip count on each tile is derived from the loaded feed page (approximate). Exact count lives on the game detail page.
- Empty state if no games yet: status panel with copy.

---

### Game Detail (`/game/:slug`)

A game's dedicated page. Feels like a mini community hub for that title.

**Layout:**

1. Game hero — cover art (3:4, large, left ~200px) + game name (Barlow Condensed 900 large) + clip count + "Top game this week" label when applicable
2. Clip feed — tabs (Latest / Top Rated) + 4-col `ClipCard` grid for this game, infinite scroll
3. Game leaderboard — section header "Top Clippers" + ranked user list (avatar + username in mint + clip count + like count)

**Notes:**

- Cover art is always an `<img>` — never CSS background. `object-cover aspect-[3/4]`.
- 404 game → not-found state with back link, not a full page crash.

---

### Trending (`/trending`)

The ranking surface. Shows what's climbing right now.

**Layout:**

1. Page header — "Trending" + time window tabs (24h / This Week; 1h / Month / All Time are rendered but disabled until the API supports them)
2. Feature band — #1 clip hero (left, ~60%) + runner-ups #2–#4 vertical list (right)
3. Section header "Hot Games" + horizontal scrollable game tile strip (8 games)
4. Section header "Full Chart" + ranked list (clip title + rank movement indicator + views)

**Notes:**

- Time window tabs: disabled states use `opacity-40 cursor-not-allowed`, not hidden — shows users what's coming.
- Rank movement (↑38 spots): small mint text, not an arrow icon. Mint for up, muted for flat.

---

### Leaderboards (`/leaderboards`)

Who has the most likes in a given window.

**Layout:**

1. Page header — "Leaderboards" + time window tabs (This Week / This Month / All Time)
2. Two-column bands side by side: "Top Clippers" (ranked user list) + "Top Games" (ranked game list)
3. Each row: rank numeral (Barlow Condensed, muted, 22px; #1 mint) + avatar/cover + name + stat

**Notes:**

- Tabs switch both bands simultaneously — one API call covers both.
- Top Clippers: avatar (32px circle) + username in mint + like count.
- Top Games: cover tile (32px, 3:4) + game name + clip count.

---

### Search (`/search?q=`)

The discovery escape hatch. Clips + games in one response.

**Layout:**

1. Large search input at top of page (not just the nav bar) — auto-focused, `h-12 rounded-lg border border-border-strong`, mint focus ring
2. Results: "Clips" section (4-col `ClipCard` grid) + "Games" section (5-col `GameCoverTile` grid)
3. Empty state (no query): prompt copy — "Search for clips, games, or players"
4. Empty results: "No results for 'x'" with a suggestion to try a different term
5. Error state: status panel with retry

**Notes:**

- Results are fetched on `?q=` param change (not on keystroke) — nav bar handles the keystroke → push.
- Show section header only if that section has results (don't show "Games" header with 0 tiles).

---

### Reels (`/feed/reels`, `/feed/reels/:id`)

Full-screen vertical video. The short-form surface.

**Layout:**

- Full viewport, one clip at a time, no page chrome visible (nav hidden while in reels)
- Video fills height, letter-boxed if wider
- Bottom overlay (gradient `rgba(0,0,0,0.85)`): game tag (mint), title (Inter 600 14px), author in mint
- Right-side action column: like (heart + count), share, mute toggle
- Swipe up / arrow down to advance; swipe down / arrow up to go back
- Back button (top-left) returns to previous route

**Notes:**

- Starts muted — unmute button visible. Autoplays on entry.
- Preloads the next clip's detail while current is playing.
- BroadcastFrame is NOT used here — the full-screen frame itself is the container.

---

### Upload (`/upload`) — auth required

3-step wizard for submitting a clip.

**Layout — Step 1 (Ingest):**

- Toggle: "Upload file" / "Import URL" (tab-style toggle, not pills)
- File mode: drag-and-drop zone (dashed border, mint on drag-over) + file picker button
- Import mode: URL input (mint focus), allowed hosts listed below in muted text
- Forward button: "Next →" (primary CTA, disabled until valid)

**Layout — Step 2 (Metadata):**

- Title input (required)
- Game selector — searchable dropdown, shows cover art thumbnails in list
- Tags input — chip-style, mint chips, enter/comma to add
- Visibility toggle: Public / Unlisted
- Description textarea (optional)
- Back + "Upload" / "Import" CTA

**Layout — Step 3 (Processing):**

- Progress indicator: tick bar animation (mint), status copy ("Uploading…" → "Processing…" → "Ready!")
- On ready: clip thumbnail preview + "View clip" primary CTA + "Upload another" secondary

**Notes:**

- No BroadcastFrame on the upload page — that was a Newsprint pattern.
- Processing poll: updates status copy every 2.5s, max ~30s.

---

### User Profile (`/user/:username`)

A player's public page and the social anchor of the platform.

**Layout:**

1. Profile hero — avatar (80px circle, mint border) + display name (Barlow Condensed 800 24px) + @username (mint) + bio (Inter 400 13px, text-secondary)
2. Stats strip — Clips / Likes / Followers / Following (each: value in Barlow Condensed 700, label in Inter 10px muted caps)
3. Action row (when viewing another user): Follow / Unfollow primary CTA + Share icon button + kebab (Report)
4. Tabs — Clips / (future: Liked)
5. 4-col `ClipCard` grid, infinite scroll

**Notes:**

- Own profile: Edit Profile button instead of Follow.
- Follow button: solid mint when not following, outline secondary when following (hover: "Unfollow" copy).
- Stats strip is inline, values separated by `·` — no bordered cell treatment.

---

### Follow List (`/user/:username/followers`, `/user/:username/following`)

Who follows whom.

**Layout:**

1. Back link to profile
2. Page header — "Followers" or "Following" + username in mint
3. User list — rows: avatar (36px) + display name + @username (mint) + follow button (if not self)

---

### Notifications (`/notifications`) — auth required

Activity feed. Likes, follows, comments.

**Layout:**

1. Page header — "Notifications" + "Mark all read" button (secondary, right-aligned)
2. Notification rows — avatar + copy ("@user liked your clip 'X'") + relative timestamp (muted) + unread dot (mint, `rounded-full`)
3. Load more button at bottom

**Notes:**

- Unread rows: `bg-surface-high` background vs `bg-surface-raised` for read.
- Clicking a row marks it read and navigates (follow → user profile, like/comment → clip).

---

### Tag (`/tag/:slug`)

Clips filtered by a specific tag.

**Layout:**

1. Page header — "#tagname" (mint) + clip count
2. 4-col `ClipCard` grid, infinite scroll

---

### Settings (`/settings/password`) — auth required

Password change. Single-purpose page.

**Layout:**

1. Page header — "Settings"
2. Form: current password + new password + confirm — standard input style
3. Save button (primary CTA)

---

### Login (`/login`) / Register (`/register`)

Auth surfaces. Minimal — these are not the destination.

**Layout:**

1. Centered card (`max-w-sm`, `bg-surface-raised border border-border rounded-xl p-8`)
2. Logo wordmark at top
3. Form: email + password (+ username on register)
4. Primary CTA: "Sign in" / "Create account"
5. Google OAuth button (secondary, Google brand colors preserved)
6. Switch link: "Don't have an account? Register" / "Already have one? Sign in"

**Notes:**

- No hero image, no background texture — just the card on `bg-surface-base`.
- Error messages: inline below the relevant field, Inter 12px, mint-colored (not red — mint is the only accent).

---

### Admin (`/admin`) — moderator role required

Moderation queue. Internal tool feel, not polished for public consumption — but still follows the Arena token system.

**Layout:**

1. Page header — "Admin"
2. Tabs — Reports / Users (or whatever sections exist)
3. Report rows — clip thumbnail (small) + report reason + reporter + timestamp + action buttons (Dismiss / Remove)

---

### Not Found (`/:pathMatch(.*)`)

Centered, `min-h-screen flex items-center justify-center`. Large muted "404" in Barlow Condensed 900, subtitle copy, "Go home" primary CTA.

---

## 12 — Claude prompt usage

This document is the source of truth for all frontend implementation decisions. Before writing any Vue component, Tailwind class, or CSS rule:

1. Check the token table (Section 3) — use token names, not raw hex values in templates.
2. Check the component patterns (Section 6) — don't reinvent; extend existing patterns.
3. Check the banned list (Section 9) — run the sweep before any PR.
4. If something isn't covered here, default to: least visual noise, most content, mint only for interactive states.
