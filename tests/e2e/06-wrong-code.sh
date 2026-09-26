#!/bin/bash
# Сценарий 6. Неверный код: связывание не происходит ни в роли R, ни в роли I, связь не сохраняется.
source "$(dirname "$0")/lib.sh"
build

# wrong_code FILE — код из строки PAIRING_CODE, изменённый на единицу: заведомо неверный.
wrong_code() {
    local code
    code=$(grep -oE "^PAIRING_CODE [0-9]{6}" "$1" | head -n 1 | cut -d' ' -f2)
    printf 'PAIRING_CODE %06d\n' $(( (10#$code + 1) % 1000000 ))
}

# Часть 1: Mac — R, двойник вводит неверный код.
MAC_NAME="e2e-mac-$SUFFIX"
PEER_NAME="e2e-peer-$SUFFIX"
start_mac mac "$MAC_NAME" "e2e-pb-$SUFFIX" --pair --auto-confirm
MAC_PID=$LAST_PID
expect "$WORK/mac.out" "^READY " 20 "Mac запущен"
start_peer peer "$PEER_NAME" "$(free_port)" --pair-with "$MAC_NAME" --code-file "$WORK/wrong-1.txt" \
    --pair-address "127.0.0.1:$(mac_port "$WORK/mac.out")"
PEER_PID=$LAST_PID
expect "$WORK/mac.out" "^PAIRING_CODE [0-9]{6} FROM $PEER_NAME" 30 "Mac показал код"
wrong_code "$WORK/mac.out" > "$WORK/wrong-1.txt"
expect "$WORK/peer.out" "^(PAIRING_FAILED|PAIR_WITH_ERROR)" 15 "двойник: код не совпал"
expect "$WORK/mac.out" "^PAIRING_FAILED" 15 "Mac: связывание не удалось"
sleep 1
expect_absent "$WORK/mac.out" "^PAIRED" "Mac не связан"
expect_absent "$WORK/peer.out" "^PAIRED" "двойник не связан"
stop "$PEER_PID"
stop "$MAC_PID"
"$PEER" list --data "$WORK/peer" > "$WORK/peer-list.out"
expect_absent "$WORK/peer-list.out" "^DEVICE" "двойник ничего не сохранил"
if [ -f "$WORK/mac/devices.json" ] && grep -q "$PEER_NAME" "$WORK/mac/devices.json"; then
    fail "Mac сохранил двойника"
fi
say "ок: Mac ничего не сохранил"

# Часть 2: Mac — I и вводит неверный код.
MAC2_NAME="e2e-mac2-$SUFFIX"
PEER2_NAME="e2e-peer2-$SUFFIX"
PEER2_PORT="$(free_port)"
start_peer peer2 "$PEER2_NAME" "$PEER2_PORT" --pair --auto-confirm
expect "$WORK/peer2.out" "^READY " 20 "двойник запущен"
start_mac mac2 "$MAC2_NAME" "e2e-pb2-$SUFFIX" --pair-with "$PEER2_NAME" --code-file "$WORK/wrong-2.txt" \
    --pair-address "127.0.0.1:$PEER2_PORT"
expect "$WORK/peer2.out" "^PAIRING_CODE [0-9]{6} FROM $MAC2_NAME" 30 "двойник показал код"
wrong_code "$WORK/peer2.out" > "$WORK/wrong-2.txt"
expect "$WORK/mac2.out" "^PAIRING_FAILED" 15 "Mac: код не совпал"
expect "$WORK/peer2.out" "^PAIRING_FAILED" 15 "двойник: связывание не удалось"
sleep 1
expect_absent "$WORK/mac2.out" "^PAIRED" "Mac не связан"
expect_absent "$WORK/peer2.out" "^(PAIRED|PAIRING_VERIFIED)" "двойник не связан и не получил подтверждения"

pass
