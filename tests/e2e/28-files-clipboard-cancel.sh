#!/bin/bash
# Сценарий 28. Новое содержимое буфера отменяет незаконченное тихое скачивание; ошибки — окошком.
# Mac связан с двумя двойниками: A отправляет файлы, B — текст. Приём файлов на Mac замедлен (--slow-files),
# чтобы новое содержимое пришло посреди скачивания.
#   Тихое скачивание дольше 1,5 с показывает окошко «Получение …» (TOAST receiving).
#   Текст с другого устройства (B) посреди тихого скачивания от A: скачивание отменено (A получает file_cancel),
#   окошко «Получение» убрано, в буфере — текст B, файлы в буфер не попадают, недокачанного не остаётся.
#   Скопировано на самом Mac посреди скачивания: то же, в буфере остаётся скопированное.
#   Окошко «Загрузить» (больше 50 МиБ) исчезает, когда с другого устройства приходит новое содержимое.
#   Файл у источника изменился посреди скачивания: окошко «Файлы … не получены» с причиной, само исчезает.
#   Медленное тихое скачивание до конца: «Получение …», затем «Готово — можно вставлять», исчезает само.
source "$(dirname "$0")/lib.sh"
build

MAC_NAME="e2e-mac-$SUFFIX"
A_NAME="e2e-a-$SUFFIX"
B_NAME="e2e-b-$SUFFIX"
A_PORT="$(free_port)"
B_PORT="$(free_port)"
BOARD="e2e-pb-$SUFFIX"
head -c $(( 48 * 1048576 )) /dev/urandom > "$WORK/сорок восемь.bin"
head -c $(( 30 * 1048576 )) /dev/urandom > "$WORK/меняется.bin"
head -c $(( 60 * 1048576 )) /dev/urandom > "$WORK/большой.bin"

pair_mac_responder mac "$MAC_NAME" a "$A_NAME" "$A_PORT"
pair_mac_responder mac "$MAC_NAME" b "$B_NAME" "$B_PORT"

# 1 МиБ за 250 мс: 48 МиБ качаются ~12 с.
start_mac mac "$MAC_NAME" "$BOARD" --slow-files 250 --toast-shots "$WORK/shots"
MAC_PID=$LAST_PID

# Часть 1: текст с другого устройства.
start_peer a "$A_NAME" "$A_PORT" --send-files "$WORK/сорок восемь.bin" --send-delay 2
A_PID=$LAST_PID
expect "$WORK/a.out" "^FILES_SENT 1 " 30 "A отправил описание (48 МиБ)"
ID="$(offer_id "$WORK/a.out")"
expect "$WORK/mac.out" "^FILE_OFFER $A_NAME $ID 1 " 10 "Mac получил описание и начал тихое скачивание"
expect "$WORK/mac.out" "^TOAST receiving $ID$" 5 "скачивание идёт дольше 1,5 с — окошко «Получение»"
start_peer b "$B_NAME" "$B_PORT" --send "текст от B $SUFFIX"
B_PID=$LAST_PID
expect_clip mac "$WORK/mac.out" "$B_NAME" "текст от B $SUFFIX" 20 "Mac получил текст B"
expect "$WORK/mac.out" "^FILES_FAILED $ID Cancelled$" 5 "тихое скачивание отменено"
expect "$WORK/a.err" "Запрос [0-9]+ от «${MAC_NAME}» отменён после [0-9]+ байт" 10 "A прекратил отправку"
sleep 1
[ "$(pb read "$BOARD")" = "текст от B $SUFFIX" ] || fail "в буфере не текст B: $(pb read "$BOARD")"
expect_absent "$WORK/mac.out" "^FILES_READY" "файлы в буфер не положены"
expect_count "$WORK/mac.out" "^TOAST hidden$" 1 5 "окошко «Получение» убрано"
[ -z "$(ls -A "$WORK/mac/Incoming/$ID" 2>/dev/null)" ] || fail "осталось недокачанное: $(ls -A "$WORK/mac/Incoming/$ID")"
say "ок: недокачанного не осталось"

# Часть 2: скопировано на самом Mac.
stop "$A_PID"
start_peer a "$A_NAME" "$A_PORT" --send-files "$WORK/сорок восемь.bin" --send-delay 2
A_PID=$LAST_PID
expect "$WORK/a.out" "^FILES_SENT 1 " 30 "A отправил описание ещё раз"
ID="$(offer_id "$WORK/a.out")"
expect "$WORK/mac.out" "^FILE_OFFER $A_NAME $ID 1 " 10 "Mac начал тихое скачивание"
expect "$WORK/mac.out" "^TOAST receiving $ID$" 5 "окошко «Получение»"
pb write "$BOARD" "скопировано на Mac $SUFFIX"
expect "$WORK/mac.out" "^FILES_FAILED $ID Cancelled$" 5 "тихое скачивание отменено"
expect_clip peer "$WORK/b.out" "$MAC_NAME" "скопировано на Mac $SUFFIX" 10 "скопированное ушло как обычно"
sleep 1
[ "$(pb read "$BOARD")" = "скопировано на Mac $SUFFIX" ] || fail "буфер Mac изменился: $(pb read "$BOARD")"
expect_absent "$WORK/mac.out" "^FILES_READY" "файлы в буфер не положены"
expect_count "$WORK/mac.out" "^TOAST hidden$" 2 5 "окошко «Получение» убрано"

# Часть 3: окошко «Загрузить» устаревает от нового содержимого с другого устройства.
stop "$A_PID"
start_peer a "$A_NAME" "$A_PORT" --send-files "$WORK/большой.bin" --send-delay 2
A_PID=$LAST_PID
expect "$WORK/a.out" "^FILES_SENT 1 " 30 "A отправил описание (60 МиБ)"
ID="$(offer_id "$WORK/a.out")"
expect "$WORK/mac.out" "^TOAST offer $ID$" 10 "окошко «Загрузить»"
stop "$B_PID"
start_peer b "$B_NAME" "$B_PORT" --send "второй текст от B $SUFFIX"
B_PID=$LAST_PID
expect_clip mac "$WORK/mac.out" "$B_NAME" "второй текст от B $SUFFIX" 20 "Mac получил новый текст B"
expect_count "$WORK/mac.out" "^TOAST hidden$" 3 5 "окошко «Загрузить» исчезло"
expect_absent "$WORK/mac.out" "^FILES_READY" "ничего не скачано"

# Часть 4: файл изменился посреди тихого скачивания.
stop "$A_PID"
start_peer a "$A_NAME" "$A_PORT" --send-files "$WORK/меняется.bin" --send-delay 2
A_PID=$LAST_PID
expect "$WORK/a.out" "^FILES_SENT 1 " 30 "A отправил описание (30 МиБ)"
ID="$(offer_id "$WORK/a.out")"
expect "$WORK/mac.out" "^FILE_OFFER $A_NAME $ID 1 " 10 "Mac начал тихое скачивание"
expect "$WORK/mac.out" "^TOAST receiving $ID$" 5 "окошко «Получение»"
head -c 10 /dev/urandom >> "$WORK/меняется.bin"
expect "$WORK/mac.out" "^FILES_FAILED $ID Changed$" 20 "Mac: файл изменился"
expect "$WORK/mac.out" "^TOAST notice Changed$" 5 "окошко с причиной"
[ -z "$(ls -A "$WORK/mac/Incoming/$ID" 2>/dev/null)" ] || fail "осталось недокачанное"
expect_count "$WORK/mac.out" "^TOAST hidden$" 4 15 "сообщение исчезло само"

# Часть 5: медленное тихое скачивание до конца — «Получение», затем «Готово — можно вставлять» на 2,5 с.
stop "$A_PID"
start_peer a "$A_NAME" "$A_PORT" --send-files "$WORK/сорок восемь.bin" --send-delay 2
A_PID=$LAST_PID
expect "$WORK/a.out" "^FILES_SENT 1 " 30 "A отправил описание (48 МиБ)"
ID="$(offer_id "$WORK/a.out")"
expect "$WORK/mac.out" "^TOAST receiving $ID$" 10 "окошко «Получение»"
expect "$WORK/mac.out" "^FILES_READY $ID 1 pasteboard$" 30 "скачано, файлы в буфере"
expect "$WORK/mac.out" "^TOAST received $ID$" 5 "окошко «Готово — можно вставлять»"
expect_count "$WORK/mac.out" "^TOAST hidden$" 5 6 "«Готово» исчезло само"

pass
