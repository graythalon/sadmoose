# Native architecture

Drift runs a native host on Windows and native peers on macOS and Android. Each peer connects directly to the Windows host on TCP port 42857. The host owns the current focus target and routes pointer and keyboard events between connected devices.

## Pairing and transport

The Windows app creates a random 256-bit pairing key and shows the host's local IP address. A peer proves possession of the key using HMAC-SHA-256. Both ends derive separate send and receive keys from fresh connection nonces. Each JSON input frame is encrypted and authenticated with AES-GCM, a direction-specific key, and a monotonically increasing counter.

The pairing key remains on the user's devices: Windows protects it with DPAPI, macOS stores it in Keychain, and Android encrypts it with a key held by Android Keystore. The initial handshake contains peer names, nonces, and HMAC proofs; it does not send the pairing key.

## Focus and routing

- The Windows picker selects the target for PC input. When the Windows pointer reaches the edge of its virtual desktop, it enters that selected peer.
- Mac and Android send an edge handoff request to the host while their screen owns focus. The host moves left or right through peers in connection order; vertical edges return to Windows.
- Peripherals on any paired peer can act as the source. The host routes their events to the active target and pins key and mouse-button releases to the device that received the matching press.
- Focus changes, peer disconnects, sharing pauses, and host shutdown release forwarded keys and mouse buttons when the destination is still connected.
- On Windows, Ctrl + Alt + Shift + Backspace returns focus to the PC.

## Platform input adapters

### Windows

The WPF host uses low-level Win32 keyboard and mouse hooks to capture local input and `SendInput` to inject input received from peers. The mouse pointer is hidden and recentered while a peer is active; relative pointer movement is sent over the encrypted connection.

### macOS

The SwiftUI client uses a Core Graphics event tap to capture input and posts received events through Core Graphics. The user must grant Accessibility and Input Monitoring access. The app bundle is created locally by `apps/macos/build-macos.sh` and uses an ad-hoc code signature; it is not notarized for distribution.

### Android

The Kotlin client uses an explicitly enabled Accessibility Service to observe hardware keyboard and mouse events, draw a software pointer, and dispatch touch-like pointer gestures. Drift Keyboard uses the focused text field's input connection to enter text or send supported key actions. A normal Android app cannot inject arbitrary system-wide key events, and secure system surfaces may refuse injected gestures or input.

## Scope and platform limits

- This release requires all devices to reach the Windows host on the same trusted local network. Pairing uses manual IP and key entry; there is no relay or automatic discovery.
- Screen placement is a left-to-right peer chain in connection order. The app does not calibrate screen sizes or gaps, or provide a draggable monitor layout.
- Android touchscreen interaction stays on the phone. Pointer sharing uses a software pointer and accessibility gestures; it is not a system cursor extension.
- Clipboard sync, remote display streaming, background startup, signed installers, and notarized macOS distribution are not part of the current source release.
