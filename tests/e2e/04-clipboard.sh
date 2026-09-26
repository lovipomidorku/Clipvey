#!/bin/bash
# Сценарий 4. Текст туда и обратно через именованный буфер Mac:
#   Mac → двойник: «копирование» в буфер Mac доходит до двойника ровно один раз;
#   двойник → Mac: текст записан в буфер Mac с маркером Clipvey и не отправлен обратно (нет эха);
#   секретное содержимое (ConcealedType) не отправляется.
source "$(dirname "$0")/lib.sh"
build

MAC_NAME="e2e-mac-$SUFFIX"
PEER_NAME="e2e-peer-$SUFFIX"
PEER_PORT="$(free_port)"
BOARD="e2e-pb-$SUFFIX"
FROM_MAC="Привет с Mac / $SUFFIX
вторая строка: «кавычки» \"двойные\" \\ 😀"
FROM_PEER="Ответ двойника / https://example.com/путь $SUFFIX"
SECRET="пароль-$SUFFIX"

pair_mac_responder mac "$MAC_NAME" peer "$PEER_NAME" "$PEER_PORT"

start_mac mac "$MAC_NAME" "$BOARD"
# Двойник отправит свой текст через 6 с после подключения — после того, как Mac отправит свой.
start_peer peer "$PEER_NAME" "$PEER_PORT" --send "$FROM_PEER" --send-delay 6
expect "$WORK/mac.out" "^CONNECTED $PEER_NAME" 30 "Mac подключился"
expect "$WORK/peer.out" "^CONNECTED $MAC_NAME" 30 "двойник подключился"

# Буфер читается, только когда есть подключения: пишем после CONNECTED.
pb write "$BOARD" "$FROM_MAC"
expect_clip peer "$WORK/peer.out" "$MAC_NAME" "$FROM_MAC" 10 "двойник получил текст Mac"

expect "$WORK/peer.out" "^SENT" 20 "двойник отправил свой текст"
expect_clip mac "$WORK/mac.out" "$PEER_NAME" "$FROM_PEER" 10 "Mac получил текст двойника"
sleep 1
[ "$(pb read "$BOARD")" = "$FROM_PEER" ] || fail "в буфере Mac не тот текст: $(pb read "$BOARD")"
say "ок: текст двойника в буфере Mac"
pb types "$BOARD" | grep -qx "$MARKER" || fail "в буфере Mac нет маркера $MARKER"
say "ок: маркер $MARKER"

# Эхо: Mac не отправляет обратно записанный им текст, двойник — полученный.
sleep 3
[ "$(peer_clip_count "$WORK/peer.out" "$MAC_NAME" "$FROM_PEER")" -eq 0 ] || fail "эхо: текст двойника вернулся к нему"
say "ок: текст двойника не вернулся (нет эха)"
[ "$(mac_clip_count "$WORK/mac.out" "$PEER_NAME" "$FROM_MAC")" -eq 0 ] || fail "эхо: текст Mac вернулся к нему"
say "ок: текст Mac не вернулся (нет эха)"
expect_count "$WORK/peer.out" "^CLIP " 1 1 "двойник получил ровно один фрагмент"
expect_count "$WORK/mac.out" "^CLIP " 1 1 "Mac получил ровно один фрагмент"

# Пароль из менеджера паролей не передаётся.
pb write-secret "$BOARD" "$SECRET"
sleep 2
# Следующее обычное копирование доходит — значит, Mac успел увидеть и пропустить секретное.
pb write "$BOARD" "после пароля $SUFFIX"
expect_clip peer "$WORK/peer.out" "$MAC_NAME" "после пароля $SUFFIX" 10 "обычный текст после секретного дошёл"
[ "$(peer_clip_count "$WORK/peer.out" "$MAC_NAME" "$SECRET")" -eq 0 ] || fail "секретный текст отправлен"
say "ок: секретный текст не отправлен"

pass
