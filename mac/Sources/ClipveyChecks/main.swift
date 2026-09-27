// Проверка реализации протокола на Swift по общим данным tests/vectors.json (их же проверяет windows/Clipvey.Tests).
//   swift run clipvey-checks                 проверить, при провале — код выхода 1
//   swift run clipvey-checks --vectors FILE  взять данные из другого файла
//   swift run clipvey-checks --generate      напечатать новые данные (только при изменении протокола)
// swift test без Xcode не работает (нет плагина макросов swift-testing и XCTest), поэтому это отдельная программа.
import ClipveyProtocol
import CryptoKit
import Foundation

let arguments = Array(CommandLine.arguments.dropFirst())

func option(_ name: String) -> String? {
    guard let index = arguments.firstIndex(of: name), index + 1 < arguments.count else { return nil }
    return arguments[index + 1]
}

// MARK: - Вспомогательное

extension Data {
    init(hex: String) {
        var bytes = [UInt8]()
        var index = hex.startIndex
        while index < hex.endIndex {
            let next = hex.index(index, offsetBy: 2)
            bytes.append(UInt8(hex[index..<next], radix: 16)!)
            index = next
        }
        self.init(bytes)
    }

    var hex: String { map { String(format: "%02x", $0) }.joined() }
}

/// Папка tests/ в корне репозитория: ищем вверх от текущей папки и от этого файла.
func findVectors() -> URL? {
    if let path = option("--vectors") {
        return URL(fileURLWithPath: path)
    }
    let starts = [
        URL(fileURLWithPath: FileManager.default.currentDirectoryPath),
        URL(fileURLWithPath: #filePath).deletingLastPathComponent(),
    ]
    for start in starts {
        var directory = start.standardizedFileURL
        while directory.path != "/" {
            let candidate = directory.appendingPathComponent("tests/vectors.json")
            if FileManager.default.fileExists(atPath: candidate.path) {
                return candidate
            }
            directory.deleteLastPathComponent()
        }
    }
    return nil
}

/// Сравнение объектов JSON как значений: порядок ключей и экранирование не важны.
func jsonObject(_ data: Data) -> NSDictionary? {
    (try? JSONSerialization.jsonObject(with: data)) as? NSDictionary
}

// MARK: - Генерация данных

/// Фиксированный скаляр P-256 из метки: SHA-256 от строки (заведомо меньше порядка кривой для этих меток).
func fixedKey(_ label: String) -> P256.KeyAgreement.PrivateKey {
    try! P256.KeyAgreement.PrivateKey(rawRepresentation: Data(SHA256.hash(data: Data(label.utf8))))
}

func keyEntry(_ key: P256.KeyAgreement.PrivateKey) -> [String: Any] {
    ["private": key.rawRepresentation.hex, "public": key.publicKey.x963Representation.hex]
}

/// Данные картинки для проверки нарезки: байт i = i mod 251 (не кратно 512 КиБ, три куска).
let blobLength = 1_100_000
let blobData = Data((0..<blobLength).map { UInt8($0 % 251) })

/// Сообщения для проверки кодировки: по одному каждого вида. Имя образца совпадает с t,
/// кроме вариантов с новыми полями (у них t записано в поле "type" образца).
func sampleMessages(keyI: Data, keyR: Data, commit: Data, nonceI: Data, nonceR: Data, ephI: Data, ephR: Data, idI: String, idR: String) -> [(String, WireMessage)] {
    [
        // Без новых полей — как у 0.1.0.
        ("pair_hello", .pairHello(name: "MacBook — Лена", key: keyI, type: .unknown)),
        ("pair_commit", .pairCommit(name: "OFFICE-PC", key: keyR, commit: commit, type: .unknown)),
        ("pair_hello_typed", .pairHello(name: "MacBook — Лена", key: keyI, type: DeviceType(os: "mac", form: "laptop"))),
        ("pair_commit_typed", .pairCommit(name: "OFFICE-PC", key: keyR, commit: commit, type: DeviceType(os: "windows", form: "desktop"))),
        ("ready_full", .ready(PeerInfo(name: "OFFICE-PC", type: DeviceType(os: "windows", form: "desktop"), caps: ["image"]))),
        ("info", .info(PeerInfo(name: "Кухня / ноутбук", type: DeviceType(os: "mac", form: "laptop"), caps: ["image"]))),
        ("info_name_only", .info(PeerInfo(name: "Новое имя"))),
        ("info_caps_empty", .info(PeerInfo(caps: []))),
        ("blob_start", .blobStart(BlobStart(
            id: "ffeeddccbbaa99887766554433221100", origin: idI, hops: 1,
            mime: "image/png", size: blobLength, sha256: Blob.sha256(blobData)))),
        ("blob_chunk", .blobChunk(id: "ffeeddccbbaa99887766554433221100", seq: 2, data: Data("последний кусок 😀".utf8))),
        ("blob_end", .blobEnd(id: "ffeeddccbbaa99887766554433221100")),
        ("pair_nonce", .pairNonce(nonceI)),
        ("pair_reveal", .pairReveal(nonceR)),
        ("pair_verified", .pairVerified),
        ("pair_done", .pairDone),
        ("pair_abort", .pairAbort(reason: "code")),
        ("error", .error(reason: "disabled")),
        ("hello", .hello(id: idI, ephemeral: ephI)),
        ("hello_ack", .helloAck(id: idR, ephemeral: ephR)),
        ("ready", .ready(PeerInfo(name: "Кухня / ноутбук"))),
        ("clip", .clip(ClipPayload(
            id: "00112233445566778899aabbccddeeff",
            origin: idI,
            hops: 3,
            text: "Привет, мир!\nhttps://example.com/путь?a=1&b=2 — «кавычки» \"двойные\" \\ обратная черта\ttab 😀"))),
        ("ping", .ping),
        ("pong", .pong),
        ("file_offer", .fileOffer(sampleOffer)),
        ("file_get", .fileGet(id: sampleOffer.id, req: 7, index: 4, offset: 3_000_000_000)),
        ("file_end", .fileEnd(req: 7, size: 2_000_000_000)),
        ("file_error", .fileError(req: 8, reason: "changed")),
        ("file_cancel", .fileCancel(req: 4_294_967_295)),
        ("settings", .settings(SharedSettings(autoDownloadMB: 300, changed: 1_790_000_000_000, by: idI))),
    ]
}

/// Общие настройки (docs/protocol.md, «Общие настройки»): ближайшее допустимое, разбор без полей и с неверными
/// полями, «кто новее». Правила, которых нет в docs, закреплены здесь для обеих реализаций.
func sampleSharedSettings() -> [String: Any] {
    let nearest: [(Double, Int)] = [
        (-5, 50), (0, 50), (50, 50), (75, 50), (76, 100), (99.5, 100), (200, 100), (201, 300), (400, 300), (401, 500),
        (750, 500), (751, 1000), (5620, 1000), (5621, 10240), (10240, 10240), (2_147_483_647, 10240), (1e20, 10240),
    ]
    let parse: [(String, String, Int, Int64, String)] = [
        ("все поля", #"{"t":"settings","autoDownloadMB":300,"changed":1790000000000,"by":"ec2b4635da6e02fc8b8f6095a12ceb88"}"#,
         300, 1_790_000_000_000, "ec2b4635da6e02fc8b8f6095a12ceb88"),
        ("без полей", #"{"t":"settings"}"#, 50, 0, ""),
        ("только значение", #"{"t":"settings","autoDownloadMB":100}"#, 100, 0, ""),
        ("без значения", #"{"t":"settings","changed":7,"by":"x"}"#, 50, 7, "x"),
        ("незнакомое значение", #"{"t":"settings","autoDownloadMB":2000,"changed":7,"by":"x"}"#, 1000, 7, "x"),
        ("дробное значение", #"{"t":"settings","autoDownloadMB":99.5,"changed":7,"by":"x"}"#, 100, 7, "x"),
        ("огромное значение", #"{"t":"settings","autoDownloadMB":1e20,"changed":7,"by":"x"}"#, 10240, 7, "x"),
        ("строки вместо чисел", #"{"t":"settings","autoDownloadMB":"300","changed":"7","by":5}"#, 50, 0, ""),
        ("true, отрицательное, null", #"{"t":"settings","autoDownloadMB":true,"changed":-3,"by":null}"#, 50, 0, ""),
        ("дробное changed", #"{"t":"settings","autoDownloadMB":300,"changed":1.5,"by":"x"}"#, 300, 0, "x"),
        ("changed не меньше 9·10^15", #"{"t":"settings","autoDownloadMB":300,"changed":9000000000000000,"by":"x"}"#, 300, 0, "x"),
    ]
    let newer: [((Int, Int64, String), (Int, Int64, String), Bool)] = [
        ((50, 5001, "0000"), (100, 5000, "aaaa"), true),
        ((300, 4999, "ffff"), (100, 5000, "aaaa"), false),
        ((50, 5000, "aaab"), (100, 5000, "aaaa"), true),
        ((300, 5000, "aaa"), (100, 5000, "aaaa"), false),
        ((100, 5000, "aaaa"), (300, 5000, "aaaa"), false),
        ((50, 1, "a"), (50, 1, "B"), true),
        ((10240, 0, ""), (50, 0, "0"), false),
    ]
    func triple(_ value: (Int, Int64, String)) -> [Any] { [value.0, value.1, value.2] }
    return [
        "comment": "Общие настройки: allowed_mb — допустимые значения (МиБ, 10240 — «всегда», ровно 10 ГиБ); nearest — ближайшее допустимое (при равном расстоянии — меньшее); parse — разбор settings: autoDownloadMB — любое число → ближайшее, нет или не число — 50; changed — целое от 0 до 9·10^15, иначе 0; by — строка, иначе \"\"; newer — новее ли a, чем b: большее changed, при равном — большее by (порядковое сравнение).",
        "allowed_mb": SharedSettings.allowedMB,
        "default_mb": SharedSettings.defaultMB,
        "nearest": nearest.map { [$0.0, $0.1] as [Any] },
        "parse": parse.map { ["name": $0.0, "json": $0.1, "autoDownloadMB": $0.2, "changed": $0.3, "by": $0.4] as [String: Any] },
        "newer": newer.map { ["a": triple($0.0), "b": triple($0.1), "a_newer": $0.2] as [String: Any] },
    ]
}

/// Описание для образцов: кириллица, эмодзи, пустая папка, пустой файл, размер больше 2^32.
let sampleOffer = FileOffer(
    id: "0123456789abcdef0123456789abcdef",
    items: [
        .directory("Отчёты 2025"),
        .file("Отчёты 2025/итог.pdf", size: 12345),
        .directory("Отчёты 2025/пустая папка"),
        .file("Отчёты 2025/пустой.txt", size: 0),
        .file("фото 😀.jpg", size: 5_000_000_000),
    ],
    total: 5_000_012_345)

/// Описание file_offer строкой JSON (для неверных образцов, которые WireMessage не закодирует).
func offerJSON(id: String = "0123456789abcdef0123456789abcdef", _ items: [[String: Any]], total: Any) -> String {
    let object: [String: Any] = ["t": "file_offer", "id": id, "items": items, "total": total]
    return String(decoding: try! JSONSerialization.data(withJSONObject: object, options: [.sortedKeys, .withoutEscapingSlashes]), as: UTF8.self)
}

func dir(_ path: String) -> [String: Any] { ["path": path, "dir": true] }
func file(_ path: String, _ size: Any) -> [String: Any] { ["path": path, "size": size] }

/// Вложенные папки из частей заданной длины: a…a/b…b/… (каждая с родителем).
func nestedDirs(_ parts: Int, _ length: Int) -> [[String: Any]] {
    var result: [[String: Any]] = []
    var path = ""
    for index in 0..<parts {
        let part = String(repeating: Character(UnicodeScalar(UInt8(0x61 + index))), count: length)
        path = path.isEmpty ? part : "\(path)/\(part)"
        result.append(dir(path))
    }
    return result
}

/// Описания для проверки правил docs/protocol.md, «Описание»: верные принимаются, неверные отбрасываются целиком.
func sampleOffers() -> [String: Any] {
    let longPart = String(repeating: "ж", count: 128)  // 256 байт UTF-8
    let valid: [(String, String)] = [
        ("только пустая папка", offerJSON([dir("Пустая")], total: 0)),
        ("пустой файл", offerJSON([file("пустой.txt", 0)], total: 0)),
        ("часть ровно 255 байт", offerJSON([file(String(repeating: "ж", count: 127) + "a", 1)], total: 1)),
        ("путь ровно 1024 байта", offerJSON(nestedDirs(5, 204), total: 0)),
        ("ровно 10 ГиБ", offerJSON([file("a", 5_368_709_120), file("b", 5_368_709_120)], total: 10_737_418_240)),
        ("точки и пробелы в именах", offerJSON([dir("...a"), file("...a/ b .txt", 3), file(".hidden", 1)], total: 4)),
    ]
    let invalid: [(String, String)] = [
        ("часть ..", offerJSON([dir("..")], total: 0)),
        ("часть . внутри", offerJSON([dir("a"), dir("a/.")], total: 0)),
        ("путь с / в начале", offerJSON([dir("/etc")], total: 0)),
        ("/ в конце", offerJSON([dir("a/")], total: 0)),
        ("пустая часть", offerJSON([dir("a"), dir("a//b")], total: 0)),
        ("обратная черта", offerJSON([file("a\\b", 1)], total: 1)),
        ("двоеточие", offerJSON([file("C:", 1)], total: 1)),
        ("символ 0", offerJSON([file("a\u{0}b", 1)], total: 1)),
        ("часть длиннее 255 байт", offerJSON([file(longPart, 1)], total: 1)),
        ("путь длиннее 1024 байт", offerJSON(nestedDirs(5, 205), total: 0)),
        ("повтор пути", offerJSON([file("a", 1), file("a", 1)], total: 2)),
        ("нет родительской папки", offerJSON([file("a/b", 1)], total: 1)),
        ("родитель — файл", offerJSON([file("a", 1), file("a/b", 1)], total: 2)),
        ("родитель ниже по списку", offerJSON([file("a/b", 1), dir("a")], total: 1)),
        ("у папки есть size", offerJSON([["path": "a", "dir": true, "size": 0]], total: 0)),
        ("у файла нет size", offerJSON([["path": "a"]], total: 0)),
        ("size дробный", offerJSON([file("a", 1.5)], total: 1.5)),
        ("size строкой", offerJSON([file("a", "1")], total: 1)),
        ("size меньше нуля", offerJSON([file("a", -1)], total: -1)),
        ("total не равен сумме", offerJSON([file("a", 1), file("b", 2)], total: 4)),
        ("больше 10 ГиБ", offerJSON([file("a", 5_368_709_120), file("b", 5_368_709_121)], total: 10_737_418_241)),
        ("пустой items", offerJSON([], total: 0)),
        ("id не hex", offerJSON(id: "0123456789abcdef0123456789abcdeg", [dir("a")], total: 0)),
        ("id заглавными", offerJSON(id: "0123456789ABCDEF0123456789ABCDEF", [dir("a")], total: 0)),
        ("id короче 32", offerJSON(id: "0123456789abcdef", [dir("a")], total: 0)),
        ("путь не строка", offerJSON([["path": 5, "size": 1]], total: 1)),
    ]
    func entries(_ list: [(String, String)]) -> [[String: String]] { list.map { ["name": $0.0, "json": $0.1] } }
    return [
        "comment": "Описания file_offer: valid принимаются, invalid отбрасываются целиком (сеанс продолжается). Больше 10 000 элементов проверяется отдельно, без образца.",
        "valid": entries(valid),
        "invalid": entries(invalid),
    ]
}

/// Имена на диске у получателя и имена в описании у отправителя (docs/protocol.md, «Поведение сторон»).
func sampleFileNames() -> [String: Any] {
    let items: [FileItem] = [
        .file("Отчёт.pdf", size: 1),
        .file("отчёт.PDF", size: 1),
        .directory("Папка"),
        .file("Папка/a?b*.txt", size: 1),
        .file("Папка/a_b_.txt", size: 1),
        .file("Папка/.hidden", size: 1),
        .file("Папка/.HIDDEN", size: 1),
        .file("con.txt", size: 1),
        .directory("LPT1"),
        .file("точка в конце.", size: 1),
        .file("пробел в конце ", size: 1),
        .file("tab\tname", size: 1),
        .file("a<b>|\"c", size: 1),
        .directory("PAPKA"),
        .directory("papka"),
        .file("papka/x.txt", size: 1),
        .file("com0", size: 1),
        .file("COM9.log", size: 1),
    ]
    let wire: [String] = ["12:05 отчёт.txt", "a\\b", "й ё".decomposedStringWithCanonicalMapping, "обычное имя.txt"]
    let numbered: [(String, Int, Bool)] = [("отчёт.pdf", 2, false), (".bashrc", 3, false), ("Папка.v2", 2, true), ("архив.tar.gz", 10, false), ("без точки", 2, false)]
    return [
        "comment": "Имена: local — пути на диске у получателя (mac — без замен, windows — с заменой недопустимого в Windows), wire — имя в описании у отправителя (NFC, «\\» и «:» → «_»), numbered — имя с номером при совпадении.",
        "items": items.map { item -> [String: Any] in
            item.isDirectory ? ["path": item.path, "dir": true] : ["path": item.path, "size": item.size!]
        },
        "mac": FileNames.localPaths(items, windows: false),
        "windows": FileNames.localPaths(items, windows: true),
        "wire": wire.map { ["local_hex": Data($0.utf8).hex, "wire": FileNames.wireName($0)] },
        "numbered": numbered.map { ["name": $0.0, "number": $0.1, "dir": $0.2, "result": FileNames.numbered($0.0, $0.1, isDirectory: $0.2)] },
    ]
}

func generate() -> [String: Any] {
    let staticI = fixedKey("Clipvey test vector: initiator static")
    let staticR = fixedKey("Clipvey test vector: responder static")
    let ephemeralI = fixedKey("Clipvey test vector: initiator ephemeral")
    let ephemeralR = fixedKey("Clipvey test vector: responder ephemeral")
    let pkI = staticI.publicKey.x963Representation
    let pkR = staticR.publicKey.x963Representation
    let eI = ephemeralI.publicKey.x963Representation
    let eR = ephemeralR.publicKey.x963Representation

    // Одноразовые числа подобраны так, чтобы код начинался с нуля: проверяется дополнение до 6 цифр.
    let nonceR = Data(SHA256.hash(data: Data("Clipvey test vector: responder nonce".utf8)))
    var nonceI = Data()
    var code = ""
    for attempt in 0... {
        nonceI = Data(SHA256.hash(data: Data("Clipvey test vector: initiator nonce \(attempt)".utf8)))
        code = ClipveyCrypto.code(initiatorKey: pkI, responderKey: pkR, initiatorNonce: nonceI, responderNonce: nonceR)
        if code.hasPrefix("00") { break }
    }
    let commit = ClipveyCrypto.commitment(responderKey: pkR, initiatorKey: pkI, responderNonce: nonceR)

    let dh1 = try! ClipveyCrypto.agree(ephemeralI, ephemeralR.publicKey)
    let dh2 = try! ClipveyCrypto.agree(staticI, ephemeralR.publicKey)
    let dh3 = try! ClipveyCrypto.agree(ephemeralI, staticR.publicKey)
    let th = ClipveyCrypto.sessionTranscript(initiatorKey: pkI, responderKey: pkR, initiatorEphemeral: eI, responderEphemeral: eR)
    let keys = ClipveyCrypto.sessionKeys(dh1: dh1, dh2: dh2, dh3: dh3, transcript: th)
    let kIR = keys.initiatorToResponder.withUnsafeBytes { Data($0) }
    let kRI = keys.responderToInitiator.withUnsafeBytes { Data($0) }

    let idI = ClipveyCrypto.deviceID(for: pkI)
    let idR = ClipveyCrypto.deviceID(for: pkR)

    // Кадры: счётчик с разными байтами ловит ошибки порядка байтов.
    let frameSpecs: [(String, Data, UInt64, String)] = [
        ("I→R, первый кадр", kIR, 0, #"{"t":"ready","name":"Кухня / ноутбук"}"#),
        ("R→I, счётчик 0x0102030405060708", kRI, 0x0102_0304_0506_0708, #"{"t":"ping"}"#),
    ]
    let frames: [[String: Any]] = frameSpecs.map { name, key, counter, text in
        var codec = SecureCodec(sendKey: SymmetricKey(data: key), receiveKey: SymmetricKey(data: key), sendCounter: counter, receiveCounter: counter)
        let payload = try! codec.seal(Data(text.utf8))
        return [
            "name": name,
            "key": key.hex,
            "counter": String(counter),
            "nonce": SecureCodec.nonce(counter).hex,
            "plaintext": Data(text.utf8).hex,
            "payload": payload.hex,
        ]
    }

    // Двоичные куски файлов: отдельный раздел (открытый текст — не JSON).
    let chunkSpecs: [(String, Data, UInt64, UInt32, Data)] = [
        ("R→I, req 1, счётчик 5", kRI, 5, 1, Data("начало файла".utf8)),
        ("I→R, req 4294967295, счётчик 0x0102030405060708", kIR, 0x0102_0304_0506_0708, 4_294_967_295, Data((0...255).map { UInt8($0) })),
    ]
    let chunkFrames: [[String: Any]] = chunkSpecs.map { name, key, counter, req, data in
        var codec = SecureCodec(sendKey: SymmetricKey(data: key), receiveKey: SymmetricKey(data: key), sendCounter: counter, receiveCounter: counter)
        let plaintext = WireMessage.fileChunk(req: req, data: data).encode()
        return [
            "name": name,
            "key": key.hex,
            "counter": String(counter),
            "nonce": SecureCodec.nonce(counter).hex,
            "req": Int(req),
            "data": data.hex,
            "plaintext": plaintext.hex,
            "payload": try! codec.seal(plaintext).hex,
        ]
    }

    let messages: [[String: Any]] = sampleMessages(
        keyI: pkI, keyR: pkR, commit: commit, nonceI: nonceI, nonceR: nonceR, ephI: eI, ephR: eR, idI: idI, idR: idR
    ).map { name, message in
        var entry: [String: Any] = ["name": name, "json": String(decoding: message.encode(), as: UTF8.self)]
        if name != message.type {
            entry["type"] = message.type
        }
        return entry
    }

    let ranges = Blob.chunkRanges(size: blobLength)
    let blob: [String: Any] = [
        "comment": "Нарезка картинки: байт i = i mod 251, длина length. Куски — по 524288 байт, последний короче.",
        "length": blobLength,
        "sha256": Blob.sha256(blobData).base64EncodedString(),
        "chunk_sizes": ranges.map(\.count),
        "chunk_sha256": ranges.map { Blob.sha256(blobData.subdata(in: $0)).hex },
    ]

    return [
        "comment": "Общие проверочные данные протокола (docs/protocol.md). Байты — hex строчными. Проверяют: cd mac && swift run clipvey-checks; dotnet test windows/Clipvey.Tests. Сообщения сравниваются как объекты JSON: порядок ключей и экранирование (например, эмодзи как \\uD83D\\uDE00 в .NET) у реализаций разные.",
        "keys": [
            "initiator_static": keyEntry(staticI),
            "responder_static": keyEntry(staticR),
            "initiator_ephemeral": keyEntry(ephemeralI),
            "responder_ephemeral": keyEntry(ephemeralR),
        ],
        "device_id": ["initiator": idI, "responder": idR],
        "pairing": [
            "responder_nonce": nonceR.hex,
            "initiator_nonce": nonceI.hex,
            "commit": commit.hex,
            "code": code,
        ],
        "session": [
            "dh1": dh1.hex,
            "dh2": dh2.hex,
            "dh3": dh3.hex,
            "th": th.hex,
            "okm": (kIR + kRI).hex,
            "k_ir": kIR.hex,
            "k_ri": kRI.hex,
        ],
        "frames": frames,
        "messages": messages,
        "blob": blob,
        "file_chunk_frames": chunkFrames,
        "file_offers": sampleOffers(),
        "file_names": sampleFileNames(),
        "shared_settings": sampleSharedSettings(),
    ]
}

// MARK: - Проверка

var passed = 0
var failures: [String] = []

@MainActor func check(_ name: String, _ condition: Bool, _ detail: @autoclosure () -> String = "") {
    if condition {
        passed += 1
        print("ок      \(name)")
    } else {
        let text = detail().isEmpty ? name : "\(name): \(detail())"
        failures.append(text)
        print("ПРОВАЛ  \(text)")
    }
}

@MainActor func expectEqual(_ name: String, _ actual: String, _ expected: String) {
    check(name, actual == expected, "получено \(actual), ожидалось \(expected)")
}

@MainActor func verify(_ v: [String: Any]) {
    let keys = v["keys"] as! [String: [String: String]]
    func privateKey(_ name: String) -> P256.KeyAgreement.PrivateKey {
        try! P256.KeyAgreement.PrivateKey(rawRepresentation: Data(hex: keys[name]!["private"]!))
    }
    func publicKey(_ name: String) -> Data { Data(hex: keys[name]!["public"]!) }

    // Ключи
    for name in keys.keys.sorted() {
        expectEqual("ключ \(name): открытый из закрытого", privateKey(name).publicKey.x963Representation.hex, publicKey(name).hex)
        check("ключ \(name): импорт X9.63", (try? ClipveyCrypto.publicKey(from: publicKey(name))) != nil)
    }
    let pkI = publicKey("initiator_static"), pkR = publicKey("responder_static")
    let eI = publicKey("initiator_ephemeral"), eR = publicKey("responder_ephemeral")

    let ids = v["device_id"] as! [String: String]
    expectEqual("deviceId I", ClipveyCrypto.deviceID(for: pkI), ids["initiator"]!)
    expectEqual("deviceId R", ClipveyCrypto.deviceID(for: pkR), ids["responder"]!)
    let identity = DeviceIdentity(privateKey: privateKey("initiator_static"))
    expectEqual("DeviceIdentity.deviceID", identity.deviceID, ids["initiator"]!)

    // Связывание
    let pairing = v["pairing"] as! [String: String]
    let nonceI = Data(hex: pairing["initiator_nonce"]!), nonceR = Data(hex: pairing["responder_nonce"]!)
    let commit = ClipveyCrypto.commitment(responderKey: pkR, initiatorKey: pkI, responderNonce: nonceR)
    expectEqual("обязательство C", commit.hex, pairing["commit"]!)
    expectEqual("код", ClipveyCrypto.code(initiatorKey: pkI, responderKey: pkR, initiatorNonce: nonceI, responderNonce: nonceR), pairing["code"]!)
    check("код: 6 цифр с ведущим нулём", pairing["code"]!.count == 6 && pairing["code"]!.hasPrefix("0"))
    check("код: роли не перепутаны", ClipveyCrypto.code(initiatorKey: pkR, responderKey: pkI, initiatorNonce: nonceR, responderNonce: nonceI) != pairing["code"]!)

    // Сеанс: dh считают обе стороны, по таблице из docs/protocol.md.
    let session = v["session"] as! [String: String]
    let sI = privateKey("initiator_static"), sR = privateKey("responder_static")
    let ephI = privateKey("initiator_ephemeral"), ephR = privateKey("responder_ephemeral")
    func agree(_ key: P256.KeyAgreement.PrivateKey, _ peer: Data) -> Data {
        try! ClipveyCrypto.agree(key, try! ClipveyCrypto.publicKey(from: peer))
    }
    let dh = [
        ("dh1", agree(ephI, eR), agree(ephR, eI)),
        ("dh2", agree(sI, eR), agree(ephR, pkI)),
        ("dh3", agree(ephI, pkR), agree(sR, eI)),
    ]
    for (name, byI, byR) in dh {
        expectEqual("\(name), сторона I", byI.hex, session[name]!)
        expectEqual("\(name), сторона R", byR.hex, session[name]!)
    }
    let th = ClipveyCrypto.sessionTranscript(initiatorKey: pkI, responderKey: pkR, initiatorEphemeral: eI, responderEphemeral: eR)
    expectEqual("th", th.hex, session["th"]!)
    let keysIR = ClipveyCrypto.sessionKeys(dh1: dh[0].1, dh2: dh[1].1, dh3: dh[2].1, transcript: th)
    let kIR = keysIR.initiatorToResponder.withUnsafeBytes { Data($0) }
    let kRI = keysIR.responderToInitiator.withUnsafeBytes { Data($0) }
    expectEqual("okm", (kIR + kRI).hex, session["okm"]!)
    expectEqual("k_IR", kIR.hex, session["k_ir"]!)
    expectEqual("k_RI", kRI.hex, session["k_ri"]!)

    // Шифрованные кадры
    for frame in v["frames"] as! [[String: String]] {
        let name = frame["name"]!
        let key = SymmetricKey(data: Data(hex: frame["key"]!))
        let counter = UInt64(frame["counter"]!)!
        let plaintext = Data(hex: frame["plaintext"]!)
        expectEqual("кадр \(name): nonce", SecureCodec.nonce(counter).hex, frame["nonce"]!)
        var sender = SecureCodec(sendKey: key, receiveKey: key, sendCounter: counter, receiveCounter: counter)
        expectEqual("кадр \(name): шифротекст‖тег", ((try? sender.seal(plaintext)) ?? Data()).hex, frame["payload"]!)
        var receiver = SecureCodec(sendKey: key, receiveKey: key, sendCounter: counter, receiveCounter: counter)
        expectEqual("кадр \(name): расшифровка", ((try? receiver.open(Data(hex: frame["payload"]!))) ?? Data()).hex, plaintext.hex)
        var wrongCounter = SecureCodec(sendKey: key, receiveKey: key, sendCounter: counter &+ 1, receiveCounter: counter &+ 1)
        check("кадр \(name): другой счётчик отвергается", (try? wrongCounter.open(Data(hex: frame["payload"]!))) == nil)
        var tampered = Data(hex: frame["payload"]!)
        tampered[tampered.count - 1] ^= 1
        var tamperedReceiver = SecureCodec(sendKey: key, receiveKey: key, sendCounter: counter, receiveCounter: counter)
        check("кадр \(name): испорченный тег отвергается", (try? tamperedReceiver.open(tampered)) == nil)
    }
    // Счётчик растёт с каждым кадром: второй кадр подряд — со счётчиком 1.
    var first = SecureCodec(sendKey: SymmetricKey(data: kIR), receiveKey: SymmetricKey(data: kIR))
    _ = try? first.seal(Data("a".utf8))
    let second = (try? first.seal(Data("b".utf8))) ?? Data()
    var atOne = SecureCodec(sendKey: SymmetricKey(data: kIR), receiveKey: SymmetricKey(data: kIR), sendCounter: 1, receiveCounter: 1)
    expectEqual("счётчик растёт с каждым кадром", second.hex, ((try? atOne.seal(Data("b".utf8))) ?? Data()).hex)

    // Сообщения: разбор образца и обратное кодирование дают тот же объект JSON.
    for message in v["messages"] as! [[String: String]] {
        let name = message["name"]!
        let expected = Data(message["json"]!.utf8)
        guard let decoded = try? WireMessage.decode(expected) else {
            check("сообщение \(name): разбор", false, "не разобрано")
            continue
        }
        expectEqual("сообщение \(name): тип", decoded.type, message["type"] ?? name)
        let encoded = decoded.encode()
        check("сообщение \(name): тот же объект JSON", jsonObject(encoded) == jsonObject(expected),
              "получено \(String(decoding: encoded, as: UTF8.self))")
        let text = String(decoding: encoded, as: UTF8.self)
        check("сообщение \(name): «/» без экранирования", !text.contains(#"\/"#))
        check("сообщение \(name): не-ASCII без \\u", !text.contains(#"\u"#), text)
    }

    // Сообщения, закодированные в .NET (эмодзи — суррогатной парой \uXXXX), разбираются так же.
    if let foreign = v["foreign_messages"] as? [[String: String]] {
        for message in foreign {
            let name = message["name"]!
            let decoded = try? WireMessage.decode(Data(message["json"]!.utf8))
            if case .clip(let clip)? = decoded {
                expectEqual("чужое сообщение \(name): текст", clip.text, message["text"]!)
            } else {
                check("чужое сообщение \(name): разбор", false, "не clip")
            }
        }
    }

    // Неверные сообщения отвергаются.
    let bad: [(String, String)] = [
        ("не JSON", "hello"),
        ("нет t", #"{"name":"x"}"#),
        ("ключ не той длины", #"{"t":"pair_hello","v":1,"name":"x","key":"AAAA"}"#),
        ("не base64", #"{"t":"pair_nonce","nonce":"***"}"#),
    ]
    for (name, json) in bad {
        check("отвергается: \(name)", (try? WireMessage.decode(Data(json.utf8))) == nil)
    }
    check("неизвестный t пропускается", (try? WireMessage.decode(Data(#"{"t":"future","x":1}"#.utf8)))?.type == "future")
    check("чужой ключ не на кривой отвергается",
          (try? ClipveyCrypto.publicKey(from: Data([0x04] + [UInt8](repeating: 1, count: 64)))) == nil)

    verifyNewFields(v)
    verifyBlob(v)
    verifyFiles(v)
    verifySharedSettings(v)

    // Подпись релиза (docs/releases.md): base64 r‖s, ECDSA P-256 с SHA-256, одноразовый ключ.
    if let release = v["release_signature"] as? [String: String] {
        let key = try? P256.Signing.PublicKey(x963Representation: Data(base64Encoded: release["public_key"]!)!)
        let signature = try? P256.Signing.ECDSASignature(rawRepresentation: Data(base64Encoded: release["signature"]!)!)
        let message = Data(release["message"]!.utf8)
        if let key, let signature {
            check("подпись релиза проверяется", key.isValidSignature(signature, for: message))
            check("подпись релиза: изменённый текст отвергается", !key.isValidSignature(signature, for: message + Data("x".utf8)))
        } else {
            check("подпись релиза: разбор ключа и подписи", false)
        }
    }
}

// MARK: - Общие настройки

@MainActor func verifySharedSettings(_ v: [String: Any]) {
    guard let section = v["shared_settings"] as? [String: Any],
          let allowed = section["allowed_mb"] as? [Int],
          let nearest = section["nearest"] as? [[NSNumber]],
          let parse = section["parse"] as? [[String: Any]],
          let newer = section["newer"] as? [[String: Any]] else {
        check("раздел shared_settings", false, "нет или неполный")
        return
    }
    check("общие настройки: допустимые значения", SharedSettings.allowedMB == allowed && SharedSettings.defaultMB == section["default_mb"] as? Int)
    check("общие настройки: «всегда» — ровно 10 ГиБ",
          SharedSettings(autoDownloadMB: SharedSettings.alwaysMB, changed: 0, by: "").autoDownloadBytes == ProtocolLimits.maxFileTotalBytes)
    check("общие настройки: 50 МиБ по умолчанию", SharedSettings.default(deviceID: "x").autoDownloadBytes == 50 * 1024 * 1024)
    for pair in nearest {
        let input = pair[0].doubleValue, expected = pair[1].intValue
        expectEqual("ближайшее к \(pair[0])", String(SharedSettings.nearest(input)), String(expected))
        if input == input.rounded(), abs(input) < 1e18 {
            expectEqual("ближайшее к \(pair[0]) (целое)", String(SharedSettings.nearest(Int(input))), String(expected))
        }
    }
    for entry in parse {
        let name = entry["name"] as! String
        let expected = SharedSettings(autoDownloadMB: entry["autoDownloadMB"] as! Int, changed: (entry["changed"] as! NSNumber).int64Value,
                                      by: entry["by"] as! String)
        if case .settings(let settings)? = try? WireMessage.decode(Data((entry["json"] as! String).utf8)) {
            check("settings разбирается: \(name)", settings == expected, "получено \(settings), ожидалось \(expected)")
        } else {
            check("settings разбирается: \(name)", false, "не settings")
        }
    }
    func settings(_ value: Any?) -> SharedSettings {
        let array = value as! [Any]
        return SharedSettings(autoDownloadMB: array[0] as! Int, changed: (array[1] as! NSNumber).int64Value, by: array[2] as! String)
    }
    for entry in newer {
        let a = settings(entry["a"]), b = settings(entry["b"])
        check("новее: \(a) против \(b)", a.isNewer(than: b) == (entry["a_newer"] as! Bool))
    }
    // Образец сообщения: разбор и обратно.
    let messages = v["messages"] as! [[String: String]]
    if let json = messages.first(where: { $0["name"] == "settings" })?["json"],
       case .settings(let sample)? = try? WireMessage.decode(Data(json.utf8)) {
        check("settings: поля образца", sample.autoDownloadMB == 300 && sample.changed == 1_790_000_000_000 && sample.by.count == 32)
    } else {
        check("образец settings разбирается", false)
    }
}

// MARK: - Обновления (docs/releases.md)

/// Разбор версии и ответа releases/latest, SHA256SUMS, подпись и хеш — как в Updater.
@MainActor func verifyReleaseChecks(_ v: [String: Any]) {
    func version(_ text: String) -> String { ReleaseVersion(text)?.description ?? "nil" }
    expectEqual("версия: v0.2.0", version("v0.2.0"), "0.2.0")
    expectEqual("версия: 10.0.1", version("10.0.1"), "10.0.1")
    for bad in ["", "1.2", "1.2.3.4", "v1.2.x", "1.2.3-beta", "1..3", " v1.2.3x", "+1.2.3", "１.2.3"] {
        check("версия отвергается: «\(bad)»", ReleaseVersion(bad) == nil)
    }
    let ordered = ["0.1.0", "0.1.1", "0.2.0", "0.10.0", "1.0.0", "1.0.10"].compactMap(ReleaseVersion.init)
    check("версии сравниваются как числа", ordered == ordered.sorted() && ordered.count == 6)
    check("равные версии не новее", !(ReleaseVersion("v0.1.0")! > ReleaseVersion("0.1.0")!))

    let json = #"""
    {"tag_name":"v0.2.0","draft":false,"assets":[
      {"name":"Clipvey-mac.zip","browser_download_url":"https://github.com/o/r/releases/download/v0.2.0/Clipvey-mac.zip"},
      {"name":"SHA256SUMS","browser_download_url":"https://github.com/o/r/releases/download/v0.2.0/SHA256SUMS"},
      {"name":"SHA256SUMS.sig","browser_download_url":"https://github.com/o/r/releases/download/v0.2.0/SHA256SUMS.sig","size":88}],
     "extra":{"x":1}}
    """#
    if let release = try? ReleaseInfo.parse(Data(json.utf8)) {
        expectEqual("releases/latest: тег", release.tag, "v0.2.0")
        expectEqual("releases/latest: версия", release.version.description, "0.2.0")
        expectEqual("releases/latest: файлы", release.assets.keys.sorted().joined(separator: ","), "Clipvey-mac.zip,SHA256SUMS,SHA256SUMS.sig")
    } else {
        check("releases/latest: разбор", false)
    }
    for bad in [#"{"assets":[]}"#, #"{"tag_name":"latest"}"#, "[]", "не json"] {
        check("releases/latest отвергается: \(bad)", (try? ReleaseInfo.parse(Data(bad.utf8))) == nil)
    }

    guard let release = v["release_signature"] as? [String: String] else {
        check("release_signature в vectors.json", false)
        return
    }
    let message = Data(release["message"]!.utf8)
    let signature = Data(release["signature"]!.utf8)
    let key = release["public_key"]!
    let sums = try? ReleaseVerifier.verifiedChecksums(sums: message, signature: signature + Data("\n".utf8), publicKeyBase64: key)
    expectEqual("SHA256SUMS: подпись и разбор", sums?["Clipvey.exe"] ?? "nil", "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210")
    check("SHA256SUMS: изменённый текст отвергается",
          (try? ReleaseVerifier.verifiedChecksums(sums: message + Data("x".utf8), signature: signature, publicKeyBase64: key)) == nil)
    check("SHA256SUMS: чужой ключ отвергается",
          (try? ReleaseVerifier.verifiedChecksums(sums: message, signature: signature)) == nil)
    var tampered = Data(base64Encoded: release["signature"]!)!
    tampered[5] ^= 1
    check("SHA256SUMS: испорченная подпись отвергается",
          (try? ReleaseVerifier.verifiedChecksums(sums: message, signature: Data(tampered.base64EncodedString().utf8), publicKeyBase64: key)) == nil)
    check("SHA256SUMS: подпись не base64 отвергается",
          (try? ReleaseVerifier.verifiedChecksums(sums: message, signature: Data("***".utf8), publicKeyBase64: key)) == nil)

    let file = Data("новая версия".utf8)
    let hash = SHA256.hash(data: file).map { String(format: "%02x", $0) }.joined()
    let list = try? ReleaseVerifier.parseChecksums(Data("\(hash)  Clipvey-mac.zip\n".utf8))
    check("хеш файла совпадает", list.map { (try? ReleaseVerifier.verifyFile(file, name: "Clipvey-mac.zip", checksums: $0)) != nil } ?? false)
    check("изменённый файл отвергается", list.map { (try? ReleaseVerifier.verifyFile(file + Data([0]), name: "Clipvey-mac.zip", checksums: $0)) == nil } ?? false)
    check("файла нет в SHA256SUMS", list.map { (try? ReleaseVerifier.verifyFile(file, name: "Clipvey.exe", checksums: $0)) == nil } ?? false)
    for bad in ["\(hash)  Clipvey-mac.zip", "\(hash) Clipvey-mac.zip\n", "\(hash.uppercased())  Clipvey-mac.zip\n", "\(hash.dropLast())  a\n",
                "\(hash)  a\n\(hash)  a\n", "\(hash)  \n"] {
        check("SHA256SUMS отвергается: \(bad.debugDescription.prefix(24))…", (try? ReleaseVerifier.parseChecksums(Data(bad.utf8))) == nil)
    }
}

/// Новые необязательные поля: без них — значения по умолчанию (как у 0.1.0), с ними — разбираются.
@MainActor func verifyNewFields(_ v: [String: Any]) {
    func sample(_ name: String) -> WireMessage? {
        let messages = v["messages"] as! [[String: String]]
        guard let json = messages.first(where: { $0["name"] == name })?["json"] else {
            check("образец \(name) есть", false)
            return nil
        }
        return try? WireMessage.decode(Data(json.utf8))
    }
    if case .ready(let info)? = sample("ready") {
        check("ready без новых полей: caps = nil (ничего)", info.caps == nil)
        check("ready без новых полей: тип неизвестен", info.type == .unknown)
    } else {
        check("ready разбирается", false)
    }
    if case .pairHello(_, _, let type)? = sample("pair_hello") {
        check("pair_hello без os/form: тип неизвестен", type == .unknown)
    } else {
        check("pair_hello разбирается", false)
    }
    if case .pairCommit(_, _, _, let type)? = sample("pair_commit_typed") {
        check("pair_commit: os и form", type == DeviceType(os: "windows", form: "desktop"))
    } else {
        check("pair_commit_typed разбирается", false)
    }
    if case .ready(let info)? = sample("ready_full") {
        check("ready: все поля", info == PeerInfo(name: "OFFICE-PC", type: DeviceType(os: "windows", form: "desktop"), caps: ["image"]))
    } else {
        check("ready_full разбирается", false)
    }
    if case .info(let info)? = sample("info_caps_empty") {
        check("info с caps: [] — пустой набор, имя не изменилось", info.caps == [] && info.name == nil)
    } else {
        check("info_caps_empty разбирается", false)
    }
    if case .info(let info)? = try? WireMessage.decode(Data(#"{"t":"info"}"#.utf8)) {
        check("пустой info: ничего не изменилось", info == PeerInfo())
    } else {
        check("пустой info разбирается", false)
    }
    // Неверные типы необязательных полей — как будто поля нет; не строки в caps пропускаются.
    if case .ready(let info)? = try? WireMessage.decode(Data(#"{"t":"ready","name":5,"os":true,"caps":["image",3]}"#.utf8)) {
        check("ready с неверными типами полей", info.name == nil && info.type.os == nil && info.caps == ["image"])
    } else {
        check("ready с неверными типами полей разбирается", false)
    }
    // Неверный blob_start не рвёт сеанс: разбирается, но картинка отвергается.
    let badStart = #"{"t":"blob_start","id":"a","origin":"b","hops":0,"kind":"file","mime":"image/gif","size":"big","sha256":"***"}"#
    if case .blobStart(let start)? = try? WireMessage.decode(Data(badStart.utf8)) {
        check("неверный blob_start разбирается и отвергается", BlobAssembly.refusal(start) != nil)
    } else {
        check("неверный blob_start разбирается", false)
    }
    if case .blobChunk(_, _, let data)? = try? WireMessage.decode(Data(#"{"t":"blob_chunk","id":"a","seq":0,"data":"***"}"#.utf8)) {
        check("blob_chunk с не-base64: данных нет", data == nil)
    } else {
        check("blob_chunk с не-base64 разбирается", false)
    }
}

/// Нарезка и сборка картинки по разделу blob.
@MainActor func verifyBlob(_ v: [String: Any]) {
    guard let blob = v["blob"] as? [String: Any],
          let length = blob["length"] as? Int,
          let sizes = blob["chunk_sizes"] as? [Int],
          let hashes = blob["chunk_sha256"] as? [String],
          let sha = (blob["sha256"] as? String).flatMap({ Data(base64Encoded: $0) }) else {
        check("раздел blob", false, "нет или неполный")
        return
    }
    let data = Data((0..<length).map { UInt8($0 % 251) })
    expectEqual("картинка: sha256", Blob.sha256(data).base64EncodedString(), sha.base64EncodedString())
    let ranges = Blob.chunkRanges(size: length)
    check("картинка: размеры кусков", ranges.map(\.count) == sizes, "\(ranges.map(\.count))")
    check("картинка: хеши кусков", ranges.map { Blob.sha256(data.subdata(in: $0)).hex } == hashes)
    check("картинка: ровно 512 КиБ и 20 МиБ", ProtocolLimits.blobChunkBytes == 524_288 && ProtocolLimits.maxImageBytes == 20_971_520)

    let header = BlobStart(id: "x", origin: "o", hops: 0, mime: "image/png", size: length, sha256: sha)
    check("картинка: заголовок принимается", BlobAssembly.refusal(header) == nil)
    var good = BlobAssembly(header)
    for (seq, range) in ranges.enumerated() {
        try? good.append(seq: seq, chunk: data.subdata(in: range))
    }
    check("картинка: сборка", (try? good.finish()) == data)

    func fails(_ name: String, _ body: (inout BlobAssembly) throws -> Void) {
        var assembly = BlobAssembly(header)
        do {
            try body(&assembly)
            _ = try assembly.finish()
            check("картинка отбрасывается: \(name)", false, "принята")
        } catch {
            check("картинка отбрасывается: \(name)", true)
        }
    }
    let chunks = ranges.map { data.subdata(in: $0) }
    fails("пропущен кусок") { try $0.append(seq: 0, chunk: chunks[0]); try $0.append(seq: 2, chunk: chunks[2]) }
    fails("нехватка данных к концу") { try $0.append(seq: 0, chunk: chunks[0]); try $0.append(seq: 1, chunk: chunks[1]) }
    fails("не последний кусок короче 512 КиБ") { try $0.append(seq: 0, chunk: chunks[0].prefix(1000)) }
    fails("данных больше size") {
        for (seq, chunk) in chunks.enumerated() { try $0.append(seq: seq, chunk: chunk) }
        try $0.append(seq: chunks.count, chunk: Data([1]))
    }
    fails("кусок без данных") { try $0.append(seq: 0, chunk: nil) }
    var wrongHash = BlobAssembly(BlobStart(id: "x", origin: "o", hops: 0, mime: "image/png", size: length, sha256: Data(repeating: 0, count: 32)))
    for (seq, chunk) in chunks.enumerated() { try? wrongHash.append(seq: seq, chunk: chunk) }
    check("картинка отбрасывается: sha256 не совпал", (try? wrongHash.finish()) == nil)

    let refused: [(String, BlobStart)] = [
        ("kind не image", BlobStart(id: "x", origin: "o", hops: 0, kind: "file", mime: "image/png", size: 10, sha256: sha)),
        ("mime image/gif", BlobStart(id: "x", origin: "o", hops: 0, mime: "image/gif", size: 10, sha256: sha)),
        ("size 0", BlobStart(id: "x", origin: "o", hops: 0, mime: "image/png", size: 0, sha256: sha)),
        ("size больше 20 МиБ", BlobStart(id: "x", origin: "o", hops: 0, mime: "image/jpeg", size: 20_971_521, sha256: sha)),
        ("sha256 не 32 байта", BlobStart(id: "x", origin: "o", hops: 0, mime: "image/png", size: 10, sha256: sha.prefix(31))),
    ]
    for (name, start) in refused {
        check("картинка не принимается: \(name)", BlobAssembly.refusal(start) != nil)
    }
    check("картинка ровно 20 МиБ принимается",
          BlobAssembly.refusal(BlobStart(id: "x", origin: "o", hops: 0, mime: "image/jpeg", size: 20_971_520, sha256: sha)) == nil)
}

// MARK: - Файлы

@MainActor func verifyFiles(_ v: [String: Any]) {
    // Двоичные куски: шифрование, расшифровка и разбор.
    guard let chunkFrames = v["file_chunk_frames"] as? [[String: Any]], !chunkFrames.isEmpty else {
        check("раздел file_chunk_frames", false, "нет")
        return
    }
    for frame in chunkFrames {
        let name = frame["name"] as! String
        let key = SymmetricKey(data: Data(hex: frame["key"] as! String))
        let counter = UInt64(frame["counter"] as! String)!
        let req = UInt32(frame["req"] as! Int)
        let data = Data(hex: frame["data"] as! String)
        let plaintext = WireMessage.fileChunk(req: req, data: data).encode()
        expectEqual("кусок \(name): открытый текст", plaintext.hex, frame["plaintext"] as! String)
        expectEqual("кусок \(name): открытый текст без копии данных",
                    (FileChunk.header(req: req) + data).hex, frame["plaintext"] as! String)
        var sender = SecureCodec(sendKey: key, receiveKey: key, sendCounter: counter, receiveCounter: counter)
        expectEqual("кусок \(name): шифротекст‖тег", ((try? sender.seal(plaintext)) ?? Data()).hex, frame["payload"] as! String)
        var receiver = SecureCodec(sendKey: key, receiveKey: key, sendCounter: counter, receiveCounter: counter)
        let opened = (try? receiver.open(Data(hex: frame["payload"] as! String))) ?? Data()
        if case .fileChunk(let gotReq, let gotData)? = try? WireMessage.decodeSession(opened) {
            check("кусок \(name): req и данные", gotReq == req && gotData == data)
        } else {
            check("кусок \(name): разбор", false, "не fileChunk")
        }
    }
    func isInvalid(_ plaintext: Data) -> Bool {
        if case .invalidFileMessage? = try? WireMessage.decodeSession(plaintext) { return true }
        return false
    }
    check("кусок без данных отбрасывается", isInvalid(FileChunk.header(req: 1)))
    check("кусок короче заголовка отбрасывается", isInvalid(Data([0, 0, 1])))
    check("кусок больше 1 МиБ отбрасывается", isInvalid(FileChunk.plaintext(req: 1, data: Data(count: 1_048_577))))
    check("кусок ровно 1 МиБ принимается", !isInvalid(FileChunk.plaintext(req: 1, data: Data(count: 1_048_576))))
    check("JSON в сеансе разбирается как раньше", (try? WireMessage.decodeSession(Data(#"{"t":"ping"}"#.utf8)))?.type == "ping")
    check("до шифрования 0x00 — не кусок", (try? WireMessage.decode(FileChunk.plaintext(req: 1, data: Data([1])))) == nil)

    // Сообщения о файлах: разбор образцов.
    let messages = v["messages"] as! [[String: String]]
    func sample(_ name: String) -> WireMessage? {
        messages.first { $0["name"] == name }.flatMap { try? WireMessage.decode(Data($0["json"]!.utf8)) }
    }
    if case .fileOffer(let offer)? = sample("file_offer") {
        check("file_offer: элементы и total", offer == sampleOffer)
        check("file_offer: файлов 3, верхний уровень 2", offer.fileCount == 3 && offer.topLevel.count == 2)
    } else {
        check("file_offer разбирается", false)
    }
    if case .fileGet(let id, let req, let index, let offset)? = sample("file_get") {
        check("file_get: поля", id == sampleOffer.id && req == 7 && index == 4 && offset == 3_000_000_000)
    } else {
        check("file_get разбирается", false)
    }
    // Неверные поля file_get не рвут сеанс: без req — пропуск, без index/offset — ответ not_found.
    if case .fileGet(_, _, let index, let offset)? = try? WireMessage.decode(Data(#"{"t":"file_get","id":"x","req":1,"index":-1,"offset":"0"}"#.utf8)) {
        check("file_get с неверными index и offset: −1", index == -1 && offset == -1)
    } else {
        check("file_get с неверными index и offset разбирается", false)
    }
    for (name, json) in [
        ("file_get без req", #"{"t":"file_get","id":"x","index":0,"offset":0}"#),
        ("file_end с req 0", #"{"t":"file_end","req":0,"size":0}"#),
        ("file_cancel с req больше 2^32−1", #"{"t":"file_cancel","req":4294967296}"#),
        ("file_end без size", #"{"t":"file_end","req":1}"#),
        ("file_offer без items", #"{"t":"file_offer","id":"0123456789abcdef0123456789abcdef","total":0}"#),
    ] {
        if case .invalidFileMessage? = try? WireMessage.decode(Data(json.utf8)) {
            check("пропускается без разрыва: \(name)", true)
        } else {
            check("пропускается без разрыва: \(name)", false)
        }
    }
    if case .fileError(_, let reason)? = try? WireMessage.decode(Data(#"{"t":"file_error","req":3}"#.utf8)) {
        check("file_error без reason — unavailable", reason == "unavailable" && FileTransferFailure(peerReason: reason) == .unavailable)
    } else {
        check("file_error без reason разбирается", false)
    }
    check("file_error: причины", FileTransferFailure(peerReason: "not_found") == .notFound
          && FileTransferFailure(peerReason: "changed") == .changed && FileTransferFailure(peerReason: "новая") == .unavailable)

    // Правила описания.
    if let offers = v["file_offers"] as? [String: Any] {
        for entry in offers["valid"] as! [[String: String]] {
            let decoded = try? WireMessage.decode(Data(entry["json"]!.utf8))
            if case .fileOffer? = decoded {
                check("описание принимается: \(entry["name"]!)", true)
            } else if case .invalidFileMessage(_, let reason)? = decoded {
                check("описание принимается: \(entry["name"]!)", false, reason)
            } else {
                check("описание принимается: \(entry["name"]!)", false, "не разобрано")
            }
        }
        for entry in offers["invalid"] as! [[String: String]] {
            let decoded = try? WireMessage.decode(Data(entry["json"]!.utf8))
            if case .invalidFileMessage(let type, _)? = decoded, type == "file_offer" {
                check("описание отбрасывается: \(entry["name"]!)", true)
            } else {
                check("описание отбрасывается: \(entry["name"]!)", false, "принято")
            }
        }
    } else {
        check("раздел file_offers", false, "нет")
    }
    let many = (0..<10_000).map { FileItem.file("f\($0)", size: 1) }
    check("ровно 10 000 элементов принимается", FileOffer.refusal(id: sampleOffer.id, items: many, total: 10_000) == nil)
    check("10 001 элемент отбрасывается",
          FileOffer.refusal(id: sampleOffer.id, items: many + [.file("f10000", size: 1)], total: 10_001) != nil)
    check("пределы файлов", ProtocolLimits.fileChunkBytes == 1_048_576 && ProtocolLimits.maxFileItems == 10_000
          && ProtocolLimits.maxFileTotalBytes == 10_737_418_240 && ProtocolLimits.fileCapability == "file")

    // Имена.
    if let names = v["file_names"] as? [String: Any] {
        let items = (names["items"] as! [[String: Any]]).map { entry in
            FileItem(path: entry["path"] as! String, size: (entry["dir"] as? Bool) == true ? nil : Int64(entry["size"] as! Int))
        }
        check("имена на диске Mac", FileNames.localPaths(items, windows: false) == names["mac"] as! [String],
              "\(FileNames.localPaths(items, windows: false))")
        check("имена на диске Windows", FileNames.localPaths(items, windows: true) == names["windows"] as! [String],
              "\(FileNames.localPaths(items, windows: true))")
        for entry in names["wire"] as! [[String: String]] {
            let local = String(decoding: Data(hex: entry["local_hex"]!), as: UTF8.self)
            expectEqual("имя в описании: \(entry["wire"]!)", FileNames.wireName(local), entry["wire"]!)
        }
        for entry in names["numbered"] as! [[String: Any]] {
            expectEqual("имя с номером: \(entry["result"]!)",
                        FileNames.numbered(entry["name"] as! String, entry["number"] as! Int, isDirectory: entry["dir"] as! Bool),
                        entry["result"] as! String)
        }
    } else {
        check("раздел file_names", false, "нет")
    }

    verifyFileTree()
}

/// Обход папок у отправителя (FileTree) и кэш полученного (IncomingCache) — на временной папке.
@MainActor func verifyFileTree() {
    let fm = FileManager.default
    let root = fm.temporaryDirectory.appendingPathComponent("clipvey-checks-\(UUID().uuidString)")
    defer { try? fm.removeItem(at: root) }
    func write(_ path: String, _ bytes: Int) {
        let url = root.appendingPathComponent(path)
        try? fm.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        fm.createFile(atPath: url.path, contents: Data(count: bytes))
    }
    // «Папка» с файлами, пустой папкой, ссылкой, каналом, .DS_Store и именем с «:» (в Finder — «/»).
    write("src/Папка/б.txt", 5)
    write("src/Папка/а.txt", 3)
    write("src/Папка/.DS_Store", 10)
    write("src/Папка/12:05.txt", 1)
    write("src/Папка/вложенная/пустой", 0)
    try? fm.createDirectory(at: root.appendingPathComponent("src/Папка/пустая"), withIntermediateDirectories: true)
    try? fm.createSymbolicLink(at: root.appendingPathComponent("src/Папка/ссылка"), withDestinationURL: root.appendingPathComponent("src/Папка/а.txt"))
    mkfifo(root.appendingPathComponent("src/Папка/канал").path, 0o600)
    write("other/Папка", 7)
    // Имя в NFD (как у файлов со старых дисков HFS+): в описании — NFC.
    write("src/" + "йод.txt".decomposedStringWithCanonicalMapping, 2)

    let roots = [root.appendingPathComponent("src/Папка"), root.appendingPathComponent("other/Папка"),
                 root.appendingPathComponent("src/" + "йод.txt".decomposedStringWithCanonicalMapping),
                 root.appendingPathComponent("src/Папка/ссылка")]
    switch FileTree.build(roots) {
    case .success(let tree):
        let paths = tree.items.map { $0.isDirectory ? $0.path + "/" : "\($0.path) \($0.size!)" }
        let expected = ["Папка/", "Папка/12_05.txt 1", "Папка/а.txt 3", "Папка/б.txt 5", "Папка/вложенная/", "Папка/вложенная/пустой 0",
                        "Папка/пустая/", "Папка (2) 7", "йод.txt 2"]
        check("обход: элементы, порядок, пропуски и имена", paths == expected, "\(paths)")
        check("обход: total", tree.total == 18)
        check("обход: у каждого элемента свой локальный путь", tree.urls.count == tree.items.count
              && tree.urls[2].lastPathComponent == "а.txt")
        check("обход: описание проходит проверку", FileOffer.refusal(id: sampleOffer.id, items: tree.items, total: tree.total) == nil)
    case .failure(let failure):
        check("обход папки", false, failure.code)
    }
    check("обход: только ссылка — пусто", {
        if case .failure(.empty) = FileTree.build([root.appendingPathComponent("src/Папка/ссылка")]) { return true }
        return false
    }())
    check("обход: нет файла — не прочитать", {
        if case .failure(.unreadable) = FileTree.build([root.appendingPathComponent("нет такого")]) { return true }
        return false
    }())
    // Имя длиннее 255 байт и путь длиннее 1024 байт на Mac не создать (NAME_MAX, PATH_MAX) — их проверяют образцы.
    // Больше 10 000 элементов: папка и 10 000 файлов в ней.
    let many = root.appendingPathComponent("many")
    try? fm.createDirectory(at: many, withIntermediateDirectories: true)
    for index in 0..<10_000 {
        fm.createFile(atPath: many.appendingPathComponent("f\(index)").path, contents: nil)
    }
    check("обход: больше 10 000 элементов", {
        if case .failure(.tooManyItems) = FileTree.build([many]) { return true }
        return false
    }())
    try? fm.removeItem(at: many.appendingPathComponent("f0"))
    check("обход: ровно 10 000 элементов", {
        if case .success(let tree) = FileTree.build([many]) { return tree.items.count == 10_000 }
        return false
    }())

    // Кэш: старше суток удаляется, свежее остаётся.
    let cache = root.appendingPathComponent("Incoming")
    let old = IncomingCache.directory(root: cache, offerID: "00000000000000000000000000000001")
    let fresh = IncomingCache.directory(root: cache, offerID: "00000000000000000000000000000002")
    try? fm.createDirectory(at: old, withIntermediateDirectories: true)
    try? fm.createDirectory(at: fresh, withIntermediateDirectories: true)
    let twoDaysAgo = Date().addingTimeInterval(-2 * 86400)
    try? fm.setAttributes([.modificationDate: twoDaysAgo, .creationDate: twoDaysAgo], ofItemAtPath: old.path)
    let removed = IncomingCache.clean(root: cache)
    check("кэш: удалена только старая папка", removed == 1 && !fm.fileExists(atPath: old.path) && fm.fileExists(atPath: fresh.path))
}

// MARK: - Запуск

if arguments.contains("--generate") {
    var generated = generate()
    // Эти разделы получены не отсюда (подпись — scripts/sign-release.swift, байты — из .NET), их переносим как есть.
    if let url = findVectors(), let data = try? Data(contentsOf: url),
       let old = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] {
        for key in ["release_signature", "foreign_messages"] {
            generated[key] = old[key]
        }
        // Порядок ключей у JSONSerialization случайный: равные по смыслу образцы оставляем как были, чтобы не шуметь в diff.
        if let oldMessages = old["messages"] as? [[String: Any]], var messages = generated["messages"] as? [[String: Any]] {
            for index in messages.indices {
                guard let name = messages[index]["name"] as? String,
                      let previous = oldMessages.first(where: { $0["name"] as? String == name })?["json"] as? String,
                      let current = messages[index]["json"] as? String,
                      jsonObject(Data(previous.utf8)) == jsonObject(Data(current.utf8)) else { continue }
                messages[index]["json"] = previous
            }
            generated["messages"] = messages
        }
    }
    let data = try! JSONSerialization.data(withJSONObject: generated, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
    FileHandle.standardOutput.write(data + Data("\n".utf8))
    exit(0)
}

guard let url = findVectors(),
      let data = try? Data(contentsOf: url),
      let vectors = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else {
    print("Не найден или не читается tests/vectors.json (укажите --vectors FILE)")
    exit(2)
}
print("Данные: \(url.path)")
verify(vectors)
verifyReleaseChecks(vectors)
print("")
if failures.isEmpty {
    print("Итог: все \(passed) проверок прошли")
    exit(0)
} else {
    print("Итог: \(failures.count) провалов, \(passed) прошли")
    for failure in failures {
        print("  - \(failure)")
    }
    exit(1)
}
