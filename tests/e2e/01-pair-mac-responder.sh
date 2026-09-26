#!/bin/bash
# Сценарий 1. Связывание, где Mac — R: Mac показывает код, двойник вводит его и связывается.
source "$(dirname "$0")/lib.sh"
build

MAC_NAME="e2e-mac-$SUFFIX"
PEER_NAME="e2e-peer-$SUFFIX"
PEER_PORT="$(free_port)"

pair_mac_responder mac "$MAC_NAME" peer "$PEER_NAME" "$PEER_PORT"

# Обе стороны сохранили связь.
"$PEER" list --data "$WORK/peer" > "$WORK/peer-list.out"
expect "$WORK/peer-list.out" "^DEVICE [0-9a-f]{32} $MAC_NAME enabled=True" 1 "двойник сохранил Mac"
grep -q "\"$PEER_NAME\"" "$WORK/mac/devices.json" || fail "Mac не сохранил двойника в devices.json"
say "ок: Mac сохранил двойника"

pass
