#!/bin/bash
# Сценарий 24. Файлы двойник → Mac: дерево с кириллицей, пустой папкой, пустым файлом и файлом 150 МиБ
# приходит на Mac целиком (структура и sha256), скорость — в выводе; отмена посреди скачивания на Mac —
# недокачанное удалено, сеанс жив; с выключенными файлами Mac их не заявляет и описаний не получает.
source "$(dirname "$0")/lib.sh"
build

MAC_NAME="e2e-mac-$SUFFIX"
PEER_NAME="e2e-peer-$SUFFIX"
PEER_PORT="$(free_port)"
BOARD="e2e-pb-$SUFFIX"
make_tree "$WORK/src"
head -c $(( 100 * 1048576 )) /dev/urandom > "$WORK/big.bin"

pair_mac_responder mac "$MAC_NAME" peer "$PEER_NAME" "$PEER_PORT"

# Часть 1: всё дерево.
start_mac mac "$MAC_NAME" "$BOARD" --save-files "$WORK/saved"
MAC_PID=$LAST_PID
start_peer peer "$PEER_NAME" "$PEER_PORT" --send-files "$WORK/src/"* --send-delay 2
PEER_PID=$LAST_PID
expect "$WORK/peer.out" "^INFO $MAC_NAME os=mac form=(laptop|desktop) caps=file,image$" 30 "двойник знает, что Mac принимает файлы"
expect "$WORK/mac.out" "^INFO $PEER_NAME os=windows form=desktop caps=file,image$" 5 "Mac знает, что двойник принимает файлы"
expect "$WORK/peer.out" "^FILES_SENT 1 [0-9a-f]{32} 10 " 20 "двойник отправил описание"
ID="$(offer_id "$WORK/peer.out")"
expect "$WORK/mac.out" "^FILE_OFFER $PEER_NAME $ID 10 " 10 "Mac получил описание"
expect "$WORK/mac.out" "^FILES_DONE $ID 6 " 120 "Mac скачал все 6 файлов"
compare_tree "$WORK/src" "$WORK/saved/$ID"
say "ок: структура и содержимое совпали"
say "скорость двойник → Mac: $(files_speed "$WORK/mac.out" "$ID")"
[ -z "$(find "$WORK/saved/$ID" -name '.clipvey-*')" ] || fail "осталась временная папка"

# Часть 2: отмена на Mac посреди 100 МиБ.
stop "$MAC_PID"
start_mac mac "$MAC_NAME" "$BOARD" --save-files "$WORK/cancelled" --cancel-files-after 20000000
MAC_PID=$LAST_PID
stop "$PEER_PID"
start_peer peer "$PEER_NAME" "$PEER_PORT" --send-files "$WORK/big.bin" --send-delay 2 --send "после отмены $SUFFIX"
PEER_PID=$LAST_PID
expect "$WORK/peer.out" "^FILES_SENT 1 " 30 "двойник отправил большой файл"
ID="$(offer_id "$WORK/peer.out")"
expect "$WORK/mac.out" "^FILES_FAILED $ID Cancelled$" 30 "Mac отменил скачивание"
# 100 МиБ идут дольше, чем file_cancel: источник обрывает отправку, не досылая файл.
expect "$WORK/peer.err" "Запрос [0-9]+ от «${MAC_NAME}» отменён после [0-9]+ байт" 10 "двойник получил file_cancel и прекратил отправку"
sleep 1
[ -z "$(ls -A "$WORK/cancelled/$ID")" ] || fail "после отмены остались файлы: $(ls -A "$WORK/cancelled/$ID")"
say "ок: после отмены ничего не осталось"
expect_clip mac "$WORK/mac.out" "$PEER_NAME" "после отмены $SUFFIX" 10 "текст проходит"
expect_absent "$WORK/mac.out" "^DISCONNECTED" "сеанс не рвался"

# Часть 3: у Mac выключены файлы.
stop "$MAC_PID"
start_mac mac "$MAC_NAME" "$BOARD" --files off --save-files "$WORK/off"
MAC_PID=$LAST_PID
stop "$PEER_PID"
start_peer peer "$PEER_NAME" "$PEER_PORT" --send-files "$WORK/src/ёлка 🎄.txt" --send-delay 2
expect "$WORK/peer.out" "^INFO $MAC_NAME os=mac form=(laptop|desktop) caps=image$" 30 "у Mac в caps нет file"
expect "$WORK/peer.out" "^FILES_SENT 0 " 20 "описание никому не отправлено"
sleep 2
expect_absent "$WORK/mac.out" "^FILE_OFFER" "Mac описаний не получал"

pass
