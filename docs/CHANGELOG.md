# Changelog

## 1.0.17

- Clip quick-menu windows and their blur to the same rounded bounds, including asynchronous resizing.
- Keep menu borders aligned with the backdrop during opening and closing fades.
- Reset backdrop animations and extended DWM glass when dismissing a surface.
- Use an opaque settings background and page fades without scaling to avoid stale blurred content.
- Add native corner and pixel-restoration diagnostics for all five quick menus and settings lifecycle checks.

## 1.0.16

- Fully dismiss quick-menu windows and their blur surfaces after closing.
- Keep top-bar menus above the side dock without stealing focus.
- Bound Launchpad hover highlights to their cells; animate only the icon and highlight opacity.
- Fade and uniformly shrink previews before sending a normal close request.
- Restore a preview if its application remains open, and preserve close requests after leaving the preview.
- Finish menu and Launchpad transitions on their UI dispatcher, including interrupted transitions.

## 1.0.15

- Suppress edge reveal during true fullscreen video.
- Toggle the circle menu with a repeated click.
- Compact the Launchpad search field and reuse prepared tiles.
- Preserve native thumbnail aspect ratios after fullscreen restoration.
- Retain dock-icon origins for newly launched window animations.

## 1.0.14

- Reduce the top bar height and move it closer to the screen edge.

## 1.0.13

- Add bottom, left and right dock placement.
- Round window previews and add close buttons.
- Give Launchpad and the top bar a shared dark interface.
- Extend the preview hover delay and refresh input-language indicators faster.

## 1.0.12

- Remove bundled decorative icon packs.
- Correct icon transparency and minification artifacts while preserving original artwork.
