#!/bin/bash
# Сценарий 17. Переименование во время сеанса в обе стороны:
#   двойник меняет имя (--rename-after) — Mac печатает RENAMED и сохраняет новое имя;
#   Mac меняет имя (--rename-after) — двойник видит новое имя.
source "$(dirname "$0")/lib.sh"
build

MAC_NAME="e2e-mac-$SUFFIX"
MAC_NEW="e2e-mac2-$SUFFIX"
PEER_NAME="e2e-peer-$SUFFIX"
PEER_NEW="e2e-peer2-$SUFFIX"
PEER_PORT="$(free_port)"

pair_mac_responder mac "$MAC_NAME" peer "$PEER_NAME" "$PEER_PORT"

start_mac mac "$MAC_NAME" "e2e-pb-$SUFFIX" --rename-after 12 "$MAC_NEW"
start_peer peer "$PEER_NAME" "$PEER_PORT" --rename-after 6 "$PEER_NEW" --send "от нового имени $SUFFIX" --send-delay 8
expect "$WORK/mac.out" "^CONNECTED $PEER_NAME" 30 "Mac подключился"
expect "$WORK/mac.out" "^RENAMED $PEER_NAME $PEER_NEW$" 30 "Mac увидел новое имя двойника"
expect "$WORK/mac.out" "^INFO $PEER_NEW os=windows form=desktop caps=file,image$" 5 "Mac: info с новым именем"
expect_clip mac "$WORK/mac.out" "$PEER_NEW" "от нового имени $SUFFIX" 15 "текст пришёл от нового имени"
expect "$WORK/mac.out" "^NAME $MAC_NEW$" 30 "Mac сменил своё имя"
expect "$WORK/peer.out" "^RENAMED $MAC_NAME $MAC_NEW$" 10 "двойник увидел новое имя Mac"
expect_absent "$WORK/mac.out" "^DISCONNECTED" "сеанс не рвался"
sleep 1
grep -q "\"$PEER_NEW\"" "$WORK/mac/devices.json" || fail "Mac не сохранил новое имя двойника"
say "ок: Mac сохранил новое имя двойника"

pass
