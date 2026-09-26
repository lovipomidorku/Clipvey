#!/bin/bash
# Сценарий 11 (только двойники). Запасной адрес занят чужим Clipvey:
# X связан с Y, но по сохранённому адресу Y теперь отвечает посторонний двойник C («unknown_device»).
# X не должен замолкать на 30 с (пауза — только после «disabled»): он пробует снова в обычном ритме
# и подключается к Y, как только тот появляется.
# E2E_X_PEER — другой двойник в роли X (например, 0.1.0, чтобы увидеть старую ошибку).
source "$(dirname "$0")/lib.sh"
build_peer

X_BIN="${E2E_X_PEER:-$PEER}"
X_NAME="e2e-x-$SUFFIX"
Y_NAME="e2e-y-$SUFFIX"
C_NAME="e2e-c-$SUFFIX"
X_PORT="$(free_port)"
Y_PORT="$(free_port)"
C_PORT="$(free_port)"

pair_peers y "$Y_NAME" "$Y_PORT" x "$X_NAME" "$X_PORT"
# Запасной адрес Y у X — порт постороннего C.
set_endpoint x "$Y_NAME" 127.0.0.1 "$C_PORT"

start_peer c "$C_NAME" "$C_PORT"
expect "$WORK/c.out" "^READY " 20 "посторонний C запущен"
rotate x
"$X_BIN" run --data "$WORK/x" --name "$X_NAME" --port "$X_PORT" --seconds 600 >> "$WORK/x.out" 2>> "$WORK/x.err" &
PIDS="$PIDS $!"
expect "$WORK/c.err" "несвязанное устройство" 20 "X постучался к C и получил unknown_device"
# Больший deviceId ждёт 5 с, затем пробует каждые 5 с; за 20 с — хотя бы две попытки (при паузе 30 с была бы одна).
sleep 20
attempts=$(count "$WORK/c.err" "несвязанное устройство")
[ "$attempts" -ge 2 ] || fail "X после unknown_device замолчал: попыток $attempts за 20 с"
say "ок: X продолжает попытки ($attempts)"

start_peer y "$Y_NAME" "$Y_PORT"
expect "$WORK/x.out" "^CONNECTED $Y_NAME" 30 "X подключился к Y"

pass
