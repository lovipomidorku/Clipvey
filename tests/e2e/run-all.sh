#!/bin/bash
# Все сквозные сценарии подряд: tests/e2e/run-all.sh [номер…]
#   tests/e2e/run-all.sh        все
#   tests/e2e/run-all.sh 2 4    только 02-*.sh и 04-*.sh
# Сборка один раз, затем сценарии с E2E_SKIP_BUILD=1. Итог — список прошедших и проваленных.
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

# Сборка — один раз, функцией build из lib.sh.
bash -c "source '$DIR/lib.sh'; build; FINISHED=1" || { echo "Сборка не удалась"; exit 1; }

passed=()
failed=()
for script in "${scripts[@]}"; do
    name="$(basename "$script" .sh)"
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
echo "=== Итог: прошли ${#passed[@]}, провалены ${#failed[@]}"
for name in ${passed[@]+"${passed[@]}"}; do
    echo "  ок      $name"
done
for name in ${failed[@]+"${failed[@]}"}; do
    echo "  ПРОВАЛ  $name"
done
[ ${#failed[@]} -eq 0 ]
