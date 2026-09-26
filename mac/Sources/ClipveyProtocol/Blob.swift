import CryptoKit
import Foundation

/// Картинки по docs/protocol.md, «Картинки»: нарезка на куски и сборка с проверками.
/// Совпадает с Clipvey.Core (Blob.cs); проверяется clipvey-checks по tests/vectors.json.
public enum Blob {
    /// Диапазоны кусков: все, кроме последнего, ровно blobChunkBytes.
    public static func chunkRanges(size: Int) -> [Range<Int>] {
        stride(from: 0, to: size, by: ProtocolLimits.blobChunkBytes).map { start in
            start ..< min(start + ProtocolLimits.blobChunkBytes, size)
        }
    }

    public static func sha256(_ data: Data) -> Data {
        Data(SHA256.hash(data: data))
    }

    /// Новый id фрагмента: 16 случайных байт в hex (то же пространство, что у clip).
    public static func newID() -> String {
        ClipveyCrypto.randomNonce().prefix(16).map { String(format: "%02x", $0) }.joined()
    }
}

/// Сборка одной картинки из blob_chunk. Ошибка любой проверки — картинка отбрасывается (сеанс продолжается).
public struct BlobAssembly: Sendable {
    public let header: BlobStart
    private var data: Data
    private var nextSeq = 0

    /// Почему картинку нельзя принять (для журнала, по-русски); nil — можно.
    /// «Передавать картинки» и повторы по id проверяет узел.
    public static func refusal(_ start: BlobStart) -> String? {
        if start.kind != "image" {
            return "незнакомый вид «\(start.kind)»"
        }
        if !ProtocolLimits.imageMimes.contains(start.mime) {
            return "незнакомый mime «\(start.mime)»"
        }
        if start.size < 1 || start.size > ProtocolLimits.maxImageBytes {
            return "размер \(start.size) байт вне допустимого"
        }
        if start.sha256.count != 32 {
            return "sha256 не 32 байта"
        }
        return nil
    }

    public init(_ header: BlobStart) {
        self.header = header
        data = Data(capacity: header.size)
    }

    public var receivedBytes: Int { data.count }

    /// Принять кусок. Бросает, если seq не следующий, данных нет или кусок не той длины.
    public mutating func append(seq: Int, chunk: Data?) throws {
        guard seq == nextSeq else {
            throw ClipveyError.protocolViolation("кусок \(seq) вместо \(nextSeq)")
        }
        guard let chunk, !chunk.isEmpty else {
            throw ClipveyError.protocolViolation("кусок \(seq) без данных")
        }
        let total = data.count + chunk.count
        guard total <= header.size else {
            throw ClipveyError.protocolViolation("данных больше заявленных \(header.size) байт")
        }
        // Кусок, после которого данных ещё не хватает, — не последний: ровно 512 КиБ.
        guard total == header.size || chunk.count == ProtocolLimits.blobChunkBytes else {
            throw ClipveyError.protocolViolation("кусок \(seq) длиной \(chunk.count) байт вместо \(ProtocolLimits.blobChunkBytes)")
        }
        data.append(chunk)
        nextSeq += 1
    }

    /// blob_end: вернуть данные, если хватает и совпал sha256.
    public func finish() throws -> Data {
        guard data.count == header.size else {
            throw ClipveyError.protocolViolation("получено \(data.count) байт из \(header.size)")
        }
        guard Blob.sha256(data) == header.sha256 else {
            throw ClipveyError.protocolViolation("sha256 не совпал")
        }
        return data
    }
}
