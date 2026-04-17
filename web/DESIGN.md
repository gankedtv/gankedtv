# GankedTV Frontend Design System

## Concept: "Underground Arena"

An editorial gaming zine meets esports broadcast UI — raw, intentional, high-contrast. Dark-first. The kind of UI where you can watch a clip at 2am without your eyes burning.

---

## Typography

| Role | Font | Weights | Usage |
|------|------|---------|-------|
| Logo / display | `Rajdhani` | 600, 700 | Navbar logo, hero text |
| Headings | `Barlow Condensed` | 500, 700 | Page titles, section headers |
| UI text | `DM Sans` | 400, 500 | Labels, buttons, body copy |
| Metadata | `DM Mono` | 400, 500 | Usernames, timestamps, stats, IDs |

Loaded via Google Fonts in `index.html`. CSS vars: `--font-display`, `--font-heading`, `--font-body`, `--font-mono`.

**Rules:**
- Page headings: Barlow Condensed, uppercase, 700
- Logo: Rajdhani, uppercase, 700 — "GANKED" white, ".TV" violet
- Never use Inter, Roboto, or system-ui as primary fonts

---

## Color Tokens

Defined in `web/src/assets/base.css` via Tailwind v4 `@theme`. Use CSS custom properties directly in `<style>` blocks or inline.

### Dark mode (default)

| Token | Value | Usage |
|-------|-------|-------|
| `--color-surface-base` | `#080810` | Page background |
| `--color-surface-raised` | `#10101c` | Cards, modals |
| `--color-surface-overlay` | `#18182a` | Dropdowns, hover states |
| `--color-text-primary` | `#f0eeff` | Main text |
| `--color-text-secondary` | `#8888aa` | Subtitles, labels |
| `--color-text-muted` | `#44445a` | Placeholders, disabled |
| `--color-border` | `#1e1e30` | Default borders |
| `--color-border-hover` | `#2e2e48` | Hovered borders |
| `--color-brand` | `#6d28d9` | Primary buttons, active states |
| `--color-brand-light` | `#7c3aed` | Hover on brand, active nav links |
| `--color-neon` | `#00e5a0` | Live indicators, success, @usernames |
| `--color-error` | `#ff4466` | Errors |
| `--color-warning` | `#ffaa00` | Warnings |
| `--color-success` | `#00e5a0` | Success states |

### Light mode (`.light` on `<html>`)

| Token | Value |
|-------|-------|
| `--color-surface-base` | `#f8f7ff` |
| `--color-surface-raised` | `#ffffff` |
| `--color-surface-overlay` | `#ededfa` |
| `--color-text-primary` | `#0a0a18` |
| `--color-text-secondary` | `#5a5a7a` |
| `--color-text-muted` | `#9898b8` |
| `--color-border` | `#ddddf0` |
| `--color-border-hover` | `#c0c0e0` |

Brand, neon, error, warning stay the same in both modes.

---

## Spacing & Radius

| Token | Value | Usage |
|-------|-------|-------|
| `--radius-sm` | `0.375rem` | Tags, badges |
| `--radius-md` | `0.625rem` | Inputs, small cards |
| `--radius-lg` | `1rem` | Cards, panels |
| `--radius-xl` | `1.5rem` | Modals, large surfaces |

---

## Layout

- **Max content width:** `1280px` (`max-w-7xl mx-auto px-6`)
- **Navbar height:** `64px` (fixed; all page content has `padding-top: 64px`)
- **Mobile breakpoint:** `640px` — nav links collapse, layout goes single column

---

## Components

### AppNav (`web/src/components/AppNav.vue`)
- Fixed top, 64px, `bg-surface-base/90` + `backdrop-filter: blur(12px)`
- Border bottom: transparent → `--color-border` on scroll past 20px
- Logo: Rajdhani bold, "GANKED" white + ".TV" violet
- Active nav link: `--color-brand-light` + 2px underline below link
- Sign In button: filled `--color-brand`, hover `--color-brand-light`

### ThemeToggle (`web/src/components/ThemeToggle.vue`)
- 36px circular button, border `--color-border`
- Sun SVG in dark mode, Moon SVG in light mode
- Vue `<Transition name="icon" mode="out-in">` — icon rotates 90deg + fades on swap
- Calls `useThemeStore().toggle()`

### Theme store (`web/src/stores/theme.ts`)
- Dark is default; light activated by `.light` class on `<html>`
- Persisted to `localStorage` key `"theme"`
- Initialize with `themeStore.applyToDOM()` in `main.ts` before mount to prevent flash

---

## Motion Principles

- **Page transitions:** 150ms opacity fade via `<Transition name="fade" mode="out-in">` in App.vue
- **Hover states:** 150–200ms transitions on color, background, border
- **Icon swaps:** rotate + scale + fade (see ThemeToggle)
- **Scroll reveals:** Navbar border fades in on scroll (200ms)
- **Clip cards (future):** scale 1→1.03 + violet glow `box-shadow` on hover; video preview autoplays on hover

**Rule:** CSS-only transitions preferred. No animation libraries for simple interactions.

---

## Tailwind Usage Rule

**Always use Tailwind utility classes in Vue templates.** This is non-negotiable.

The `@theme` tokens in `base.css` map directly to Tailwind utilities:

| CSS custom property | Tailwind utility |
|---------------------|-----------------|
| `--color-surface-base` | `bg-surface-base` |
| `--color-surface-raised` | `bg-surface-raised` |
| `--color-surface-overlay` | `bg-surface-overlay` |
| `--color-text-primary` | `text-text-primary` |
| `--color-text-secondary` | `text-text-secondary` |
| `--color-text-muted` | `text-text-muted` |
| `--color-brand` | `bg-brand`, `text-brand`, `border-brand` |
| `--color-brand-light` | `bg-brand-light`, `text-brand-light` |
| `--color-neon` | `text-neon`, `bg-neon` |
| `--color-border` | `border-border` |
| `--color-border-hover` | `border-border-hover` |
| `--font-display` | `font-display` |
| `--font-heading` | `font-heading` |
| `--font-body` | `font-body` |
| `--font-mono` | `font-mono` |
| `--radius-sm/md/lg/xl` | `rounded-sm/md/lg/xl` |

**Only use `<style>` blocks for things Tailwind can't do:**
- Keyframe animations (`@keyframes`)
- Third-party component overrides
- Vue `<Transition>` class hooks (`.fade-enter-from` etc.)

`::after`/`::before` pseudo-elements with dynamic class targeting are doable in Tailwind v4 via stacked arbitrary variants — e.g. `after:content-[''] after:opacity-0 [&.active]:after:opacity-100`.

**Never** write `var(--color-*)` or `font-family: 'Rajdhani'` in a `<style>` block when a Tailwind class exists for it.

---

## Anti-patterns

- No white or off-white backgrounds as default (dark-first)
- No gradients on primary backgrounds — use flat surfaces with border depth
- No Inter, Roboto, system-ui as display fonts
- No purple-gradient-on-white (generic AI aesthetic)
- No uniform grid layouts where asymmetry would serve better
- No scoped CSS for layout/color/typography — use Tailwind utilities
