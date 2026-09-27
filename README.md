# Clipvey

**English** · [Русский](README.ru.md)

Syncs the clipboard (text, images, files and folders) between Mac and Windows over the local network.

## Download

- **Mac** (macOS 14+, Apple Silicon and Intel): [Clipvey.dmg](https://github.com/lovipomidorku/Clipvey/releases/latest/download/Clipvey.dmg) — drag Clipvey to Applications.
- **Windows** 10/11 x64: [Clipvey.exe](https://github.com/lovipomidorku/Clipvey/releases/latest/download/Clipvey.exe) — a single file, no installation needed.

The apps are not signed yet. Mac: on first launch open System Settings → Privacy & Security and click "Open Anyway". Windows: in SmartScreen click "More info" → "Run anyway".

On first launch allow local network and clipboard access on Mac ("Always Allow"), and private networks in Windows Firewall.

Build from source: `mac/scripts/build.sh --install`, `windows/publish.sh`.

## Usage

1. In Settings, click "Add Device" on both computers, pick one from the list on the other, enter the code and confirm with "Done".
2. A new device only needs to be paired with one of the others: data is relayed along the chain.
3. The switch next to a device pauses syncing; "Unpair" removes the pairing.

Copied files and folders up to 50 MB can be pasted right away. Larger ones are downloaded on Windows when you paste them into a folder, and on Mac when you click "Download" in the pop-up (saved to Downloads → Clipvey).

In the settings: interface language, this computer's name, local names for other devices, "Sync images", "Sync files", automatic update checks (daily, installed only after you confirm).

Not synced: passwords (concealed clipboard types), text over 1 MiB, images over 20 MiB, files over 10 GB at once, formatting.

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
