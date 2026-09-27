#!/bin/bash
# Сценарий 23 (только двойники). Файлы и двойник 0.1.0: он не заявляет file, поэтому описаний не получает
# (ни одного file_* в его журнале), сеанс не рвётся, текст в обе стороны проходит.
source "$(dirname "$0")/lib.sh"
build_peer
require_old_peer

NEW_NAME="e2e-new-$SUFFIX"
OLD_NAME="e2e-old-$SUFFIX"
NEW_PORT="$(free_port)"
OLD_PORT="$(free_port)"
mkdir -p "$WORK/src/папка"
printf 'файл\n' > "$WORK/src/папка/файл.txt"

pair_peers old "$OLD_NAME" "$OLD_PORT" new "$NEW_NAME" "$NEW_PORT" "$OLD_PEER" "$PEER"

start_old_peer old "$OLD_NAME" "$OLD_PORT" --send "от 0.1.0 $SUFFIX" --send-delay 4
start_peer new "$NEW_NAME" "$NEW_PORT" --send-files "$WORK/src/папка" --send "от нового $SUFFIX" --send-delay 2
expect "$WORK/new.out" "^CONNECTED $OLD_NAME" 30 "новый подключён к 0.1.0"
expect "$WORK/new.out" "^INFO $OLD_NAME os=- form=- caps=-$" 5 "у 0.1.0 нет caps"
expect "$WORK/new.out" "^FILES_SENT 0 " 15 "описание 0.1.0 не отправлено"
expect_clip peer "$WORK/old.out" "$NEW_NAME" "от нового $SUFFIX" 10 "0.1.0 получил текст нового"
expect_clip peer "$WORK/new.out" "$OLD_NAME" "от 0.1.0 $SUFFIX" 10 "новый получил текст 0.1.0"
sleep 2
expect_absent "$WORK/old.err" "file_" "0.1.0 не получал сообщений о файлах"
expect_absent "$WORK/old.out" "^DISCONNECTED" "сеанс с 0.1.0 не рвался"
expect_absent "$WORK/new.out" "^DISCONNECTED" "сеанс с новым не рвался"

pass
