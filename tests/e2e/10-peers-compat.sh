#!/bin/bash
# Сценарий 10 (только двойники). Совместимость нового двойника с двойником 0.1.0:
#   связывание (0.1.0 — R, новый — I), текст в обе стороны;
#   новый видит, что 0.1.0 картинок не принимает (caps нет), и не шлёт их;
#   info (переименование нового) 0.1.0 пропускает, сеанс не рвётся.
# Роль I для 0.1.0 требует mDNS и проверяется в сценарии 16 (с Mac).
source "$(dirname "$0")/lib.sh"
build_peer
require_old_peer

NEW_NAME="e2e-new-$SUFFIX"
NEW_RENAMED="e2e-new2-$SUFFIX"
OLD_NAME="e2e-old-$SUFFIX"
NEW_PORT="$(free_port)"
OLD_PORT="$(free_port)"
IMAGE="$WORK/image.png"
make_png "$IMAGE" 200000 1

pair_peers old "$OLD_NAME" "$OLD_PORT" new "$NEW_NAME" "$NEW_PORT" "$OLD_PEER" "$PEER"

start_old_peer old "$OLD_NAME" "$OLD_PORT" --send "от 0.1.0 $SUFFIX" --send-delay 3
start_peer new "$NEW_NAME" "$NEW_PORT" --send-image "$IMAGE" --send "от нового $SUFFIX" --send-delay 2 --rename-after 8 "$NEW_RENAMED"
expect "$WORK/new.out" "^CONNECTED $OLD_NAME" 30 "новый подключён к 0.1.0"
expect "$WORK/old.out" "^CONNECTED $NEW_NAME" 10 "0.1.0 подключён к новому"
expect "$WORK/new.out" "^INFO $OLD_NAME os=- form=- caps=-$" 5 "у 0.1.0 нет типа и caps"
expect "$WORK/new.out" "^IMAGE_SENT 0$" 15 "картинка 0.1.0 не отправлена"
expect_clip peer "$WORK/old.out" "$NEW_NAME" "от нового $SUFFIX" 10 "0.1.0 получил текст нового"
expect_clip peer "$WORK/new.out" "$OLD_NAME" "от 0.1.0 $SUFFIX" 10 "новый получил текст 0.1.0"
expect "$WORK/new.out" "^NAME $NEW_RENAMED$" 20 "новый сменил имя (info ушёл 0.1.0)"
expect "$WORK/old.err" "Неизвестное сообщение от «${NEW_NAME}»: info" 10 "0.1.0 пропустил info"
sleep 3
expect_absent "$WORK/old.err" "blob_" "0.1.0 не получал кадров картинки"
expect_absent "$WORK/old.out" "^DISCONNECTED" "сеанс с 0.1.0 не рвался"
expect_absent "$WORK/new.out" "^DISCONNECTED" "сеанс с новым не рвался"

pass
