# Resort OS design: Lagoon Glass

This is the look for the staff app from the "new look" restyle. The owner chose **Lagoon Glass** on 2026-11-09, with a **light and dark theme and a toggle button**.

Clickable mockup of the front-desk screen (light): https://claude.ai/artifact/NwLWRZvKH4DxtCrjdbEjCa (option A).

Read this before changing any screen. It says what the app should look like, which tokens to use, and what to leave alone.

## Status (13 Nov 2026)

Built: build-order steps 1 to 6 for every screen (tokens and both themes with the toggle, fonts, glow and glass with the
`ro-solid` fallback and the Solid panels switch, the sectioned floating sidebar, top bar with search and theme button,
phone tab bar with More), the Today dashboard and Reports with charts, page-enter and hover motion, and the new sign-in
page. Not yet: the tablet icon rail (tablets use the slide-over menu), route cross-fade, Lottie animations (step 8),
count-up numbers. Chart colours: `--ro-chart-1..6` per theme, validated for colour blindness and contrast.

## 1. The idea in one paragraph

Soft, airy and modern, like a good boutique resort's own app. Screens are made of **frosted glass panels** that float over a **slow colour glow** of sea green, sunrise coral and dusk lilac. In the dark theme the same glow becomes a lagoon at night. Corners are generous, shadows are soft, and the one strong colour is **lagoon teal**, used for the main action on each screen. Coral is the second colour and is used for money and highlights, never for buttons.

It is still a work tool. Staff use it on busy shifts and on cheap phones, often in sunlight. So the glass and glow stay in the background, text always sits on a solid enough surface to read, and the busy screens (front desk, room board, billing) stay calm.

## 2. Rules that never bend

1. **Text contrast:** body text at least 4.5:1 against what is behind it, large numbers and icons at least 3:1. Every pair in section 4 has been checked. A glass panel must never be so clear that the glow behind it lowers contrast (see the minimum panel opacity in 4.3).
2. **Errors say why**, under the field they concern (owner rule, see `docs/sprints/PROCESS.md`). The restyle changes how errors look, never what they say.
3. **Respect "reduce motion".** With `prefers-reduced-motion: reduce`, the glow stops moving, nothing slides or bounces, and Lottie animations show their last frame.
4. **Tap targets at least 44 × 44 px** on phones.
5. **No hex or px in components.** Use the tokens below. If a colour is missing, add a token here and in `styles.scss`, with both a light and a dark value.
6. **Both themes, every time.** A screen is not done until it has been checked in light and dark.

## 3. Themes and the toggle button

There are three settings: **Light**, **Dark** and **Match my phone** (system). The default is *Match my phone*.

- The choice is stored per device in `localStorage` under `ro-theme` (`light`, `dark` or `system`). It is a convenience, so if storage is blocked the app just follows the system.
- The app sets `data-theme="light"` or `data-theme="dark"` on `<html>`. With *Match my phone* it sets no attribute and CSS follows `prefers-color-scheme`.
- Set the attribute **before Angular boots** (a tiny inline script in `index.html`) so the page never flashes the wrong theme.
- Also update `<meta name="theme-color">` so the phone's status bar and the installed PWA match: `#eaf4f2` light, `#071a1f` dark.

**Where the button goes**

- Desktop: in the top bar, between search and the bell. An icon button (sun, moon, or half-circle for system) with a tooltip, "Theme: Light", "Theme: Dark" or "Theme: Match my phone". Clicking cycles Light → Dark → Match my phone.
- Phone: in the profile menu (tap your avatar), as a three-option segmented control with the same three labels. The top bar on a phone is too crowded for another icon.
- Keyboard: the button is a normal `<button>`, with `aria-label="Theme: Dark. Change theme"` and so on.

**Switching motion:** the colours cross-fade over 250 ms (`transition: background-color, color, border-color`). Where the browser supports it, use the View Transitions API for a circular reveal from the button. Skip both with reduced motion.

**CSS shape** (every token gets a light value on bare `html`, then is redefined twice for dark, so the toggle wins in both directions):

```scss
html { /* light values of every --ro-* token */ color-scheme: light; }

@media (prefers-color-scheme: dark) {
  html:not([data-theme='light']) { /* dark values */ color-scheme: dark; }
}
html[data-theme='dark'] { /* the same dark values */ color-scheme: dark; }
```

Keep the dark values in one Sass mixin (`@mixin ro-dark-tokens`) included in both dark blocks, so they cannot drift apart.

Angular Material: keep `mat.theme(...)` as it is and map `--mat-sys-*` onto the `--ro-*` tokens (as `styles.scss` already does). Because they point at variables, Material components switch theme with no extra work. Add `--mat-sys-*` lines for any Material role that still shows a light colour in dark mode (check menus, dialogs, snack bars, date pickers, tooltips).

## 4. Colour tokens

The token **names stay the same** as the Hallmark polish (`--ro-*`), so existing component styles keep working. Only the values change, and the new glass and glow tokens are added. Old names that no longer fit are mapped in 4.5.

### 4.1 Core colours

| Token | Use | Light | Dark |
|---|---|---|---|
| `--ro-bg` (new) | Page ground under the glow | `#eaf4f2` mist | `#071a1f` lagoon night |
| `--ro-ink` | Main text | `#13303a` | `#e3f2f1` |
| `--ro-mute` | Secondary text, labels | `#4f6a73` | `#93b3b6` |
| `--ro-faint` | Marks, dots, field edges (not text) | `#728b92` | `#5f7f84` |
| `--ro-line` | Dividers, card edges | `rgba(19,48,58,.10)` | `rgba(227,242,241,.10)` |
| `--ro-line-strong` | Field outlines, table heads | `rgba(19,48,58,.22)` | `rgba(227,242,241,.22)` |
| `--ro-accent` (new) | Primary buttons, current nav item, focus ring, links | `#0e7c80` lagoon | `#3cc4c2` lagoon glow |
| `--ro-on-accent` (new) | Text on accent | `#ffffff` | `#04282a` |
| `--ro-accent-strong` (new) | Accent text on light surfaces, hover | `#0b6b6f` | `#6fdad8` |
| `--ro-accent-soft` (new) | Selected rows, chips | `rgba(14,124,128,.12)` | `rgba(60,196,194,.16)` |
| `--ro-coral` (new) | Money highlights, sparklines, avatar, badges | `#b23d22` (text) / `#ff7a59` (fills) | `#ff8e70` |
| `--ro-lilac` (new) | Third glow colour, info chips | `#c9d2ff` | `#2a2f66` |

Checked contrast: ink on mist 12.4:1, mute on mist 4.7:1, white on lagoon 5.0:1, dark ink on lagoon glow 7.4:1, dark text tokens on lagoon night 7.3:1 or more.

### 4.2 Status colours (room board, chips, banners)

Each status is a **background + text pair**. Never put status text on a different background.

| Status | Light bg / text | Dark bg / text |
|---|---|---|
| Occupied | `#dfe7fb` / `#23408f` | `rgba(110,150,255,.18)` / `#a9c1ff` |
| Leaves today | `#fde6d3` / `#8a4300` | `rgba(255,153,77,.18)` / `#ffc48f` |
| Ready, arriving | `#d6ecea` / `#075a5d` | `rgba(60,196,194,.18)` / `#7eecef` |
| To clean | `#fbe0e0` / `#8f1d1d` | `rgba(255,107,125,.18)` / `#ff9aa7` |
| Being cleaned | `#fdf0c4` / `#6b5000` | `rgba(242,194,91,.18)` / `#f2c25b` |
| Clean, empty | `rgba(255,255,255,.7)` / `#46606a` | `rgba(255,255,255,.05)` / `#9fb7ba` |
| Out of order | `#2b3f46` / `#e6f0f0` | `#2a3a40` / `#9fb1b5` |
| OK / paid (`--ro-ok`, `--ro-ok-bg`) | `#e0f2e9` / `#1d5b36` | `rgba(111,207,154,.16)` / `#6fcf9a` |
| Warning (`--ro-warn`, `--ro-warn-bg`) | `#fbefd8` / `#8a5a00` | `rgba(242,194,91,.16)` / `#f2c25b` |
| Error (`--ro-error`, `--ro-error-bg`) | `#fdeceb` / `#b3261e` | `rgba(255,138,128,.16)` / `#ff8a80` |

All light pairs are 6:1 or better; all dark pairs are 7:1 or better against lagoon night.

### 4.3 Glass and glow

| Token | Use | Light | Dark |
|---|---|---|---|
| `--ro-glass` | Cards, panels, sidebar | `rgba(255,255,255,.62)` | `rgba(14,38,45,.62)` |
| `--ro-glass-strong` | Dialogs, menus, top bar when scrolled, phone tab bar | `rgba(255,255,255,.82)` | `rgba(12,32,38,.86)` |
| `--ro-glass-edge` | 1 px edge that catches the light | `rgba(255,255,255,.75)` | `rgba(255,255,255,.08)` |
| `--ro-glass-sheen` | Inset top highlight | `inset 0 1px 0 rgba(255,255,255,.7)` | `inset 0 1px 0 rgba(255,255,255,.06)` |
| `--ro-blur` | Backdrop blur | `blur(18px) saturate(1.4)` | `blur(18px) saturate(1.2)` |
| `--ro-shadow` | Card shadow | `0 8px 30px rgba(19,48,58,.08)` | `0 8px 30px rgba(0,0,0,.35)` |
| `--ro-shadow-lift` | Hover, dialogs | `0 14px 40px rgba(19,48,58,.14)` | `0 14px 40px rgba(0,0,0,.5)` |
| `--ro-glow-1` | Glow blob, top left | `#8fe0d2` | `#0f5e60` |
| `--ro-glow-2` | Glow blob, bottom right | `#ffc3a8` | `#5a2a22` |
| `--ro-glow-3` | Glow blob, middle | `#c9d2ff` | `#252b5e` |

**Minimum opacity:** text never sits on glass thinner than `--ro-glass`. Tables, forms and long lists sit on `--ro-glass-strong`.

**Low-power fallback:** blur is costly on cheap Android phones and makes text harder to read in sunlight. Add a class `ro-solid` on `<html>` when any of these is true: `prefers-reduced-transparency: reduce`, `navigator.deviceMemory <= 4`, or the user turns on **Settings → Display → Solid panels**. With `ro-solid`, glass tokens become solid (`#f4faf9` light, `#0b2329` dark), `backdrop-filter` is off, and the glow is a still gradient. The app must look finished in this mode too.

### 4.4 Gradients

- **Primary button and current nav item:** `linear-gradient(135deg, #0e7c80, #17a3a0)` in light, `linear-gradient(135deg, #2fb3b1, #57d6d3)` in dark, with a soft coloured shadow `0 6px 18px rgba(14,124,128,.35)` (dark: `rgba(60,196,194,.25)`).
- No other gradients. Coral is never a gradient or a button.

### 4.5 Old tokens

`--ro-kokum` was the primary colour. It is retired as the action colour: point `--mat-sys-primary` at `--ro-accent`. Keep `--ro-kokum` defined (light `#8a2449`, dark `#f08bb0`) for the guest pay page and invoices, which keep the resort's brand. `--ro-tide*`, `--ro-sea*`, `--ro-wash`, `--ro-paper`, `--ro-sunk` stay as names and point at the new values: `--ro-wash` → `--ro-bg`, `--ro-paper` → `--ro-glass-strong`, `--ro-sunk` → `--ro-accent-soft`, `--ro-tide` → `--ro-ink`. Remove the aliases once no component uses them.

## 5. Type

Both fonts are free on Google Fonts (load with `display=swap`; self-host later for the offline PWA).

| Role | Font | Fallback | Where |
|---|---|---|---|
| Display | **Unbounded** 500 / 600 | `'Onest', system-ui, sans-serif` | Page titles, big numbers on dashboards, empty-state headlines. Never for body text, buttons or tables. |
| UI and body | **Onest** 400 / 500 / 600 / 700 | `system-ui, sans-serif` | Everything else, including tables and forms. |
| Numbers | Onest with `font-variant-numeric: tabular-nums` | | Money, room numbers, times, counts in tables. |

Scale (rem at 15 px base):

| Token | Size / line height | Font | Use |
|---|---|---|---|
| `--ro-text-display` | 2rem / 1.1, weight 600 | Unbounded | KPI numbers (₹48,250) |
| `--ro-text-title` | 1.6rem / 1.2, weight 600 | Unbounded | Page title ("Front desk") |
| `--ro-text-section` | 1rem / 1.3, weight 600 | Unbounded | Card titles ("Arriving today") |
| `--ro-text-body` | 1rem / 1.5 | Onest | Body, table cells |
| `--ro-text-small` | 0.85rem / 1.4 | Onest | Hints, sub-labels |
| `--ro-text-label` | 0.75rem / 1.3, weight 600, letter-spacing .04em | Onest | Small labels above values |

On phones, the title drops to 1.35rem and KPI numbers to 1.75rem. Unbounded is wide, so never put it in a space narrower than about 8 characters of text.

## 6. Shape and spacing

- Corners: `--ro-radius-lg: 22px` (cards, panels, sidebar), `--ro-radius: 14px` (room tiles, inputs, menus), `--ro-radius-sm: 10px` (chips inside cards), `999px` (buttons, search, tab bar pills). Map `--mat-sys-corner-medium` to 14px and `--mat-sys-corner-small` to 10px.
- Spacing: keep the `--space-1` to `--space-8` scale. Cards use `--space-4` inside and `--space-4` gaps between them on desktop, `--space-3` on phones.
- Borders: 1 px `--ro-glass-edge` on glass, never a darker outline around a card.

## 7. Layout

### Desktop (from 1024 px)

- **Floating sidebar**, 250 px wide, a glass panel 14 px in from the left, top and bottom edges, with 26 px corners. Logo and property name at the top, the signed-in person at the bottom.
- **Sidebar sections** replace the long flat list: *Today* (Today, Front desk, Room board), *Guests & money* (Reservations, Calendar, Bills & payments, Invoices, Guests, Messages), *Rooms* (Housekeeping, Maintenance, Rooms setup), *Resort* (Activities, Equipment, Beach transport, Restaurant, Kitchen), *Stock & buying* (Inventory, Purchasing, Suppliers, Expenses, Budget), *Team* (Employees, Roster, Attendance, Timesheets, Leave, Payroll export), *Settings* (Room types, Rates, Packages, GST, Properties, Staff accounts, Roles & permissions, Audit trail, Reports). Sections can be collapsed and remember their state per device. Staff only see sections they have permission for.
- **Top bar** (no background until the page scrolls, then `--ro-glass-strong`): page title and date on the left; search ("Search guests, rooms, bills", opens with Ctrl K), theme button and bell on the right.
- **Dashboards:** a row of four KPI cards, then a two-column grid of panels. Front desk: *Arriving today* and *Leaving today* on the left, *Room board* on the right.
- **Lists and forms** (reservations, bills, settings) sit in one wide `--ro-glass-strong` panel. Do not put a table inside a card inside a card.

### Tablet (768 to 1023 px)

The sidebar collapses to a 72 px glass icon rail; labels show on hover and in a slide-over when the menu button is pressed. KPIs go to two per row.

### Phone (up to 767 px, designed at 360 px)

- No sidebar. A **floating glass tab bar** at the bottom, 10 px from the edges, with five items: Today, Front desk, Rooms, Bills, More. *More* opens a sheet with the other sections. Add `env(safe-area-inset-bottom)` to its padding.
- Top bar: title and bell; search becomes a full-width field under the title.
- KPIs two by two, panels in one column in this order: arrivals, room board, departures.
- Room board: four tiles per row, room number large, status in small text below.
- Row actions (Check in, Settle) stay as visible buttons. Do not hide them behind swipe gestures.

## 8. Components

- **KPI card:** label, big Unbounded number, one line of context ("3 in, 4 to come"), then a thin progress bar (accent) or a coral sparkline for money. Lifts 3 px on hover.
- **Buttons:** primary = accent gradient pill; secondary = `--ro-glass-strong` pill with `--ro-line-strong` edge; text buttons in `--ro-accent-strong`. Pressed state scales to 0.96. Disabled at 55% opacity and says why in a tooltip or hint.
- **Room tile:** status background and text pair from 4.2, glass edge, room number in Onest 600. Lifts on hover. Keep the coloured left edge on housekeeping cards (it is the status, waived in Hallmark).
- **Chips:** small pills. Booking source chips are neutral; status chips use the status pairs.
- **Inputs:** `--ro-glass-strong` fill, `--ro-line-strong` outline, 14 px corners, accent outline when focused, error text under the field in `--ro-error`.
- **Dialogs and menus:** `--ro-glass-strong` with `--ro-blur` and `--ro-shadow-lift`, over a backdrop of `rgba(7,26,31,.35)` with a 3 px blur.
- **Toasts:** dark glass in light theme, light-edged dark glass in dark theme, bottom centre (above the phone tab bar). Success toasts draw a tick; error toasts use the error pair and say exactly what to do next ("Room 205 is still being cleaned. Mark it clean on the room board first.").
- **Avatars:** initials on coral in the sidebar; on `--ro-accent-soft` in lists.
- **Focus ring:** 2 px `--ro-accent` with 2 px offset everywhere, including on the sidebar.

## 9. Motion

Motion should feel like water: smooth, never bouncy, and never in the way.

| What | How | Time |
|---|---|---|
| Background glow | Three blurred blobs drift and scale slightly | 18 to 26 s loop, alternate |
| Page enter | Cards rise 14 px into place, 70 ms apart | 600 ms, `cubic-bezier(.2,.8,.2,1)` |
| KPI numbers | Count up from 0 on first load of the day only | 900 ms, ease-out |
| Hover on cards and tiles | Lift 2 to 3 px, shadow grows | 200 ms |
| Button press | Scale to 0.96 | 150 ms |
| Route change | Cross-fade the content area (Angular route animation) | 200 ms |
| Theme switch | Colour cross-fade, or circular View Transition from the button | 250 ms |
| Toast | Slide up with slight overshoot; tick draws itself | 450 ms |

Rules: animate only `transform`, `opacity` and colours. Content is readable at every frame of an entrance (no starting at opacity 0). No motion on the room board when its data refreshes; only the tiles that changed get a 600 ms soft highlight. Everything stops under reduced motion.

## 10. Illustrations and animations (instead of GIFs)

Use **Lottie** (via `lottie-web`'s light player, loaded only on the screens that use it) or small **WebM** clips. No GIFs: they are large and blurry. Each animation has a still fallback image and plays once, except on the login screen.

| Place | Animation | Notes |
|---|---|---|
| Login | Slow sunrise over the sea, looping | Light: dawn colours. Dark: moonlight on water. |
| Empty states ("No arrivals today", "No rooms to clean") | A small wave or a sea shell, plays once | Under 40 KB each. |
| Check-in done | Door opens with a tick | In the toast. |
| Payment received | Coin drops into a shell | In the toast and on the guest pay page. |
| Night audit / day closed | Moon rises | Owner and manager only. |

Pick or commission animations with a licence that allows commercial use (LottieFiles free licence or made in-house). Store them in `frontend/src/assets/motion/` and list their source and licence in that folder's README. Every animation must use colours that work on both themes, or come in a light and a dark version.

## 11. Where the fun goes, and where it doesn't

- **More personality:** login, the guest pay page, empty states, success moments, the owner's Today dashboard.
- **Calm and fast:** front desk, room board, housekeeping, billing, restaurant orders, kitchen screen. Glass and colour yes, extra animation no.
- **Kitchen screen** and any screen meant for a wall TV use `ro-solid` always, for readability.

## 12. Performance budget

- Only three blurred layers on screen at once (sidebar, top bar or tab bar, and one dialog). Cards inside a glass panel do not blur again.
- The glow is a single fixed layer (`position: fixed; z-index: -1; pointer-events: none`), not repeated per page.
- Fonts: at most two families and five weights. Lottie player and animation files load lazily.
- Target on a mid-range Android phone: first paint under 2 s on 4G, scrolling at 60 fps on the front desk.

## 13. Build order

Do it one step at a time, committing after each, and check light, dark and `ro-solid` at 360 px and 1280 px each time.

1. **Tokens and themes:** new values and tokens in `frontend/src/styles.scss`, the dark mixin, Material mapping, the inline theme script in `index.html`, the theme button and the theme-color meta.
2. **Fonts and type scale.**
3. **Glow background and glass surfaces**, including the `ro-solid` fallback and the Solid panels setting.
4. **Shell:** floating sidebar with sections, top bar, phone tab bar and More sheet.
5. **Front desk and room board** in the new style (match the mockup).
6. **Other screens**, section by section, starting with the busiest.
7. **Motion:** page enter, hovers, toasts, route fade.
8. **Lottie** on login, empty states and success moments.
9. **Screenshots and handbooks:** refresh e2e screenshots, update `docs/architecture/frontend.md`, the system-design guide and the changelog.

e2e tests: the restyle should not change any visible text. If a label must change, change the test in the same commit.

## 14. Checklist for every screen

- [ ] Uses only `--ro-*` and `--space-*` tokens.
- [ ] Looks right in light, dark and solid panels.
- [ ] Works at 360 px with no sideways scroll and 44 px tap targets.
- [ ] Text contrast at least 4.5:1, checked on top of the glow.
- [ ] Errors say why, under the field.
- [ ] Reduced motion turns every animation off.
- [ ] Busy screen? No decorative motion.
