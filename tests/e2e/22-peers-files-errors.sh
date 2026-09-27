#!/bin/bash
# Сценарий 22 (только двойники). Ошибки передачи файлов A → B, сеанс при этом не рвётся:
#   файл изменился после описания — changed; отмена посреди скачивания — недокачанное удалено,
#   следующее скачивание идёт; A перезапущен (описание забыто) — not_found; A выключен — устройство недоступно;
#   у B выключены файлы — описаний он не получает, в caps нет file.
source "$(dirname "$0")/lib.sh"
build_peer

A_NAME="e2e-a-$SUFFIX"
B_NAME="e2e-b-$SUFFIX"
A_PORT="$(free_port)"
B_PORT="$(free_port)"
mkdir -p "$WORK/src/папка"
head -c 2000000 /dev/urandom > "$WORK/src/папка/меняется.bin"
printf 'постоянный\n' > "$WORK/src/папка/постоянный.txt"
head -c $(( 100 * 1048576 )) /dev/urandom > "$WORK/big.bin"

pair_peers b "$B_NAME" "$B_PORT" a "$A_NAME" "$A_PORT"

# Часть 1: файл изменился между описанием и скачиванием.
start_peer b "$B_NAME" "$B_PORT" --save-files "$WORK/changed" --save-delay 3
B_PID=$LAST_PID
start_peer a "$A_NAME" "$A_PORT" --send-files "$WORK/src/папка" --send-delay 2
A_PID=$LAST_PID
expect "$WORK/a.out" "^FILES_SENT 1 " 30 "A отправил описание"
ID="$(offer_id "$WORK/a.out")"
head -c 10 /dev/urandom >> "$WORK/src/папка/меняется.bin"
expect "$WORK/b.out" "^FILES_FAILED $ID Changed$" 20 "B: файл изменился — Changed"
expect "$WORK/a.err" "changed — размер" 5 "A ответил changed"
[ -z "$(ls -A "$WORK/changed/$ID")" ] || fail "после ошибки остались файлы: $(ls -A "$WORK/changed/$ID")"
say "ок: недокачанное удалено"

# Часть 2: отмена посреди скачивания 100 МиБ, затем то же скачивание целиком.
stop "$B_PID"
start_peer b "$B_NAME" "$B_PORT" --save-files "$WORK/cancelled" --cancel-files-after 20000000
B_PID=$LAST_PID
stop "$A_PID"
start_peer a "$A_NAME" "$A_PORT" --send-files "$WORK/big.bin" --send-delay 2
A_PID=$LAST_PID
expect "$WORK/a.out" "^FILES_SENT 1 " 30 "A отправил большой файл"
ID="$(offer_id "$WORK/a.out")"
expect "$WORK/b.out" "^FILES_FAILED $ID Cancelled$" 30 "B отменил скачивание"
# 100 МиБ идут дольше, чем file_cancel: источник обрывает отправку, не досылая файл.
expect "$WORK/a.err" "Запрос [0-9]+ от «${B_NAME}» отменён после [0-9]+ байт" 10 "A получил file_cancel и прекратил отправку"
sleep 1
[ -z "$(ls -A "$WORK/cancelled/$ID")" ] || fail "после отмены остались файлы: $(ls -A "$WORK/cancelled/$ID")"
say "ок: после отмены ничего не осталось"
stop "$B_PID"
start_peer b "$B_NAME" "$B_PORT" --save-files "$WORK/after-cancel"
B_PID=$LAST_PID
stop "$A_PID"
start_peer a "$A_NAME" "$A_PORT" --send-files "$WORK/big.bin" --send-delay 2 --send "после отмены $SUFFIX"
A_PID=$LAST_PID
expect "$WORK/a.out" "^FILES_SENT 1 " 30 "A отправил большой файл ещё раз"
ID="$(offer_id "$WORK/a.out")"
expect "$WORK/b.out" "^FILES_DONE $ID 1 " 60 "B скачал его целиком"
cmp -s "$WORK/big.bin" "$WORK/after-cancel/$ID/big.bin" || fail "скачан не тот файл"
say "ок: файл совпал"
expect_clip peer "$WORK/b.out" "$A_NAME" "после отмены $SUFFIX" 10 "текст проходит"

# Часть 3: A перезапущен и описание забыл — not_found (запрос идёт по новому сеансу).
stop "$B_PID"
start_peer b "$B_NAME" "$B_PORT" --save-files "$WORK/not-found" --save-delay 10
B_PID=$LAST_PID
stop "$A_PID"
start_peer a "$A_NAME" "$A_PORT" --send-files "$WORK/src/папка/постоянный.txt" --send-delay 2
A_PID=$LAST_PID
expect "$WORK/a.out" "^FILES_SENT 1 " 30 "A отправил описание"
ID="$(offer_id "$WORK/a.out")"
expect "$WORK/b.out" "^FILE_OFFER $A_NAME $ID " 10 "B получил описание"
stop "$A_PID"
start_peer a "$A_NAME" "$A_PORT"
A_PID=$LAST_PID
expect "$WORK/b.out" "^FILES_FAILED $ID NotFound$" 30 "B: описание забыто — NotFound"

# Часть 4: A выключен до скачивания — устройство недоступно.
stop "$B_PID"
start_peer b "$B_NAME" "$B_PORT" --save-files "$WORK/unavailable" --save-delay 4
B_PID=$LAST_PID
stop "$A_PID"
start_peer a "$A_NAME" "$A_PORT" --send-files "$WORK/src/папка/постоянный.txt" --send-delay 2
A_PID=$LAST_PID
expect "$WORK/a.out" "^FILES_SENT 1 " 30 "A отправил описание"
ID="$(offer_id "$WORK/a.out")"
stop "$A_PID"
expect "$WORK/b.out" "^FILES_FAILED $ID DeviceUnavailable$" 20 "B: A выключен — DeviceUnavailable"

# Часть 5: у B выключены файлы.
stop "$B_PID"
start_peer b "$B_NAME" "$B_PORT" --files off --save-files "$WORK/off"
B_PID=$LAST_PID
start_peer a "$A_NAME" "$A_PORT" --send-files "$WORK/src/папка" --send-delay 2 --send "без файлов $SUFFIX"
A_PID=$LAST_PID
expect "$WORK/a.out" "^INFO $B_NAME os=windows form=desktop caps=image$" 30 "у B в caps нет file"
expect "$WORK/a.out" "^FILES_SENT 0 " 20 "описание никому не отправлено"
expect_clip peer "$WORK/b.out" "$A_NAME" "без файлов $SUFFIX" 10 "текст проходит"
sleep 2
expect_absent "$WORK/b.out" "^FILE_OFFER" "с выключенными файлами описаний нет"
expect_absent "$WORK/b.err" "file_offer" "описание к B даже не отправлялось"

pass
