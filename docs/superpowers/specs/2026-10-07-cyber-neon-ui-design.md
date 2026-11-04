# Cyber Neon UI — Design Spec

Date: 2026-10-07  
App: Dashboard.Net (`Dashboard.Api`)  
Status: approved direction — waiting for implementation plan

## Goal

Restyle the existing English UI to a **cyber neon / glass** look inspired by [@laddtnov/cyberpunk-ui](https://laddtnov.github.io/cyberpunk-ui/demo/), intensity level **B** (bright outlines, button pulse, noticeable glow). Keep the current Adam Abundis Unsplash scrolling photo background unchanged.

## Non-goals

- Do not change layout: face form (Amount / What / Why), right-side More drawer, logout inside More, captcha puzzle logic.
- Do not replace or restyle the page background animation (Unsplash `::before` scroll stays as-is).
- Do not add full-page CRT scanlines, animated grid backdrop, or glitch headlines everywhere.
- Do not pull the npm package as a runtime dependency unless a pinned local copy of tokens/effects is clearly cleaner; prefer hand-ported CSS variables and rules into `wwwroot/css/dashboard.css` so the app stays self-contained.
- Do not change auth, encryption, API, or English copy.

## Visual language

| Token | Value / role |
|-------|----------------|
| Neon cyan | `#00f2ff` — primary borders, focus rings, More glow, primary buttons |
| Neon pink | `#ff00aa` (or close) — danger / Sign out / delete |
| Surface | dark translucent panels with `backdrop-filter: blur` over the photo |
| Text | Orbitron (headings / big amount), Rajdhani (body / labels) via Google Fonts |
| Glow | multi-layer `box-shadow` / `text-shadow` using cyan or pink |
| Pulse | subtle opacity/shadow keyframes on primary actions (Sign in, Create account, Add); respect `prefers-reduced-motion` |

Inspiration source: cyberpunk-ui tokens and `.cy-btn` / `.cy-card` / `.cy-input` patterns. Implementation maps those looks onto existing classes (`.panel`, `.composer`, `.drawer`, `.glass-btn`, inputs) rather than rewriting all markup to `cy-*` classes, unless a small class add is cleaner.

## Screens (everywhere)

### Login / Register

- Panel: glass card, cyan edge glow.
- Inputs: dark fill; `:focus-visible` cyan glow border.
- Primary button: neon cyan outline / fill on hover, soft pulse.
- Error notes: pink/red glow treatment.
- Captcha: keep vertical slider behavior; board border matches neon chrome.
- Links: cyan-tinted, readable on dark glass.

### Entry (`/transactions`)

- Composer: same glass + rim light as login panel.
- Amount field: large type with light cyan text-glow.
- Add: primary wow button (cyan + pulse).
- More tab: stronger cyan border/glow (builds on current right tab).
- Drawer: glass surface, section dots in cyan glow; tables/stats/bars use neon accents; Sign out pink outline glow.

### Edit

- Same panel/button/input language as Login.

## Technical approach

1. Load Orbitron + Rajdhani in Login, Register, Index, Edit views.
2. Extend `wwwroot/css/dashboard.css` with CSS variables under `body.gate-body` / `body.app-body` (or `:root` scoped to those bodies) and restyle existing selectors. Keep the Unsplash `::before` block untouched.
3. Optional: small class hooks (e.g. `neon-pulse` on primary buttons) if keyframes need a dedicated class.
4. Hard-refresh / cache-bust `dashboard.css` if needed after deploy locally.
5. Stop `Dashboard.Api` before rebuild if views change; restart on `http://localhost:5011`.

## Acceptance

- Background still matches the current scrolling Unsplash pattern (no black band, seamless loop).
- All four views look cyber neon; English strings unchanged.
- Captcha still usable (board visible, slider not crushed).
- Primary buttons have noticeable glow/pulse; focus states visible.
- `prefers-reduced-motion: reduce` disables or softens pulse (background scroll already slowed per existing rules).
- Existing tests still pass (4).

## Out of scope for later

- Installing `@laddtnov/cyberpunk-ui` as a package and rewriting markup to `cy-*`.
- Theme switcher / light theme.
- Glitch titles or terminal chrome.
