#!/bin/bash
# Сценарий 30 (только двойники). Обрыв посреди передачи файлов A → B: A закрывает сеанс, отдав 40 МБ
# (--drop-after-bytes), дальше — обычное переподключение. B ждёт новый сеанс и продолжает с того же места:
#   дерево приходит целиком (структура и sha256), B не пишет FILES_FAILED;
#   продолжение, а не повтор: B продолжает с ненулевого места, а A по законченным запросам отдал меньше,
#   чем всё описание (прерванный файл по новому сеансу — только остаток);
#   то же через OpenFileStream (как виртуальные файлы Проводника).
source "$(dirname "$0")/lib.sh"
build_peer

A_NAME="e2e-a-$SUFFIX"
B_NAME="e2e-b-$SUFFIX"
A_PORT="$(free_port)"
B_PORT="$(free_port)"
make_tree "$WORK/src"
say "дерево: $(du -sh "$WORK/src" | cut -f1)"

pair_peers b "$B_NAME" "$B_PORT" a "$A_NAME" "$A_PORT"

# resume_part MODE_TITLE CHECK_REGEX [флаги B…] — передать дерево с обрывом и проверить продолжение.
resume_part() {
    local title="$1" resumed="$2"
    shift 2
    # a.err и b.err дописываются при каждом запуске: считаем строки, а не ищем любую.
    local drop="Проверка: сеанс с «${B_NAME}» закрыт после [0-9]+ байт файлов"
    local drops resumes
    drops=$(count "$WORK/a.err" "$drop")
    resumes=$(count "$WORK/b.err" "$resumed")
    start_peer b "$B_NAME" "$B_PORT" --save-files "$WORK/saved-$title" "$@"
    B_PID=$LAST_PID
    start_peer a "$A_NAME" "$A_PORT" --send-files "$WORK/src/"* --send-delay 2 --drop-after-bytes 40000000
    A_PID=$LAST_PID
    expect "$WORK/a.out" "^FILES_SENT 1 [0-9a-f]{32} 10 " 30 "$title: A отправил описание"
    local id total
    id="$(offer_id "$WORK/a.out")"
    total="$(offer_total "$WORK/a.out" "$id")"
    expect_count "$WORK/a.err" "$drop" $(( drops + 1 )) 30 "$title: A оборвал сеанс посреди передачи"
    expect "$WORK/b.out" "^DISCONNECTED $A_NAME" 10 "$title: B заметил обрыв"
    expect_count "$WORK/b.out" "^CONNECTED $A_NAME" 2 20 "$title: B снова подключён к A"
    expect "$WORK/b.out" "^FILES_DONE $id 6 " 60 "$title: B скачал все 6 файлов"
    expect_count "$WORK/b.err" "$resumed" $(( resumes + 1 )) 1 "$title: B продолжил по новому сеансу с ненулевого места"
    expect_absent "$WORK/b.out" "^FILES_FAILED" "$title: ошибок скачивания нет"
    compare_tree "$WORK/src" "$WORK/saved-$title/$id"
    say "ок: $title: структура и содержимое совпали"
    stop "$A_PID"
    stop "$B_PID"
    SERVED="$(served_bytes "$WORK/a.err" "$B_NAME")"  # в части 1 в a.err — только она
    TOTAL="$total"
}

# Часть 1: скачивание целиком (DownloadFilesAsync).
resume_part download "продолжаю по новому сеансу с «${A_NAME}», получено [1-9][0-9]* байт"
[ "$SERVED" -lt "$TOTAL" ] || fail "A отдал по законченным запросам $SERVED байт из $TOTAL: похоже, файл запрашивался заново с начала"
say "ок: A отдал по законченным запросам $SERVED из $TOTAL байт — прерванный файл докачан с места обрыва"

# Часть 2: те же файлы через OpenFileStream.
resume_part stream "Чтение файла [0-9]+ \([0-9a-f]{32}\): продолжаю по новому сеансу с «${A_NAME}» с [1-9][0-9]* байт" --save-mode stream

pass
