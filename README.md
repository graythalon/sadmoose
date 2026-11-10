# Drift

**One keyboard and pointer for your Windows PC, Mac, and Android device.**

Drift is a set of native apps for sharing mouse, trackpad, and keyboard input across devices on the same local network. The Windows app hosts the session. Mac and Android apps pair with it, and the host routes input to whichever device currently has focus.

## What works

- Move the Windows pointer to the edge of the display to enter the selected device.
- On an active Mac or Android screen, move the pointer to an edge to hand off to the next paired device. Left and right follow the connection order; top and bottom return to Windows.
- Use peripherals connected to any paired device. Their input follows the active focus through the Windows hub.
- Share mouse and keyboard independently from the Windows host, pause sharing at any time, or disconnect a peer.
- Pair with a random 256-bit key. The apps authenticate peers and encrypt input frames over the local network.
- Press **Ctrl + Alt + Shift + Backspace** on Windows to return focus to the PC.

## Apps

| Device | Native app | Input support |
| --- | --- | --- |
| Windows 10/11 | WPF host | Captures local keyboard and pointer input, accepts paired peers, and injects received input. |
| macOS 14 or later | SwiftUI client | Shares local keyboard and pointer input and receives remote input after Accessibility and Input Monitoring permissions are granted. |
| Android 14 or later | Kotlin app | Shares attached hardware keyboard and mouse input through its Accessibility Service; receives pointer gestures through accessibility and keyboard input through Drift Keyboard. |

## Pair your devices

1. Build and open the Windows app. Choose **Create pairing key** and allow Drift through the Windows private-network firewall prompt.
2. On each Mac or Android device, enter the Windows PC's local IP address and pairing key, then connect.
3. Start sharing on Windows. Choose the device that should receive the PC's input.
4. If a Mac or Android device should also send its attached keyboard or mouse, turn on that device's **Send this device's keyboard and mouse** option. A paired screen can still receive input when this source option is off.
5. On macOS, grant Drift **Accessibility** and **Input Monitoring** access in System Settings. On Android, enable Drift's **Accessibility Service**. To type into Android apps, enable Drift Keyboard and select it while a text field is focused.

The Windows app displays the IP address and pairing key. The key is saved locally with Windows DPAPI, macOS Keychain, or Android Keystore protection. Create a new key on Windows to rotate it and disconnect existing peers.

## Build

Build each app on its native platform:

### Windows

Install the .NET 10 SDK and run PowerShell from the repository root:

```powershell
dotnet publish apps/windows/Drift.Windows.csproj -c Release -r win-x64 --self-contained true
```

The publish folder is under `apps/windows/bin/Release/`.

### macOS

On macOS 14 or later, install Xcode Command Line Tools and Swift 5.9 or later, then run:

```sh
bash apps/macos/build-macos.sh
```

The app bundle is written to `apps/macos/.build/Drift.app` and signed locally with an ad-hoc signature. macOS will still ask you to grant the input permissions on first use.

### Android

Install JDK 17, Gradle 8.10.2, and Android SDK 35, then run from the repository root:

```sh
gradle -p apps/android :app:assembleDebug
```

The debug APK is written to `apps/android/app/build/outputs/apk/debug/app-debug.apk`. The minimum supported Android version is Android 14 (API 34).

The GitHub Actions workflow builds all three apps on their native platforms and stores the resulting packages as downloadable run artifacts.

## Network and privacy

The Windows host listens on TCP port **42857**. Keep paired devices on a network you trust and allow inbound access to this port only on your private network. Drift has no cloud relay, account, or automatic LAN discovery. The pairing key authenticates peers; input frames use per-connection AES-GCM keys and ordered counters.

Sharing is off until enabled. macOS input capture requires the permissions shown in the app. Android requires the user to enable its accessibility service; Drift Keyboard only enters text through the focused text field's input connection. Android does not let an ordinary app inject arbitrary keys into every system surface, so secure screens and some apps can reject forwarded keyboard actions. Android pointer control uses accessibility gestures, not a second hardware cursor. Touchscreen taps remain local to the phone.

## Current scope

Drift currently uses a Windows hub and manual host entry. Paired devices form a simple left-to-right chain in connection order rather than a draggable map of monitor sizes. Automatic discovery, clipboard sync, background startup, and signed distribution packages are not included.

## Project layout

```text
apps/
  android/                 Android client
  macos/                   macOS client
  windows/                 Windows hub
shared/
  Drift.Protocol/          Authenticated encrypted wire protocol
docs/
  NATIVE_ARCHITECTURE.md   Input routing and platform notes
```

## License

Drift is available under the [MIT License](LICENSE).
