#!/bin/bash
# Сценарий 18. Совместимость нового Mac с двойником 0.1.0:
#   связывание в обеих ролях, текст в обе стороны;
#   0.1.0 не заявляет image — Mac ему картинок не шлёт, сеанс не рвётся.
# Двойник 0.1.0 находит Mac и объявляет себя только через mDNS, поэтому сценарию нужен рабочий mDNS двойников.
source "$(dirname "$0")/lib.sh"
build
require_old_peer

MAC_NAME="e2e-mac-$SUFFIX"
OLD_NAME="e2e-old-$SUFFIX"
OLD_PORT="$(free_port)"
BOARD="e2e-pb-$SUFFIX"
IMAGE="$WORK/image.png"
make_png "$IMAGE" 300000 1

# Часть 1: Mac — R, 0.1.0 — I.
start_mac mac "$MAC_NAME" "e2e-pb1-$SUFFIX" --pair --auto-confirm
MAC_PID=$LAST_PID
expect "$WORK/mac.out" "^READY " 20 "Mac запущен"
start_old_peer old "$OLD_NAME" "$OLD_PORT" --pair-with "$MAC_NAME" --code-file "$WORK/mac.out"
OLD_PID=$LAST_PID
expect "$WORK/mac.out" "^PAIRING_CODE [0-9]{6} FROM $OLD_NAME" 30 "Mac показал код 0.1.0"
expect "$WORK/old.out" "^PAIRED $MAC_NAME" 30 "0.1.0 связан с Mac (Mac — R)"
expect "$WORK/mac.out" "^PAIRED $OLD_NAME" 30 "Mac связан с 0.1.0 (Mac — R)"
stop "$OLD_PID"
stop "$MAC_PID"

# Часть 2: Mac — I, 0.1.0 — R (свои папки данных).
start_old_peer old2 "$OLD_NAME-r" "$(free_port)" --pair --auto-confirm
OLD2_PID=$LAST_PID
expect "$WORK/old2.out" "^READY " 20 "0.1.0 запущен (R)"
start_mac mac2 "$MAC_NAME-i" "e2e-pb2-$SUFFIX" --pair-with "$OLD_NAME-r" --code-file "$WORK/old2.out"
MAC2_PID=$LAST_PID
expect "$WORK/old2.out" "^PAIRING_CODE [0-9]{6} FROM $MAC_NAME-i" 30 "0.1.0 показал код"
expect "$WORK/mac2.out" "^PAIRED $OLD_NAME-r" 30 "Mac связан с 0.1.0 (Mac — I)"
expect "$WORK/old2.out" "^PAIRED $MAC_NAME-i" 30 "0.1.0 связан с Mac (Mac — I)"
stop "$MAC2_PID"
stop "$OLD2_PID"

# Сеанс по связи из части 1.
fix_saved_ports mac old "$OLD_PORT"
start_mac mac "$MAC_NAME" "$BOARD"
start_old_peer old "$OLD_NAME" "$OLD_PORT" --send "от 0.1.0 $SUFFIX" --send-delay 6
expect "$WORK/mac.out" "^CONNECTED $OLD_NAME" 30 "Mac подключён к 0.1.0"
expect "$WORK/mac.out" "^INFO $OLD_NAME os=- form=- caps=-$" 5 "у 0.1.0 нет типа и caps"
expect "$WORK/old.out" "^CONNECTED $MAC_NAME" 30 "0.1.0 подключён к Mac"
expect_clip mac "$WORK/mac.out" "$OLD_NAME" "от 0.1.0 $SUFFIX" 20 "Mac получил текст 0.1.0"
pb write-image "$BOARD" "$IMAGE"
sleep 3
pb write "$BOARD" "от Mac $SUFFIX"
expect_clip peer "$WORK/old.out" "$MAC_NAME" "от Mac $SUFFIX" 15 "0.1.0 получил текст Mac"
expect_absent "$WORK/old.err" "blob_" "0.1.0 не получал кадров картинки"
expect_absent "$WORK/old.out" "^DISCONNECTED" "сеанс с 0.1.0 не рвался"

pass
