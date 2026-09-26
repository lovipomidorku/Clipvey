#!/bin/bash
# Сценарий 16. Выключенные картинки у получателя:
#   у Mac «Передавать картинки» выключено — он не заявляет image, двойник ему картинок не шлёт;
#   у двойника выключено — Mac ему картинок не шлёт, а текст по-прежнему передаётся.
source "$(dirname "$0")/lib.sh"
build

MAC_NAME="e2e-mac-$SUFFIX"
PEER_NAME="e2e-peer-$SUFFIX"
PEER_PORT="$(free_port)"
BOARD="e2e-pb-$SUFFIX"
IMAGE="$WORK/image.png"
make_png "$IMAGE" 300000 1

pair_mac_responder mac "$MAC_NAME" peer "$PEER_NAME" "$PEER_PORT"

# Часть 1: картинки выключены у Mac.
start_mac mac "$MAC_NAME" "$BOARD" --images off
MAC_PID=$LAST_PID
start_peer peer "$PEER_NAME" "$PEER_PORT" --send-image "$IMAGE" --send-delay 2
PEER_PID=$LAST_PID
expect "$WORK/peer.out" "^INFO $MAC_NAME os=mac form=(laptop|desktop) caps=-$" 30 "двойник видит, что Mac картинки не принимает"
expect "$WORK/peer.out" "^IMAGE_SENT 0$" 20 "двойник не отправил картинку"
pb write-image "$BOARD" "$IMAGE"
sleep 3
pb write "$BOARD" "текст при выключенных картинках $SUFFIX"
expect_clip peer "$WORK/peer.out" "$MAC_NAME" "текст при выключенных картинках $SUFFIX" 15 "текст передаётся"
expect_absent "$WORK/mac.out" "^IMAGE " "Mac картинок не получал"
expect_absent "$WORK/peer.out" "^IMAGE " "Mac картинок не отправлял"
stop "$PEER_PID"
stop "$MAC_PID"

# Часть 2: картинки выключены у двойника.
start_mac mac "$MAC_NAME" "$BOARD"
start_peer peer "$PEER_NAME" "$PEER_PORT" --images off
expect "$WORK/mac.out" "^INFO $PEER_NAME os=windows form=desktop caps=-$" 30 "Mac видит, что двойник картинки не принимает"
pb write-image "$BOARD" "$IMAGE"
sleep 3
pb write "$BOARD" "после картинки $SUFFIX"
expect_clip peer "$WORK/peer.out" "$MAC_NAME" "после картинки $SUFFIX" 15 "Mac обработал буфер: текст дошёл"
expect_absent "$WORK/peer.out" "^IMAGE " "двойник с выключенными картинками их не получил"

pass
