#!/bin/bash
# Сценарий 19. Запасной адрес занят чужим Clipvey — то же, что сценарий 11, но в роли X — Mac:
# по сохранённому адресу двойника Y отвечает посторонний двойник C («unknown_device»),
# Mac не замолкает на 30 с (пауза — только после «disabled»), а пробует снова в обычном ритме.
source "$(dirname "$0")/lib.sh"
build

MAC_NAME="e2e-mac-$SUFFIX"
Y_NAME="e2e-y-$SUFFIX"
C_NAME="e2e-c-$SUFFIX"
Y_PORT="$(free_port)"
C_PORT="$(free_port)"

pair_mac_responder mac "$MAC_NAME" y "$Y_NAME" "$Y_PORT"
python3 - "$WORK/mac/devices.json" "$C_PORT" <<'PY'
import json, sys
path, port = sys.argv[1], int(sys.argv[2])
devices = json.load(open(path))
for device in devices:
    device["lastHost"], device["lastPort"] = "127.0.0.1", port
json.dump(devices, open(path, "w"), indent=2, ensure_ascii=False)
PY

start_peer c "$C_NAME" "$C_PORT"
expect "$WORK/c.out" "^READY " 20 "посторонний C запущен"
start_mac mac "$MAC_NAME" "e2e-pb-$SUFFIX"
expect "$WORK/c.err" "несвязанное устройство" 20 "Mac постучался к C и получил unknown_device"
sleep 20
attempts=$(count "$WORK/c.err" "несвязанное устройство")
[ "$attempts" -ge 2 ] || fail "Mac после unknown_device замолчал: попыток $attempts за 20 с"
say "ок: Mac продолжает попытки ($attempts)"

pass
