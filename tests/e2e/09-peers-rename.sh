#!/bin/bash
# Сценарий 9 (только двойники). Тип устройства и переименование:
#   ready несёт os/form/caps, другая сторона их сохраняет;
#   A меняет имя во время сеанса (info) — B видит новое имя, сохраняет его, текст приходит от нового имени;
#   после перезапуска новое имя приходит в ready.
source "$(dirname "$0")/lib.sh"
build_peer

A_NAME="e2e-a-$SUFFIX"
A_NEW="e2e-a2-$SUFFIX"
B_NAME="e2e-b-$SUFFIX"
A_PORT="$(free_port)"
B_PORT="$(free_port)"

pair_peers b "$B_NAME" "$B_PORT" a "$A_NAME" "$A_PORT"

start_peer b "$B_NAME" "$B_PORT" --os windows --form laptop
start_peer a "$A_NAME" "$A_PORT" --os mac --form desktop --rename-after 6 "$A_NEW" --send "после переименования $SUFFIX" --send-delay 9
A_PID=$LAST_PID
expect "$WORK/b.out" "^CONNECTED $A_NAME$" 30 "B подключён к A"
expect "$WORK/b.out" "^INFO $A_NAME os=mac form=desktop caps=image$" 5 "B получил тип A из ready"
expect "$WORK/a.out" "^INFO $B_NAME os=windows form=laptop caps=image$" 5 "A получил тип B из ready"
expect "$WORK/a.out" "^NAME $A_NEW$" 20 "A сменил имя"
expect "$WORK/b.out" "^RENAMED $A_NAME $A_NEW$" 10 "B увидел новое имя (info)"
expect "$WORK/b.out" "^INFO $A_NEW os=mac form=desktop caps=image$" 5 "B: info с новым именем"
expect_clip peer "$WORK/b.out" "$A_NEW" "после переименования $SUFFIX" 15 "текст пришёл от нового имени"
expect_absent "$WORK/b.out" "^DISCONNECTED" "сеанс не прерывался"

"$PEER" list --data "$WORK/b" > "$WORK/b-list.out"
expect "$WORK/b-list.out" "^DEVICE [0-9a-f]{32} $A_NEW enabled=True .* os=mac form=desktop" 1 "B сохранил новое имя и тип A"

# Перезапуск A под новым именем: в ready уже новое имя, повторного RENAMED нет.
stop "$A_PID"
expect "$WORK/b.out" "^DISCONNECTED $A_NEW" 60 "B заметил отключение A"
start_peer a "$A_NEW" "$A_PORT" --os mac --form desktop
expect_count "$WORK/b.out" "^CONNECTED $A_NEW$" 1 40 "B снова подключён к A под новым именем"
expect_count "$WORK/b.out" "^RENAMED " 1 1 "повторного переименования нет"

pass
