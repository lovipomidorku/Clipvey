# Общие функции сквозных сценариев. Подключается из tests/e2e/NN-*.sh: source "$(dirname "$0")/lib.sh"
#
# Сценарии запускают проверочный Mac-экземпляр (mac/.build/debug/Clipvey --test) и «двойников»
# Windows (clipvey-peer) в отдельных папках данных, с уникальными именами и именованным буфером.
# Настоящий Clipvey на этом Mac они не трогают: другое имя, другой порт, свои данные, свой буфер.
#
# Переменные окружения:
#   E2E_SKIP_BUILD=1   не собирать (run-all.sh собирает один раз)
#   E2E_KEEP=1         не удалять папку сценария tests/e2e/tmp/<сценарий>-<суффикс> после успеха
#   E2E_TIMEOUT=N      множитель таймаутов (по умолчанию 1)
#   E2E_SUFFIX=S       суффикс имён устройств вместо случайного
set -euo pipefail

E2E_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$E2E_DIR/../.." && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

PEER="$ROOT/windows/Clipvey.Peer/bin/Debug/net10.0/clipvey-peer"
MAC="$ROOT/mac/.build/debug/Clipvey"
PBTOOL="$E2E_DIR/tmp/pasteboard"
MARKER="io.github.lovipomidorku.clipvey.remote"

SCENARIO="$(basename "$0" .sh)"
# Уникальный суффикс: имена устройств, буферов и папок не пересекаются с другими запусками.
# E2E_SUFFIX задаёт суффикс имён вручную (например, чтобы повторить запуск с теми же именами).
RUN_ID="$(printf '%04x%04x' $RANDOM $RANDOM)"
SUFFIX="${E2E_SUFFIX:-$RUN_ID}"
WORK="$E2E_DIR/tmp/$SCENARIO-$RUN_ID"
mkdir -p "$WORK"

PIDS=""
LOG_PID=""
PASTEBOARDS=""
FINISHED=0

say() { echo "[$SCENARIO] $*"; }

# Уборка: останавливаются только процессы, запущенные этим сценарием.
cleanup() {
    local status=$?
    local pid
    for pid in $PIDS $LOG_PID; do
        kill "$pid" 2>/dev/null || true
    done
    for pid in $PIDS $LOG_PID; do
        for _ in 1 2 3 4 5 6 7 8 9 10; do
            kill -0 "$pid" 2>/dev/null || break
            sleep 0.2
        done
        kill -9 "$pid" 2>/dev/null || true
        wait "$pid" 2>/dev/null || true
    done
    local board
    for board in $PASTEBOARDS; do
        [ -x "$PBTOOL" ] && "$PBTOOL" clear "$board" 2>/dev/null || true
    done
    if [ "$status" -eq 0 ] && [ "$FINISHED" -eq 1 ] && [ -z "${E2E_KEEP:-}" ]; then
        rm -rf "$WORK"
    else
        echo "[$SCENARIO] файлы сценария: $WORK"
    fi
    exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT TERM

fail() {
    echo "ПРОВАЛ [$SCENARIO]: $*"
    local file
    for file in "$WORK"/*.out "$WORK"/*.err; do
        [ -f "$file" ] || continue
        echo "---- $(basename "$file") (конец)"
        tail -n 25 "$file"
    done
    exit 1
}

pass() {
    FINISHED=1
    echo "ПРОЙДЕН [$SCENARIO]"
}

# Сборка: Mac-приложение (debug), двойник, помощник для именованного буфера.
build() {
    [ -n "${E2E_SKIP_BUILD:-}" ] && [ -x "$MAC" ] && [ -x "$PEER" ] && [ -x "$PBTOOL" ] && return 0
    say "сборка"
    (cd "$ROOT/mac" && swift build --product Clipvey 2>&1 | grep -E 'error|Compiling|Build complete' | grep -v '^$' | tail -n 3) \
        || fail "swift build"
    [ -x "$MAC" ] || fail "нет $MAC"
    dotnet build "$ROOT/windows/Clipvey.Peer/Clipvey.Peer.csproj" -v quiet -nologo > "$WORK/build-peer.log" 2>&1 \
        || { cat "$WORK/build-peer.log"; fail "dotnet build"; }
    mkdir -p "$(dirname "$PBTOOL")"
    if [ ! -x "$PBTOOL" ] || [ "$E2E_DIR/pasteboard.swift" -nt "$PBTOOL" ]; then
        swiftc -O "$E2E_DIR/pasteboard.swift" -o "$PBTOOL" || fail "swiftc pasteboard.swift"
    fi
    preflight_network
}

# Проверка, что проверочный Mac принимает соединения по адресу в локальной сети, а не только
# через 127.0.0.1. Их может придержать сетевой фильтр (Little Snitch и т. п.) или «Локальная сеть»
# в настройках конфиденциальности: после каждой пересборки у ad-hoc-подписанного бинарника новая
# подпись, и фильтр спрашивает заново. Без этой проверки сценарии падали бы по таймауту без объяснения.
preflight_network() {
    local out="$WORK/preflight.out"
    "$MAC" --test --data "$WORK/preflight" --name "e2e-preflight-$SUFFIX" --pasteboard "e2e-preflight-$SUFFIX" \
        > "$out" 2>&1 &
    local pid=$!
    PIDS="$PIDS $pid"
    wait_for "$out" "^READY " 20 || fail "проверочный Mac не запустился"
    local port
    port=$(grep -oE "port=[0-9]+" "$out" | head -n 1 | cut -d= -f2)
    local result
    result=$(python3 - "$port" <<'PY'
import base64, json, socket, struct, sys
port = int(sys.argv[1])
probe = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
probe.connect(("224.0.0.251", 5353))       # адрес в той сети, куда уходит mDNS
host = probe.getsockname()[0]
probe.close()
hello = json.dumps({"t": "hello", "v": 1, "id": "00" * 16,
                    "eph": base64.b64encode(b"\x04" + b"\x01" * 64).decode()}).encode()
try:
    connection = socket.create_connection((host, port), timeout=5)
    connection.sendall(struct.pack(">I", len(hello)) + hello)
    header = connection.recv(4)
    print("ok" if len(header) == 4 else f"{host}: соединение закрыто без ответа")
except OSError as error:
    print(f"{host}: {error}")
PY
)
    stop "$pid"
    [ "$result" = "ok" ] || fail "проверочный Mac не отвечает по адресу в локальной сети ($result). \
Вероятно, соединения нового бинарника $MAC держит сетевой фильтр (Little Snitch) или запрет «Локальная сеть» — \
разрешите их и повторите."
    say "ок: проверочный Mac отвечает по адресу в локальной сети"
}

timeout_scaled() { echo $(( $1 * ${E2E_TIMEOUT:-1} )); }

# wait_for FILE REGEX SECONDS — ждать строку (grep -E) в файле; 1 — не дождались.
wait_for() {
    local file="$1" pattern="$2" limit
    limit=$(( $(timeout_scaled "$3") * 5 ))
    local i=0
    while [ "$i" -lt "$limit" ]; do
        if [ -f "$file" ] && grep -qE -- "$pattern" "$file"; then
            return 0
        fi
        sleep 0.2
        i=$(( i + 1 ))
    done
    return 1
}

# expect FILE REGEX SECONDS ОПИСАНИЕ — то же, но при неудаче сценарий проваливается.
expect() {
    wait_for "$1" "$2" "$3" || fail "за $3 с не дождались: $4 (${1##*/}: /$2/)"
    say "ок: $4"
}

# expect_absent FILE REGEX ОПИСАНИЕ — строки не должно быть.
expect_absent() {
    if [ -f "$1" ] && grep -qE -- "$2" "$1"; then
        fail "не должно быть: $3 (${1##*/}: $(grep -E -- "$2" "$1" | head -n 1))"
    fi
    say "ок: $3"
}

# count FILE REGEX — сколько строк совпало.
count() {
    if [ -f "$1" ]; then grep -cE -- "$2" "$1" || true; else echo 0; fi
}

# expect_count FILE REGEX N SECONDS ОПИСАНИЕ — дождаться, пока совпадений станет ровно N (или больше — провал).
expect_count() {
    local limit
    limit=$(( $(timeout_scaled "$4") * 5 ))
    local i=0 n
    while [ "$i" -lt "$limit" ]; do
        n=$(count "$1" "$2")
        [ "$n" -gt "$3" ] && fail "$5: совпадений $n, ожидалось $3 (${1##*/}: /$2/)"
        [ "$n" -eq "$3" ] && { say "ок: $5"; return 0; }
        sleep 0.2
        i=$(( i + 1 ))
    done
    fail "за $4 с не дождались: $5 (${1##*/}: /$2/ — $(count "$1" "$2") из $3)"
}

# Свободный TCP-порт: двойник каждый раз слушает тот же порт, иначе после перезапуска
# в кэше mDNS мог бы остаться старый.
free_port() {
    python3 -c 'import socket; s=socket.socket(); s.bind(("",0)); print(s.getsockname()[1]); s.close()'
}

# Вывод прошлого запуска с тем же TAG — в TAG.out.prev, чтобы expect не находил старые строки.
rotate() {
    if [ -s "$WORK/$1.out" ]; then
        { echo "==== $(date +%T)"; cat "$WORK/$1.out"; } >> "$WORK/$1.out.prev"
    fi
    : > "$WORK/$1.out"
    echo "==== запуск $(date +%T)" >> "$WORK/$1.err"
}

# start_peer TAG NAME PORT [флаги clipvey-peer…] — двойник с данными в $WORK/TAG; вывод — $WORK/TAG.out/.err.
# Последний запуск с тем же TAG дописывает в те же файлы (с разделителем).
start_peer() {
    local tag="$1" name="$2" port="$3"
    shift 3
    rotate "$tag"
    "$PEER" run --data "$WORK/$tag" --name "$name" --port "$port" --seconds 600 "$@" \
        >> "$WORK/$tag.out" 2>> "$WORK/$tag.err" &
    LAST_PID=$!
    PIDS="$PIDS $LAST_PID"
}

# start_mac TAG NAME PASTEBOARD [флаги Clipvey --test…] — проверочный Mac-экземпляр.
start_mac() {
    local tag="$1" name="$2" board="$3"
    shift 3
    PASTEBOARDS="$PASTEBOARDS $board"
    rotate "$tag"
    "$MAC" --test --data "$WORK/$tag" --name "$name" --pasteboard "$board" "$@" \
        >> "$WORK/$tag.out" 2>> "$WORK/$tag.err" &
    LAST_PID=$!
    PIDS="$PIDS $LAST_PID"
}

# stop PID — остановить один процесс сценария и дождаться завершения.
stop() {
    local pid="$1"
    kill "$pid" 2>/dev/null || true
    for _ in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15; do
        kill -0 "$pid" 2>/dev/null || break
        sleep 0.2
    done
    kill -9 "$pid" 2>/dev/null || true
    wait "$pid" 2>/dev/null || true
    local rest=""
    local p
    for p in $PIDS; do
        [ "$p" = "$pid" ] || rest="$rest $p"
    done
    PIDS="$rest"
}

# Журнал проверочного Mac-экземпляра (только процессы из этого репозитория) → $WORK/mac.log.
start_mac_log() {
    /usr/bin/log stream --style compact --level info \
        --predicate "subsystem == \"io.github.lovipomidorku.clipvey\" AND processImagePath BEGINSWITH \"$ROOT/mac/.build\"" \
        > "$WORK/mac.log" 2>&1 &
    LOG_PID=$!
    sleep 1
}

# peer_clip_count FILE FROM TEXT — сколько раз двойник напечатал «CLIP FROM "текст"» с этим текстом
# (двойник печатает текст строкой JSON).
peer_clip_count() {
    python3 - "$1" "$2" "$3" <<'PY'
import json, sys
path, sender, text = sys.argv[1:]
prefix = "CLIP " + sender + " "
n = 0
try:
    for line in open(path, encoding="utf-8"):
        if line.startswith(prefix) and json.loads(line[len(prefix):]) == text:
            n += 1
except FileNotFoundError:
    pass
print(n)
PY
}

# mac_clip_count FILE FROM TEXT — то же для Mac: «CLIP FROM текст», переводы строк как \n.
mac_clip_count() {
    python3 - "$1" "$2" "$3" <<'PY'
import sys
path, sender, text = sys.argv[1:]
line_expected = "CLIP " + sender + " " + text.replace("\n", "\\n")
n = 0
try:
    n = sum(1 for line in open(path, encoding="utf-8") if line.rstrip("\n") == line_expected)
except FileNotFoundError:
    pass
print(n)
PY
}

# expect_clip peer|mac FILE FROM TEXT SECONDS ОПИСАНИЕ — дождаться, что текст получен ровно один раз.
expect_clip() {
    local kind="$1" file="$2" from="$3" text="$4" limit
    limit=$(( $(timeout_scaled "$5") * 5 ))
    local i=0 n
    while [ "$i" -lt "$limit" ]; do
        n=$("${kind}_clip_count" "$file" "$from" "$text")
        [ "$n" -gt 1 ] && fail "$6: получено $n раз"
        [ "$n" -eq 1 ] && { say "ок: $6"; return 0; }
        sleep 0.3
        i=$(( i + 1 ))
    done
    fail "за $5 с не дождались: $6"
}

# pasteboard write|read|types NAME [TEXT]
pb() { "$PBTOOL" "$@"; }

# После связывания сторона R запоминает адрес I с портом по умолчанию 48620 (порт, с которого I
# подключался, временный). На этом Mac порт 48620 занят настоящим Clipvey, и проверочный экземпляр
# стучался бы к нему: получал unknown_device и 30 с не пробовал снова. Поэтому в сохранённых связях
# порт 48620 заменяется: у Mac — на известный порт двойника, у двойника — на 0 (только поиск mDNS,
# порт проверочного Mac при каждом запуске новый).
# fix_saved_ports MAC_TAG PEER_TAG PEER_PORT
fix_saved_ports() {
    python3 - "$WORK/$1/devices.json" "$WORK/$2/devices.json" "$3" <<'PY'
import json, os, sys
mac_path, peer_path, peer_port = sys.argv[1], sys.argv[2], int(sys.argv[3])
for path, key, value in ((mac_path, "lastPort", peer_port), (peer_path, "LastPort", 0)):
    if not os.path.exists(path):
        continue
    devices = json.load(open(path))
    for device in devices:
        if device.get(key) == 48620:
            device[key] = value
    json.dump(devices, open(path, "w"), indent=2)
PY
}

# Связывание: Mac — R (показывает код), двойник — I (вводит код из вывода Mac).
# pair_mac_responder MAC_TAG MAC_NAME PEER_TAG PEER_NAME PEER_PORT
pair_mac_responder() {
    local mac_tag="$1" mac_name="$2" peer_tag="$3" peer_name="$4" peer_port="$5"
    start_mac "$mac_tag" "$mac_name" "e2e-pb-$SUFFIX-$mac_tag" --pair --auto-confirm
    local mac_pid=$LAST_PID
    expect "$WORK/$mac_tag.out" "^READY " 20 "Mac запущен"
    start_peer "$peer_tag" "$peer_name" "$peer_port" --pair-with "$mac_name" --code-file "$WORK/$mac_tag.out"
    local peer_pid=$LAST_PID
    expect "$WORK/$mac_tag.out" "^PAIRING_CODE [0-9]{6} FROM $peer_name" 30 "Mac показал код"
    expect "$WORK/$peer_tag.out" "^PAIRED $mac_name" 30 "двойник связан с Mac"
    expect "$WORK/$mac_tag.out" "^PAIRED $peer_name" 30 "Mac связан с двойником"
    stop "$peer_pid"
    stop "$mac_pid"
    fix_saved_ports "$mac_tag" "$peer_tag" "$peer_port"
}

# Связывание: Mac — I (вводит код из вывода двойника), двойник — R.
# pair_mac_initiator MAC_TAG MAC_NAME PEER_TAG PEER_NAME PEER_PORT
pair_mac_initiator() {
    local mac_tag="$1" mac_name="$2" peer_tag="$3" peer_name="$4" peer_port="$5"
    start_peer "$peer_tag" "$peer_name" "$peer_port" --pair --auto-confirm
    local peer_pid=$LAST_PID
    expect "$WORK/$peer_tag.out" "^READY " 20 "двойник запущен"
    start_mac "$mac_tag" "$mac_name" "e2e-pb-$SUFFIX-$mac_tag" --pair-with "$peer_name" --code-file "$WORK/$peer_tag.out"
    local mac_pid=$LAST_PID
    expect "$WORK/$mac_tag.out" "^PAIRING_WITH $peer_name" 30 "Mac нашёл двойника"
    expect "$WORK/$peer_tag.out" "^PAIRING_CODE [0-9]{6} FROM $mac_name" 30 "двойник показал код"
    expect "$WORK/$mac_tag.out" "^PAIRED $peer_name" 30 "Mac связан с двойником"
    expect "$WORK/$peer_tag.out" "^PAIRED $mac_name" 30 "двойник связан с Mac"
    stop "$mac_pid"
    stop "$peer_pid"
    fix_saved_ports "$mac_tag" "$peer_tag" "$peer_port"
}
