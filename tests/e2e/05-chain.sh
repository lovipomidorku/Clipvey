#!/bin/bash
# Сценарий 5. Цепочка из трёх устройств: двойник A ↔ Mac ↔ двойник B (A и B между собой не связаны).
# Текст A доходит до B через Mac и обратно, текст Mac — до обоих; каждый получает фрагмент ровно один раз.
source "$(dirname "$0")/lib.sh"
build

MAC_NAME="e2e-mac-$SUFFIX"
A_NAME="e2e-a-$SUFFIX"
B_NAME="e2e-b-$SUFFIX"
A_PORT="$(free_port)"
B_PORT="$(free_port)"
BOARD="e2e-pb-$SUFFIX"
FROM_A="от A / $SUFFIX"
FROM_B="от B / $SUFFIX"
FROM_MAC="от Mac / $SUFFIX"

pair_mac_responder mac "$MAC_NAME" a "$A_NAME" "$A_PORT"
pair_mac_responder mac "$MAC_NAME" b "$B_NAME" "$B_PORT"

start_mac mac "$MAC_NAME" "$BOARD"
start_peer b "$B_NAME" "$B_PORT" --send "$FROM_B" --send-delay 15
expect "$WORK/mac.out" "^CONNECTED $B_NAME" 30 "Mac подключён к B"
start_peer a "$A_NAME" "$A_PORT" --send "$FROM_A" --send-delay 3
expect "$WORK/mac.out" "^CONNECTED $A_NAME" 30 "Mac подключён к A"

# A → Mac → B
expect_clip mac "$WORK/mac.out" "$A_NAME" "$FROM_A" 15 "Mac получил текст A"
expect_clip peer "$WORK/b.out" "$MAC_NAME" "$FROM_A" 10 "B получил текст A через Mac"

# B → Mac → A
expect_clip mac "$WORK/mac.out" "$B_NAME" "$FROM_B" 25 "Mac получил текст B"
expect_clip peer "$WORK/a.out" "$MAC_NAME" "$FROM_B" 10 "A получил текст B через Mac"
sleep 1
pb types "$BOARD" | grep -qx "$MARKER" || fail "в буфере Mac нет маркера после пересылки"
say "ок: пересланный текст в буфере Mac с маркером"

# Mac → A и B
pb write "$BOARD" "$FROM_MAC"
expect_clip peer "$WORK/a.out" "$MAC_NAME" "$FROM_MAC" 10 "A получил текст Mac"
expect_clip peer "$WORK/b.out" "$MAC_NAME" "$FROM_MAC" 10 "B получил текст Mac"

# Никаких повторов и возвратов к источнику.
sleep 3
expect_count "$WORK/a.out" "^CLIP " 2 1 "A получил ровно 2 фрагмента"
expect_count "$WORK/b.out" "^CLIP " 2 1 "B получил ровно 2 фрагмента"
expect_count "$WORK/mac.out" "^CLIP " 2 1 "Mac получил ровно 2 фрагмента"

pass
