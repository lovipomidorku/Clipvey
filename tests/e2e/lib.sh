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
#   E2E_NO_PREFLIGHT=1 режим loopback: не проверять, что Mac доступен по адресу в локальной сети, и
#                      связывать Mac с двойниками по 127.0.0.1 без mDNS (см. loopback_mode). Поиск
#                      через Bonjour/mDNS и путь через локальную сеть тогда не проверяются.
#   E2E_OLD_PEER=FILE  готовый двойник 0.1.0 для сценариев совместимости
set -euo pipefail

E2E_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$E2E_DIR/../.." && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

PEER="$ROOT/windows/Clipvey.Peer/bin/Debug/net10.0/clipvey-peer"
# Двойник версии 0.1.0 для проверки совместимости (сценарии *-compat-*). По умолчанию собирается сам
# (require_old_peer) из коммита «Clipvey 0.1.0» в tests/e2e/tmp/peer-0.1.0; готовый можно указать в E2E_OLD_PEER.
OLD_PEER_DIR="$E2E_DIR/tmp/peer-0.1.0"
OLD_PEER="${E2E_OLD_PEER:-$OLD_PEER_DIR/windows/Clipvey.Peer/bin/Debug/net10.0/clipvey-peer}"
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
    build_pbtool
    [ -n "${E2E_SKIP_BUILD:-}" ] && [ -x "$MAC" ] && [ -x "$PEER" ] && return 0
    say "сборка"
    (cd "$ROOT/mac" && swift build --product Clipvey 2>&1 | grep -E 'error|Compiling|Build complete' | grep -v '^$' | tail -n 3) \
        || fail "swift build"
    [ -x "$MAC" ] || fail "нет $MAC"
    build_peer
    if [ -n "${E2E_NO_PREFLIGHT:-}" ]; then
        say "предварительная проверка сети пропущена (E2E_NO_PREFLIGHT)"
    else
        preflight_network
    fi
}

# Только двойник (сценарии NN-peers-*.sh: без Mac-приложения, их не держит сетевой фильтр).
build_peer() {
    [ -n "${E2E_SKIP_BUILD:-}" ] && [ -x "$PEER" ] && return 0
    dotnet build "$ROOT/windows/Clipvey.Peer/Clipvey.Peer.csproj" -v quiet -nologo > "$WORK/build-peer.log" 2>&1 \
        || { cat "$WORK/build-peer.log"; fail "dotnet build"; }
}

build_pbtool() {
    mkdir -p "$(dirname "$PBTOOL")"
    if [ ! -x "$PBTOOL" ] || [ "$E2E_DIR/pasteboard.swift" -nt "$PBTOOL" ]; then
        swiftc -O "$E2E_DIR/pasteboard.swift" -o "$PBTOOL" || fail "swiftc pasteboard.swift"
    fi
}

# Двойник 0.1.0: если его нет, собрать из коммита «Clipvey 0.1.0» (git archive, без worktree).
require_old_peer() {
    [ -x "$OLD_PEER" ] && return 0
    [ -n "${E2E_OLD_PEER:-}" ] && fail "нет двойника 0.1.0: $OLD_PEER"
    local commit
    commit=$(git -C "$ROOT" log --format=%H --grep='^Clipvey 0.1.0' | tail -n 1)
    [ -n "$commit" ] || fail "не найден коммит «Clipvey 0.1.0» (задайте E2E_OLD_PEER)"
    say "сборка двойника 0.1.0 из $commit"
    rm -rf "$OLD_PEER_DIR"
    mkdir -p "$OLD_PEER_DIR"
    git -C "$ROOT" archive "$commit" windows | tar -x -C "$OLD_PEER_DIR" || fail "git archive"
    dotnet build "$OLD_PEER_DIR/windows/Clipvey.Peer/Clipvey.Peer.csproj" -v quiet -nologo > "$WORK/build-old-peer.log" 2>&1 \
        || { cat "$WORK/build-old-peer.log"; fail "сборка двойника 0.1.0"; }
    [ -x "$OLD_PEER" ] || fail "нет $OLD_PEER после сборки"
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

# start_old_peer TAG NAME PORT [флаги…] — то же для двойника 0.1.0 (флаги картинок и имени он не знает).
start_old_peer() {
    local tag="$1" name="$2" port="$3"
    shift 3
    rotate "$tag"
    "$OLD_PEER" run --data "$WORK/$tag" --name "$name" --port "$port" --seconds 600 "$@" \
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

# Режим loopback (E2E_NO_PREFLIGHT=1): связывание Mac с двойником идёт по 127.0.0.1 (--pair-address)
# без поиска через mDNS и без адреса в локальной сети. Иначе сценарии связываются как пользователь —
# через список найденных устройств (Bonjour / mDNS), что заодно проверяет TXT.
loopback_mode() { [ -n "${E2E_NO_PREFLIGHT:-}" ]; }

# mac_port FILE — порт проверочного Mac из строки READY.
mac_port() {
    grep -oE "^READY .* port=[0-9]+" "$1" | tail -n 1 | grep -oE "[0-9]+$"
}

# Связывание: Mac — R (показывает код), двойник — I (вводит код из вывода Mac).
# pair_mac_responder MAC_TAG MAC_NAME PEER_TAG PEER_NAME PEER_PORT
pair_mac_responder() {
    local mac_tag="$1" mac_name="$2" peer_tag="$3" peer_name="$4" peer_port="$5"
    start_mac "$mac_tag" "$mac_name" "e2e-pb-$SUFFIX-$mac_tag" --pair --auto-confirm
    local mac_pid=$LAST_PID
    expect "$WORK/$mac_tag.out" "^READY " 20 "Mac запущен"
    local direct=()
    loopback_mode && direct=(--pair-address "127.0.0.1:$(mac_port "$WORK/$mac_tag.out")")
    start_peer "$peer_tag" "$peer_name" "$peer_port" --pair-with "$mac_name" --code-file "$WORK/$mac_tag.out" \
        ${direct[@]+"${direct[@]}"}
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
    local direct=()
    loopback_mode && direct=(--pair-address "127.0.0.1:$peer_port")
    start_mac "$mac_tag" "$mac_name" "e2e-pb-$SUFFIX-$mac_tag" --pair-with "$peer_name" --code-file "$WORK/$peer_tag.out" \
        ${direct[@]+"${direct[@]}"}
    local mac_pid=$LAST_PID
    expect "$WORK/$mac_tag.out" "^PAIRING_WITH $peer_name" 30 "Mac нашёл двойника"
    expect "$WORK/$peer_tag.out" "^PAIRING_CODE [0-9]{6} FROM $mac_name" 30 "двойник показал код"
    expect "$WORK/$mac_tag.out" "^PAIRED $peer_name" 30 "Mac связан с двойником"
    expect "$WORK/$peer_tag.out" "^PAIRED $mac_name" 30 "двойник связан с Mac"
    stop "$mac_pid"
    stop "$peer_pid"
    fix_saved_ports "$mac_tag" "$peer_tag" "$peer_port"
}

# Связывание двух двойников: R показывает код, I вводит его. BIN_R/BIN_I — какие двойники (по умолчанию новые).
# Новый двойник в роли I подключается к R по 127.0.0.1 (--pair-address), не дожидаясь mDNS: поиск двойников
# через mDNS на этом Mac может не работать (VPN, сетевой фильтр). Затем обе стороны запоминают 127.0.0.1 и
# настоящие порты друг друга (set_endpoint), а не порт по умолчанию 48620, занятый настоящим Clipvey.
# pair_peers R_TAG R_NAME R_PORT I_TAG I_NAME I_PORT [BIN_R] [BIN_I]
pair_peers() {
    local r_tag="$1" r_name="$2" r_port="$3" i_tag="$4" i_name="$5" i_port="$6"
    local r_bin="${7:-$PEER}" i_bin="${8:-$PEER}"
    local direct=()
    [ "$i_bin" = "$PEER" ] && direct=(--pair-address "127.0.0.1:$r_port")
    rotate "$r_tag"
    "$r_bin" run --data "$WORK/$r_tag" --name "$r_name" --port "$r_port" --seconds 600 --pair --auto-confirm \
        >> "$WORK/$r_tag.out" 2>> "$WORK/$r_tag.err" &
    local r_pid=$!
    PIDS="$PIDS $r_pid"
    expect "$WORK/$r_tag.out" "^READY " 20 "$r_name запущен (R)"
    rotate "$i_tag"
    "$i_bin" run --data "$WORK/$i_tag" --name "$i_name" --port "$i_port" --seconds 600 \
        --pair-with "$r_name" --code-file "$WORK/$r_tag.out" ${direct[@]+"${direct[@]}"} \
        >> "$WORK/$i_tag.out" 2>> "$WORK/$i_tag.err" &
    local i_pid=$!
    PIDS="$PIDS $i_pid"
    expect "$WORK/$r_tag.out" "^PAIRING_CODE [0-9]{6} FROM $i_name" 30 "$r_name показал код"
    expect "$WORK/$i_tag.out" "^PAIRED $r_name" 30 "$i_name связан с $r_name"
    expect "$WORK/$r_tag.out" "^PAIRED $i_name" 30 "$r_name связан с $i_name"
    stop "$i_pid"
    stop "$r_pid"
    set_endpoint "$r_tag" "$i_name" 127.0.0.1 "$i_port"
    set_endpoint "$i_tag" "$r_name" 127.0.0.1 "$r_port"
}

# set_endpoint TAG DEVICE_NAME HOST PORT — запасной адрес устройства в devices.json двойника TAG.
set_endpoint() {
    python3 - "$WORK/$1/devices.json" "$2" "$3" "$4" <<'PY'
import json, sys
path, name, host, port = sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4])
devices = json.load(open(path))
found = [device for device in devices if device["Name"] == name]
if not found:
    sys.exit(f"в {path} нет устройства {name}")
for device in found:
    device["LastHost"], device["LastPort"] = host, port
json.dump(devices, open(path, "w"), indent=2, ensure_ascii=False)
PY
}

# make_png FILE BYTES [SEED] — настоящий PNG из шума примерно указанного размера (шум не сжимается).
make_png() {
    python3 - "$1" "$2" "${3:-1}" <<'PY'
import os, random, struct, sys, zlib
path, size, seed = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
width = 512
height = max(1, size // (width * 3))
random.seed(seed)
row = lambda: b"\x00" + random.randbytes(width * 3)
raw = b"".join(row() for _ in range(height))
def chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xffffffff)
png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)) \
    + chunk(b"IDAT", zlib.compress(raw, 1)) + chunk(b"IEND", b"")
open(path, "wb").write(png)
PY
}

file_size() { stat -f %z "$1"; }
file_sha() { shasum -a 256 "$1" | cut -d' ' -f1; }

# expect_image FILE FROM IMAGE_FILE SECONDS ОПИСАНИЕ — дождаться «IMAGE FROM размер sha256» ровно один раз.
expect_image() {
    local file="$1" from="$2" image="$3"
    local pattern="^IMAGE $from $(file_size "$image") $(file_sha "$image")\$"
    expect "$file" "$pattern" "$4" "$5"
    sleep 1
    [ "$(count "$file" "$pattern")" -eq 1 ] || fail "$5: получено $(count "$file" "$pattern") раз"
}

# make_tree DIR [BIG_MB] — дерево для передачи файлов: кириллица и эмодзи в именах, пустая папка, пустой файл,
# вложенные папки, случайные данные и большой файл BIG_MB МиБ (по умолчанию 150). Всё — прямо в DIR (верхний уровень).
make_tree() {
    local dir="$1" big="${2:-150}"
    mkdir -p "$dir/Документы/пустая папка" "$dir/Документы/вложенная/ещё глубже"
    printf 'Привет из Clipvey\n' > "$dir/Документы/отчёт.txt"
    : > "$dir/Документы/пустой файл.txt"
    head -c 3000000 /dev/urandom > "$dir/Документы/вложенная/ещё глубже/данные.bin"
    head -c 1048576 /dev/urandom > "$dir/Документы/вложенная/ровно 1 МиБ.bin"
    printf 'ёлка\n' > "$dir/ёлка 🎄.txt"
    head -c $(( big * 1048576 )) /dev/urandom > "$dir/большой.bin"
}

# compare_tree EXPECTED ACTUAL — в ACTUAL те же папки и файлы (имена в NFC), что в EXPECTED, с теми же sha256.
compare_tree() {
    python3 - "$1" "$2" <<'PY' || fail "деревья $1 и $2 различаются"
import hashlib, os, sys, unicodedata
def walk(root):
    result = {}
    for base, dirs, files in os.walk(root):
        rel = os.path.relpath(base, root)
        for name in dirs:
            result[unicodedata.normalize("NFC", os.path.normpath(os.path.join(rel, name)))] = "dir"
        for name in files:
            path = os.path.join(base, name)
            digest = hashlib.sha256(open(path, "rb").read()).hexdigest()
            result[unicodedata.normalize("NFC", os.path.normpath(os.path.join(rel, name)))] = f"{os.path.getsize(path)} {digest}"
    return result
expected, actual = walk(sys.argv[1]), walk(sys.argv[2])
if expected != actual:
    for key in sorted(set(expected) | set(actual)):
        if expected.get(key) != actual.get(key):
            print(f"  {key}: ожидалось {expected.get(key)}, получено {actual.get(key)}")
    sys.exit(1)
print(f"  одинаково: {sum(1 for v in expected.values() if v == 'dir')} папок, {sum(1 for v in expected.values() if v != 'dir')} файлов")
PY
}

# files_speed FILE ID — скорость по строке «FILES_DONE ID файлов байт мс» в МБ/с (10^6 байт).
files_speed() {
    grep -E "^FILES_DONE $2 " "$1" | tail -n 1 | awk '{ if ($5 > 0) printf "%.0f МБ/с (%d байт за %d мс)", $4 / $5 / 1000, $4, $5; else print "?" }'
}

# offer_id FILE — id последнего отправленного описания («FILES_SENT получателей id …»).
offer_id() {
    grep -E "^FILES_SENT " "$1" | tail -n 1 | cut -d' ' -f3
}
