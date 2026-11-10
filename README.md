# Drift

### One pointer. Every screen.

Drift is an early cross-device input-sharing prototype. It explores a simple desk setup where a mouse and keyboard can move between a Windows PC, a Mac, and an Android device as the pointer crosses screen edges.

> **Current status:** this repository contains a browser-based UI preview. It does not yet connect devices or forward real input.

## Try the preview

1. Download or clone this repository.
2. Open `index.html` in a modern browser.
3. Explore the screen map, pairing dialog, input-source selector, settings, and edge-switching animation.

The preview is self-contained and has no package install or build step. Pairing, sharing switches, and cursor movement are demonstrations only; nothing is sent to another device.

## What Drift is exploring

- A screen map that mirrors how devices sit on a desk
- Pointer handoff when crossing a screen edge
- Keyboard focus that follows the active pointer
- Per-device pairing, connection status, and privacy controls
- Input peripherals connected to any paired device

## What it takes to make this native

Each operating system needs a native input adapter and clear user consent:

- **Windows:** capture local input, detect edge crossings, and inject received events using Windows input APIs.
- **macOS:** use a menu-bar companion with the required Accessibility and Input Monitoring permissions.
- **Android:** use an explicitly enabled Accessibility Service for pointer gestures. Android does not let an ordinary app inject arbitrary system-wide keyboard events, so keyboard sharing needs a supported input-method route or a managed-device setup.

The shared core will need authenticated pairing, encrypted device-to-device transport, a screen-layout graph, and a visible pause/escape control. These pieces are not implemented yet.

## Project files

| File | Purpose |
| --- | --- |
| `index.html` | App preview and page structure |
| `styles.css` | Responsive visual design |
| `app.js` | Local preview interactions |

## License

Drift is released under the [MIT License](LICENSE).
