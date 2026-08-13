name: Emuera Web UI - Prototype 3
version: 1.0
status: accepted

## Overview

This document records the accepted visual language for the Emuera Web UI. The
game picker follows `game-picker-prototype3.html`; the gameplay, Debug, and
Settings surfaces use the same dark gray palette and control language.

The UI is quiet and work-focused: no decorative gradients, glow effects, or
bright outline controls. Surfaces, spacing, and restrained state layers should
support repeated use without competing with game output.

## Tokens

### Colors

| Token | Value | Use |
| --- | --- | --- |
| `--color-bg` | `#171717` | Page and gameplay canvas |
| `--color-surface` | `#1f1f1f` | App bars, input area, panels |
| `--color-surface-raised` | `#2a2b2e` | Menus, selected surfaces, dialogs |
| `--color-border` | `#303134` | Structural separators only |
| `--color-text` | `#e8eaed` | Primary text |
| `--color-text-muted` | `#bdc1c6` | Secondary text and metadata |
| `--color-indicator` | `#a8c7fa` | Selection indicator and active status |
| `--color-success` | `#81c995` | Success/connected state |
| `--color-warning` | `#fdd663` | Warnings and countdowns |
| `--color-error` | `#f87171` | Errors and destructive status |

Interactive controls use solid `#353638` backgrounds and white text. Hover
and active states use `#414247`. Controls must not use bright borders or glow
effects.

### Typography

- UI text: `var(--font-ui)` (`system-ui`, Segoe UI, Roboto, Noto Sans SC).
- Game output and paths: `var(--font-mono)` where a monospace face is needed.
- Picker/game controls use the UI font; input placeholders must never inherit
  the game output font.

### Shape and spacing

- `--radius-control`: `12px` for inputs, filled buttons, rows, and controls.
- `--radius-surface`: `12px` for menus, dialogs, and framed surfaces.
- Circular icon buttons use `border-radius: 9999px`.
- Standard page inset is `24px`; touch targets are at least `48px`.
- The picker list uses the same `24px` horizontal inset as its hero title.

## Game Picker

### Full-window layout

- The picker occupies the full window and the document itself scrolls.
- The game list must not create an inner vertical scroll container. The outer
  blank space beside the list remains part of the scroll/touch surface.
- Game rows are full-width within the `24px` page inset and use large `12px`
  corners. The last-played row uses a raised surface and a left blue indicator.

### Header and scrolling

- The top app bar is `56px`, sticky, opaque, and above the directory row.
- The three-dot menu button is circular and aligned with the game-row arrows.
- The directory menu is above the list and uses a `12px` surface radius.
- Menu item backgrounds are transparent at rest and become rounded highlights
  only on hover/focus/active.
- The compact `选择游戏` title appears in the sticky app bar after the scroll
  threshold (`24px`), with opacity/position transition.
- The large `选择游戏` title fades from its scroll-bound opacity to zero over
  the same `24px` interval. Its CSS time easing is controlled by the
  `.hero-title` `transition: opacity` declaration.
- The `当前目录` row is sticky below the app bar (`top: 56px`), spans the
  complete window width, and covers list content without side gaps.
- The `当前目录` label and path text use `opacity: 0.6`; the path may wrap.
- The sticky app bar and directory row must be opaque: no backdrop blur or
  glass effect.

### Picker controls

- Picker icon buttons are transparent at rest and show a circular state layer
  only on hover/active/focus.
- Empty-state actions use the same filled-control treatment as other pages.

## Gameplay Surface

- Gameplay uses the same `#171717` / `#1f1f1f` / `#2a2b2e` palette and `12px`
  control corners as the picker.
- The two fixed top-right game controls show only white icons at rest. They
  become circular dark highlights on hover/active; active state does not add a
  bright outline.
- Input fields are light dark-gray solid rounded bars, UI-font placeholders,
  no underline, no border, and no focus highlight.
- Send/continue buttons are solid dark-gray, white text, `12px` corners, and
  no border or glow. Disabled state changes opacity only.
- Text buttons rendered inside game output remain borderless and inherit the
  game text styling; hover may use a subtle background/underline.

### Gameplay menu

- The popup surface is opaque and rounded, with no bright outer border.
- Normal menu items are transparent at rest and show a rounded dark highlight
  only on hover/focus/active; labels and icons remain white.
- The zoom section is two rows: the current percentage on the first row and
  the `- / 100% / +` controls on the second row.
- Zoom controls remain solid dark-gray buttons with white text and no border.

## Debug and Settings

- Debug and Settings use the same dark-gray surfaces, UI font, `12px` control
  radius, solid white-text controls, and no bright control borders.
- Debug input and send controls follow the gameplay input bar treatment.
- Settings log actions (`查看最新`, `导出`, `复制`) use solid controls rather
  than outlined buttons.
- The Settings log switch is a borderless solid track with a white knob; state
  changes use the track/knob position, not a bright outline.
- Panel and section separators may retain `--color-border` as structural
  dividers. These are not interactive control borders.

## Motion and Accessibility

- Picker title intro: opacity-only `520ms` fade-in.
- Picker compact title: short opacity/position transition on the `24px` scroll
  threshold.
- Picker hero opacity easing is tuned in `.hero-title`; keep the scroll target
  and compact-title threshold synchronized.
- Respect `prefers-reduced-motion` by reducing transitions and animations.
- Preserve visible keyboard focus without introducing permanent bright borders;
  focus states may use the global focus outline where required for keyboard
  accessibility.

## Do / Don't

### Do

- Use opaque sticky layers for content that must cover scrolling rows.
- Keep the picker and gameplay controls visually related through the same
  palette, large corners, white text, and restrained hover state layers.
- Keep structural separators distinct from interactive control borders.

### Don't

- Do not add backdrop blur, glass panels, glow, gradients, or bright outline
  buttons to the accepted picker/game surfaces.
- Do not create a nested vertical scroll area inside the full-window picker.
- Do not use the game output font for UI labels or input placeholders.
