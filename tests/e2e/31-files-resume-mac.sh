#!/bin/bash
# Сценарий 31. Обрыв посреди «Загрузить» на Mac (больше 50 МиБ): двойник закрывает сеанс, отдав 30 МБ
# (--drop-after-bytes), дальше — обычное переподключение. Mac ждёт новый сеанс и продолжает с того же места:
#   загрузка не прерывается ошибкой, окошко доходит до «Готово», файлы — в папке загрузок и в буфере (sha256);
#   продолжение, а не повтор: Mac продолжает с ненулевого места (FILES_RESUMED), а двойник по законченным
#   запросам отдал меньше, чем всё описание.
source "$(dirname "$0")/lib.sh"
build

MAC_NAME="e2e-mac-$SUFFIX"
PEER_NAME="e2e-peer-$SUFFIX"
PEER_PORT="$(free_port)"
BOARD="e2e-pb-$SUFFIX"
make_tree "$WORK/big" 100

pair_mac_responder mac "$MAC_NAME" peer "$PEER_NAME" "$PEER_PORT"

start_mac mac "$MAC_NAME" "$BOARD" --downloads "$WORK/downloads" --auto-download --toast-shots "$WORK/shots"
start_peer peer "$PEER_NAME" "$PEER_PORT" --send-files "$WORK/big/"* --send-delay 2 --drop-after-bytes 30000000
expect "$WORK/peer.out" "^FILES_SENT 1 [0-9a-f]{32} 10 " 30 "двойник отправил описание (≈104 МБ)"
ID="$(offer_id "$WORK/peer.out")"
TOTAL="$(offer_total "$WORK/peer.out" "$ID")"
expect "$WORK/mac.out" "^TOAST offer $ID$" 10 "окошко «Загрузить»"
expect "$WORK/mac.out" "^TOAST_CLICK download ok$" 5 "нажата «Загрузить»"
expect "$WORK/peer.err" "Проверка: сеанс с «${MAC_NAME}» закрыт после [0-9]+ байт файлов" 30 "двойник оборвал сеанс посреди загрузки"
expect "$WORK/mac.out" "^DISCONNECTED $PEER_NAME" 10 "Mac заметил обрыв"
expect_count "$WORK/mac.out" "^CONNECTED $PEER_NAME" 2 20 "Mac снова подключён к двойнику"
expect "$WORK/mac.out" "^FILES_RESUMED $ID [1-9][0-9]*$" 20 "Mac продолжил по новому сеансу с ненулевого места"
expect "$WORK/mac.out" "^FILES_READY $ID 3 pasteboard$" 60 "загружено и положено в буфер"
expect "$WORK/mac.out" "^TOAST done $ID pasteboard$" 5 "окошко «Готово»"
expect_absent "$WORK/mac.out" "^FILES_FAILED" "загрузка не прерывалась ошибкой"
compare_tree "$WORK/big" "$WORK/downloads"
say "ок: файлы в папке загрузок совпадают с исходными"
pb types "$BOARD" | grep -qx "$MARKER" || fail "в буфере нет маркера"
SERVED="$(served_bytes "$WORK/peer.err" "$MAC_NAME")"
[ "$SERVED" -lt "$TOTAL" ] || fail "двойник отдал по законченным запросам $SERVED байт из $TOTAL: похоже, файл запрашивался заново с начала"
say "ок: двойник отдал по законченным запросам $SERVED из $TOTAL байт — прерванный файл докачан с места обрыва"

pass
