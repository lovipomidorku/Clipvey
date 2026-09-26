#!/bin/bash
# Сценарий 20. Двойник 0.1.0 в роли I связывается с новым Mac (Mac — R). 0.1.0 находит Mac только
# через mDNS (флага --pair-address у него нет), поэтому сценарию нужен рабочий mDNS двойников
# и в режиме loopback он тоже идёт через mDNS.
source "$(dirname "$0")/lib.sh"
build
require_old_peer

MAC_NAME="e2e-mac-$SUFFIX"
OLD_NAME="e2e-old-$SUFFIX"

start_mac mac "$MAC_NAME" "e2e-pb-$SUFFIX" --pair --auto-confirm
expect "$WORK/mac.out" "^READY " 20 "Mac запущен (R)"
start_old_peer old "$OLD_NAME" "$(free_port)" --pair-with "$MAC_NAME" --code-file "$WORK/mac.out"
expect "$WORK/mac.out" "^PAIRING_CODE [0-9]{6} FROM $OLD_NAME" 30 "Mac показал код (0.1.0 нашёл Mac через mDNS)"
expect "$WORK/old.out" "^PAIRED $MAC_NAME" 30 "0.1.0 связан с Mac (Mac — R)"
expect "$WORK/mac.out" "^PAIRED $OLD_NAME" 30 "Mac связан с 0.1.0 (Mac — R)"
"$OLD_PEER" list --data "$WORK/old" > "$WORK/old-list.out"
expect "$WORK/old-list.out" "^DEVICE [0-9a-f]{32} $MAC_NAME enabled=True" 1 "0.1.0 сохранил Mac"

pass
