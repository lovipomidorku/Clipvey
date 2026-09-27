#!/bin/bash
# Сценарий 25. Файлы Mac → двойник: дерево с кириллицей, пустой папкой, пустым файлом и файлом 150 МиБ
# приходит целиком (структура и sha256), скорость — в выводе; то же через потоки OpenFileStream;
# файл изменился после описания — Mac отвечает changed.
source "$(dirname "$0")/lib.sh"
build

MAC_NAME="e2e-mac-$SUFFIX"
PEER_NAME="e2e-peer-$SUFFIX"
PEER_PORT="$(free_port)"
BOARD="e2e-pb-$SUFFIX"
make_tree "$WORK/src"
mkdir -p "$WORK/changing"
head -c 2000000 /dev/urandom > "$WORK/changing/меняется.bin"

pair_mac_responder mac "$MAC_NAME" peer "$PEER_NAME" "$PEER_PORT"

# Часть 1: скачивание целиком.
start_peer peer "$PEER_NAME" "$PEER_PORT" --save-files "$WORK/saved"
PEER_PID=$LAST_PID
start_mac mac "$MAC_NAME" "$BOARD" --send-files "$WORK/src/"* --send-delay 2
MAC_PID=$LAST_PID
expect "$WORK/mac.out" "^FILES_SENT 1 [0-9a-f]{32} 10 " 30 "Mac отправил описание"
ID="$(offer_id "$WORK/mac.out")"
expect "$WORK/peer.out" "^FILE_OFFER $MAC_NAME $ID 10 " 10 "двойник получил описание"
expect "$WORK/peer.out" "^FILES_DONE $ID 6 " 120 "двойник скачал все 6 файлов"
compare_tree "$WORK/src" "$WORK/saved/$ID"
say "ок: структура и содержимое совпали"
say "скорость Mac → двойник: $(files_speed "$WORK/peer.out" "$ID")"

# Часть 2: потоки OpenFileStream (как Проводник).
stop "$PEER_PID"
start_peer peer "$PEER_NAME" "$PEER_PORT" --save-files "$WORK/streamed" --save-mode stream
PEER_PID=$LAST_PID
stop "$MAC_PID"
start_mac mac "$MAC_NAME" "$BOARD" --send-files "$WORK/src/"* --send-delay 2
MAC_PID=$LAST_PID
expect "$WORK/mac.out" "^FILES_SENT 1 " 30 "Mac отправил описание ещё раз"
ID="$(offer_id "$WORK/mac.out")"
expect "$WORK/peer.out" "^FILES_DONE $ID 6 " 120 "двойник прочитал все файлы потоками"
compare_tree "$WORK/src" "$WORK/streamed/$ID"
say "ок: через потоки — то же самое"
say "скорость Mac → двойник (потоки): $(files_speed "$WORK/peer.out" "$ID")"

# Часть 3: файл изменился после описания.
stop "$PEER_PID"
start_peer peer "$PEER_NAME" "$PEER_PORT" --save-files "$WORK/changed" --save-delay 3 --send "после ошибки $SUFFIX" --send-delay 6
PEER_PID=$LAST_PID
stop "$MAC_PID"
start_mac mac "$MAC_NAME" "$BOARD" --send-files "$WORK/changing" --send-delay 2
MAC_PID=$LAST_PID
expect "$WORK/mac.out" "^FILES_SENT 1 " 30 "Mac отправил описание"
ID="$(offer_id "$WORK/mac.out")"
head -c 10 /dev/urandom >> "$WORK/changing/меняется.bin"
expect "$WORK/peer.out" "^FILES_FAILED $ID Changed$" 20 "двойник: файл изменился — Changed"
[ -z "$(ls -A "$WORK/changed/$ID")" ] || fail "после ошибки остались файлы"
expect_clip mac "$WORK/mac.out" "$PEER_NAME" "после ошибки $SUFFIX" 15 "текст проходит"
expect_absent "$WORK/mac.out" "^DISCONNECTED" "сеанс не рвался"

pass
