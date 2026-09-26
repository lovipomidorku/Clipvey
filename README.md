# Clipvey

**English** · [Русский](README.ru.md)

Syncs the clipboard (text and images) between Mac and Windows over the local network.

## Install

- **Mac** (macOS 14+): `mac/scripts/build.sh --install` → `~/Applications/Clipvey.app`, icon in the menu bar.
- **Windows** 10/11 x64: `windows/publish.sh` → `dist/windows/Clipvey.exe`, a single file, no installation needed.

On first launch allow: local network access and clipboard access on Mac ("Always Allow" in Privacy & Security), private networks in Windows Firewall. The exe is unsigned: in SmartScreen click "More info" → "Run anyway".

## Usage

1. Click "Pair New Device" on both computers, pick one from the list on the other, enter the code and confirm with "Done".
2. A new device only needs to be paired with one of the others: data is relayed along the chain.
3. The switch next to a device pauses syncing; "Unpair" removes the pairing.

In the settings: interface language, this computer's name, local names for other devices, "Sync images", automatic update checks (daily, installed only after you confirm).

Not synced: passwords (concealed clipboard types), text over 1 MiB, images over 20 MiB, files, formatting.

## Data

| | Mac | Windows |
|---|---|---|
| Key and devices | `~/Library/Application Support/Clipvey/` | `%APPDATA%\Clipvey\` |
| Log | `log stream --predicate 'subsystem == "io.github.lovipomidorku.clipvey"'` | `%LOCALAPPDATA%\Clipvey\clipvey.log` |

## Security

One-time 6-digit pairing code that is never sent over the network; AES-256-GCM encryption with new keys for every connection; only paired devices can connect. Protocol: [docs/protocol.md](docs/protocol.md).

<!-- Support: fill in the link and remove this comment.
## Support

Clipvey is free. You can support its development: LINK
-->

## License

All rights reserved. Official builds are free; the code may be read and built for personal use, but not redistributed. See [LICENSE](LICENSE).
