# Drift

**One keyboard and mouse for your Windows PC and paired devices.**

Drift is a native Windows hub for moving your pointer between screens and sharing keyboard and mouse input over your local network. The Windows release is a single self-contained EXE. You do not need to install .NET.

## Download for Windows

Download **[Drift.exe from the latest GitHub release](https://github.com/graythalon/sadmoose/releases/latest)** and run it. The release targets Windows 10/11, x64.

The EXE is currently unsigned, so Windows may show a SmartScreen prompt. Only run it if you downloaded it from this repository or built it yourself.

## Connect a device

1. Open Drift on Windows. Select **Create pairing key** and approve the Windows firewall prompt for your private network.
2. On the paired device, enter the Windows PC's local IP address and the displayed key, then connect.
3. In Drift, choose the device under **Input owner** and select **Start sharing**.
4. Move the Windows pointer to a screen edge to enter the selected device. Move to an edge on that device to return control to Windows or continue to the next connected screen.

The Android client is a separate APK. Find the latest **Drift-Android-debug** artifact in [GitHub Actions](https://github.com/graythalon/sadmoose/actions/workflows/native-build.yml). Android requires Drift's Accessibility Service to receive pointer gestures. To type into Android apps, enable Drift Keyboard and select it in Android settings. Turn on **Send this device's keyboard and mouse** to share peripherals attached to Android back through the Windows hub.

On Windows, press **Ctrl + Alt + Shift + Backspace** to return control to the PC immediately. You can pause sharing or disconnect any paired device from the hub window.

## Windows build

The release is a self-contained, compressed single-file publish. To build it from the repository, install the .NET 10 SDK and run:

```powershell
dotnet publish apps/windows/Drift.Windows.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o artifacts/windows
```

The resulting app is `artifacts/windows/Drift.exe`. GitHub Actions builds and attaches this EXE to version tags such as `v0.1.0`; regular builds are available as downloadable workflow artifacts.

## Android build

Install JDK 17, Gradle 8.10.2, and Android SDK 35, then run:

```powershell
gradle -p apps/android :app:assembleDebug
```

The APK is written to `apps/android/app/build/outputs/apk/debug/app-debug.apk`. Drift supports Android 14 (API 34) or later.

## Privacy and platform notes

- Drift connects directly over your local network. It has no cloud relay, account, or automatic device discovery.
- The Windows hub listens on TCP port **42857**. Allow it only on a trusted private network.
- Pairing uses a random 256-bit key. Peers authenticate and encrypt input frames with per-connection AES-GCM keys.
- The Windows pairing key is protected with DPAPI. Sharing starts only when you turn it on.
- Android limits system-wide keyboard injection. Secure screens and some apps may reject forwarded key actions. Android pointer control uses accessibility gestures rather than a system-wide hardware cursor.
- macOS app development is paused for now; its source remains in `apps/macos` and is not included in the current Windows release workflow.

## Project layout

```text
apps/
  windows/                 Windows hub and single-file release
  android/                 Android companion app
  macos/                   Paused macOS client source
shared/
  Drift.Protocol/          Authenticated encrypted input protocol
docs/
  NATIVE_ARCHITECTURE.md   Input routing and platform notes
```

## License

Drift is available under the [MIT License](LICENSE).
