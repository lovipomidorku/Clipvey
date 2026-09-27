#!/bin/bash
# Сценарий 32. Скорость передачи файлов: папка из 300 файлов по 400 КиБ (≈123 МБ), один файл 120 МиБ и папка
# из 1000 файлов по 4 КиБ —
# двойник → двойник, двойник → Mac (Mac качает сам, --save-files) и Mac → двойник. Содержимое сверяется,
# время и скорость — по строке FILES_DONE получателя, итог — таблицей в конце.
# На множестве файлов видно, стоят ли маленькие сообщения (file_get, file_end) в очереди за подтверждениями:
# без TCP_NODELAY каждый файл ждал бы отложенного подтверждения, и папка шла бы в разы медленнее одного файла
# того же размера. Проверка: папка не медленнее одного файла больше чем вдвое (и чем на 5 секунд).
#   E2E_SPEED_RUNS=N  повторить каждый замер N раз (по умолчанию 1), в таблице — медиана.
source "$(dirname "$0")/lib.sh"
build

RUNS="${E2E_SPEED_RUNS:-1}"
MAC_NAME="e2e-mac-$SUFFIX"
A_NAME="e2e-a-$SUFFIX"
B_NAME="e2e-b-$SUFFIX"
A_PORT="$(free_port)"
B_PORT="$(free_port)"
BOARD="e2e-pb-$SUFFIX"
# Что отправляется: $WORK/many/Папка (папка целиком), $WORK/one/один файл.bin и $WORK/small/Мелкие (папка).
mkdir -p "$WORK/many/Папка" "$WORK/one" "$WORK/small/Мелкие"
head -c $(( 300 * 409600 )) /dev/urandom | split -b 409600 - "$WORK/many/Папка/файл-"
[ "$(ls "$WORK/many/Папка" | wc -l | tr -d ' ')" -eq 300 ] || fail "не 300 файлов"
head -c $(( 120 * 1048576 )) /dev/urandom > "$WORK/one/один файл.bin"
head -c $(( 1000 * 4096 )) /dev/urandom | split -a 3 -b 4096 - "$WORK/small/Мелкие/файл-"
[ "$(ls "$WORK/small/Мелкие" | wc -l | tr -d ' ')" -eq 1000 ] || fail "не 1000 мелких файлов"
say "данные: папка $(du -sh "$WORK/many" | cut -f1) (300 файлов), один файл $(du -sh "$WORK/one" | cut -f1)"

pair_peers b "$B_NAME" "$B_PORT" a "$A_NAME" "$A_PORT"
pair_mac_responder mac "$MAC_NAME" a "$A_NAME" "$A_PORT"

RESULTS="$WORK/results.txt"
: > "$RESULTS"

# measure НАПРАВЛЕНИЕ ЧТО ВЫВОД_ПОЛУЧАТЕЛЯ ПАПКА_ПОЛУЧАТЕЛЯ ВЫВОД_ОТПРАВИТЕЛЯ НОМЕР — после FILES_SENT у отправителя:
# дождаться FILES_DONE, сверить содержимое с $WORK/ЧТО (в первом замере), записать время и удалить полученное.
measure() {
    local direction="$1" kind="$2" out="$3" saved="$4" sender_out="$5" run="$6"
    expect "$sender_out" "^FILES_SENT 1 [0-9a-f]{32} " 30 "$direction, $kind, замер $run: описание отправлено"
    local id
    id="$(offer_id "$sender_out")"
    expect "$out" "^FILES_DONE $id " 180 "$direction, $kind, замер $run: скачано"
    [ "$run" -eq 1 ] && compare_tree "$WORK/$kind" "$saved/$id"
    local ms bytes
    ms=$(grep -E "^FILES_DONE $id " "$out" | tail -n 1 | cut -d' ' -f5)
    bytes=$(grep -E "^FILES_DONE $id " "$out" | tail -n 1 | cut -d' ' -f4)
    echo "$direction|$kind|$ms|$bytes" >> "$RESULTS"
    say "$direction, $kind: $(files_speed "$out" "$id")"
    rm -rf "${saved:?}/$id"
}

# Часть 1: двойник → двойник.
start_peer b "$B_NAME" "$B_PORT" --save-files "$WORK/b-saved"
B_PID=$LAST_PID
for kind in many one small; do
    for run in $(seq "$RUNS"); do
        start_peer a "$A_NAME" "$A_PORT" --send-files "$WORK/$kind/"* --send-delay 1
        A_PID=$LAST_PID
        measure "двойник → двойник" "$kind" "$WORK/b.out" "$WORK/b-saved" "$WORK/a.out" "$run"
        stop "$A_PID"
    done
done
stop "$B_PID"

# Часть 2: двойник → Mac.
start_mac mac "$MAC_NAME" "$BOARD" --save-files "$WORK/mac-saved"
MAC_PID=$LAST_PID
for kind in many one small; do
    for run in $(seq "$RUNS"); do
        start_peer a "$A_NAME" "$A_PORT" --send-files "$WORK/$kind/"* --send-delay 1
        A_PID=$LAST_PID
        measure "двойник → Mac" "$kind" "$WORK/mac.out" "$WORK/mac-saved" "$WORK/a.out" "$run"
        stop "$A_PID"
    done
done
stop "$MAC_PID"

# Часть 3: Mac → двойник.
start_peer a "$A_NAME" "$A_PORT" --save-files "$WORK/a-saved"
A_PID=$LAST_PID
for kind in many one small; do
    for run in $(seq "$RUNS"); do
        start_mac mac "$MAC_NAME" "$BOARD" --send-files "$WORK/$kind/"* --send-delay 1
        MAC_PID=$LAST_PID
        measure "Mac → двойник" "$kind" "$WORK/a.out" "$WORK/a-saved" "$WORK/mac.out" "$run"
        stop "$MAC_PID"
    done
done
stop "$A_PID"

# Итог: медиана по замерам; папка против одного файла.
python3 - "$RESULTS" <<'PY' || fail "папка передаётся намного медленнее одного файла того же размера"
import statistics, sys
rows = [line.rstrip("\n").split("|") for line in open(sys.argv[1], encoding="utf-8") if line.strip()]
table = {}
for direction, kind, ms, size in rows:
    table.setdefault(direction, {}).setdefault(kind, []).append((int(ms), int(size)))
names = {"many": "300 файлов", "one": "1 файл", "small": "1000 мелких"}
print(f"{'направление':<20} {'что':<12} {'мс (медиана)':>13} {'МБ/с':>7}  замеры, мс")
slow = []
for direction, kinds in table.items():
    for kind in ("many", "one", "small"):
        runs = kinds.get(kind, [])
        if not runs:
            continue
        ms = statistics.median(value for value, _ in runs)
        size = runs[0][1]
        print(f"{direction:<20} {names[kind]:<12} {ms:>13.0f} {size / ms / 1000 if ms else 0:>7.0f}  {', '.join(str(v) for v, _ in runs)}")
    if "many" in kinds and "one" in kinds:
        many = statistics.median(value for value, _ in kinds["many"])
        one = statistics.median(value for value, _ in kinds["one"])
        if many > 2 * one and many > one + 5000:
            slow.append(f"{direction}: папка {many:.0f} мс, один файл {one:.0f} мс")
for line in slow:
    print("медленно:", line)
sys.exit(1 if slow else 0)
PY

pass
