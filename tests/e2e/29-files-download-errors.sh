#!/bin/bash
# Сценарий 29. Ошибки «Загрузить» (больше 50 МиБ) — окошко с понятной причиной и кнопкой «Закрыть»:
#   источник отключился до нажатия — «устройство недоступно»;
#   в папку загрузок нельзя писать — «нет доступа»;
#   на диске меньше места, чем нужно (маленький образ диска, скрытый от Finder), — «недостаточно места»,
#   и скачивание даже не начинается.
source "$(dirname "$0")/lib.sh"
build

MAC_NAME="e2e-mac-$SUFFIX"
PEER_NAME="e2e-peer-$SUFFIX"
PEER_PORT="$(free_port)"
BOARD="e2e-pb-$SUFFIX"
head -c $(( 60 * 1048576 )) /dev/urandom > "$WORK/большой.bin"
mkdir -p "$WORK/readonly"
chmod 555 "$WORK/readonly"
VOLUME="$WORK/volume"
VOLUME_ATTACHED=0
# Отключается по флагу, а не по выводу mount: там путь с настоящим регистром букв (…/Clipvey), а $WORK может
# быть …/clipvey — и образ оставался подключённым.
detach_volume() {
    if [ "$VOLUME_ATTACHED" -eq 1 ]; then
        hdiutil detach "$VOLUME" -quiet -force 2>/dev/null || true
        VOLUME_ATTACHED=0
    fi
}
trap 'status=$?; detach_volume; chmod 755 "$WORK/readonly" 2>/dev/null; (exit $status); cleanup' EXIT

pair_mac_responder mac "$MAC_NAME" peer "$PEER_NAME" "$PEER_PORT"

# Часть 1: источник отключился до нажатия «Загрузить».
start_mac mac "$MAC_NAME" "$BOARD" --downloads "$WORK/downloads" --auto-download --toast-shots "$WORK/shots"
MAC_PID=$LAST_PID
start_peer peer "$PEER_NAME" "$PEER_PORT" --send-files "$WORK/большой.bin" --send-delay 2
PEER_PID=$LAST_PID
expect "$WORK/peer.out" "^FILES_SENT 1 " 30 "двойник отправил описание (60 МиБ)"
ID="$(offer_id "$WORK/peer.out")"
expect "$WORK/mac.out" "^TOAST offer $ID$" 10 "окошко «Загрузить»"
stop "$PEER_PID"
expect "$WORK/mac.out" "^DISCONNECTED $PEER_NAME" 5 "двойник отключился"
expect "$WORK/mac.out" "^TOAST_CLICK download ok$" 5 "нажата «Загрузить»"
expect "$WORK/mac.out" "^FILES_FAILED $ID DeviceUnavailable$" 5 "загрузка не удалась"
expect "$WORK/mac.out" "^TOAST failed DeviceUnavailable$" 5 "окошко: устройство недоступно"
sleep 2
[ -z "$(ls -A "$WORK/downloads" 2>/dev/null)" ] || fail "в папке загрузок что-то осталось: $(ls -A "$WORK/downloads")"

# Часть 2: в папку загрузок нельзя писать.
stop "$MAC_PID"
start_mac mac "$MAC_NAME" "$BOARD" --downloads "$WORK/readonly/Clipvey" --auto-download --toast-shots "$WORK/shots"
MAC_PID=$LAST_PID
start_peer peer "$PEER_NAME" "$PEER_PORT" --send-files "$WORK/большой.bin" --send-delay 2
PEER_PID=$LAST_PID
expect "$WORK/peer.out" "^FILES_SENT 1 " 30 "двойник отправил описание"
ID="$(offer_id "$WORK/peer.out")"
expect "$WORK/mac.out" "^TOAST_CLICK download ok$" 15 "нажата «Загрузить»"
expect "$WORK/mac.out" "^FILES_FAILED $ID NoAccess$" 5 "нет доступа к папке"
expect "$WORK/mac.out" "^TOAST failed NoAccess$" 5 "окошко: нет доступа"

# Часть 3: мало места — маленький образ диска вместо папки загрузок.
hdiutil create -size 20m -fs HFS+ -volname "e2e-$SUFFIX" -layout NONE "$WORK/small.dmg" -quiet || fail "hdiutil create"
mkdir -p "$VOLUME"
hdiutil attach "$WORK/small.dmg" -nobrowse -mountpoint "$VOLUME" -quiet || fail "hdiutil attach"
VOLUME_ATTACHED=1
stop "$MAC_PID"
start_mac mac "$MAC_NAME" "$BOARD" --downloads "$VOLUME/Clipvey" --auto-download --toast-shots "$WORK/shots"
MAC_PID=$LAST_PID
stop "$PEER_PID"
start_peer peer "$PEER_NAME" "$PEER_PORT" --send-files "$WORK/большой.bin" --send-delay 2
PEER_PID=$LAST_PID
expect "$WORK/peer.out" "^FILES_SENT 1 " 30 "двойник отправил описание"
ID="$(offer_id "$WORK/peer.out")"
expect "$WORK/mac.out" "^TOAST_CLICK download ok$" 15 "нажата «Загрузить»"
expect "$WORK/mac.out" "^FILES_FAILED $ID NoSpace$" 5 "места не хватает"
expect "$WORK/mac.out" "^TOAST failed NoSpace$" 5 "окошко: недостаточно места"
sleep 1
expect_absent "$WORK/peer.err" "Запрос [0-9]+ от" "скачивание даже не начиналось"
[ -z "$(ls -A "$VOLUME/Clipvey" 2>/dev/null)" ] || fail "на диске что-то осталось: $(ls -A "$VOLUME/Clipvey")"
stop "$MAC_PID"
detach_volume

pass
