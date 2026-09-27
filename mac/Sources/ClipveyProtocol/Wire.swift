import Foundation

/// Почему не удалось связаться или подключиться. Код для интерфейса: текст на нужном языке
/// подбирает интерфейс, узел отдаёт только код. Журнал пишется по-русски (ClipveyError.errorDescription).
public enum FailureReason: Equatable, Sendable {
    /// На другом устройстве не открыт режим связывания (error not_pairing).
    case notPairing
    /// Другое устройство уже связывается с кем-то (error busy).
    case busy
    /// На другом устройстве введён неверный код (pair_abort code).
    case peerCodeMismatch
    /// Связывание отменено на другом устройстве (pair_abort cancel).
    case peerCancelled
    /// Обязательство не совпало — возможно, соединение перехвачено.
    case commitMismatch
    /// Другое устройство не знает это — нужно связать заново (error unknown_device).
    case unknownDevice
    /// На другом устройстве синхронизация с этим выключена (error disabled).
    case disabled
    /// Другое устройство отказало по незнакомой причине.
    case rejected(String)
    /// Введённый здесь код не совпал.
    case codeMismatch
    /// Связывание отменено здесь или истекло время.
    case cancelled
    /// По адресу ответило не то устройство.
    case wrongDevice
    /// Нет соединения.
    case connectionFailed
    /// Нарушение протокола.
    case protocolError

    /// Машинное имя для журнала и событий самопроверки; совпадает с именем FailureReason в C#.
    public var code: String {
        switch self {
        case .notPairing: "NotPairing"
        case .busy: "Busy"
        case .peerCodeMismatch: "PeerCodeMismatch"
        case .peerCancelled: "PeerCancelled"
        case .commitMismatch: "CommitMismatch"
        case .unknownDevice: "UnknownDevice"
        case .disabled: "Disabled"
        case .rejected: "Rejected"
        case .codeMismatch: "CodeMismatch"
        case .cancelled: "Cancelled"
        case .wrongDevice: "WrongDevice"
        case .connectionFailed: "ConnectionFailed"
        case .protocolError: "ProtocolError"
        }
    }

    /// Код по причине из error / pair_abort.
    public init(peerReason reason: String) {
        switch reason {
        case "not_pairing": self = .notPairing
        case "busy": self = .busy
        case "code": self = .peerCodeMismatch
        case "cancel": self = .peerCancelled
        case "commit": self = .commitMismatch
        case "unknown_device": self = .unknownDevice
        case "disabled": self = .disabled
        default: self = .rejected(reason)
        }
    }
}

public enum ClipveyError: LocalizedError, Sendable {
    case protocolViolation(String)
    /// Другое устройство прислало error или pair_abort.
    case rejected(reason: String)
    case codeMismatch
    case cancelled
    case connectionFailed(String)
    /// По адресу ответило не то устройство, с которым было связывание.
    case wrongDevice
    /// Обязательство связывания не совпало.
    case commitMismatch

    /// Текст для журнала (по-русски).
    public var errorDescription: String? {
        switch self {
        case .protocolViolation(let message): message
        case .rejected(let reason): Self.describe(reason)
        case .codeMismatch: "Код не совпал"
        case .cancelled: "Связывание отменено"
        case .connectionFailed(let message): "Нет соединения: \(message)"
        case .wrongDevice: "Ответило не то устройство, с которым было связывание"
        case .commitMismatch: "Проверка связывания не прошла — возможно, соединение перехвачено"
        }
    }

    /// Код для интерфейса.
    public var reason: FailureReason {
        switch self {
        case .protocolViolation: .protocolError
        case .rejected(let reason): FailureReason(peerReason: reason)
        case .codeMismatch: .codeMismatch
        case .cancelled: .cancelled
        case .connectionFailed: .connectionFailed
        case .wrongDevice: .wrongDevice
        case .commitMismatch: .commitMismatch
        }
    }

    public var isRejection: Bool {
        if case .rejected = self { return true }
        return false
    }

    /// Код для любой ошибки: не ClipveyError — нет соединения.
    public static func reason(of error: Error) -> FailureReason {
        (error as? ClipveyError)?.reason ?? .connectionFailed
    }

    private static func describe(_ reason: String) -> String {
        switch reason {
        case "not_pairing": "На другом устройстве не открыт режим связывания"
        case "busy": "Другое устройство уже связывается с кем-то"
        case "code": "На другом устройстве введён неверный код"
        case "cancel": "Связывание отменено на другом устройстве"
        case "commit": "Проверка связывания не прошла"
        case "unknown_device": "Другое устройство не знает это — свяжите заново"
        case "disabled": "На другом устройстве синхронизация с этим выключена"
        default: "Другое устройство отказало: \(reason)"
        }
    }
}

/// Пределы и константы протокола (docs/protocol.md).
public enum ProtocolLimits {
    /// Текст clip — не больше 1 МиБ в UTF-8.
    public static let maxClipBytes = 1024 * 1024
    /// Картинка — от 1 до 20 МиБ.
    public static let maxImageBytes = 20_971_520
    /// Кусок картинки до кодирования: все, кроме последнего, ровно 512 КиБ.
    public static let blobChunkBytes = 524_288
    /// Дальше этого числа пересылок фрагмент не передаётся (пересылка — если hops+1 < maxHops).
    public static let maxHops = 8
    /// Сколько последних id (общих для clip и картинок) помнить для отбрасывания повторов.
    public static let seenCapacity = 500
    /// Возможность в caps: устройство принимает картинки.
    public static let imageCapability = "image"
    /// Допустимые mime картинок.
    public static let imageMimes: Set<String> = ["image/png", "image/jpeg"]
    /// Возможность в caps: устройство принимает файлы.
    public static let fileCapability = "file"
    /// Данные двоичного куска файла — от 1 байта до 1 МиБ.
    public static let fileChunkBytes = 1_048_576
    /// Элементов в описании файлов — не больше.
    public static let maxFileItems = 10_000
    /// Сумма размеров файлов в описании — не больше 10 ГиБ.
    public static let maxFileTotalBytes: Int64 = 10_737_418_240
    /// Часть пути в описании — не длиннее, байт UTF-8.
    public static let maxFileNameBytes = 255
    /// Весь путь в описании — не длиннее, байт UTF-8.
    public static let maxFilePathBytes = 1024
}

/// Тип устройства: необязательные поля os и form (docs/protocol.md, «Тип устройства»).
/// nil — поле не пришло; незнакомое значение хранится как есть, интерфейс показывает общий значок.
public struct DeviceType: Equatable, Sendable, Codable {
    public var os: String?
    public var form: String?

    public init(os: String? = nil, form: String? = nil) {
        self.os = os
        self.form = form
    }

    public static let unknown = DeviceType()
}

/// Содержимое ready и info. Все поля необязательные: в ready отсутствие caps значит «ничего»,
/// в info отсутствие поля значит «не изменилось».
public struct PeerInfo: Equatable, Sendable {
    public var name: String?
    public var type: DeviceType
    public var caps: [String]?

    public init(name: String? = nil, type: DeviceType = .unknown, caps: [String]? = nil) {
        self.name = name
        self.type = type
        self.caps = caps
    }
}

/// Начало картинки: blob_start.
public struct BlobStart: Equatable, Sendable {
    public let id: String
    public let origin: String
    public let hops: Int
    public let kind: String
    public let mime: String
    public let size: Int
    /// 32 байта; другая длина отвергается проверкой BlobAssembly.refusal.
    public let sha256: Data

    public init(id: String, origin: String, hops: Int, kind: String = "image", mime: String, size: Int, sha256: Data) {
        self.id = id
        self.origin = origin
        self.hops = hops
        self.kind = kind
        self.mime = mime
        self.size = size
        self.sha256 = sha256
    }

    public func with(hops: Int) -> BlobStart {
        BlobStart(id: id, origin: origin, hops: hops, kind: kind, mime: mime, size: size, sha256: sha256)
    }
}

/// Фрагмент буфера обмена с данными для пересылки между несколькими устройствами.
public struct ClipPayload: Sendable {
    public let id: String
    public let origin: String
    public let hops: Int
    public let text: String

    public init(id: String, origin: String, hops: Int, text: String) {
        self.id = id
        self.origin = origin
        self.hops = hops
        self.text = text
    }
}

/// Сообщения протокола (docs/protocol.md). Кодируются объектами JSON с полем "t".
public enum WireMessage: Sendable {
    case pairHello(name: String, key: Data, type: DeviceType)
    case pairCommit(name: String, key: Data, commit: Data, type: DeviceType)
    case pairNonce(Data)
    case pairReveal(Data)
    case pairVerified
    case pairDone
    case pairAbort(reason: String)
    case error(reason: String)
    case hello(id: String, ephemeral: Data)
    case helloAck(id: String, ephemeral: Data)
    case ready(PeerInfo)
    case info(PeerInfo)
    case clip(ClipPayload)
    case blobStart(BlobStart)
    /// data = nil — поле не base64 (картинка отбрасывается, сеанс продолжается).
    case blobChunk(id: String, seq: Int, data: Data?)
    case blobEnd(id: String)
    /// Описание файлов (проверено: см. FileOffer.refusal).
    case fileOffer(FileOffer)
    /// Запрос содержимого. index и offset — −1, если поля нет или оно неверное (ответ not_found).
    case fileGet(id: String, req: UInt32, index: Int, offset: Int64)
    case fileEnd(req: UInt32, size: Int64)
    case fileError(req: UInt32, reason: String)
    case fileCancel(req: UInt32)
    /// Двоичный кусок файла: шифрованный кадр, открытый текст которого начинается с 0x00. data — срез без копии.
    case fileChunk(req: UInt32, data: Data)
    /// Сообщение о файлах (или двоичный кадр) не прошло проверку: пишется в журнал и пропускается, сеанс продолжается.
    case invalidFileMessage(type: String, reason: String)
    case ping
    case pong
    case unknown(String)

    public var type: String {
        switch self {
        case .pairHello: "pair_hello"
        case .pairCommit: "pair_commit"
        case .pairNonce: "pair_nonce"
        case .pairReveal: "pair_reveal"
        case .pairVerified: "pair_verified"
        case .pairDone: "pair_done"
        case .pairAbort: "pair_abort"
        case .error: "error"
        case .hello: "hello"
        case .helloAck: "hello_ack"
        case .ready: "ready"
        case .info: "info"
        case .clip: "clip"
        case .blobStart: "blob_start"
        case .blobChunk: "blob_chunk"
        case .blobEnd: "blob_end"
        case .fileOffer: "file_offer"
        case .fileGet: "file_get"
        case .fileEnd: "file_end"
        case .fileError: "file_error"
        case .fileCancel: "file_cancel"
        case .fileChunk: "file_chunk"
        case .invalidFileMessage(let type, _): type
        case .ping: "ping"
        case .pong: "pong"
        case .unknown(let type): type
        }
    }

    /// Открытый текст сообщения: JSON в UTF-8, у двоичного куска — `00 ‖ req ‖ данные`.
    public func encode() -> Data {
        if case .fileChunk(let req, let data) = self {
            return FileChunk.plaintext(req: req, data: data)
        }
        var object: [String: Any] = ["t": type]
        func put(_ deviceType: DeviceType) {
            if let os = deviceType.os { object["os"] = os }
            if let form = deviceType.form { object["form"] = form }
        }
        switch self {
        case .pairHello(let name, let key, let deviceType):
            object["v"] = 1
            object["name"] = name
            object["key"] = key.base64EncodedString()
            put(deviceType)
        case .pairCommit(let name, let key, let commit, let deviceType):
            object["name"] = name
            object["key"] = key.base64EncodedString()
            object["commit"] = commit.base64EncodedString()
            put(deviceType)
        case .pairNonce(let nonce), .pairReveal(let nonce):
            object["nonce"] = nonce.base64EncodedString()
        case .pairAbort(let reason), .error(let reason):
            object["reason"] = reason
        case .hello(let id, let ephemeral):
            object["v"] = 1
            object["id"] = id
            object["eph"] = ephemeral.base64EncodedString()
        case .helloAck(let id, let ephemeral):
            object["id"] = id
            object["eph"] = ephemeral.base64EncodedString()
        case .ready(let info), .info(let info):
            if let name = info.name { object["name"] = name }
            put(info.type)
            if let caps = info.caps { object["caps"] = caps }
        case .clip(let clip):
            object["id"] = clip.id
            object["origin"] = clip.origin
            object["hops"] = clip.hops
            object["text"] = clip.text
        case .blobStart(let start):
            object["id"] = start.id
            object["origin"] = start.origin
            object["hops"] = start.hops
            object["kind"] = start.kind
            object["mime"] = start.mime
            object["size"] = start.size
            object["sha256"] = start.sha256.base64EncodedString()
        case .blobChunk(let id, let seq, let data):
            object["id"] = id
            object["seq"] = seq
            object["data"] = (data ?? Data()).base64EncodedString()
        case .blobEnd(let id):
            object["id"] = id
        case .fileOffer(let offer):
            object["id"] = offer.id
            object["items"] = offer.jsonItems()
            object["total"] = offer.total
        case .fileGet(let id, let req, let index, let offset):
            object["id"] = id
            object["req"] = req
            object["index"] = index
            object["offset"] = offset
        case .fileEnd(let req, let size):
            object["req"] = req
            object["size"] = size
        case .fileError(let req, let reason):
            object["req"] = req
            object["reason"] = reason
        case .fileCancel(let req):
            object["req"] = req
        case .pairVerified, .pairDone, .ping, .pong, .unknown, .fileChunk, .invalidFileMessage:
            break
        }
        // Словарь из строк, чисел и массивов строк всегда сериализуется.
        return (try? JSONSerialization.data(withJSONObject: object, options: [.withoutEscapingSlashes])) ?? Data()
    }

    /// Открытый текст шифрованного кадра сеанса: 0x00 в начале — двоичный кусок файла, иначе JSON.
    /// До шифрования двоичных кадров нет: там — decode.
    public static func decodeSession(_ plaintext: Data) throws -> WireMessage {
        guard plaintext.first == 0 else { return try decode(plaintext) }
        switch FileChunk.parse(plaintext) {
        case .success(let chunk): return .fileChunk(req: chunk.req, data: chunk.data)
        case .failure(let error): return .invalidFileMessage(type: "file_chunk", reason: error.reason)
        }
    }

    public static func decode(_ data: Data) throws -> WireMessage {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let type = object["t"] as? String else {
            throw ClipveyError.protocolViolation("Неверное сообщение")
        }
        func string(_ key: String) throws -> String {
            guard let value = object[key] as? String else {
                throw ClipveyError.protocolViolation("Нет поля «\(key)»")
            }
            return value
        }
        func bytes(_ key: String, _ length: Int) throws -> Data {
            guard let value = Data(base64Encoded: try string(key)) else {
                throw ClipveyError.protocolViolation("Поле «\(key)» — не base64")
            }
            guard value.count == length else {
                throw ClipveyError.protocolViolation("Поле «\(key)»: ожидалось \(length) байт, получено \(value.count)")
            }
            return value
        }
        /// Целое число; дробное, строка или отсутствие — nil.
        func integer(_ key: String) -> Int? {
            guard let number = object[key] as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID() else { return nil }
            let value = number.doubleValue
            guard value == value.rounded(), abs(value) < 9e15 else { return nil }
            return number.intValue
        }
        // Необязательные поля: не строка — как будто поля нет.
        let deviceType = DeviceType(os: object["os"] as? String, form: object["form"] as? String)
        func peerInfo() -> PeerInfo {
            PeerInfo(
                name: object["name"] as? String,
                type: deviceType,
                caps: (object["caps"] as? [Any]).map { $0.compactMap { $0 as? String } })
        }
        let keyLength = ClipveyCrypto.publicKeyLength
        switch type {
        case "pair_hello": return .pairHello(name: try string("name"), key: try bytes("key", keyLength), type: deviceType)
        case "pair_commit":
            return .pairCommit(name: try string("name"), key: try bytes("key", keyLength), commit: try bytes("commit", 32), type: deviceType)
        case "pair_nonce": return .pairNonce(try bytes("nonce", 32))
        case "pair_reveal": return .pairReveal(try bytes("nonce", 32))
        case "pair_verified": return .pairVerified
        case "pair_done": return .pairDone
        case "pair_abort": return .pairAbort(reason: (object["reason"] as? String) ?? "cancel")
        case "error": return .error(reason: (object["reason"] as? String) ?? "error")
        case "hello": return .hello(id: try string("id"), ephemeral: try bytes("eph", keyLength))
        case "hello_ack": return .helloAck(id: try string("id"), ephemeral: try bytes("eph", keyLength))
        case "ready": return .ready(peerInfo())
        case "info": return .info(peerInfo())
        case "clip":
            return .clip(ClipPayload(
                id: try string("id"),
                origin: try string("origin"),
                hops: integer("hops") ?? 0,
                text: try string("text")))
        case "blob_start":
            // Неверные kind, mime, size и sha256 не рвут сеанс: картинку отвергает BlobAssembly.refusal.
            return .blobStart(BlobStart(
                id: try string("id"),
                origin: try string("origin"),
                hops: integer("hops") ?? 0,
                kind: (object["kind"] as? String) ?? "",
                mime: (object["mime"] as? String) ?? "",
                size: integer("size") ?? 0,
                sha256: (object["sha256"] as? String).flatMap { Data(base64Encoded: $0) } ?? Data()))
        case "blob_chunk":
            return .blobChunk(
                id: try string("id"),
                seq: integer("seq") ?? -1,
                data: (object["data"] as? String).flatMap { Data(base64Encoded: $0) })
        case "blob_end": return .blobEnd(id: try string("id"))
        case "file_offer", "file_get", "file_end", "file_error", "file_cancel":
            return decodeFileMessage(type, object)
        case "ping": return .ping
        case "pong": return .pong
        default: return .unknown(type)
        }
    }

    /// Сообщения о файлах не рвут сеанс: неверное — .invalidFileMessage.
    private static func decodeFileMessage(_ type: String, _ object: [String: Any]) -> WireMessage {
        if type == "file_offer" {
            switch FileOffer.parse(object) {
            case .success(let offer): return .fileOffer(offer)
            case .failure(let error): return .invalidFileMessage(type: type, reason: error.reason)
            }
        }
        guard let rawReq = JSONNumbers.integer(object["req"]), (1...Int64(UInt32.max)).contains(rawReq) else {
            return .invalidFileMessage(type: type, reason: "req не целое от 1 до 4294967295")
        }
        let req = UInt32(rawReq)
        switch type {
        case "file_get":
            let index = JSONNumbers.integer(object["index"]).flatMap { $0 >= 0 && $0 <= Int64(Int32.max) ? Int($0) : nil } ?? -1
            let offset = JSONNumbers.integer(object["offset"]).flatMap { $0 >= 0 ? $0 : nil } ?? -1
            return .fileGet(id: (object["id"] as? String) ?? "", req: req, index: index, offset: offset)
        case "file_end":
            guard let size = JSONNumbers.integer(object["size"]), size >= 0 else {
                return .invalidFileMessage(type: type, reason: "size не целое ≥ 0")
            }
            return .fileEnd(req: req, size: size)
        case "file_error":
            return .fileError(req: req, reason: (object["reason"] as? String) ?? "unavailable")
        default:
            return .fileCancel(req: req)
        }
    }

    /// Бросает .rejected для error и pair_abort, иначе — нарушение протокола.
    public func unexpected(expecting expected: String) -> ClipveyError {
        switch self {
        case .error(let reason), .pairAbort(let reason): .rejected(reason: reason)
        default: .protocolViolation("Ожидалось «\(expected)», получено «\(type)»")
        }
    }
}
