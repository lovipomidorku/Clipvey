#!/bin/bash
# Сценарий 21 (только двойники). Файлы и папки A → B:
#   дерево с кириллицей и эмодзи в именах, пустой папкой, пустым файлом и файлом 150 МиБ приходит целиком
#   (структура и sha256), описание получает только B; скорость — в выводе;
#   то же через OpenFileStream (как виртуальные файлы Проводника: кусками по 64 КиБ, 4 файла одновременно);
#   после передачи сеанс жив — текст проходит;
#   файлы не пересылаются по цепочке: C, связанный только с B, описания A не получает.
source "$(dirname "$0")/lib.sh"
build_peer

A_NAME="e2e-a-$SUFFIX"
B_NAME="e2e-b-$SUFFIX"
C_NAME="e2e-c-$SUFFIX"
A_PORT="$(free_port)"
B_PORT="$(free_port)"
C_PORT="$(free_port)"
make_tree "$WORK/src"
say "дерево: $(du -sh "$WORK/src" | cut -f1)"

pair_peers b "$B_NAME" "$B_PORT" a "$A_NAME" "$A_PORT"

# Часть 1: скачивание целиком (DownloadFilesAsync).
start_peer b "$B_NAME" "$B_PORT" --save-files "$WORK/saved"
B_PID=$LAST_PID
start_peer a "$A_NAME" "$A_PORT" --send-files "$WORK/src/"* --send-delay 2
A_PID=$LAST_PID
expect "$WORK/a.out" "^INFO $B_NAME os=windows form=desktop caps=file,image$" 30 "A знает, что B принимает файлы"
expect "$WORK/a.out" "^FILES_SENT 1 [0-9a-f]{32} 10 " 20 "A отправил описание (10 элементов) одному устройству"
ID="$(offer_id "$WORK/a.out")"
expect "$WORK/b.out" "^FILE_OFFER $A_NAME $ID 10 " 10 "B получил описание"
expect "$WORK/b.out" "^FILES_DONE $ID 6 " 120 "B скачал все 6 файлов"
compare_tree "$WORK/src" "$WORK/saved/$ID"
say "ок: структура и содержимое совпали"
say "скорость двойник → двойник (скачивание): $(files_speed "$WORK/b.out" "$ID")"
[ -z "$(find "$WORK/saved/$ID" -name '.clipvey-*')" ] || fail "осталась временная папка"
expect_absent "$WORK/b.out" "^FILES_FAILED" "ошибок скачивания нет"

# Часть 2: те же файлы через OpenFileStream.
stop "$A_PID"
stop "$B_PID"
start_peer b "$B_NAME" "$B_PORT" --save-files "$WORK/streamed" --save-mode stream
B_PID=$LAST_PID
start_peer a "$A_NAME" "$A_PORT" --send-files "$WORK/src/"* --send-delay 2 --send "после файлов $SUFFIX"
A_PID=$LAST_PID
expect "$WORK/a.out" "^FILES_SENT 1 " 30 "A отправил описание ещё раз"
ID="$(offer_id "$WORK/a.out")"
expect "$WORK/b.out" "^FILES_DONE $ID 6 " 120 "B прочитал все файлы потоками"
compare_tree "$WORK/src" "$WORK/streamed/$ID"
say "ок: через потоки — то же самое"
say "скорость двойник → двойник (потоки, 4 одновременно): $(files_speed "$WORK/b.out" "$ID")"
expect_clip peer "$WORK/b.out" "$A_NAME" "после файлов $SUFFIX" 10 "текст проходит"
expect_absent "$WORK/b.out" "^DISCONNECTED" "сеанс не рвался"

# Часть 3: цепочка A ↔ B ↔ C — описание получает только B.
stop "$A_PID"
stop "$B_PID"
pair_peers b "$B_NAME" "$B_PORT" c "$C_NAME" "$C_PORT"
start_peer b "$B_NAME" "$B_PORT"
start_peer c "$C_NAME" "$C_PORT" --save-files "$WORK/chain-c"
expect "$WORK/b.out" "^CONNECTED $C_NAME" 30 "B подключён к C"
start_peer a "$A_NAME" "$A_PORT" --send-files "$WORK/src/ёлка 🎄.txt" --send-delay 2 --send "по цепочке $SUFFIX"
expect "$WORK/a.out" "^FILES_SENT 1 " 30 "A отправил описание (только B)"
ID="$(offer_id "$WORK/a.out")"
expect "$WORK/b.out" "^FILE_OFFER $A_NAME $ID " 10 "B получил описание"
expect_clip peer "$WORK/c.out" "$B_NAME" "по цепочке $SUFFIX" 15 "текст A дошёл до C через B"
expect_absent "$WORK/c.out" "^FILE_OFFER" "описание A до C не дошло"

pass
