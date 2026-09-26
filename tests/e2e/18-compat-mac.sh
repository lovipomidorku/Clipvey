#!/bin/bash
# Сценарий 18. Совместимость нового Mac с двойником 0.1.0:
#   связывание в обеих ролях, текст в обе стороны;
#   0.1.0 не заявляет image — Mac ему картинок не шлёт, info (переименование Mac) 0.1.0 пропускает.
# Mac — I (в режиме loopback — по 127.0.0.1), сеанс — по этой связи. 0.1.0 в роли I — сценарий 20.
source "$(dirname "$0")/lib.sh"
build
require_old_peer

MAC_NAME="e2e-mac-$SUFFIX"
MAC_NEW="e2e-mac2-$SUFFIX"
OLD_NAME="e2e-old-$SUFFIX"
OLD_PORT="$(free_port)"
BOARD="e2e-pb-$SUFFIX"
IMAGE="$WORK/image.png"
make_png "$IMAGE" 300000 1

# Связывание: Mac — I, 0.1.0 — R.
start_old_peer old "$OLD_NAME" "$OLD_PORT" --pair --auto-confirm
OLD_PID=$LAST_PID
expect "$WORK/old.out" "^READY " 20 "0.1.0 запущен (R)"
DIRECT=()
loopback_mode && DIRECT=(--pair-address "127.0.0.1:$OLD_PORT")
start_mac mac "$MAC_NAME" "e2e-pb1-$SUFFIX" --pair-with "$OLD_NAME" --code-file "$WORK/old.out" ${DIRECT[@]+"${DIRECT[@]}"}
MAC_PID=$LAST_PID
expect "$WORK/old.out" "^PAIRING_CODE [0-9]{6} FROM $MAC_NAME" 30 "0.1.0 показал код"
expect "$WORK/mac.out" "^PAIRED $OLD_NAME" 30 "Mac связан с 0.1.0 (Mac — I)"
expect "$WORK/old.out" "^PAIRED $MAC_NAME" 30 "0.1.0 связан с Mac (Mac — I)"
stop "$MAC_PID"
stop "$OLD_PID"
fix_saved_ports mac old "$OLD_PORT"

start_mac mac "$MAC_NAME" "$BOARD" --rename-after 15 "$MAC_NEW"
start_old_peer old "$OLD_NAME" "$OLD_PORT" --send "от 0.1.0 $SUFFIX" --send-delay 3
expect "$WORK/mac.out" "^CONNECTED $OLD_NAME" 30 "Mac подключён к 0.1.0"
expect "$WORK/mac.out" "^INFO $OLD_NAME os=- form=- caps=-$" 5 "у 0.1.0 нет типа и caps"
expect "$WORK/old.out" "^CONNECTED $MAC_NAME" 30 "0.1.0 подключён к Mac"
expect_clip mac "$WORK/mac.out" "$OLD_NAME" "от 0.1.0 $SUFFIX" 20 "Mac получил текст 0.1.0"
pb write-image "$BOARD" "$IMAGE"
sleep 3
pb write "$BOARD" "от Mac $SUFFIX"
expect_clip peer "$WORK/old.out" "$MAC_NAME" "от Mac $SUFFIX" 15 "0.1.0 получил текст Mac"
expect "$WORK/mac.out" "^NAME $MAC_NEW$" 30 "Mac сменил имя"
expect "$WORK/old.err" "Неизвестное сообщение от «${MAC_NAME}»: info" 10 "0.1.0 пропустил info"
pb write "$BOARD" "после info $SUFFIX"
expect_clip peer "$WORK/old.out" "$MAC_NAME" "после info $SUFFIX" 15 "текст после info дошёл"
expect_absent "$WORK/old.err" "blob_" "0.1.0 не получал кадров картинки"
expect_absent "$WORK/old.out" "^DISCONNECTED" "сеанс с 0.1.0 не рвался"

pass
