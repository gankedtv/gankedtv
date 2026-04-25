# GankedTV Frontend Design System

## Concept

An editorial gaming zine meets esports broadcast UI — raw, intentional, high-contrast. Dark-first. The same components and layout ship under three interchangeable palettes; only tokens change.

---

## Ambient atmosphere

The body renders two persistent full-viewport layers (z-index 1 and 2, pointer-events none):
- **Grid gradient** (`body::before`): radial glow in brand color at the top of the viewport via `--grid-bg`.
- **Noise + scanlines** (`body::after`): SVG fractal noise texture at `--noise-opacity` (0.035 dark / 0.02 light), mixed via `overlay`.

These are defined in `base.css` and require no opt-in per component.

---

## Typography

| Role | Font | Weights | Usage |
|------|------|---------|-------|
| Logo / display | `Rajdhani` | 500, 600, 700 | Nav logo, hero callouts |
| Headings | `Barlow Condensed` | 500, 700 | Page titles, section headers, stat values |
| UI text | `DM Sans` | 400, 500 | Labels, buttons, body copy |
| Metadata | `DM Mono` | 400, 500 | Usernames, timestamps, stats, IDs, tags |

Google Fonts loaded in `index.html`. CSS vars: `--font-display`, `--font-heading`, `--font-body`, `--font-mono`.

**Rules:**
- Page titles: `font-heading` (Barlow Condensed), `font-weight: 700`, `text-transform: uppercase`, `font-size: clamp(32px, 4vw, 52px)`
- Section titles: `font-heading` 24px bold uppercase, with a 6×22px brand bar before (via `::before` or inline `<span>`)
- Logo: `font-display` (Rajdhani), uppercase, 700 — "GANKED" primary, ".TV" brand-light
- Metadata / timestamps / IDs / tags: always `font-mono`

---

## Themes

Three palettes share the same CSS-variable contract. The active palette is selected by `data-theme` on `<html>`, written by `useThemeStore().setName(name)`. Components don't change — they read `var(--color-…)` (directly or via Tailwind utilities) and repaint when tokens swap.

| Theme | Vibe | Brand | Accent | Corners | Background |
|-------|------|-------|--------|---------|------------|
| `underground` | Editorial dark | `#6d28d9` purple | `#00e5a0` neon green | Chamfered (10px cut) | Radial purple glow |
| `tactical` | HUD / broadcast | `#ff7a00` orange | `#ffffff` white | Square | Repeating line grid |
| `arcade` | CRT / chunky neon | `#ff3d8b` pink | `#ffe600` yellow | Square | Pink/yellow gradient + scanlines |

**Default for new visitors:** `arcade`. Persisted under `localStorage['theme:name']`. Stamped on `<html>` before mount in `main.ts` to avoid FOUC.

Tactical also swaps `--font-heading` to `Rajdhani` (everything else stays); Underground and Arcade use `Barlow Condensed`.

## Color Tokens

All tokens are in [web/src/assets/base.css](src/assets/base.css). The `@theme {}` block defines Tailwind utilities (`bg-brand`, `text-neon`, `border-border`, …) and acts as the static fallback. Each `[data-theme="…"]` block overrides the same set of variables at runtime, so utilities resolve to whatever the active theme dictates.

### Underground (token reference)

| Token | Value | Usage |
|-------|-------|-------|
| `--color-surface-base` | `#080810` | Page background |
| `--color-surface-raised` | `#10101c` | Cards, panels |
| `--color-surface-overlay` | `#18182a` | Dropdowns, hover states |
| `--color-surface-sunken` | `#05050c` | Video player bg, deep recesses |
| `--color-text-primary` | `#f0eeff` | Main text |
| `--color-text-secondary` | `#8888aa` | Subtitles, secondary labels |
| `--color-text-muted` | `#44445a` | Placeholders, timestamps, disabled |
| `--color-border` | `#1e1e30` | Default borders |
| `--color-border-hover` | `#2e2e48` | Hovered borders |
| `--color-border-strong` | `#3a3a58` | Emphasized borders, game tags |
| `--color-brand` | `#6d28d9` | Primary buttons, active states |
| `--color-brand-light` | `#7c3aed` | Hover on brand, active nav underline |
| `--color-brand-glow` | `rgba(124,58,237,0.35)` | Card hover shadows |
| `--color-neon` | `#00e5a0` | Live dots, @usernames, success |
| `--color-neon-dim` | `rgba(0,229,160,0.18)` | Neon-tinted icon backgrounds |
| `--color-error` | `#ff4466` | Errors, live badges |
| `--color-warning` | `#ffaa00` | Warnings |

Tactical and Arcade override the same tokens with their own values — see `base.css` for the full set.

### Light mode (`.light` on `<html>`)

Toggled by `useThemeStore().toggle()`. Overrides surface, text, and border tokens scoped per theme (`html.light[data-theme="underground"]`, etc). Brand stays; `--color-neon` flips to a high-contrast color in Tactical and Arcade for legibility on light backgrounds.

### Theme CSS-only vars (not Tailwind utilities)

| Var | Underground | Tactical | Arcade | Usage |
|-----|-------------|----------|--------|-------|
| `--corner-cut` | `10px` | `0px` | `0px` | `.chamfer` polygon corners (no-op in Tactical/Arcade) |
| `--grid-bg` | radial purple | line grid | pink/yellow gradient | body::before ambient layer |
| `--noise-opacity` | `0.035` | `0.02` | `0.03` | body::after noise layer |
| `--scanline-opacity` | `0` | `0` | `0.08` | body::after scanlines |
| `--feed-gap` | `16px` | `16px` | `16px` | `.feed-grid` gap (theme-agnostic) |

---

## Spacing & Radius

| Token | Value | Usage |
|-------|-------|-------|
| `--radius-sm` | `0.375rem` | Tags, badges, small chips |
| `--radius-md` | `0.625rem` | Inputs, buttons, cards |
| `--radius-lg` | `1rem` | Hero cards, large panels |
| `--radius-xl` | `1.5rem` | Modals |

Page max-width: `1440px`. Content padding: `32px 24px 120px` (desktop), `16px 14px 80px` (mobile).

---

## Navigation

`AppNav` is `position: sticky; top: 0; z-index: 50; height: 64px`. Content sits directly below with no padding-top offset.

Elements: logo mark (polygon clip-path + neon dot) → logo text → nav links (Feed, Games, Trending) → search bar (≥1281px) → icon buttons → upload button → avatar.

Active nav link: `color: var(--color-text-primary)` + 2px brand-light underline.

---

## Components

### Avatar (`web/src/components/Avatar.vue`)
Colored initial block. Background is a linear gradient derived from the user's avatar color. Always circular. Shows 2-letter uppercase initials in `font-mono` bold.

Props: `user: string` (key in USERS), `size?: number` (px, default 32).

### ClipCard (`web/src/components/ClipCard.vue`)
Grid tile: 16:9 thumbnail + game tag (top-left) + duration (bottom-right) + body with title (line-clamp 2) + avatar + @username (neon) + stats (likes / views).

Hover: `translateY(-2px)` + brand glow `box-shadow` + border-brand.

Emits: `click`.

### Feed grid (`.feed-grid` global class)
`grid-template-columns: repeat(auto-fill, minmax(280px, 1fr))`, 4-col at ≥1200px. Gap driven by `--feed-gap`.

---

## Motion Principles

- **Page transitions:** 150ms opacity fade (`<Transition name="fade" mode="out-in">` in App.vue)
- **Hover states:** 150–200ms on color, border, transform
- **Card hover:** `translateY(-2px)` + brand glow shadow
- **Pulsing dot:** `@keyframes pulse` 2s — opacity 1 → 0.3 → 1 — used on live indicators and eyebrow dots
- **Toast (killfeed):** `slideUp` 300ms in, `slideDown` 300ms out after 2.2s

CSS-only. No animation libraries.

---

## Logo mark

```html
<span class="logo__mark"></span>
```
Global CSS in `base.css`: polygon clip-path with neon corner dot via `::before` and recessed inner shape via `::after`. Do not reproduce inline.

---

## Chamfer utility

```html
<div class="chamfer">…</div>
```
Clips two opposite corners via `clip-path: polygon(var(--corner-cut) 0, …)`. `--corner-cut` is `10px` by default.

---

## Tailwind usage rules

Always use Tailwind utility classes for colors, spacing, typography. Use `style=""` bindings only for dynamic runtime values (e.g. avatar gradient). Use `<style scoped>` only for pseudo-elements or media queries that Tailwind can't express.

Never write `var(--color-*)` directly in a template element when a Tailwind class covers it.

---

## Anti-patterns

- No light backgrounds as default — dark-first always
- No gradients on primary surfaces (use flat + border depth)
- No Inter, Roboto, or system-ui as display fonts
- No scoped CSS for layout, color, or typography — Tailwind utilities
- No `pt-16` or `pt-[64px]` padding on page content — nav is sticky, not fixed
- No hardcoded brand/surface/text hex values in components — always go through the token (`var(--color-…)` or the matching Tailwind utility), or theme switching breaks for that element
