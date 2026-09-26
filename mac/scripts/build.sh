#!/bin/zsh
# Сборка приложения: swift build → .app → подпись (ad-hoc, для запуска на этом Mac).
#   scripts/build.sh            собрать в build/
#   scripts/build.sh --install  собрать, установить в ~/Applications и запустить
set -euo pipefail

APP_NAME="Clipvey"
BUNDLE_ID="io.github.lovipomidorku.clipvey"   # не меняйте: к нему привязаны настройки и разрешения macOS
EXECUTABLE="Clipvey"

ROOT="${0:A:h:h}"
cd "$ROOT"

# Версия — из файла VERSION в корне репозитория (docs/releases.md).
VERSION="$(tr -d '[:space:]' < "$ROOT/../VERSION")"
if [[ ! "$VERSION" =~ '^[0-9]+\.[0-9]+\.[0-9]+$' ]]; then
    echo "В VERSION ожидается версия вида 1.2.3, а там «$VERSION»" >&2
    exit 1
fi

# Пути к исходникам заменяются на «.», чтобы в бинарник не попали локальные папки.
SWIFT_FLAGS=(-c release -Xswiftc -file-prefix-map -Xswiftc "$ROOT=.")
swift build "${SWIFT_FLAGS[@]}"
BIN_DIR="$(swift build "${SWIFT_FLAGS[@]}" --show-bin-path)"

APP="$ROOT/build/$APP_NAME.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN_DIR/$EXECUTABLE" "$APP/Contents/MacOS/$EXECUTABLE"

# Иконка: из Resources/AppIcon.png (1024×1024, рисует scripts/make-icon.swift)
# нарезаем все размеры и собираем AppIcon.icns.
ICON_SOURCE="$ROOT/Resources/AppIcon.png"
ICON_PLIST_ENTRY=""
if [[ -f "$ICON_SOURCE" ]]; then
    ICONSET="$ROOT/build/AppIcon.iconset"
    rm -rf "$ICONSET"
    mkdir -p "$ICONSET"
    for size in 16 32 128 256 512; do
        sips -z $size $size "$ICON_SOURCE" --out "$ICONSET/icon_${size}x${size}.png" > /dev/null
        sips -z $((size * 2)) $((size * 2)) "$ICON_SOURCE" --out "$ICONSET/icon_${size}x${size}@2x.png" > /dev/null
    done
    iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"
    ICON_PLIST_ENTRY="<key>CFBundleIconFile</key>
    <string>AppIcon</string>"
fi

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleIdentifier</key>
    <string>$BUNDLE_ID</string>
    <key>CFBundleName</key>
    <string>$APP_NAME</string>
    <key>CFBundleDisplayName</key>
    <string>$APP_NAME</string>
    <key>CFBundleExecutable</key>
    <string>$EXECUTABLE</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleVersion</key>
    <string>$(date +%Y%m%d%H%M)</string>
    <key>CFBundleDevelopmentRegion</key>
    <string>en</string>
    <key>CFBundleLocalizations</key>
    <array>
        <string>en</string>
        <string>ru</string>
    </array>
    <key>LSMinimumSystemVersion</key>
    <string>14.0</string>
    <key>LSUIElement</key>
    <true/>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSLocalNetworkUsageDescription</key>
    <string>Clipvey finds your computers on the local network and syncs the clipboard with them.</string>
    <key>NSBonjourServices</key>
    <array>
        <string>_clipvey._tcp</string>
    </array>
    $ICON_PLIST_ENTRY
</dict>
</plist>
PLIST

# Переводы строк Info.plist (вопрос о доступе к локальной сети). Интерфейс переводится
# в коде (L("…", "…")), а эти файлы нужны, чтобы macOS показала вопрос на языке пользователя.
mkdir -p "$APP/Contents/Resources/en.lproj" "$APP/Contents/Resources/ru.lproj"
cat > "$APP/Contents/Resources/en.lproj/InfoPlist.strings" <<'STRINGS'
"NSLocalNetworkUsageDescription" = "Clipvey finds your computers on the local network and syncs the clipboard with them.";
STRINGS
cat > "$APP/Contents/Resources/ru.lproj/InfoPlist.strings" <<'STRINGS'
"NSLocalNetworkUsageDescription" = "Clipvey находит ваши компьютеры в локальной сети и синхронизирует с ними буфер обмена.";
STRINGS

codesign --force --sign - "$APP"
echo "Собрано: $APP"

if [[ "${1:-}" == "--install" ]]; then
    TARGET="$HOME/Applications/$APP_NAME.app"
    PROCESS="$TARGET/Contents/MacOS/$EXECUTABLE"

    # Закрываем запущенную копию и ждём, пока она завершится.
    pkill -f "$PROCESS" || true
    for _ in {1..30}; do
        pgrep -f "$PROCESS" > /dev/null || break
        sleep 0.1
    done

    mkdir -p "$HOME/Applications"
    rm -rf "$TARGET"
    cp -R "$APP" "$TARGET"
    open "$TARGET"
    echo "Установлено и запущено: $TARGET"
fi
