#!/bin/bash
# Файлы релиза по docs/releases.md из уже собранных частей:
#   mac/build/Clipvey.app     (mac/scripts/build.sh)
#   dist/windows/Clipvey.exe  (windows/publish.sh или dotnet publish)
# Результат — dist/release/: Clipvey-mac.zip, Clipvey.exe, SHA256SUMS и, если задан ключ, SHA256SUMS.sig.
#
#   scripts/package-release.sh [--key-file FILE]
# Ключ подписи — переменная CLIPVEY_SIGNING_KEY или --key-file (base64 от 32 байт, см. scripts/sign-release.swift).
# Без ключа SHA256SUMS не подписывается (для пробной сборки).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP="$ROOT/mac/build/Clipvey.app"
EXE="$ROOT/dist/windows/Clipvey.exe"
OUT="$ROOT/dist/release"
SIGN=(swift "$ROOT/scripts/sign-release.swift")
# Открытый ключ, зашитый в приложения (docs/releases.md).
RELEASE_PUBLIC_KEY="BH2yxPlYNbki9eTLDttU3YTv5qjUJ8Biz4ChVq7y7OdeOqkmmtYi5Zch3XAfrF0x4qSrNvvNOaFMqMONHERTJJs="

KEY_ARGS=()
if [ "${1:-}" = "--key-file" ]; then
    [ -n "${2:-}" ] || { echo "после --key-file нужен путь" >&2; exit 1; }
    KEY_ARGS=(--key-file "$2")
fi

VERSION="$(tr -d '[:space:]' < "$ROOT/VERSION")"
[ -d "$APP" ] || { echo "Нет $APP — сначала mac/scripts/build.sh" >&2; exit 1; }
[ -f "$EXE" ] || { echo "Нет $EXE — сначала windows/publish.sh" >&2; exit 1; }
APP_VERSION="$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' "$APP/Contents/Info.plist")"
[ "$APP_VERSION" = "$VERSION" ] || { echo "Версия Clipvey.app $APP_VERSION, а в VERSION $VERSION — пересоберите" >&2; exit 1; }

rm -rf "$OUT"
mkdir -p "$OUT"
ditto -c -k --keepParent "$APP" "$OUT/Clipvey-mac.zip"
cp "$EXE" "$OUT/Clipvey.exe"

# «<sha256>  <имя>» — два пробела, \n в конце каждой строки; имена без пути.
(cd "$OUT" && shasum -a 256 Clipvey-mac.zip Clipvey.exe > SHA256SUMS)

if [ ${#KEY_ARGS[@]} -gt 0 ] || [ -n "${CLIPVEY_SIGNING_KEY:-}" ]; then
    "${SIGN[@]}" sign "$OUT/SHA256SUMS" ${KEY_ARGS[@]+"${KEY_ARGS[@]}"}
    PUBLIC_KEY="$("${SIGN[@]}" public-key ${KEY_ARGS[@]+"${KEY_ARGS[@]}"})"
    "${SIGN[@]}" verify "$OUT/SHA256SUMS" "$OUT/SHA256SUMS.sig" --public-key "$PUBLIC_KEY"
    if [ "$PUBLIC_KEY" != "$RELEASE_PUBLIC_KEY" ]; then
        echo "Внимание: ключ не тот, что зашит в приложения, — установленные копии не примут это обновление." >&2
    fi
else
    echo "Ключ не задан: SHA256SUMS не подписан." >&2
fi

echo "Релиз $VERSION: $OUT"
ls -l "$OUT"
cat "$OUT/SHA256SUMS"
