#!/bin/bash
# Сценарий 3. Переподключение по сохранённой связи: после связывания оба перезапускаются
# без режима связывания и сами устанавливают сеанс; затем двойник перезапускается ещё раз.
source "$(dirname "$0")/lib.sh"
build

MAC_NAME="e2e-mac-$SUFFIX"
PEER_NAME="e2e-peer-$SUFFIX"
PEER_PORT="$(free_port)"

pair_mac_responder mac "$MAC_NAME" peer "$PEER_NAME" "$PEER_PORT"

start_mac mac "$MAC_NAME" "e2e-pb-$SUFFIX"
start_peer peer "$PEER_NAME" "$PEER_PORT"
PEER_PID=$LAST_PID
expect "$WORK/mac.out" "^CONNECTED $PEER_NAME" 30 "Mac подключился к двойнику"
expect "$WORK/peer.out" "^CONNECTED $MAC_NAME" 30 "двойник подключился к Mac"

# Обрыв со стороны двойника и повторное подключение.
stop "$PEER_PID"
expect "$WORK/mac.out" "^DISCONNECTED $PEER_NAME" 60 "Mac заметил обрыв"
start_peer peer "$PEER_NAME" "$PEER_PORT"
expect "$WORK/peer.out" "^CONNECTED $MAC_NAME" 40 "двойник снова подключён"
expect_count "$WORK/mac.out" "^CONNECTED $PEER_NAME" 2 10 "Mac снова подключён"
sleep 3
expect_count "$WORK/mac.out" "^CONNECTED $PEER_NAME" 2 1 "лишних сеансов нет"

pass
