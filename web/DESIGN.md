# GankedTV Design System — Newsprint ("Broadcast Almanac")

**Status:** v2, shipped. Replaces the "Underground Arena" multi-theme system.
**Spec:** [docs/superpowers/specs/2026-06-08-newsprint-design-system-design.md](../docs/superpowers/specs/2026-06-08-newsprint-design-system-design.md)

Read this before writing any frontend code (Vue components, Tailwind classes).

---

## 1 — Concept

An editorial sports yearbook that knows how to render video. Every page reads like a
printed issue — kicker labels, hairline rules, oversized condensed numerals, Roman-numbered
sections — while watch surfaces (clip detail, reels, transcode previews) wear a broadcast
HUD on top: corner brackets, telemetry strips, mono-everywhere.

The site is an *archive* of gaming's loudest seconds. Every clip is filed under an issue
number. Every page belongs to a volume.

## 2 — The anti-AI rules (load-bearing)

Dropping any of these lets the system drift back into template territory:

1. **Every clip has an issue number** (`No. 042`) — top-left of every thumbnail, repeated as
   an oversized numeral in hero positions. Derived client-side via [`src/lib/issue.ts`](src/lib/issue.ts).
2. **Roman numerals on section kickers** (`II By Game`). The page is an *issue*, not a *feed*.
3. **Hairline rules instead of cards-with-shadows.** Borders define regions. No `box-shadow`,
   no glow, no `backdrop-blur` glassmorphism.
4. **Oversized condensed numerals** (Barlow Condensed 700, 28–96px+) for issue numbers, hero
   stats, list ranks, telemetry values.
5. **Telemetry strips** for stats — mono 9px kicker over a condensed 26px value
   ([`TelemetryStrip.vue`](src/components/TelemetryStrip.vue)).
6. **Corner brackets only on watch surfaces** ([`BroadcastFrame.vue`](src/components/BroadcastFrame.vue)) —
   the clip player, reels viewer, upload transcode preview. They earn meaning by not being everywhere.
7. **No `translateY` / scale hover, ever.** Hover = border color swap to ink + title color
   shift to ink. Cards stay put.
8. **Tabs as text rules, not pills.** Underline-only, mono 11px caps, 2px ink underline on
   active ([`UnderlineTabs.vue`](src/components/UnderlineTabs.vue)).
9. **No rounded corners ≥ 8px.** `--radius-sm: 0`, `--radius-md: 2px` (inputs only).
   `rounded-full` is allowed only on the pulsing live dot.
10. **Footer as colophon** ([`AppFooter.vue`](src/components/AppFooter.vue)) — publication
    signoff, not sitemap dump.

## 3 — Tokens

One palette, dark (default) + light via `.light` on `<html>`. Defined in the
`@theme` block of [`src/assets/base.css`](src/assets/base.css) — the source of truth for
token → Tailwind-utility mapping (`bg-ink`, `text-signal`, `border-border`, …).

| Token | Dark | Light | Used for |
| --- | --- | --- | --- |
| `--color-surface-base` | `#161410` | `#f4f1e8` | page background |
| `--color-surface-raised` | `#221e16` | `#ece8da` | inputs, hover backgrounds (rare) |
| `--color-surface-sunken` | `#0c0a08` | `#d8d3c4` | video player background, deep recess |
| `--color-text-primary` | `#f4f1e8` | `#1a1810` | body text, headings |
| `--color-text-secondary` | `#c5bca7` | `#5a5444` | bylines, captions, kicker labels |
| `--color-text-muted` | `#8a8474` | `#6b6553` | timestamps, placeholders, disabled |
| `--color-border` | `#2c2820` | `#1a1810` | default borders, hairline rules |
| `--color-border-strong` | `#3c3a30` | `#1a1810` | emphasized borders, popover panels |
| `--color-ink` | `#ed3a47` | `#c41825` | **brand.** Issue numbers, @usernames, links, hover borders, active underlines |
| `--color-signal` | `#6c9bcf` | `#3a6fb5` | **alert / live.** LIVE dot, errors, NEW badges — earned moments only |
| `--color-signal-text` | `#161410` | `#f4f1e8` | text on solid ink/signal fills |

Usage rules:

- **Ink is the brand color** — use freely, but visible-not-loud at small sizes.
- **Signal is for earned moments only**: LIVE indicators, error copy, NEW badges. Never body
  text, never a hover state, never a button background.
- **Two badge styles, one color**: solid ink (`bg-ink text-signal-text`) for primary badges
  (game tag on thumbs, LIVE); outline ink (`border-ink text-ink`) for secondary (NEW, PINNED).
- **No gradients on UI surfaces.** The sole sanctioned gradient is the flat striped
  `.placeholder-art` render-failure fallback in base.css. Legibility bands over video/art use
  solid `bg-black/55`–`/60`.
- **Accessibility:** light-mode signal `#3a6fb5` under cream `#f4f1e8` text on solid fills is
  ~5.3:1 — above WCAG AA. Documented pair; don't substitute.
- Vendor colors (`--color-discord`, `--color-google`) are identity-locked and unchanged.

Text over video/art (duration badges, reels overlays) uses the literal `#f4f1e8` rather than
`text-text-primary` on purpose — it must stay light in both modes.

## 4 — Typography

| Role | Font | Used for |
| --- | --- | --- |
| `font-display` | Rajdhani 600/700 | "GANKED.TV" wordmark only |
| `font-heading` | Barlow Condensed 500/700 | page/section/clip titles, issue numbers, numerals, telemetry values |
| `font-body` | DM Sans 400/500 | body copy, button labels, blurbs |
| `font-mono` | DM Mono 400/500 | kickers, timestamps, @usernames, IDs, badges, nav links |

Scale highlights: hero numeral `clamp(56px,8vw,96px)`; page title `clamp(36px,4.5vw,52px)`;
section title `clamp(28px,3vw,38px)`; card title 16px/500 uppercase; kicker 10px/0.22em
caps; mono meta 11px/0.12em caps. Line-height ~1.05 on condensed display, 1.55 body, 1 on
mono caps strips. Don't use font-weight as the primary hierarchy lever — use size + position + case.

## 5 — Layout rhythm

- Page shell: sticky 64px nav with bottom hairline; content `max-w-360` (1440px), padding
  `px-8 pt-10 pb-30` desktop / `px-4 pt-5 pb-20` mobile (`max-tablet:`).
- Every content band opens with [`SectionHeader.vue`](src/components/SectionHeader.vue)
  (roman + kicker → title → blurb → hairline) and `pt-10` between bands. Air is the editorial feel.
- Grid gaps: `gap-x-5.5 gap-y-7` (22/28px). Workhorse feed grid:
  `grid-cols-[repeat(auto-fill,minmax(280px,1fr))]`.
- Band library: Hero+List (`2fr/1fr`), Game Tiles (5-up 3:4), Feature+List (`1.6fr/1fr`),
  4-up Grid, Telemetry Strip, Broadcast Frame.
- Mobile chrome: [`MobileTabBar.vue`](src/components/MobileTabBar.vue) (fixed bottom, `lg:hidden`,
  Feed/Games/Upload/Reels/You). Trending + Leaderboards stay reachable via the footer and search.

## 6 — Atom inventory

| Component | Role |
| --- | --- |
| [`SectionHeader.vue`](src/components/SectionHeader.vue) | universal band header (roman, kicker, title, blurb, more→ / `#right` slot) |
| [`TelemetryStrip.vue`](src/components/TelemetryStrip.vue) | bordered stat cells; `action` cells are tappable, `ink` highlights a value |
| [`BroadcastFrame.vue`](src/components/BroadcastFrame.vue) | watch-surface HUD: inset ink border, 4 corner brackets, mono topbar, live dot |
| [`AppFooter.vue`](src/components/AppFooter.vue) | colophon footer + `volIssMeta()` signoff row |
| [`MobileTabBar.vue`](src/components/MobileTabBar.vue) | 5-tab phone nav, solid-ink upload square |
| [`PageHeader.vue`](src/components/PageHeader.vue) | page-level kicker + condensed clamp title (`live` dot is an earned moment) |
| [`ClipCard.vue`](src/components/ClipCard.vue) | chrome-less card: bordered thumb + issue No. + solid-ink game tag + mono meta |
| [`UnderlineTabs.vue`](src/components/UnderlineTabs.vue) | text-rule tabs, 2px ink active underline |
| [`StatusPanel.vue`](src/components/StatusPanel.vue) | loading ticker / hairline-banded empty & error states |
| [`src/lib/issue.ts`](src/lib/issue.ts) | `issueNumber()`, `formatIssueNo()`, `volIssMeta()` |

## 7 — Recipes

**Buttons** (three, no others):

- Primary: `bg-ink text-signal-text … hover:brightness-108` (transition `[filter]`).
- Secondary: `border border-border bg-transparent … hover:border-ink hover:text-ink`.
- Link: `text-ink hover:underline`.
- No icon-only round buttons — square (`size-8.5 border border-border`) is the icon-button shape.

**Inputs:** `h-11 rounded-sm border border-border bg-surface-raised … focus:border-ink`.
Labels are mono 10px `tracking-[0.18em]` caps in `text-text-secondary`.

**Dialogs:** solid `bg-surface-sunken/90` scrim (no blur), panel `border border-border-strong
bg-surface-base` (no shadow, sharp), mono kicker over a condensed uppercase title, hairline
header/footer rules, secondary cancel + primary ink confirm. Destructive confirms stay solid
ink — the body copy carries the warning.

**Popovers/listboxes** (kebab, notifications, search, pickers): `border border-border-strong
bg-surface-base`, mono kicker group labels, rows `hover:bg-surface-raised`, hairlines between
groups, danger items `text-ink`.

**Loading:** the ticker — `h-1.5 w-5.5 bg-surface-raised` shell with an inner
`origin-left bg-ink animate-[tick_1.6s_ease-in-out_infinite]` bar. No spinners.

## 8 — Motion

- Page transitions: 150ms opacity fade (App.vue).
- Hover: 150ms on `color` / `border-color` only — never `transform`.
- Live dot: 2s opacity pulse (`animate-[pulse_2s_infinite]`), signal color.
- Toasts: 300ms `slideUp` in / `slideDown` out.
- Sheets/dialogs may slide/fade on enter/leave — the transform ban is on hover states.

## 9 — Theme mode

[`src/stores/theme.ts`](src/stores/theme.ts): `mode: 'dark' | 'light'`, persisted under
`localStorage['theme']`, applied as `.light` on `<html>`. The v1 multi-theme system
(`theme:name`, `data-theme`, ThemePicker) is gone; the store silently removes the legacy key
and scrubs the attribute. Toggle lives in the nav (`ThemeModeToggle`) and Settings → Appearance.

## 10 — Banned list + sweep

No `box-shadow` / `shadow-*` (the Plyr override that *removes* its menu shadow is the one
exception), no `backdrop-blur`, no hover transforms, no `rounded-(md+)` except `rounded-sm`
on inputs and `rounded-full` on live dots, no gradients outside `.placeholder-art`, no
brand/neon/data-theme remnants. Verify with:

```bash
cd web/src
grep -rnE "shadow-\[|shadow-(sm|md|lg|xl)" . --include="*.vue"
grep -rn  "backdrop-blur" . --include="*.vue"
grep -rnE "hover:(-)?translate|hover:scale" . --include="*.vue"
grep -rnE "rounded-(md|lg|xl|2xl|3xl|full)" . --include="*.vue"   # full → live dots only
grep -rnE "\b(bg|text|border|fill)-(brand|neon)\b|data-theme|chamfer|corner-cut" . --include="*.vue" --include="*.css"
grep -rn  "gradient" . --include="*.vue" --include="*.css"        # placeholder-art only
```

## 11 — Component conventions (unchanged)

- Tailwind utility classes in templates; tokens via the `@theme` block. Scoped CSS only when
  utilities genuinely can't express it.
- Dynamic runtime values (user accent colors, hashed avatar fills) go through inline `:style`.
- Icons are stroke-based SVG components inheriting `currentColor`.
- Images from user input render as `<img>`, never CSS `background-image`.
