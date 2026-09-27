#!/bin/bash
# Сценарий 8 (только двойники). Картинки по цепочке A ↔ B ↔ C (A и C между собой не связаны):
#   картинка A доходит до B и через B до C, ровно один раз, без эха, с тем же sha256;
#   картинка больше 20 МиБ отвергается получателем без разрыва сеанса;
#   C с выключенными картинками их не заявляет — B ему не пересылает.
source "$(dirname "$0")/lib.sh"
build_peer

A_NAME="e2e-a-$SUFFIX"
B_NAME="e2e-b-$SUFFIX"
C_NAME="e2e-c-$SUFFIX"
A_PORT="$(free_port)"
B_PORT="$(free_port)"
C_PORT="$(free_port)"
IMAGE="$WORK/image.png"
BIG="$WORK/big.png"
make_png "$IMAGE" 1200000 1
make_png "$BIG" 21500000 2
say "картинки: $(file_size "$IMAGE") и $(file_size "$BIG") байт"

pair_peers b "$B_NAME" "$B_PORT" a "$A_NAME" "$A_PORT"
pair_peers b "$B_NAME" "$B_PORT" c "$C_NAME" "$C_PORT"

# Часть 1: цепочка.
start_peer b "$B_NAME" "$B_PORT"
B_PID=$LAST_PID
start_peer c "$C_NAME" "$C_PORT" --save-images "$WORK/saved-c"
C_PID=$LAST_PID
expect "$WORK/b.out" "^CONNECTED $C_NAME" 30 "B подключён к C"
expect "$WORK/b.out" "^INFO $C_NAME os=windows form=desktop caps=file,image$" 5 "B знает тип и caps C"
start_peer a "$A_NAME" "$A_PORT" --os mac --form laptop --send-image "$IMAGE" --send-delay 2
A_PID=$LAST_PID
expect "$WORK/b.out" "^INFO $A_NAME os=mac form=laptop caps=file,image$" 30 "B знает тип и caps A"
expect "$WORK/a.out" "^IMAGE_SENT 1$" 15 "A поставил картинку в очередь для B"
expect_image "$WORK/b.out" "$A_NAME" "$IMAGE" 20 "B получил картинку A"
expect_image "$WORK/c.out" "$B_NAME" "$IMAGE" 20 "C получил картинку A через B"
[ "$(file_sha "$WORK/saved-c/$(file_sha "$IMAGE").png")" = "$(file_sha "$IMAGE")" ] || fail "C сохранил не ту картинку"
say "ок: C сохранил ту же картинку"
sleep 2
expect_absent "$WORK/a.out" "^IMAGE " "картинка не вернулась к A (нет эха)"

# Часть 2: больше 20 МиБ — B отказывает, сеанс продолжается (A потом переименуется по тому же сеансу).
stop "$A_PID"
start_peer a "$A_NAME" "$A_PORT" --send-image "$BIG" --ignore-image-limit --send-delay 1 --rename-after 12 "$A_NAME-2"
A_PID=$LAST_PID
expect "$WORK/a.out" "^IMAGE_SENT 1$" 30 "A отправляет картинку больше 20 МиБ"
expect "$WORK/b.err" "Картинка от «${A_NAME}» не принята: размер" 20 "B отказался от картинки больше 20 МиБ"
expect "$WORK/b.out" "^RENAMED $A_NAME $A_NAME-2$" 30 "сеанс A–B жив после отказа (пришёл info)"
expect_count "$WORK/b.out" "^IMAGE " 1 1 "B не принял большую картинку"
expect_count "$WORK/c.out" "^IMAGE " 1 1 "C не получил большую картинку"
expect_count "$WORK/b.out" "^DISCONNECTED $A_NAME" 1 1 "A отключался от B только при перезапуске"

# Часть 3: у C картинки выключены — B ему не пересылает.
stop "$A_PID"
stop "$C_PID"
start_peer c "$C_NAME" "$C_PORT" --images off
expect "$WORK/b.out" "^INFO $C_NAME os=windows form=desktop caps=file$" 30 "B видит, что C картинки не принимает"
make_png "$WORK/image2.png" 300000 3
start_peer a "$A_NAME-2" "$A_PORT" --send-image "$WORK/image2.png" --send-delay 2
expect_image "$WORK/b.out" "$A_NAME-2" "$WORK/image2.png" 30 "B получил вторую картинку A"
sleep 3
expect_absent "$WORK/c.out" "^IMAGE " "C с выключенными картинками вторую не получил"
# Первая картинка пересылалась C в части 1; вторая — нет.
expect_count "$WORK/b.err" "Картинка пересылается" 1 1 "B не пересылал вторую картинку C"

pass
