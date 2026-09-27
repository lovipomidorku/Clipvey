#!/bin/bash
# Сценарий 12. Картинка двойник → Mac: Mac получает её целиком (тот же sha256), кладёт PNG в именованный
# буфер вместе с маркером Clipvey и не отправляет обратно (нет эха). Заодно — тип устройства и caps в ready.
source "$(dirname "$0")/lib.sh"
build

MAC_NAME="e2e-mac-$SUFFIX"
PEER_NAME="e2e-peer-$SUFFIX"
PEER_PORT="$(free_port)"
BOARD="e2e-pb-$SUFFIX"
IMAGE="$WORK/image.png"
make_png "$IMAGE" 1200000 1

pair_mac_responder mac "$MAC_NAME" peer "$PEER_NAME" "$PEER_PORT"

start_mac mac "$MAC_NAME" "$BOARD"
start_peer peer "$PEER_NAME" "$PEER_PORT" --send-image "$IMAGE" --send-delay 2
expect "$WORK/mac.out" "^CONNECTED $PEER_NAME" 30 "Mac подключился"
expect "$WORK/mac.out" "^INFO $PEER_NAME os=windows form=desktop caps=file,image$" 5 "Mac знает тип и caps двойника"
expect "$WORK/peer.out" "^INFO $MAC_NAME os=mac form=(laptop|desktop) caps=file,image$" 30 "двойник знает тип и caps Mac"
expect "$WORK/peer.out" "^IMAGE_SENT 1$" 20 "двойник отправил картинку"
expect_image "$WORK/mac.out" "$PEER_NAME" "$IMAGE" 20 "Mac получил картинку"

sleep 1
pb types "$BOARD" > "$WORK/types.txt"
grep -qx "public.png" "$WORK/types.txt" || fail "в буфере Mac нет public.png: $(tr '\n' ' ' < "$WORK/types.txt")"
grep -qx "$MARKER" "$WORK/types.txt" || fail "в буфере Mac нет маркера $MARKER"
say "ок: в буфере Mac PNG и маркер"
pb read-data "$BOARD" public.png "$WORK/from-board.png" || fail "не прочитан PNG из буфера Mac"
[ "$(file_sha "$WORK/from-board.png")" = "$(file_sha "$IMAGE")" ] || fail "в буфере Mac не та картинка"
say "ок: в буфере Mac та же картинка"

sleep 3
expect_absent "$WORK/peer.out" "^IMAGE " "картинка не вернулась к двойнику (нет эха)"

pass
