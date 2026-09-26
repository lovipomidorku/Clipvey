import CryptoKit
import Foundation

/// Вычисления связывания и сеанса по docs/protocol.md. Должны совпадать до байта с реализацией на C#.
enum ClipveyCrypto {
    static let publicKeyLength = 65

    private static let commitLabel = Data("Clipvey v1 commit".utf8)
    private static let codeLabel = Data("Clipvey v1 code".utf8)
    private static let sessionLabel = Data("Clipvey v1 session".utf8)
    private static let keysLabel = Data("Clipvey v1 keys".utf8)

    static func deviceID(for publicKey: Data) -> String {
        SHA256.hash(data: publicKey).prefix(16).map { String(format: "%02x", $0) }.joined()
    }

    static func commitment(responderKey: Data, initiatorKey: Data, responderNonce: Data) -> Data {
        hash(commitLabel, responderKey, initiatorKey, responderNonce)
    }

    static func code(initiatorKey: Data, responderKey: Data, initiatorNonce: Data, responderNonce: Data) -> String {
        let digest = hash(codeLabel, initiatorKey, responderKey, initiatorNonce, responderNonce)
        let value = digest.prefix(4).reduce(UInt32(0)) { $0 << 8 | UInt32($1) } % 1_000_000
        return String(format: "%06u", value)
    }

    static func sessionTranscript(initiatorKey: Data, responderKey: Data, initiatorEphemeral: Data, responderEphemeral: Data) -> Data {
        hash(sessionLabel, initiatorKey, responderKey, initiatorEphemeral, responderEphemeral)
    }

    static func sessionKeys(dh1: Data, dh2: Data, dh3: Data, transcript: Data) -> (initiatorToResponder: SymmetricKey, responderToInitiator: SymmetricKey) {
        let okm = HKDF<SHA256>.deriveKey(
            inputKeyMaterial: SymmetricKey(data: dh1 + dh2 + dh3),
            salt: transcript,
            info: keysLabel,
            outputByteCount: 64
        )
        let bytes = okm.withUnsafeBytes { Data($0) }
        return (SymmetricKey(data: bytes.prefix(32)), SymmetricKey(data: bytes.suffix(32)))
    }

    static func agree(_ privateKey: P256.KeyAgreement.PrivateKey, _ publicKey: P256.KeyAgreement.PublicKey) throws -> Data {
        try privateKey.sharedSecretFromKeyAgreement(with: publicKey).withUnsafeBytes { Data($0) }
    }

    /// Импорт публичного ключа X9.63 с проверкой, что точка лежит на кривой.
    static func publicKey(from data: Data) throws -> P256.KeyAgreement.PublicKey {
        guard data.count == publicKeyLength, data.first == 0x04 else {
            throw ClipveyError.protocolViolation("Неверный формат публичного ключа")
        }
        do {
            return try P256.KeyAgreement.PublicKey(x963Representation: data)
        } catch {
            throw ClipveyError.protocolViolation("Публичный ключ не лежит на кривой P-256")
        }
    }

    static func randomNonce() -> Data {
        var bytes = [UInt8](repeating: 0, count: 32)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        return Data(bytes)
    }

    private static func hash(_ parts: Data...) -> Data {
        var hasher = SHA256()
        for part in parts {
            hasher.update(data: part)
        }
        return Data(hasher.finalize())
    }
}

/// Долговременный ключ этого устройства.
struct DeviceIdentity: Sendable {
    let privateKey: P256.KeyAgreement.PrivateKey

    var publicKey: Data { privateKey.publicKey.x963Representation }
    var deviceID: String { ClipveyCrypto.deviceID(for: publicKey) }

    static func loadOrCreate(at url: URL) throws -> DeviceIdentity {
        if let data = try? Data(contentsOf: url) {
            return DeviceIdentity(privateKey: try P256.KeyAgreement.PrivateKey(rawRepresentation: data))
        }
        let key = P256.KeyAgreement.PrivateKey()
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try key.rawRepresentation.write(to: url, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
        return DeviceIdentity(privateKey: key)
    }
}

/// Шифрование кадров сеанса: AES-256-GCM, свой ключ и счётчик для каждого направления.
struct SecureCodec: Sendable {
    let sendKey: SymmetricKey
    let receiveKey: SymmetricKey
    private var sendCounter: UInt64 = 0
    private var receiveCounter: UInt64 = 0

    init(sendKey: SymmetricKey, receiveKey: SymmetricKey) {
        self.sendKey = sendKey
        self.receiveKey = receiveKey
    }

    mutating func seal(_ plaintext: Data) throws -> Data {
        let box = try AES.GCM.seal(plaintext, using: sendKey, nonce: try AES.GCM.Nonce(data: Self.nonce(sendCounter)))
        sendCounter += 1
        return box.ciphertext + box.tag
    }

    mutating func open(_ payload: Data) throws -> Data {
        guard payload.count >= 16 else {
            throw ClipveyError.protocolViolation("Слишком короткий шифрованный кадр")
        }
        let nonce = try AES.GCM.Nonce(data: Self.nonce(receiveCounter))
        receiveCounter += 1
        do {
            let box = try AES.GCM.SealedBox(nonce: nonce, ciphertext: payload.prefix(payload.count - 16), tag: payload.suffix(16))
            return try AES.GCM.open(box, using: receiveKey)
        } catch {
            throw ClipveyError.protocolViolation("Не удалось расшифровать кадр")
        }
    }

    private static func nonce(_ counter: UInt64) -> Data {
        var data = Data(count: 4)
        withUnsafeBytes(of: counter.bigEndian) { data.append(contentsOf: $0) }
        return data
    }
}
