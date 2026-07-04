# Newsprint — GankedTV Design System v2

**Status:** design spec, awaiting implementation
**Replaces:** the "Underground Arena" system documented in [web/DESIGN.md](../../../web/DESIGN.md), specifically the multi-theme contract (`underground` / `tactical` / `arcade`) and the Tailwind-violet brand pairing.
**Out of scope:** product features. This spec only changes how the existing 19 views look and feel.

---

## 1 — Concept

**"Broadcast Almanac."** An editorial sports yearbook that knows how to render video. Each page reads like a printed issue — kicker labels, hairline rules, oversized condensed numerals, paragraph-numbered sections — but watch surfaces (clip detail, reels, live indicators) wear a broadcast HUD on top: corner brackets, telemetry strips, framed regions, mono-everywhere.

The site is an *archive* of gaming's loudest seconds. Every clip is filed under an issue number. Every page belongs to a volume.

### Why this direction

The current site reads as AI-generated for three concrete reasons: the violet brand + emerald accent pairing is the Tailwind/shadcn default; the card grid uses `translateY(-2px)` + soft glow on hover (the single most-AI move in dark UI); and the three-theme abstraction dilutes the brand into a configuration. Newsprint fixes all three. It commits to one palette referenced from print (paper cream + ink black + press red + royal blue), borrows its visual grammar from publications people have held in their hands, and replaces "card hover" with "border swap + title color shift."

---

## 2 — The anti-AI rules (load-bearing)

These are the gestures that, once applied consistently, prevent the system from drifting back into template territory. If any of them gets dropped during implementation, the system loses its distinctness.

1. **Every clip has an issue number** (`No. 042`). Shown top-left of every thumbnail, in the URL meta strip, repeated as a 50–96px condensed numeral in hero positions. The motif is the brand.
2. **Roman numerals on section kickers** (`II By Game`, `III Trending · 24h`, `IV The Feed`). The page reads as an *issue*, not a *feed*.
3. **Hairline rules instead of cards-with-shadows.** Borders define regions. No drop shadows. No `box-shadow` glow. No `backdrop-blur` glassmorphism.
4. **Oversized condensed numerals as a recurring motif** (Barlow Condensed 700, 32–96px). Used for issue numbers, hero stats, list ranks, telemetry values.
5. **Telemetry strips** for stats: a row of cells, each with a `font-mono 9px uppercase` kicker label above a `Barlow Condensed 700 26px` value. Reused on clip detail, profile header, game pages.
6. **Corner brackets** (`4 × 14px L-shape strokes in --color-ink`) appear only on watch surfaces (the clip player frame, reels viewer, live indicators). They earn meaning by not being everywhere.
7. **No `translateY(-2px)` hover, ever.** Hover = border color swap to `--color-ink` + title color shift to `--color-ink`. Cards stay put.
8. **Tabs as text rules, not pills.** Underline-only, `font-mono 11px caps`, 2px `--color-ink` underline on active. No rounded backgrounds. No fills.
9. **No rounded corners ≥ 8px** anywhere. `--radius-sm: 0` (sharp), `--radius-md: 2px` (very subtle softening on inputs only). The chamfer system from v1 is removed; clip-paths are not used.
10. **Footer as colophon, not sitemap dump.** Three to four column groups plus a brand line and a bottom rule that reads like a publication signoff (`VOL 1 · ISS 042 · ARCHIVE STATUS · NOMINAL · COMPILED IN 0.04s`).

---

## 3 — Direction system (which surface leads with what)

The system has two voices. Most pages lean toward one; some pages blend both. The rule is **content type → voice**:

| Page | Voice | Why |
|---|---|---|
| HomeView, TrendingView, GamesView, SearchView, TagView, UserFollowListView, NotificationsView | **Editorial** (A) | Browsing. Whitespace + hierarchy via size carry attention across many items. |
| ClipView, ReelsView, UploadView (during transcode), Live indicators | **Broadcast** (B) | Watching / processing. Corner brackets and telemetry communicate "this is a live surface." |
| UserView, GameView, LeaderboardsView | **Hybrid** | Editorial layout (header + kicker + grid) with a Broadcast telemetry strip for the numbers row. |
| Auth (LoginView, RegisterView, AuthCallbackView), Settings, Admin | **Editorial, restrained** | No telemetry. No corner brackets. Sub-issue forms — minimal chrome. |

### Editorial voice

- Page top: `kicker + Vol/Iss meta row` → `hairline rule` → `hero numeral + hero title + hero thumbnail`
- Body: stacked **bands**, each with a section header (kicker with Roman numeral + oversized condensed title + optional blurb + hairline rule) and a layout chosen from the band library (see §6).
- Footer: colophon (§7).

### Broadcast voice

- Outer frame: 14px inset border (`1px solid --color-ink at 0.35 alpha`) + 4 corner brackets in `--color-ink` (full alpha).
- Topbar: mono caps row (left: channel/feed ID, center: status, right: spec / quality).
- Below the video: a 4-cell **telemetry strip** showing the page's key stats.
- Meta block under the strip: title in Barlow Condensed 700 uppercase, byline in mono with `@username` colored in `--color-ink`.

### Hybrid voice

- Editorial header (avatar + name in Barlow Condensed 52px + handle in `--color-ink` mono).
- Followed by a 4-cell telemetry strip (Total views, Followers, Avg clip, Top game).
- Editorial tabs (text rules) below.
- Editorial grid of clip cards.

---

## 4 — Palette (Newsprint)

The whole system runs on **one palette with dark + light modes.** The existing 3-theme contract (underground/tactical/arcade) is removed. `useThemeStore` collapses to a single `mode: 'dark' | 'light'` boolean.

### Tokens

| Token | Dark | Light | Used for |
|---|---|---|---|
| `--color-surface-base` | `#161410` | `#f4f1e8` | page background |
| `--color-surface-raised` | `#221e16` | `#ece8da` | nested panels, table headers, hover backgrounds (rare) |
| `--color-surface-sunken` | `#0c0a08` | `#d8d3c4` | video player background, deep recess |
| `--color-text-primary` | `#f4f1e8` | `#1a1810` | body text, headings |
| `--color-text-secondary` | `#c5bca7` | `#5a5444` | byline, captions, kicker labels |
| `--color-text-muted` | `#8a8474` | `#6b6553` | timestamps, placeholders, disabled |
| `--color-border` | `#2c2820` | `#1a1810` | default borders, hairline rules |
| `--color-border-strong` | `#3c3a30` | `#1a1810` | emphasized borders (active tab underline base) |
| `--color-ink` | `#ed3a47` | `#c41825` | **brand.** Issue numbers, @usernames, links, hover borders, active nav underline, telemetry value accent, game tags |
| `--color-signal` | `#6c9bcf` | `#3a6fb5` | **alert / live.** LIVE badge, error states, "new" indicators, breaking news strips |
| `--color-signal-text` | `#161410` | `#f4f1e8` | text color on solid signal-fill badges |

### Usage rules

- **Ink is the brand color.** Use freely — issue numbers, byline @usernames, link text, active nav underline, hover border, hover title, hover thumbnail border. Should be visible but not loud at small sizes.
- **Signal is for earned moments only.** LIVE indicators, error toasts, "new" badges, breaking-news strips. Never used in body text, never as a hover state. If you find yourself reaching for signal to differentiate two non-urgent items, use weight or position instead.
- **Two badge styles, one color.** Solid-fill in `--color-ink` for primary badges (LIVE on watch, game-tag on thumbs). Outline-only in `--color-ink` for secondary (PINNED, NEW status, filter chips). The two styles differentiate without needing a third color.
- **No `box-shadow`.** Use `border` + `--color-surface-raised` for depth.
- **No gradients on UI surfaces.** Hero/thumbnail placeholders may use a flat 2-color linear gradient (`#221e16 → #0c0a08`) as a render-failure fallback only.

### Ambient atmosphere

Replace the body's `::before` (radial purple glow) and `::after` (noise + scanlines) layers with:

- `body::before` — a hairline `1px solid --color-border` at `top: 64px` (just below the sticky nav), full-viewport-width. Reads as a printed top-of-fold rule. Pointer-events none, z-index 1.
- `body::after` — a very subtle SVG fractal noise at `opacity: 0.025` (dark) / `0.018` (light), mixed via `overlay`. Just enough to take the digital sheen off.

No scanlines. No radial glows. No animated particles.

---

## 5 — Typography

Keep the four-font stack from v1 — it's already on-brief for editorial. The role assignments change slightly.

| Role | Font | Weights | Notes |
|---|---|---|---|
| Logo / display | `Rajdhani` | 600, 700 | "GANKED.TV" wordmark only |
| Headings / numerals | `Barlow Condensed` | 500, 700 | Page titles, section titles, issue numbers, hero numerals, telemetry values, clip titles |
| UI / body | `DM Sans` | 400, 500 | Body copy, button labels, blurbs, paragraph text |
| Metadata / mono | `DM Mono` | 400, 500 | Kickers, timestamps, @usernames, IDs, badge text, telemetry kicker labels, nav meta strip |

### Type scale (responsive)

| Use | Size | Weight | Tracking | Case |
|---|---|---|---|---|
| Hero numeral | `clamp(56px, 8vw, 96px)` | 700 | `-0.01em` | — |
| Hero title | `clamp(28px, 3.5vw, 44px)` | 700 | `0` | UPPERCASE |
| Section title | `clamp(28px, 3vw, 38px)` | 700 | `0` | UPPERCASE |
| Card title | `16px` | 500 | `0` | UPPERCASE |
| List rank numeral | `28px` | 700 | `0` | — |
| Telemetry value | `26px` | 700 | `0` | — |
| Body | `15px` (desktop), `14px` (mobile) | 400 | `0` | sentence |
| Kicker | `10px` | 500 | `0.22em` | UPPERCASE |
| Mono meta | `11px` | 400 | `0.12em` | UPPERCASE |
| Badge | `10px` (solid), `10px` (outline) | 700 | `0.15em` | UPPERCASE |

### Editorial typography rules

- Page max-width: `1440px`. Reading column max-width: `64ch` (for any prose section over a paragraph long).
- Line-height: `1.05` for Barlow Condensed display sizes; `1.55` for DM Sans body; `1` for mono caps strips.
- Letter-spacing: tighter (`-0.005em` to `-0.01em`) on Barlow Condensed at hero sizes; wider (`0.12em–0.25em`) on mono kickers.
- **No font-weight as the primary hierarchy lever.** Use size + position + case. Weight is a tiebreaker.

---

## 6 — Layout rhythm

### Page shell

- Sticky nav: `64px` tall, `1px solid --color-border` bottom rule. No padding-top compensation on content.
- Content padding: `40px 32px 120px` (desktop), `20px 16px 80px` (mobile).
- Vertical rhythm between bands: `40px` (section padding-top) + `1px` hairline + `24px` (content start). Don't shortcut this — air is the editorial feel.

### Section header (universal pattern)

Every content band uses the same header structure:

```
[ kicker row:  ROMAN  ·  Section name        |        more →  ]
[ section title — Barlow Condensed 700 38px uppercase           ]
[ optional blurb — DM Sans 13px secondary, max-width 56ch       ]
[ ──────────── hairline rule across band width ───────────────  ]
[ band content                                                  ]
```

- `ROMAN` numeral in `--color-ink`, mono caps, `0.22em` tracking, `mr: 8px`.
- "more →" link in `--color-text-secondary`, mono caps. Right-aligned.

### Band library (pick one per section)

The variety comes from layout, not header style. Mix these freely as long as the section header is consistent.

1. **Hero + List** (Home: Clip of the Day + Latest Drops). 2-column grid, hero takes 2fr, list takes 1fr. List items are ranked rows (`28px numeral | title + handle | stat`).
2. **Game Tiles** (Home: By Game). 5-column grid of 3:4 vertical tiles. Each tile: top row (game tag + rank numeral), bottom row (game name in Barlow Condensed 22–26px, count below in mono).
3. **Feature + List** (Trending). 1.6fr (one big editorial feature: oversized numeral + 16:9 thumb + title + byline) + 1fr (four list rows with small 16:9 thumbs).
4. **4-up Grid** (The Feed). 4-column responsive grid (`grid-template-columns: repeat(auto-fill, minmax(280px, 1fr))`). The workhorse band.
5. **Telemetry Strip** (profile / game stats). 4–6 cell horizontal row with `1px solid --color-border` between cells. Top of cell: mono kicker. Bottom: condensed numeral value.
6. **Editorial Feature** (single highlighted clip, used sparingly). Full-width thumb + 18ch hero title + extended byline + tags strip below.
7. **Broadcast Frame** (clip detail). The B-voice container — corner brackets + topbar + framed video + telemetry strip + meta block.

A typical page uses 3–4 bands. Home uses Hero+List → Game Tiles → Feature+List → 4-up Grid. Profile uses Telemetry Strip → 4-up Grid.

### Page max-widths and gutters

- `1440px` outer max-width across all pages.
- Band gutter (between sections): `40px` top padding inside the band.
- Within-band grid gap: `28px` vertical, `22px` horizontal (for grid bands).

---

## 7 — Atoms

### Nav (`AppNav.vue` — refresh)

Layout: `[logo mark] [GANKED.TV wordmark] [Feed] [Games] [Trending] [search bar ≥1281px] [icon buttons] [upload] [avatar] [Vol/Iss meta]`

- Height: `64px`. Sticky. Bottom: `1px solid --color-border`. No padding-top on body.
- Logo: Rajdhani 700, 16px, "GANKED" in `--color-text-primary`, ".TV" in `--color-ink`. Drop the polygon clip-path mark. Replace with **a single 8×8 square in `--color-ink`, left of the wordmark, vertically centered.** A simpler mark for a simpler system.
- Nav links: DM Mono 11px, `0.12em` tracking, uppercase. Active link: text in `--color-text-primary` + `2px solid --color-ink` underline (`padding-bottom: 6px`, `margin-bottom: -8px` to overlap the nav bottom rule). Inactive: `--color-text-secondary`. Hover: text to `--color-ink`.
- Right of the search bar: a **meta strip** — `font-mono 10px 0.15em uppercase --color-text-muted`, content `VOL 1 · ISS 042 · 06.08.26`. Auto-generated from the current date and an incrementing daily counter. Hidden below 1100px.

### ClipCard (`ClipCard.vue` — rebuild)

```
[ thumbnail  16:9  ─────────────────────── ]
[   No. 042         (issue number, top-left, Barlow Condensed 24px ink, opacity .85)
[   VAL         0:09 ]   (bottom: game tag in solid --color-ink, duration in rgba(0,0,0,.55))
[ title — Barlow Condensed 500 16px uppercase, line-clamp 2, min-height 36px ]
[ @author          1.2M · 94K ♥  ]   (mono 10px, @author in --color-ink, stats right-aligned)
```

- No border-radius on thumbnail. Thumbnail border: `1px solid --color-border`.
- Hover: thumbnail border → `--color-ink`, title color → `--color-ink`. **No transform. No shadow. No glow.**
- Click target: the entire card. Cursor: pointer.
- Card has **no outer background.** It sits directly on `--color-surface-base`.

### Game card (`GameCoverTile.vue` — refresh)

3:4 vertical tile. `1px solid --color-border`. Padding: `14px`. Two-row flex: top row (game abbrev tag + rank numeral right-aligned), bottom anchored (game name in Barlow Condensed 22px, optional sub-stat in mono). Hover: border → `--color-ink`.

### Section header (new — extract from inline patterns)

A reusable `SectionHeader.vue` component. Props: `roman: string`, `kicker: string`, `title: string`, `blurb?: string`, `moreHref?: string`. Renders the universal pattern from §6.

### Telemetry strip (new — `TelemetryStrip.vue`)

A reusable horizontal row of telemetry cells. Props: `cells: Array<{ label: string; value: string }>`. Cells separated by `1px solid --color-border`. Per cell: kicker label on top, condensed numeral value below. Used on ClipView, UserView, GameView.

### Broadcast frame (new — `BroadcastFrame.vue`)

The B-voice container. Props: `channelId: string`, `status: string`, `spec: string`. Slot for video. Renders the inset border + 4 corner brackets + topbar above the slot.

### Badges (utility classes)

- `.badge--ink-solid` — solid `--color-ink` bg, `--color-signal-text` text. Primary urgency (LIVE, game tag on thumb).
- `.badge--ink-outline` — transparent bg, `--color-ink` text + border. Secondary state (PINNED, NEW, filter chip).
- `.badge--signal` — solid `--color-signal` bg, `--color-signal-text` text. Earned moments only.

### Tabs (`UnderlineTabs.vue` — refresh)

Already underline-based; refresh the active style: `2px solid --color-ink` (was brand-light). Mono 11px 0.15em uppercase. No background on active. Hover: text to `--color-ink`.

### Buttons

- Primary: solid `--color-ink` background, `--color-signal-text` text. DM Sans 500 14px. Padding `10px 18px`. No border-radius (corners are sharp at `--radius-sm: 0`). Hover: filter `brightness(1.08)`.
- Secondary: transparent bg, `1px solid --color-border` border, text in `--color-text-primary`. Hover: border to `--color-ink`, text to `--color-ink`.
- Tertiary / link: text in `--color-ink`. Underline on hover.
- **No icon-only round buttons.** Square or pill — square is preferred.

### Footer (new — `AppFooter.vue`)

A 5-column grid colophon: `[brand block] [The Site] [Account] [Off-site] [Boring]`, all on `--color-surface-base` with a top `1px solid --color-border`. Below: a thin row with mono caps left (`VOL 1 · ISS 042 · ARCHIVE STATUS · NOMINAL`) and right (`© 2026 GANKED.TV · COMPILED IN 0.04s`). The `0.04s` line is a wink — it's a real number (server-side render time, gauge).

---

## 8 — Motion

- **Page transitions:** keep the existing 150ms opacity fade.
- **Hover (cards, buttons):** 150ms transition on `color`, `border-color`. **Not on `transform` — there is no transform.**
- **Live indicator pulse:** 2s `opacity 1 → 0.3 → 1` on the LIVE dot only. Same as v1.
- **Toast (notifications):** 300ms `slideUp` in, 300ms `slideDown` out after 2.2s.
- **Loading state:** replace generic spinners with a single 6×22px `--color-ink` bar that pulses width `0% → 100% → 0%` over 1.6s. Reads as a "ticker." Used on the LoadMoreButton and any in-flight state.
- **No backdrop-blur. No glassmorphism. No animated gradients.**

---

## 9 — Per-page application

For each view, the voice and band sequence. Implementation detail (data flow, props, store binding) is unchanged — only visual/layout changes.

### HomeView (Editorial)

- Hero band: **Hero + List** — Clip of the Day (oversized numeral + title + thumbnail + byline) on left 2fr, "Latest Drops" ranked list on right 1fr.
- Band II — **Game Tiles** ("By Game · Where the bracket lives this week").
- Band III — **Feature + List** ("Trending · 24h · What climbed the chart overnight").
- Band IV — **4-up Grid** ("The Feed · Everything else, freshest first").
- Footer: colophon.

### ClipView (Broadcast)

- Above the fold: Broadcast Frame around the player. Topbar shows `FEED · MM:SS | LIVE FROM ARCHIVE | 1080p · AV1`. Telemetry strip below player (Views, Likes, Shares, Filed date). Title + byline meta block.
- Below: editorial bands — **Section II · Recommended** (4-up grid) and **Section III · Comments** (single column).

### UserView (Hybrid)

- Editorial header: avatar block left, name (Barlow Condensed 52px) center, since-line in mono. No telemetry yet.
- Telemetry strip: 4 cells (Total views, Followers, Avg clip, Top game).
- Tabs: Clips / Liked / Reels / About (UnderlineTabs).
- Grid: 3-column editorial grid (denser than Home's 4-up), each item has rank numeral + thumb + title + stat.

### GameView (Hybrid)

- Editorial header: game cover (3:4 tile on left, 100px) + game name (Barlow Condensed 52px) + meta (filed-since, total clips).
- Telemetry strip: 4 cells (Clips, Top creator, This week, All-time).
- Embedded `GameLeaderboardBlock` redesigned to use the band II/III patterns.
- 4-up grid of clips below.

### GamesView (Editorial)

- Band: **Game Tiles** at scale — 5×N grid. Each tile gets a 3:4 cover, abbreviation tag, name, count. No hero.

### TrendingView (Editorial)

- Band I — **Feature + List** (top mover + 4 runner-ups).
- Band II — **4-up Grid** (the long tail).

### LeaderboardsView (Hybrid)

- Telemetry strip header (Total clips, Total creators, Total views, Updated).
- Tabs by game.
- List rows (rank numeral + creator avatar + handle + stat numeral).

### ReelsView (Broadcast)

- Full-bleed broadcast frame around the video. Corner brackets + side-rail meta (mono caps, vertical) + bottom telemetry strip. Swipe / arrow-key navigation unchanged.

### SearchView, TagView (Editorial)

- Title: query echoed in Barlow Condensed 38px uppercase. Mono meta below ("123 results · sorted by relevance").
- 4-up grid.

### NotificationsView (Editorial, restrained)

- Single column list. Each row: kicker (mono "NEW" outline badge if unread) + body (DM Sans) + timestamp (mono right-aligned). Hairline between rows. No card chrome.

### UserFollowListView (Editorial, restrained)

- Tabs (Followers / Following) at top.
- Single column list of users — avatar + handle + small stat + follow button. Hairline between rows.

### UploadView (Broadcast during processing)

- Form sections are editorial (kicker + title + form fields).
- **During transcode**, the preview card adopts the Broadcast Frame treatment with a live progress meter (replace the loading bar — see §8 motion).

### Auth views (LoginView, RegisterView, AuthCallbackView)

- Centered form card on `--color-surface-base`. `1px solid --color-border`. Editorial header: kicker (`SIGN IN` / `JOIN THE ARCHIVE`) + title (Barlow Condensed) + brief blurb.
- Form fields are tall (`44px`), thin border, sharp corners. Submit button is primary.
- No social-login icon clutter; if OAuth providers exist, they're text-link rows below the form.

### SettingsPasswordView, AdminView

- Editorial header + form sections separated by hairline rules. No card chrome on individual sections. Mono kickers above each section.

### NotFoundView

- Single editorial hero: `404` as a 240px Barlow Condensed numeral in `--color-ink`. Title + blurb + a "back to feed" text link. No illustration. The number is the illustration.

---

## 10 — What gets removed

This system is partly defined by what it deletes.

- **The three-theme system.** `useThemeStore.themeName`, `ThemePicker.vue`, the `data-theme` attribute on `<html>`, and the `[data-theme="…"]` blocks in `base.css`. Replaced by `useThemeStore.mode: 'dark' | 'light'`, written as `class="light"` on `<html>`. The settings UI loses the theme picker (keep the dark/light toggle).
- **`.chamfer` utility class + `--corner-cut` token + all polygon `clip-path` styling.** Corners are sharp.
- **Logo mark polygon** (`.logo__mark` ::before/::after rules in `base.css`). Replaced by a single 8×8 `--color-ink` square.
- **`--color-brand`, `--color-brand-light`, `--color-brand-glow`, `--color-neon`, `--color-neon-dim` tokens.** Replaced by `--color-ink`, `--color-signal`, `--color-signal-text`.
- **All `box-shadow` values on cards and panels.** Borders replace them.
- **The `body::before` purple radial glow + `body::after` scanlines.** Replaced by the hairline + minimal noise (§4).
- **All `translateY(-2px)` hover transforms.** Hover is color-only.
- **The `--feed-gap` token** if it's used inconsistently — standardize on the §6 grid gaps.

---

## 11 — Implementation notes (non-binding)

- The change is large enough to warrant a separate PR per major page group: (a) tokens + atoms + nav + footer + base.css, (b) Home + ClipCard, (c) Clip + Reels (Broadcast voice), (d) User + Game + Leaderboards (Hybrid), (e) remaining editorial views, (f) cleanup (theme system removal, dead-token sweep).
- The tokens-and-atoms PR should be entirely additive at first — introduce `--color-ink`, `--color-signal`, `SectionHeader`, `TelemetryStrip`, `BroadcastFrame`, `AppFooter` — without breaking the existing Underground tokens. The page-by-page PRs migrate consumers. The cleanup PR deletes the dead tokens last.
- **DESIGN.md should be rewritten** to reflect this spec (not just patched). The current file documents the multi-theme system in detail — most of it becomes obsolete.
- **Tailwind `@theme {}` block in `base.css` is the source of truth for token → utility mapping.** Add `bg-ink`, `text-ink`, `border-ink`, `bg-signal`, `text-signal`, `border-signal` and remove the brand/neon equivalents.
- **Accessibility check:** the `#3a6fb5` light-mode signal against cream `#f4f1e8` text on solid-fill badges sits comfortably above WCAG AA (~5.3:1). Document this contrast pair in `DESIGN.md`.
- **Tests:** the existing snapshot tests on ClipCard, AppNav, and the page views will all fail after this redesign — expected. Regenerate them. The auth/network/routing coverage gate (`src/api/**`, `src/router/**`, `src/stores/**`) is unaffected.

---

## 12 — Open implementation questions (for the planning phase)

- Per-band background variation: should every band sit flat on `--color-surface-base`, or should specific bands (e.g. the Hero, the Trending feature) use `--color-surface-raised` for a subtle separation? Current spec assumes flat; revisit during the Home PR.
- Issue number generation: where does "No. 042" come from? Options: (a) the clip's daily ordinal (1st, 2nd, …), (b) a sequence derived from `clip_id` modulo, (c) a true incrementing column in the DB. Visual works regardless; data choice matters for canonical URLs.
- Theme picker removal — graceful migration: users currently on `tactical` or `arcade` should be auto-migrated to Newsprint dark on first load, with a one-time toast explaining the change. Or skipped silently. Pick one before shipping.
- The DM Mono / DM Sans / Barlow Condensed / Rajdhani Google Fonts load is already in place; verify the new system doesn't add any new weights (it doesn't).
