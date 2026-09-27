#!/bin/bash
# Сценарий 27. Файлы Mac → двойник через буфер, как у пользователя: скопировал в Finder — вставляет на Windows.
#   В именованный буфер Mac кладутся ссылки на файлы, как их кладёт Finder (file:///.file/id=…, имя текстом рядом):
#   уходит описание, двойник скачивает дерево целиком; имя файла текстом не уходит.
#   Больше 10 ГиБ (разреженный файл) и одна символическая ссылка — не отправляются, окошко с причиной.
#   «Передавать файлы» выключено переключателем (--files-off-after): Mac не заявляет file и файлы из буфера
#   не отправляет — ни описанием, ни именем текстом.
source "$(dirname "$0")/lib.sh"
build

MAC_NAME="e2e-mac-$SUFFIX"
PEER_NAME="e2e-peer-$SUFFIX"
PEER_PORT="$(free_port)"
BOARD="e2e-pb-$SUFFIX"
make_tree "$WORK/src" 60
mkfile -n 11g "$WORK/huge.bin"
ln -s "$WORK/src/ёлка 🎄.txt" "$WORK/ссылка.txt"

pair_mac_responder mac "$MAC_NAME" peer "$PEER_NAME" "$PEER_PORT"

# Часть 1: дерево из буфера Mac.
start_peer peer "$PEER_NAME" "$PEER_PORT" --save-files "$WORK/saved"
PEER_PID=$LAST_PID
start_mac mac "$MAC_NAME" "$BOARD" --toast-shots "$WORK/shots"
MAC_PID=$LAST_PID
expect "$WORK/mac.out" "^INFO $PEER_NAME os=windows form=desktop caps=file,image$" 30 "Mac знает, что двойник принимает файлы"
expect "$WORK/peer.out" "^CONNECTED $MAC_NAME" 10 "двойник подключился"
# Буфер читается, только когда есть подключения: пишем после CONNECTED.
pb write-files "$BOARD" "$WORK/src/"*
expect "$WORK/mac.out" "^FILES_SENT 1 [0-9a-f]{32} 10 " 10 "Mac отправил описание из буфера"
ID="$(offer_id "$WORK/mac.out")"
expect "$WORK/peer.out" "^FILE_OFFER $MAC_NAME $ID 10 " 10 "двойник получил описание"
expect "$WORK/peer.out" "^FILES_DONE $ID 6 " 60 "двойник скачал все 6 файлов"
compare_tree "$WORK/src" "$WORK/saved/$ID"
say "ок: структура и содержимое совпали"
sleep 1
expect_absent "$WORK/peer.out" "^CLIP " "имя файла текстом не ушло"
expect_absent "$WORK/mac.out" "^TOAST " "окошек не было"

# Часть 2: больше 10 ГиБ.
pb write-files "$BOARD" "$WORK/huge.bin"
expect "$WORK/mac.out" "^FILES_REFUSED TooLarge$" 10 "11 ГиБ не отправлены"
expect "$WORK/mac.out" "^TOAST notice TooLarge$" 5 "окошко: больше 10 ГБ"

# Часть 3: только символическая ссылка — отправлять нечего.
pb write-files "$BOARD" "$WORK/ссылка.txt"
expect "$WORK/mac.out" "^FILES_REFUSED Empty$" 10 "ссылка не отправлена"
expect "$WORK/mac.out" "^TOAST notice Empty$" 5 "окошко: нечего отправлять (заменило прежнее)"
expect "$WORK/mac.out" "^TOAST hidden$" 15 "сообщение исчезло само"
expect_count "$WORK/peer.out" "^FILE_OFFER " 1 1 "двойник получил только первое описание"
expect_absent "$WORK/peer.out" "^CLIP " "имена текстом не ушли"

# Часть 4: «Передавать файлы» выключено переключателем.
stop "$MAC_PID"
start_mac mac "$MAC_NAME" "$BOARD" --files-off-after 3
MAC_PID=$LAST_PID
expect "$WORK/peer.out" "^INFO $MAC_NAME os=mac form=(laptop|desktop) caps=file,image$" 30 "сначала Mac принимает файлы"
expect "$WORK/mac.out" "^FILES_ENABLED 0$" 10 "переключатель выключен"
expect "$WORK/peer.out" "^INFO $MAC_NAME os=mac form=(laptop|desktop) caps=image$" 10 "двойник узнал, что Mac больше не принимает файлы"
pb write-files "$BOARD" "$WORK/src/ёлка 🎄.txt"
sleep 3
expect_absent "$WORK/mac.out" "^FILES_(SENT|REFUSED)" "Mac файлы из буфера не отправлял"
expect_count "$WORK/peer.out" "^FILE_OFFER " 1 1 "двойник новых описаний не получил"
expect_absent "$WORK/peer.out" "^CLIP " "имя файла текстом не ушло"
# Текст при этом ходит как обычно.
pb write "$BOARD" "текст после выключения $SUFFIX"
expect_clip peer "$WORK/peer.out" "$MAC_NAME" "текст после выключения $SUFFIX" 10 "текст проходит"

pass
