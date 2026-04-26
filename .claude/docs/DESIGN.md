# GankedTV Frontend Design System

This file is the authoritative design reference for all frontend work. Read [web/DESIGN.md](../../web/DESIGN.md) for the full specification.

The design system is "Underground Arena" — esports broadcast aesthetic, dark-first, high-contrast. Core tokens, global CSS, and ambient effects live in `web/src/assets/base.css`. Components at `web/src/components/`.

Key rules:

- Nav is `sticky` (not fixed) — no `pt-16` padding on page content
- All color/typography/spacing via Tailwind utilities from `@theme` tokens
- `style=""` only for dynamic runtime values (avatar gradients, etc.)
- `<style scoped>` only for pseudo-elements and media queries
