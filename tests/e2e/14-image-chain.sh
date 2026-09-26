#!/bin/bash
# Сценарий 14. Картинка по цепочке двойник A → Mac → двойник B (A и B между собой не связаны):
# B получает ту же картинку ровно один раз, к A она не возвращается, у Mac она в буфере.
source "$(dirname "$0")/lib.sh"
build

MAC_NAME="e2e-mac-$SUFFIX"
A_NAME="e2e-a-$SUFFIX"
B_NAME="e2e-b-$SUFFIX"
A_PORT="$(free_port)"
B_PORT="$(free_port)"
BOARD="e2e-pb-$SUFFIX"
IMAGE="$WORK/image.png"
make_png "$IMAGE" 1500000 1

pair_mac_responder mac "$MAC_NAME" a "$A_NAME" "$A_PORT"
pair_mac_responder mac "$MAC_NAME" b "$B_NAME" "$B_PORT"

start_mac mac "$MAC_NAME" "$BOARD"
start_peer b "$B_NAME" "$B_PORT" --save-images "$WORK/saved-b"
expect "$WORK/mac.out" "^CONNECTED $B_NAME" 30 "Mac подключён к B"
start_peer a "$A_NAME" "$A_PORT" --send-image "$IMAGE" --send-delay 2
expect "$WORK/mac.out" "^CONNECTED $A_NAME" 30 "Mac подключён к A"

expect_image "$WORK/mac.out" "$A_NAME" "$IMAGE" 20 "Mac получил картинку A"
expect_image "$WORK/b.out" "$MAC_NAME" "$IMAGE" 20 "B получил картинку A через Mac"
[ -f "$WORK/saved-b/$(file_sha "$IMAGE").png" ] || fail "B не сохранил картинку"
pb read-data "$BOARD" public.png "$WORK/from-board.png" || fail "в буфере Mac нет PNG"
[ "$(file_sha "$WORK/from-board.png")" = "$(file_sha "$IMAGE")" ] || fail "в буфере Mac не та картинка"
say "ок: пересланная картинка в буфере Mac"
sleep 3
expect_absent "$WORK/a.out" "^IMAGE " "картинка не вернулась к A"
expect_count "$WORK/b.out" "^IMAGE " 1 1 "B получил ровно одну картинку"

pass
