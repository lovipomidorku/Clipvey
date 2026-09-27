#!/bin/bash
# Все сквозные сценарии подряд: tests/e2e/run-all.sh [номер…]
#   tests/e2e/run-all.sh        все
#   tests/e2e/run-all.sh 2 4    только 02-*.sh и 04-*.sh
# Сборка один раз, затем сценарии с E2E_SKIP_BUILD=1. Итог — прошедшие, проваленные и заблокированные.
# Сценарии NN-peers-*.sh обходятся без Mac-приложения. Если проверочный Mac собрать не удалось или он
# не прошёл предварительную проверку сети, остальные сценарии не запускаются и считаются заблокированными,
# а сценарии двойников идут как обычно. Код выхода — 1 только при настоящих провалах.
# Файлы: 21–23 — только двойники (23 — с двойником 0.1.0, см. E2E_OLD_PEER в lib.sh), 24–25 — с Mac,
# 26–29 — через буфер Mac (тихое получение, окошко «Загрузить», отмена новым содержимым, выключатель, ошибки),
# 30–31 — обрыв посреди передачи и продолжение с того же места (30 — только двойники, 31 — «Загрузить» на Mac).
# Сценарии 26–29 показывают окошко проверочного Mac в правом верхнем углу экрана; 29 подключает маленький образ
# диска (hdiutil, скрыт от Finder) и отключает его в конце.
# Сценарии файлов пишут в tests/e2e/tmp больше 300 МБ на сценарий (удаляются после успеха).
set -uo pipefail
DIR="$(cd "$(dirname "$0")" && pwd)"

scripts=()
if [ $# -eq 0 ]; then
    for script in "$DIR"/[0-9][0-9]-*.sh; do
        scripts+=("$script")
    done
else
    for number in "$@"; do
        for script in "$DIR"/$(printf '%02d' "$number")-*.sh; do
            [ -f "$script" ] && scripts+=("$script")
        done
    done
fi

# Сборка — один раз, функциями из lib.sh: сначала двойник и помощник буфера, затем Mac с проверкой сети.
bash -c "source '$DIR/lib.sh'; build_pbtool; build_peer; FINISHED=1" || { echo "Сборка двойника не прошла (см. выше)"; exit 1; }
mac_ready=1
if ! bash -c "source '$DIR/lib.sh'; build; FINISHED=1"; then
    mac_ready=0
    echo
    echo "Проверочный Mac недоступен (сборка или сеть, см. выше): сценарии с Mac пропускаются как заблокированные."
fi

passed=()
failed=()
blocked=()
for script in "${scripts[@]}"; do
    name="$(basename "$script" .sh)"
    case "$name" in
        *-peers-*) ;;
        *)
            if [ "$mac_ready" -eq 0 ]; then
                blocked+=("$name")
                continue
            fi
            ;;
    esac
    echo
    echo "=== $name"
    started=$(date +%s)
    if E2E_SKIP_BUILD=1 "$script"; then
        passed+=("$name ($(( $(date +%s) - started )) с)")
    else
        failed+=("$name")
    fi
done

echo
echo "=== Итог: прошли ${#passed[@]}, провалены ${#failed[@]}, заблокированы ${#blocked[@]}"
for name in ${passed[@]+"${passed[@]}"}; do
    echo "  ок          $name"
done
for name in ${failed[@]+"${failed[@]}"}; do
    echo "  ПРОВАЛ      $name"
done
for name in ${blocked[@]+"${blocked[@]}"}; do
    echo "  ЗАБЛОКИРОВАН (Mac недоступен)  $name"
done
[ ${#failed[@]} -eq 0 ]
