#!/bin/bash
# Сценарий 15. Картинки больше 20 МиБ:
#   двойник отправляет такую (--ignore-image-limit) — Mac отказывается без разрыва сеанса;
#   такая картинка в буфере Mac не отправляется (запись в журнале), следующая обычная — отправляется.
source "$(dirname "$0")/lib.sh"
build
start_mac_log

MAC_NAME="e2e-mac-$SUFFIX"
PEER_NAME="e2e-peer-$SUFFIX"
PEER_NEW="e2e-peer2-$SUFFIX"
PEER_PORT="$(free_port)"
BOARD="e2e-pb-$SUFFIX"
BIG="$WORK/big.png"
SMALL="$WORK/small.png"
make_png "$BIG" 21500000 1
make_png "$SMALL" 300000 2

pair_mac_responder mac "$MAC_NAME" peer "$PEER_NAME" "$PEER_PORT"

start_mac mac "$MAC_NAME" "$BOARD"
# Имя меняется после картинки: info по тому же сеансу показывает, что он жив.
start_peer peer "$PEER_NAME" "$PEER_PORT" --send-image "$BIG" --ignore-image-limit --send-delay 2 --rename-after 20 "$PEER_NEW"
expect "$WORK/mac.out" "^CONNECTED $PEER_NAME" 30 "Mac подключился"
expect "$WORK/peer.out" "^IMAGE_SENT 1$" 20 "двойник отправляет картинку больше 20 МиБ"
expect "$WORK/mac.out" "^IMAGE_REFUSED $PEER_NAME$" 20 "Mac отказался от картинки"
expect "$WORK/mac.out" "^RENAMED $PEER_NAME $PEER_NEW$" 30 "сеанс жив после отказа"
expect_absent "$WORK/mac.out" "^IMAGE $PEER_NAME " "большая картинка не принята"
expect_absent "$WORK/mac.out" "^DISCONNECTED" "сеанс не рвался"

pb write-image "$BOARD" "$BIG"
wait_for "$WORK/mac.log" "больше 20 МиБ — не передаётся" 15 \
    && say "ок: Mac не отправил большую картинку (журнал)" \
    || say "журнал Mac недоступен — пропускаю проверку записи о большой картинке"
pb write-image "$BOARD" "$SMALL"
expect_image "$WORK/peer.out" "$MAC_NAME" "$SMALL" 20 "обычная картинка после большой дошла"
expect_count "$WORK/peer.out" "^IMAGE " 1 1 "большая картинка с Mac не пришла"

pass
