# Clipvey

**English** · [Русский](README.ru.md)

Clipboard sync between a Mac and Windows PCs on your local network: text and images. Copy on one device, paste on any other.

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

The interface is in English and Russian. By default it follows the system language; to change it, use Language in the settings on the Mac, or in the panel or icon menu on Windows.

1. **Pair devices.** Click "Pair New Device" on both. On one, pick the other from the list and enter the code it shows; on the other, click "Done". The code is single-use and valid for 2 minutes.
2. **More devices.** Each new device only needs to be paired with any one device that's already paired: data is relayed along the chain. Pairing more devices directly makes delivery more reliable without creating duplicates.
3. **Turn a device off.** The switch next to it stops syncing without removing the pairing. "Unpair" removes the pairing for good.
4. **Names.** Set this computer's name in the settings; other devices see it. You can also rename any other device: that name is only shown on your computer, and "Reset" brings back the name the device reports.
5. **Images.** PNG, JPEG and screenshots up to 20 MiB are synced; HEIC photos from an iPhone are converted to JPEG. "Sync images" in the settings turns this off. An image only goes to devices that have image sync turned on too.
6. **Updates.** Clipvey checks for a new version at launch and once a day, and installs it only after you confirm. Automatic checks can be turned off in the settings.

What is not synced:
- **passwords**, i.e. anything password managers mark as concealed;
- **text larger than 1 MiB and images larger than 20 MiB**;
- **files and formatting**: text arrives as plain text.

## Where things are stored

| | Mac | Windows |
|---|---|---|
| Device key and device list | `~/Library/Application Support/Clipvey/` | `%APPDATA%\Clipvey\` (the key is encrypted with DPAPI) |
| Log | `log stream --predicate 'subsystem == "io.github.lovipomidorku.clipvey"' --level info` | `%LOCALAPPDATA%\Clipvey\clipvey.log` (icon menu → "Open log") |
| Settings | `defaults read io.github.lovipomidorku.clipvey` | launch at login: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` |

## Security

- Pairing uses a single-use 6-digit code. The code itself is never sent over the network, and guessing it in the single allowed attempt is impractical.
- All data between devices is encrypted (AES-256-GCM), with fresh session keys on every connection.
- Only paired devices can connect.
- Details: `docs/protocol.md` (in Russian).

<!-- Support: fill in the link and remove this comment.
## Support

Clipvey is free. If it is useful to you, you can support its development: LINK
-->

## License

All rights reserved: you may read the code and build it for yourself, but not redistribute it or publish modified versions. Official builds are free. See [LICENSE](LICENSE).
