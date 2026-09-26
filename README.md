# Clipvey

**English** · [Русский](README.ru.md)

Clipboard text sync between a Mac and Windows PCs on your local network. Copy on one device, paste on any other.

Text from an iPhone gets through via the Mac: Apple's Universal Clipboard carries it from the iPhone to the Mac, and Clipvey passes it on to Windows. Requirements: the Mac is on and awake, the iPhone is nearby, both use the same Apple ID, and Handoff is on.

## Installation

- **Mac:** `mac/scripts/build.sh --install`. The app is installed to `~/Applications/Clipvey.app` and its icon appears in the menu bar.
- **Windows 10/11 x64:** build with `windows/publish.sh` and copy `dist/windows/Clipvey.exe` to the PC any way you like. Nothing needs to be installed: just run the file and its icon appears in the notification area.

On first launch the system asks a few questions. Answer "Allow" to all of them:
- **Mac, local network access.** Without it Clipvey can't find other devices.
- **Mac, clipboard access.** macOS 15.4+ asks when an app reads the clipboard. Choose "Always Allow" for Clipvey in System Settings → Privacy & Security.
- **Windows Firewall.** Allow access on private networks.
- **Little Snitch** (if installed): allow Clipvey's incoming and outgoing connections on the local network.

The unsigned `.exe` triggers a SmartScreen warning on first launch. Click "More info" → "Run anyway".

## Usage

The interface is in Russian for now; button names are given in the original.

1. **Pair devices.** Click «Связать новое устройство» (Pair new device) on both. On one, pick the other from the list and enter the code it shows; on the other, click «Готово» (Done). The code is single-use and valid for 2 minutes.
2. **More devices.** Each new device only needs to be paired with any one device that's already paired: text is relayed along the chain. Pairing more devices directly makes delivery more reliable without creating duplicates.
3. **Turn a device off.** The switch next to it stops syncing without removing the pairing. «Разорвать связь» (Unpair) removes the pairing for good.

What is not synced:
- **passwords**, i.e. anything password managers mark as concealed;
- **text larger than 1 MiB**;
- **files, images and formatting**: the first version syncs plain text only.

## Where things are stored

| | Mac | Windows |
|---|---|---|
| Device key and device list | `~/Library/Application Support/Clipvey/` | `%APPDATA%\Clipvey\` (the key is encrypted with DPAPI) |
| Log | `log stream --predicate 'subsystem == "io.github.lovipomidorku.clipvey"' --level info` | `%LOCALAPPDATA%\Clipvey\clipvey.log` (icon menu → «Открыть журнал») |
| Settings | `defaults read io.github.lovipomidorku.clipvey` | launch at login: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` |

## Security

- Pairing uses a single-use 6-digit code. The code itself is never sent over the network, and guessing it in the single allowed attempt is impractical.
- All data between devices is encrypted (AES-256-GCM), with fresh session keys on every connection.
- Only paired devices can connect.
- Details: `docs/protocol.md` (in Russian).

## License

All rights reserved: you may read the code and build it for yourself, but not redistribute it or publish modified versions. Official builds are free. See [LICENSE](LICENSE).
