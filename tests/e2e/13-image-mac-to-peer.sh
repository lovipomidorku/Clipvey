#!/bin/bash
# Сценарий 13. Картинки Mac → двойник через именованный буфер:
#   PNG и JPEG уходят как есть (тот же sha256), TIFF — переводится в PNG;
#   картинка со ссылкой рядом (так копирует браузер) уходит картинкой;
#   картинка рядом с обычным текстом (так копируют офисные программы) — уходит текстом.
source "$(dirname "$0")/lib.sh"
build

MAC_NAME="e2e-mac-$SUFFIX"
PEER_NAME="e2e-peer-$SUFFIX"
PEER_PORT="$(free_port)"
BOARD="e2e-pb-$SUFFIX"
PNG="$WORK/image.png"
JPEG="$WORK/image.jpg"
TIFF="$WORK/image.tiff"
make_png "$PNG" 700000 1
make_png "$WORK/source.png" 300000 2
sips -s format jpeg "$WORK/source.png" --out "$JPEG" > /dev/null || fail "sips jpeg"
sips -s format tiff "$WORK/source.png" --out "$TIFF" > /dev/null || fail "sips tiff"

pair_mac_responder mac "$MAC_NAME" peer "$PEER_NAME" "$PEER_PORT"

start_mac mac "$MAC_NAME" "$BOARD"
start_peer peer "$PEER_NAME" "$PEER_PORT" --save-images "$WORK/saved"
expect "$WORK/mac.out" "^CONNECTED $PEER_NAME" 30 "Mac подключился"
expect "$WORK/peer.out" "^CONNECTED $MAC_NAME" 30 "двойник подключился"

# Буфер читается, только когда есть подключения: пишем после CONNECTED.
pb write-image "$BOARD" "$PNG"
expect_image "$WORK/peer.out" "$MAC_NAME" "$PNG" 20 "PNG дошёл как есть"

pb write-image "$BOARD" "$JPEG"
expect_image "$WORK/peer.out" "$MAC_NAME" "$JPEG" 20 "JPEG дошёл как есть"
[ -f "$WORK/saved/$(file_sha "$JPEG").jpg" ] || fail "JPEG сохранён не как image/jpeg"
say "ок: JPEG пришёл как image/jpeg"

pb write-image "$BOARD" "$TIFF"
expect_count "$WORK/peer.out" "^IMAGE $MAC_NAME " 3 20 "TIFF дошёл"
converted=$(grep -E "^IMAGE $MAC_NAME " "$WORK/peer.out" | tail -n 1 | cut -d' ' -f4)
[ "$(head -c 8 "$WORK/saved/$converted.png" | xxd -p)" = "89504e470d0a1a0a" ] || fail "TIFF не переведён в PNG"
say "ок: TIFF переведён в PNG"

make_png "$WORK/with-url.png" 200000 3
pb write-image "$BOARD" "$WORK/with-url.png" "https://example.com/picture.png"
expect_image "$WORK/peer.out" "$MAC_NAME" "$WORK/with-url.png" 20 "картинка со ссылкой ушла картинкой"
expect_absent "$WORK/peer.out" "^CLIP .*example.com/picture.png" "ссылка рядом с картинкой не ушла текстом"

make_png "$WORK/with-text.png" 200000 4
pb write-image "$BOARD" "$WORK/with-text.png" "ячейка с текстом $SUFFIX"
expect_clip peer "$WORK/peer.out" "$MAC_NAME" "ячейка с текстом $SUFFIX" 15 "картинка рядом с текстом ушла текстом"
expect_count "$WORK/peer.out" "^IMAGE $MAC_NAME " 4 1 "картинка рядом с текстом не отправлена"

pass
