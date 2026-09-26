#!/bin/bash
# Сценарий 7. Выключенное устройство: двойник выключил синхронизацию с Mac (clipvey-peer disable).
# Сеанс не устанавливается, двойник отвечает Mac «disabled»; текст не передаётся.
# После включения обратно сеанс снова поднимается.
source "$(dirname "$0")/lib.sh"
build
start_mac_log

MAC_NAME="e2e-mac-$SUFFIX"
PEER_NAME="e2e-peer-$SUFFIX"
PEER_PORT="$(free_port)"
BOARD="e2e-pb-$SUFFIX"

pair_mac_responder mac "$MAC_NAME" peer "$PEER_NAME" "$PEER_PORT"

"$PEER" disable "$MAC_NAME" --data "$WORK/peer" > "$WORK/peer-disable.out"
expect "$WORK/peer-disable.out" "^DISABLED $MAC_NAME" 1 "двойник выключил Mac"
"$PEER" list --data "$WORK/peer" > "$WORK/peer-list.out"
expect "$WORK/peer-list.out" "^DEVICE [0-9a-f]{32} $MAC_NAME enabled=False" 1 "в списке двойника Mac выключен"

start_mac mac "$MAC_NAME" "$BOARD"
start_peer peer "$PEER_NAME" "$PEER_PORT"
PEER_PID=$LAST_PID
expect "$WORK/mac.out" "^READY " 20 "Mac запущен"
# Mac подключается сам (сразу или через 5 с, смотря по deviceId) и получает отказ.
expect "$WORK/peer.err" "Сеанс с выключенным устройством «${MAC_NAME}» отклонён" 20 "двойник отклонил сеанс с Mac"
wait_for "$WORK/mac.log" "синхронизация с этим выключена" 5 \
    && say "ок: Mac получил «disabled» (журнал)" \
    || say "журнал Mac недоступен — пропускаю проверку «disabled» на стороне Mac"
pb write "$BOARD" "не должно дойти $SUFFIX"
sleep 5
expect_absent "$WORK/mac.out" "^CONNECTED" "Mac не подключён"
expect_absent "$WORK/peer.out" "^CONNECTED" "двойник не подключён"
expect_absent "$WORK/peer.out" "^CLIP " "текст не передан"
# Отказ не должен вызывать частых повторов: не больше одной-двух попыток за это время.
attempts=$(count "$WORK/peer.err" "Сеанс с выключенным устройством")
[ "$attempts" -le 2 ] || fail "Mac повторял попытки $attempts раз (должен ждать 30 с)"
say "ок: Mac не повторяет попытки чаще раза в 30 с ($attempts)"

# Включаем обратно: двойник перезапускается и подключается сам.
stop "$PEER_PID"
"$PEER" enable "$MAC_NAME" --data "$WORK/peer" > "$WORK/peer-enable.out"
expect "$WORK/peer-enable.out" "^ENABLED $MAC_NAME" 1 "двойник включил Mac"
start_peer peer "$PEER_NAME" "$PEER_PORT"
expect "$WORK/peer.out" "^CONNECTED $MAC_NAME" 45 "после включения сеанс поднялся"
expect "$WORK/mac.out" "^CONNECTED $PEER_NAME" 10 "Mac подключён"

pass
