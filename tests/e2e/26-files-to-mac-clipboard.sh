#!/bin/bash
# Сценарий 26. Файлы двойник → Mac, как у пользователя: скопировал на Windows — вставляет на Mac.
#   До 50 МиБ (и ровно 50 МиБ) — тихо, в фоне, без окошка: в именованном буфере Mac — ссылки на файлы
#   (public.file-url) и маркер, файлы лежат в кэше внутри папки данных и совпадают с исходными; обратно
#   ничего не уходит.
#   Больше 50 МиБ — окошко «Загрузить»; кнопка нажимается событиями мыши (--auto-download), идёт прогресс,
#   затем «Готово»: файлы — в папке --downloads и в буфере; пока мышь «на окошке» (--toast-hover), оно не исчезает.
#   «Отмена» (--auto-cancel) прерывает загрузку: окошко закрывается, недокачанного не остаётся.
source "$(dirname "$0")/lib.sh"
build

MAC_NAME="e2e-mac-$SUFFIX"
PEER_NAME="e2e-peer-$SUFFIX"
PEER_PORT="$(free_port)"
BOARD="e2e-pb-$SUFFIX"
make_tree "$WORK/small" 20
mkdir -p "$WORK/exact"
head -c 52428800 /dev/urandom > "$WORK/exact/ровно 50 МиБ.bin"
make_tree "$WORK/big" 60

pair_mac_responder mac "$MAC_NAME" peer "$PEER_NAME" "$PEER_PORT"

# Часть 1: до 50 МиБ — тихо.
start_mac mac "$MAC_NAME" "$BOARD" --downloads "$WORK/downloads" --toast-shots "$WORK/shots"
MAC_PID=$LAST_PID
start_peer peer "$PEER_NAME" "$PEER_PORT" --send-files "$WORK/small/"* --send-delay 2
PEER_PID=$LAST_PID
expect "$WORK/peer.out" "^FILES_SENT 1 [0-9a-f]{32} 10 " 30 "двойник отправил описание (≈24 МБ)"
ID="$(offer_id "$WORK/peer.out")"
expect "$WORK/mac.out" "^FILE_OFFER $PEER_NAME $ID 10 " 10 "Mac получил описание"
expect "$WORK/mac.out" "^FILES_READY $ID 3 pasteboard$" 30 "Mac скачал тихо и положил файлы в буфер"
pb types "$BOARD" | grep -qx "public.file-url" || fail "в буфере нет public.file-url"
pb types "$BOARD" | grep -qx "$MARKER" || fail "в буфере нет маркера"
pb read-files "$BOARD" > "$WORK/pasted.txt"
[ "$(wc -l < "$WORK/pasted.txt" | tr -d ' ')" -eq 3 ] || fail "в буфере не 3 элемента: $(cat "$WORK/pasted.txt")"
if grep -v "^$WORK/mac/Incoming/$ID/" "$WORK/pasted.txt"; then
    fail "файлы в буфере не из кэша в папке данных"
fi
say "ок: в буфере 3 ссылки на файлы из кэша и маркер"
compare_tree "$WORK/small" "$WORK/mac/Incoming/$ID"
say "ок: файлы в буфере совпадают с исходными"
[ -z "$(find "$WORK/mac/Incoming" -name '.clipvey-*')" ] || fail "осталась временная папка"
sleep 2
expect_absent "$WORK/mac.out" "^TOAST " "окошка не было"
expect_absent "$WORK/mac.out" "^FILES_SENT" "Mac не отправил полученные файлы обратно"
expect_absent "$WORK/peer.out" "^(FILE_OFFER|CLIP) " "двойник ничего не получил обратно"

# Часть 2: ровно 50 МиБ — всё ещё тихо.
stop "$PEER_PID"
start_peer peer "$PEER_NAME" "$PEER_PORT" --send-files "$WORK/exact/ровно 50 МиБ.bin" --send-delay 2
PEER_PID=$LAST_PID
expect "$WORK/peer.out" "^FILES_SENT 1 [0-9a-f]{32} 1 52428800$" 30 "двойник отправил файл ровно 50 МиБ"
ID="$(offer_id "$WORK/peer.out")"
expect "$WORK/mac.out" "^FILES_READY $ID 1 pasteboard$" 30 "ровно 50 МиБ — тихо, в буфер"
cmp -s "$WORK/exact/ровно 50 МиБ.bin" "$(pb read-files "$BOARD")" || fail "файл в буфере отличается"
expect_absent "$WORK/mac.out" "^TOAST " "окошка не было"

# Часть 3: больше 50 МиБ — окошко, «Загрузить», прогресс, «Готово».
stop "$MAC_PID"
start_mac mac "$MAC_NAME" "$BOARD" --downloads "$WORK/downloads" --toast-shots "$WORK/shots" \
    --auto-download --slow-files 40 --toast-hover 8
MAC_PID=$LAST_PID
stop "$PEER_PID"
start_peer peer "$PEER_NAME" "$PEER_PORT" --send-files "$WORK/big/"* --send-delay 2
PEER_PID=$LAST_PID
expect "$WORK/peer.out" "^FILES_SENT 1 " 30 "двойник отправил описание (≈64 МБ)"
ID="$(offer_id "$WORK/peer.out")"
expect "$WORK/mac.out" "^TOAST offer $ID$" 10 "окошко «Загрузить»"
expect_absent "$WORK/mac.out" "^FILES_READY $ID" "без нажатия ничего не скачивается"
expect "$WORK/mac.out" "^TOAST_CLICK download ok$" 5 "нажата «Загрузить»"
expect "$WORK/mac.out" "^TOAST progress $ID$" 5 "кнопка сработала: идёт загрузка"
expect "$WORK/mac.out" "^FILES_READY $ID 3 pasteboard$" 60 "загружено и положено в буфер"
expect "$WORK/mac.out" "^TOAST done $ID pasteboard$" 5 "окошко «Готово»"
compare_tree "$WORK/big" "$WORK/downloads"
say "ок: файлы в папке загрузок совпадают с исходными"
pb read-files "$BOARD" | sort > "$WORK/pasted.txt"
(cd "$WORK/downloads" && for name in *; do echo "$WORK/downloads/$name"; done) | sort > "$WORK/expected.txt"
diff "$WORK/expected.txt" "$WORK/pasted.txt" > /dev/null || fail "в буфере не те файлы: $(cat "$WORK/pasted.txt")"
pb types "$BOARD" | grep -qx "$MARKER" || fail "в буфере нет маркера"
say "ок: в буфере — загруженные файлы и маркер"
[ -z "$(find "$WORK/downloads" -name '.clipvey-*')" ] || fail "осталась временная папка"
# «Готово» исчезает через 6 с, но мышь «на окошке» 8 с (+1,5 с после ухода).
sleep 7
expect_absent "$WORK/mac.out" "^TOAST hidden" "пока мышь на окошке, «Готово» не исчезает"
expect "$WORK/mac.out" "^TOAST hidden$" 8 "мышь ушла — «Готово» исчезло"

# Часть 4: «Отмена» посреди загрузки.
stop "$MAC_PID"
start_mac mac "$MAC_NAME" "$BOARD" --downloads "$WORK/cancelled" --auto-download --auto-cancel 1 --slow-files 100
MAC_PID=$LAST_PID
stop "$PEER_PID"
start_peer peer "$PEER_NAME" "$PEER_PORT" --send-files "$WORK/big/большой.bin" --send-delay 2
PEER_PID=$LAST_PID
expect "$WORK/peer.out" "^FILES_SENT 1 " 30 "двойник отправил большой файл"
ID="$(offer_id "$WORK/peer.out")"
expect "$WORK/mac.out" "^TOAST progress $ID$" 15 "загрузка началась"
expect "$WORK/mac.out" "^TOAST_CLICK cancel ok$" 5 "нажата «Отмена»"
expect "$WORK/mac.out" "^FILES_FAILED $ID Cancelled$" 5 "загрузка отменена"
expect "$WORK/mac.out" "^TOAST hidden$" 5 "окошко закрылось"
expect "$WORK/peer.err" "Запрос [0-9]+ от «${MAC_NAME}» отменён после [0-9]+ байт" 10 "двойник прекратил отправку"
sleep 1
[ -z "$(ls -A "$WORK/cancelled" 2>/dev/null)" ] || fail "после отмены остались файлы: $(ls -A "$WORK/cancelled")"
say "ок: после отмены ничего не осталось"
expect_absent "$WORK/mac.out" "^DISCONNECTED" "сеанс не рвался"

pass
