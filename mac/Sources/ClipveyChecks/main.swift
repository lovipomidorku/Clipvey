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
