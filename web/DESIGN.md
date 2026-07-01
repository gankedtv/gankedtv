# GankedTV Design System — Arena

**Status:** Shipped. This file is the source of truth for all frontend work.
**Spec:** [docs/superpowers/specs/2026-06-29-arena-design-system-design.md](../docs/superpowers/specs/2026-06-29-arena-design-system-design.md)
**Replaces:** Newsprint ("Broadcast Almanac") v2.

---

## 1 — Concept

A premium gaming clip platform. The UI is confident but invisible — one accent color, clean surfaces, no decorative metaphors. Content (clips, game covers) does the talking.

Two modes with genuinely different personalities:

- **Dark (primary):** Late-night LAN. Near-black, Neon Mint accent, high contrast.
- **Light (secondary):** Game day. Warm cream page, white cards, deeper mint for contrast. Not an inversion — a different mood.

Reference: Medal.tv as the floor. Beat it in aesthetics, navigation clarity, discovery, and search.

---

## 2 — The rules (load-bearing)

Dropping any of these lets the system drift back toward generic:

1. **One accent, everywhere.** Mint owns every interactive state: active nav links, CTA buttons, game tags, author handles, section labels, active tab underlines, filter pill borders. No second accent — errors, danger, and "new" states are also mint; copy and weight carry the meaning.
2. **Borders, not shadows.** Depth is surface color difference (`surface-base` → `surface-raised` → `surface-high`). No `box-shadow`, no `shadow-*` utilities — ever.
3. **No hover transforms.** Hover = border color shift + text color shift. Cards stay put. Sole exception: game cover tiles get `-translate-y-0.5` — earned because the catalogue is browsable, not a feed.
4. **Plain section names.** "Top Games", "Trending", "Recent Clips". No editorial kicker copy, no roman numerals, no issue framing.
5. **No issue numbers anywhere.** Ranking is plain numerals (`01`, `02`, …) in condensed type — not a publishing metaphor.
6. **8px card radius.** Cards/nav/popovers/buttons are `rounded-lg` (8px). Inputs `rounded-md` (6px). Tiny badges `rounded-sm` (4px). Avatars/pills/dots `rounded-full`. Nothing sharp, nothing above `rounded-lg`.
7. **No UI gradients.** The sanctioned exceptions: the legibility overlay on video thumbnails/reels (`bg-[linear-gradient(transparent,rgba(0,0,0,0.85–0.88))]`) and the logo mark's internal SVG gradients + glow (`LogoMark.vue` — brand art, not UI).
8. **Backdrop blur on the nav only.** `backdrop-blur-md` lives in `AppNav.vue` and nowhere else.
9. **Tabs as underlines, not pills.** Feed/window tabs use a 2px mint bottom border on active (`UnderlineTabs.vue`). Filter pills (game filters, tag chips) are the only pill shapes.
10. **Mint deepens in light mode.** Light accent is `#00b87d` (WCAG AA on cream) — the token handles it; never hardcode `#00e5a0` on light surfaces.

---

## 3 — Tokens

One palette, dark (default) + light via `.light` on `<html>` (persisted by `useThemeStore`, `localStorage['theme']`). Defined in the `@theme` block of [src/assets/base.css](src/assets/base.css).

| Token | Dark | Light | Role |
|---|---|---|---|
| `--color-surface-base` | `#0b0b0f` | `#f7f5f0` | Page background |
| `--color-surface-raised` | `#111116` | `#ffffff` | Cards, nav |
| `--color-surface-high` | `#18181f` | `#f0ece3` | Inputs, hover rows, unread rows |
| `--color-text-primary` | `#f0f0f4` | `#1a1a22` | Headings, body, card titles |
| `--color-text-secondary` | `rgba(255,255,255,0.50)` | `#888070` | Bylines, meta |
| `--color-text-muted` | `rgba(255,255,255,0.28)` | `#b0a898` | Timestamps, placeholders, rank numerals |
| `--color-border` | `rgba(255,255,255,0.07)` | `#e8e4dc` | Default borders, dividers |
| `--color-border-strong` | `rgba(255,255,255,0.12)` | `#d0ccc0` | Focused inputs, popovers, card hover |
| `--color-accent` | `#00e5a0` | `#00b87d` | Mint — all interactive states |
| `--color-accent-bg` | `rgba(0,229,160,0.08)` | `#e8faf4` | Tag fills, active pill background |
| `--color-accent-border` | `rgba(0,229,160,0.25)` | `#b3ead7` | Tag borders, focus accents |

Usage rules:

- **Text over video** (duration badges, overlay titles, reels controls) uses literals: `#f4f1e8`, `text-[#f4f1e8]/80`, `bg-black/55–75`, `border-white/20–30` — never tokens; it must stay light in both modes. Same for text on mint CTAs: `text-[#080f0d]`.
- **Video letterbox/player backgrounds** are `bg-black` (literal); dialog scrims are `bg-black/70`.
- **Vendor colors** (`--color-discord`, `--color-google` + hovers) are identity-locked.

---

## 4 — Typography

Two fonts (Google Fonts, loaded in `index.html`). No others.

| Role | Font | Weights | Used for |
|---|---|---|---|
| `font-condensed` | Barlow Condensed | 700 / 800 / 900 | Wordmark, page/section titles, hero titles, rank numerals, stat values |
| default (Inter) | Inter | 400–700 | Everything else — no font class needed |

Scale highlights: wordmark 18px/900 caps; page title `clamp(30px,3.6vw,42px)` 900 caps; section title 20px/800 caps; hero overlay title `clamp(22px,2.6vw,34px)` 900 caps; card title 12px Inter 600 sentence case (2-line clamp); kicker 10px Inter 700 caps `tracking-[0.14em]`; meta 10–11px Inter 400–500 sentence case; rank numeral 22px condensed 900 (#1 mint, rest muted); button label 12px Inter 600–700 sentence case.

---

## 5 — Layout rhythm

- **Nav** ([AppNav.vue](src/components/AppNav.vue)): 56px sticky (`h-14`), `bg-surface-raised/90 backdrop-blur-md`, hairline bottom border. Logo + links (active = mint pill highlight `bg-accent-bg`), centered search (max 300px, `⌘K` chip, global ⌘K/Ctrl+K focuses it), mint Upload CTA, bell + mint badge, 30px avatar with mint border.
- **Page shell:** `mx-auto max-w-300 px-7 pt-7 pb-16 max-tablet:px-4`.
- **Band separation:** `mt-8 border-t border-border pt-7` on each section after the first.
- **Section header** ([SectionHeader.vue](src/components/SectionHeader.vue)): `{ kicker, title, moreTo?, moreLabel? }` → mint kicker + condensed title on one baseline + `ml-auto` "See all →".
- **Home band order:** feed controls (tabs + game pills) → hero band `grid-cols-[1fr_300px] gap-5` (overlay hero + ranked 01–05 sidebar) → Top Games `grid-cols-5 gap-3` → Trending `grid-cols-[1fr_280px]` → Recent Clips `grid-cols-4 gap-3.5` → Load more.
- **Grid gaps:** `gap-3` game tiles, `gap-3.5` clip cards.
- **Mobile:** `MobileTabBar` fixed bottom (Feed / Games / Upload / Reels / You), active item mint, center mint upload button. Nav collapses; feeds go 2-col → 1-col (`max-lg` / `max-tablet`).

---

## 6 — Component recipes

- **Clip card** ([ClipCard.vue](src/components/ClipCard.vue)): `rounded-lg border border-border bg-surface-raised overflow-hidden hover:border-border-strong`. Thumb `aspect-video bg-black` with GameTag top-left + DurationBadge bottom-right. Below: Inter 600 12px title (2-line clamp) + meta (`@author` mint · time · views/likes right). No tag chips on cards — tags live on detail surfaces.
- **Hero (Home):** full-bleed 16:9 with legibility gradient, mint kicker ("Clip of the Day"), condensed 900 overlay title in `#f4f1e8`, meta row, centered play button (`rounded-full bg-black/55 border-white/30`).
- **Ranked lists:** `grid-cols-[36px_56px_1fr]` (rank / 16:9 thumb / title+meta), rank `font-condensed text-[22px] font-black`, #1 `text-accent`, rest `text-text-muted`, zero-padded.
- **Game tile** ([GameCoverTile.vue](src/components/GameCoverTile.vue)): `aspect-3/4 rounded-lg border border-border`, optional rank numeral on cover, name below (Inter 700 11px) + `#footer-extra` for clip counts. `group-hover:-translate-y-0.5 group-hover:border-accent-border` — the sole transform.
- **Tabs** ([UnderlineTabs.vue](src/components/UnderlineTabs.vue)): `px-4 py-2.5 text-xs font-semibold border-b-2`; active `border-accent text-text-primary`; `disabled: true` renders `opacity-40 cursor-not-allowed` (never hidden — shows what's coming).
- **Filter pills:** `rounded-full border px-3 py-1 text-[11px] font-semibold`; active `bg-accent-bg border-accent-border text-accent`; idle `border-border text-text-muted hover:border-accent-border hover:text-accent`.
- **Buttons:** primary `rounded-lg bg-accent px-4 py-1.5 text-xs font-bold text-[#080f0d] hover:brightness-105`; secondary `rounded-lg border border-border-strong text-text-secondary hover:border-accent hover:text-accent`; ghost `text-accent hover:underline`; icon `size-8.5 rounded-lg border border-border hover:border-border-strong`.
- **Inputs:** `rounded-md border border-border bg-surface-high px-3 text-sm focus:border-accent focus:outline-none`; labels Inter 10px 700 caps `tracking-widest text-text-secondary`. Errors: mint text / `border-accent-border bg-accent-bg` boxes.
- **Dialogs:** scrim `bg-black/70`; panel `rounded-lg border border-border-strong bg-surface-raised`. Popovers/menus: `rounded-lg border border-border-strong bg-surface-base`.
- **Toasts:** `rounded-lg border border-border-strong bg-surface-raised`, `slideUp`/`slideDown` keyframes (250ms).
- **Status states** ([StatusPanel.vue](src/components/StatusPanel.vue)): loading = mint tick bar; empty/error = raised card with kicker + copy.
- **Logo** ([LogoMark.vue](src/components/LogoMark.vue)): mint HUD-frame SVG mark; wordmark `GANKED.TV` condensed 900 caps, `.TV` mint. Nav usage: `:size="23" glow`.

---

## 7 — Motion

- Page transitions: 150ms opacity fade (App.vue).
- Border/text hovers: 150ms `transition-colors`.
- Game tile lift: 150ms `transition-[border-color,transform]`.
- Mint CTAs: `hover:brightness-105` on `transition-[filter]`.
- Toasts: 250ms `slideUp` in, `slideDown` out. Loading bars: `tick` keyframe.
- No `transition-transform` on clip cards — they stay put.

---

## 8 — Data honesty (missing-API policy)

What the API doesn't serve is **not rendered** — no mock numbers:

- No "players online" count (nav) and no "follows online" panel (hero) until a presence endpoint exists.
- Home "Top Rated" tab renders disabled until a likes-weighted feed sort exists.
- "For You" maps to the latest feed until personalization exists.
- Home game filter pills deep-link to `/game/:slug` until the feed API grows a `gameId` param.
- No rank-movement copy ("up 38 spots") — the API has no rank history.

Backend follow-ups are tracked as GitHub issues (online presence, top-rated sort, for-you feed, feed game filter).

---

## 9 — Banned list + sweep

Run before any PR:

```bash
cd web/src
grep -rnE "shadow-\[|shadow-(sm|md|lg|xl|2xl)" . --include="*.vue"          # zero
grep -rn  "backdrop-blur" . --include="*.vue"                               # AppNav only
grep -rnE "hover:(-)?translate|hover:scale|group-hover:(-)?translate" . --include="*.vue"  # GameCoverTile only
grep -rnE "rounded-(xl|2xl|3xl)" . --include="*.vue"                        # zero
grep -rn  "gradient" . --include="*.vue" --include="*.css"                  # legibility overlays + LogoMark only
grep -rniE "No\.\s*\{|issue.number|vol.*iss" . --include="*.vue" --include="*.ts"  # zero
grep -rnE "font-display|font-mono|font-heading|Rajdhani|DM Mono|DM Sans" . --include="*.vue" --include="*.css"  # zero
grep -rnE "text-ink|bg-ink|border-ink|-signal|surface-sunken" . --include="*.vue" --include="*.ts"  # zero
```

Sanctioned exceptions (the only allowed hits): nav backdrop-blur, game-tile lift, thumbnail/reels legibility gradients, LogoMark SVG gradients + glow, and the Plyr menu `box-shadow: none` override in base.css (it *removes* a vendor shadow).

---

## 10 — Component conventions

- Tailwind utility classes in templates; tokens via `@theme`. Scoped CSS only when utilities can't express it (custom keyframes live in base.css).
- Dynamic values (user accent, avatar fills) via inline `:style`.
- Icons: stroke-based SVG components inheriting `currentColor` ([src/components/icons/](src/components/icons/)).
- Images from user input: `<img>`, never CSS `background-image`.
- Game cover art: `<img>` with `object-cover aspect-3/4`. Clip thumbnails: `<img>` with `object-cover aspect-video`.
- Theme mechanism: `.light` class on `<html>` via `useThemeStore` — do not introduce `data-theme`.

Before writing any component: check the token table (§3), reuse the recipes (§6), run the sweep (§9). If something isn't covered: least visual noise, most content, mint only for interactive states.
