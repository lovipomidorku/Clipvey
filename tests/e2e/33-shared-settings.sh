#!/bin/bash
# Сценарий 33. Общие настройки (docs/protocol.md, «Общие настройки») — «Скачивать автоматически»:
#   цепочка двойник A ↔ Mac ↔ двойник B (A и B не связаны): изменение на A доходит до B через Mac, все сохраняют
#   одну и ту же тройку (значение, changed, by), после перезапуска ничего не меняется;
#   устройство, которое было выключено, при подключении получает более новое значение;
#   одновременные изменения на A и B сходятся к одному (большее changed, при равном — большее by);
#   порог действительно меняет поведение Mac: файл 80 МиБ при пороге 100 — тихо в буфер, при 50 (изменено
#   на A) — окошко «Загрузить»;
#   двойник 0.1.0 сообщение settings пропускает, сеанс не рвётся.
source "$(dirname "$0")/lib.sh"
build
require_old_peer

MAC_NAME="e2e-mac-$SUFFIX"
A_NAME="e2e-a-$SUFFIX"
B_NAME="e2e-b-$SUFFIX"
OLD_NAME="e2e-old-$SUFFIX"
A_PORT="$(free_port)"
B_PORT="$(free_port)"
OLD_PORT="$(free_port)"
BOARD="e2e-pb-$SUFFIX"
mkdir -p "$WORK/big"
head -c $(( 80 * 1048576 )) /dev/urandom > "$WORK/big/80 МиБ.bin"

pair_mac_responder mac "$MAC_NAME" a "$A_NAME" "$A_PORT"
pair_mac_responder mac "$MAC_NAME" b "$B_NAME" "$B_PORT"

device_id() { grep -oE "^READY .* id=[0-9a-f]{32}" "$1" | tail -n 1 | grep -oE "[0-9a-f]{32}$"; }
# last_settings FILE СОБЫТИЕ — последняя тройка «МиБ changed by» из строк SETTINGS или AUTO_DOWNLOAD_SET;
# saved_settings FILE — тройка из shared-settings.json.
last_settings() { grep -E "^$2 " "$1" | tail -n 1 | cut -d' ' -f2-; }
saved_settings() {
    python3 - "$1" <<'PY'
import json, sys
data = json.load(open(sys.argv[1]))
print(data["autoDownloadMB"], data["autoDownloadChanged"], data["autoDownloadBy"])
PY
}

start_all() {
    start_mac mac "$MAC_NAME" "$BOARD" "$@"
    MAC_PID=$LAST_PID
    expect "$WORK/mac.out" "^READY " 20 "Mac запущен"
}

# Часть 1: изменение на A доходит по цепочке до B.
start_all
start_peer b "$B_NAME" "$B_PORT"
B_PID=$LAST_PID
expect "$WORK/mac.out" "^CONNECTED $B_NAME" 30 "Mac подключён к B"
start_peer a "$A_NAME" "$A_PORT" --auto-download-mb 300 --settings-delay 6
A_PID=$LAST_PID
expect "$WORK/mac.out" "^CONNECTED $A_NAME" 30 "Mac подключён к A"
expect "$WORK/a.out" "^AUTO_DOWNLOAD_SET 300 [1-9][0-9]+ [0-9a-f]{32}$" 15 "на A изменено: 300 МиБ"
A_ID="$(device_id "$WORK/a.out")"
MAC_ID="$(device_id "$WORK/mac.out")"
B_ID="$(device_id "$WORK/b.out")"
CHANGE="$(last_settings "$WORK/a.out" AUTO_DOWNLOAD_SET)"
[ "${CHANGE##* }" = "$A_ID" ] || fail "by — не A: $CHANGE"
expect "$WORK/mac.out" "^SETTINGS $CHANGE$" 10 "Mac принял изменение A"
expect "$WORK/b.out" "^SETTINGS $CHANGE$" 10 "B получил изменение A через Mac"
sleep 2
[ "$(saved_settings "$WORK/mac/shared-settings.json")" = "$CHANGE" ] || fail "Mac сохранил не то: $(saved_settings "$WORK/mac/shared-settings.json")"
[ "$(saved_settings "$WORK/b/shared-settings.json")" = "$CHANGE" ] || fail "B сохранил не то"
[ "$(saved_settings "$WORK/a/shared-settings.json")" = "$CHANGE" ] || fail "A сохранил не то"
say "ок: все трое сохранили $CHANGE"

# После перезапуска все помнят одно и то же: при подключении ничего не меняется.
stop "$A_PID"
stop "$B_PID"
stop "$MAC_PID"
start_all
start_peer b "$B_NAME" "$B_PORT"
B_PID=$LAST_PID
expect "$WORK/mac.out" "^CONNECTED $B_NAME" 30 "после перезапуска Mac подключён к B"
sleep 3
expect_absent "$WORK/mac.out" "^SETTINGS " "Mac после перезапуска ничего не принял (помнит то же)"
expect_absent "$WORK/b.out" "^SETTINGS " "B после перезапуска ничего не принял"

# Часть 2: B выключен, A меняет; B при подключении получает более новое.
stop "$B_PID"
start_peer a "$A_NAME" "$A_PORT" --auto-download-mb 1000 --settings-delay 3
A_PID=$LAST_PID
expect "$WORK/a.out" "^AUTO_DOWNLOAD_SET 1000 [1-9][0-9]+ $A_ID$" 20 "на A изменено: 1 ГБ"
CHANGE2="$(last_settings "$WORK/a.out" AUTO_DOWNLOAD_SET)"
expect "$WORK/mac.out" "^SETTINGS $CHANGE2$" 30 "Mac принял 1 ГБ, пока B выключен"
start_peer b "$B_NAME" "$B_PORT"
B_PID=$LAST_PID
expect "$WORK/b.out" "^SETTINGS $CHANGE2$" 30 "B при подключении получил более новое значение"
sleep 2
expect_count "$WORK/mac.out" "^SETTINGS " 1 2 "старое значение B Mac не принял"
[ "$(last_settings "$WORK/b.out" SETTINGS)" = "$CHANGE2" ] || fail "у B не последнее значение"

# Часть 3: одновременные изменения на A и B сходятся к одному.
stop "$A_PID"
stop "$B_PID"
start_peer a "$A_NAME" "$A_PORT" --auto-download-mb 100 --settings-delay 8
A_PID=$LAST_PID
start_peer b "$B_NAME" "$B_PORT" --auto-download-mb 500 --settings-delay 8
B_PID=$LAST_PID
expect "$WORK/a.out" "^AUTO_DOWNLOAD_SET 100 " 20 "на A изменено: 100 МиБ"
expect "$WORK/b.out" "^AUTO_DOWNLOAD_SET 500 " 20 "на B изменено: 500 МиБ"
SET_A="$(last_settings "$WORK/a.out" AUTO_DOWNLOAD_SET)"
SET_B="$(last_settings "$WORK/b.out" AUTO_DOWNLOAD_SET)"
WINNER="$(python3 - "$SET_A" "$SET_B" <<'PY'
import sys
a, b = (s.split(" ") for s in sys.argv[1:])
key = lambda s: (int(s[1]), s[2])
print(" ".join(max(a, b, key=key)))
PY
)"
say "A: $SET_A; B: $SET_B; побеждает $WINNER"
converged=0
for _ in $(seq 1 50); do
    if [ "$(last_settings "$WORK/mac.out" SETTINGS)" = "$WINNER" ] \
        && [ "$(last_settings "$WORK/a.out" SETTINGS)" = "$WINNER" ] \
        && [ "$(last_settings "$WORK/b.out" SETTINGS)" = "$WINNER" ]; then
        converged=1
        break
    fi
    sleep 0.2
done
[ "$converged" -eq 1 ] || fail "не сошлись: Mac $(last_settings "$WORK/mac.out" SETTINGS), A $(last_settings "$WORK/a.out" SETTINGS), B $(last_settings "$WORK/b.out" SETTINGS)"
sleep 2
for tag in mac a b; do
    [ "$(last_settings "$WORK/$tag.out" SETTINGS)" = "$WINNER" ] || fail "$tag потом сменил значение: $(last_settings "$WORK/$tag.out" SETTINGS)"
done
say "ок: все трое сошлись к $WINNER"

# Часть 4: порог меняет поведение Mac. 100 МиБ (задано на Mac) — файл 80 МиБ тихо в буфер.
stop "$A_PID"
stop "$B_PID"
stop "$MAC_PID"
start_all --set-auto-download-mb 100 --downloads "$WORK/downloads" --auto-download
expect "$WORK/mac.out" "^AUTO_DOWNLOAD_SET 100 " 10 "на Mac порог 100 МиБ"
start_peer a "$A_NAME" "$A_PORT" --send-files "$WORK/big/80 МиБ.bin" --send-delay 2
A_PID=$LAST_PID
expect "$WORK/a.out" "^FILES_SENT 1 [0-9a-f]{32} 1 83886080$" 30 "A отправил файл 80 МиБ"
ID="$(offer_id "$WORK/a.out")"
expect "$WORK/mac.out" "^FILES_READY $ID 1 pasteboard$" 60 "при пороге 100 МиБ — тихо, в буфер"
expect_absent "$WORK/mac.out" "^TOAST " "окошка не было"
cmp -s "$WORK/big/80 МиБ.bin" "$(pb read-files "$BOARD")" || fail "файл в буфере отличается"

# Порог 50 МиБ изменён на A, пока Mac работает: тот же файл — окошко «Загрузить».
stop "$A_PID"
start_peer a "$A_NAME" "$A_PORT" --auto-download-mb 50 --settings-delay 1 --send-files "$WORK/big/80 МиБ.bin" --send-delay 5
A_PID=$LAST_PID
expect "$WORK/mac.out" "^SETTINGS 50 [1-9][0-9]+ $A_ID$" 30 "Mac получил 50 МиБ от A"
expect "$WORK/a.out" "^FILES_SENT 1 " 30 "A отправил файл ещё раз"
ID="$(offer_id "$WORK/a.out")"
expect "$WORK/mac.out" "^TOAST offer $ID$" 15 "при пороге 50 МиБ — окошко «Загрузить»"
expect "$WORK/mac.out" "^TOAST_CLICK download ok$" 5 "нажата «Загрузить»"
expect "$WORK/mac.out" "^FILES_READY $ID 1 pasteboard$" 60 "загружено и положено в буфер"
cmp -s "$WORK/big/80 МиБ.bin" "$WORK/downloads/80 МиБ.bin" || fail "загруженный файл отличается"
stop "$A_PID"
stop "$MAC_PID"

# Часть 5: двойник 0.1.0 settings пропускает. Связывание: Mac — I.
start_old_peer old "$OLD_NAME" "$OLD_PORT" --pair --auto-confirm
OLD_PID=$LAST_PID
expect "$WORK/old.out" "^READY " 20 "0.1.0 запущен (R)"
DIRECT=()
loopback_mode && DIRECT=(--pair-address "127.0.0.1:$OLD_PORT")
start_mac mac "$MAC_NAME" "e2e-pb1-$SUFFIX" --pair-with "$OLD_NAME" --code-file "$WORK/old.out" ${DIRECT[@]+"${DIRECT[@]}"}
MAC_PID=$LAST_PID
expect "$WORK/mac.out" "^PAIRED $OLD_NAME" 30 "Mac связан с 0.1.0"
expect "$WORK/old.out" "^PAIRED $MAC_NAME" 30 "0.1.0 связан с Mac"
stop "$MAC_PID"
stop "$OLD_PID"
fix_saved_ports mac old "$OLD_PORT"
# Изменение на A Mac пересылает всем остальным сеансам, в том числе 0.1.0.
start_all
start_old_peer old "$OLD_NAME" "$OLD_PORT" --send "от 0.1.0 $SUFFIX" --send-delay 15
OLD_PID=$LAST_PID
expect "$WORK/mac.out" "^CONNECTED $OLD_NAME" 30 "Mac подключён к 0.1.0"
expect_count "$WORK/old.err" "Неизвестное сообщение от «${MAC_NAME}»: settings" 1 10 "0.1.0 пропустил settings после ready"
start_peer a "$A_NAME" "$A_PORT" --auto-download-mb 300 --settings-delay 3
A_PID=$LAST_PID
expect "$WORK/mac.out" "^SETTINGS 300 [1-9][0-9]+ $A_ID$" 30 "Mac принял 300 МиБ от A"
expect_count "$WORK/old.err" "Неизвестное сообщение от «${MAC_NAME}»: settings" 2 10 "0.1.0 пропустил и пересланное изменение"
expect_clip mac "$WORK/mac.out" "$OLD_NAME" "от 0.1.0 $SUFFIX" 25 "текст 0.1.0 дошёл"
expect_absent "$WORK/old.out" "^DISCONNECTED" "сеанс с 0.1.0 не рвался"
expect_absent "$WORK/mac.out" "^DISCONNECTED $OLD_NAME" "сеанс Mac с 0.1.0 не рвался"

pass
