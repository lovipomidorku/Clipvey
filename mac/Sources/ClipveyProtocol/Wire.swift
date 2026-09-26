import Foundation

public enum ClipveyError: LocalizedError, Sendable {
    case protocolViolation(String)
    /// Другое устройство прислало error или pair_abort.
    case rejected(reason: String)
    case codeMismatch
    case cancelled
    case connectionFailed(String)

    public var errorDescription: String? {
        switch self {
        case .protocolViolation(let message): message
        case .rejected(let reason): Self.describe(reason)
        case .codeMismatch: "Код не совпал"
        case .cancelled: "Связывание отменено"
        case .connectionFailed(let message): "Нет соединения: \(message)"
        }
    }

    public var isRejection: Bool {
        if case .rejected = self { return true }
        return false
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
    case pairHello(name: String, key: Data)
    case pairCommit(name: String, key: Data, commit: Data)
    case pairNonce(Data)
    case pairReveal(Data)
    case pairVerified
    case pairDone
    case pairAbort(reason: String)
    case error(reason: String)
    case hello(id: String, ephemeral: Data)
    case helloAck(id: String, ephemeral: Data)
    case ready(name: String?)
    case clip(ClipPayload)
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
        case .clip: "clip"
        case .ping: "ping"
        case .pong: "pong"
        case .unknown(let type): type
        }
    }

    public func encode() -> Data {
        var object: [String: Any] = ["t": type]
        switch self {
        case .pairHello(let name, let key):
            object["v"] = 1
            object["name"] = name
            object["key"] = key.base64EncodedString()
        case .pairCommit(let name, let key, let commit):
            object["name"] = name
            object["key"] = key.base64EncodedString()
            object["commit"] = commit.base64EncodedString()
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
        case .ready(let name):
            object["name"] = name
        case .clip(let clip):
            object["id"] = clip.id
            object["origin"] = clip.origin
            object["hops"] = clip.hops
            object["text"] = clip.text
        case .pairVerified, .pairDone, .ping, .pong, .unknown:
            break
        }
        // Словарь из строк и чисел всегда сериализуется.
        return (try? JSONSerialization.data(withJSONObject: object, options: [.withoutEscapingSlashes])) ?? Data()
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
        let keyLength = ClipveyCrypto.publicKeyLength
        switch type {
        case "pair_hello": return .pairHello(name: try string("name"), key: try bytes("key", keyLength))
        case "pair_commit": return .pairCommit(name: try string("name"), key: try bytes("key", keyLength), commit: try bytes("commit", 32))
        case "pair_nonce": return .pairNonce(try bytes("nonce", 32))
        case "pair_reveal": return .pairReveal(try bytes("nonce", 32))
        case "pair_verified": return .pairVerified
        case "pair_done": return .pairDone
        case "pair_abort": return .pairAbort(reason: (object["reason"] as? String) ?? "cancel")
        case "error": return .error(reason: (object["reason"] as? String) ?? "error")
        case "hello": return .hello(id: try string("id"), ephemeral: try bytes("eph", keyLength))
        case "hello_ack": return .helloAck(id: try string("id"), ephemeral: try bytes("eph", keyLength))
        case "ready": return .ready(name: object["name"] as? String)
        case "clip":
            return .clip(ClipPayload(
                id: try string("id"),
                origin: try string("origin"),
                hops: (object["hops"] as? Int) ?? 0,
                text: try string("text")))
        case "ping": return .ping
        case "pong": return .pong
        default: return .unknown(type)
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
