import Foundation
import Network

/// Соединение с другим устройством: кадры `u32 длина + нагрузка` поверх NWConnection,
/// после рукопожатия — шифрование. Шифрование кадра и постановка его в очередь NWConnection
/// идут без точек приостановки, поэтому порядок кадров на проводе совпадает с порядком счётчиков.
actor PeerLink {
    static let maxFrameBytes = 4 * 1024 * 1024
    private static let queue = DispatchQueue(label: "io.github.lovipomidorku.clipvey.network")

    nonisolated let connection: NWConnection
    private var buffer = Data()
    private var codec: SecureCodec?

    init(connection: NWConnection) {
        self.connection = connection
    }

    /// Дождаться готовности соединения (исходящее при этом подключается).
    /// Если за timeout не вышло, соединение отменяется.
    func start(timeout: TimeInterval) async throws {
        let connection = self.connection
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            let once = ResumeOnce(continuation)
            connection.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    once.resume(with: .success(()))
                case .failed(let error), .waiting(let error):
                    if once.resume(with: .failure(ClipveyError.connectionFailed(error.localizedDescription))) {
                        connection.cancel()
                    }
                case .cancelled:
                    once.resume(with: .failure(ClipveyError.connectionFailed("соединение закрыто")))
                default:
                    break
                }
            }
            connection.start(queue: Self.queue)
            Self.queue.asyncAfter(deadline: .now() + timeout) {
                if once.resume(with: .failure(ClipveyError.connectionFailed("нет ответа"))) {
                    connection.cancel()
                }
            }
        }
    }

    func enableEncryption(_ codec: SecureCodec) {
        self.codec = codec
    }

    func send(_ message: WireMessage) async throws {
        var payload = message.encode()
        if var codec {
            payload = try codec.seal(payload)
            self.codec = codec
        }
        try await sendFrame(payload)
    }

    func receive() async throws -> WireMessage {
        let frame = try await receiveFrame()
        guard var codec else {
            return try WireMessage.decode(frame)
        }
        let plaintext = try codec.open(frame)
        self.codec = codec
        return try WireMessage.decode(plaintext)
    }

    nonisolated func cancel() {
        connection.cancel()
    }

    /// Адрес другой стороны (для запоминания последнего удачного адреса).
    nonisolated var remoteEndpoint: (host: String, port: Int)? {
        guard case .hostPort(let host, let port) = connection.currentPath?.remoteEndpoint else { return nil }
        let hostString: String
        switch host {
        case .ipv4(let address): hostString = "\(address)"
        case .ipv6(let address): hostString = "\(address)"
        case .name(let name, _): hostString = name
        @unknown default: return nil
        }
        return (hostString.components(separatedBy: "%").first ?? hostString, Int(port.rawValue))
    }

    // MARK: - Кадры

    private func sendFrame(_ payload: Data) async throws {
        guard !payload.isEmpty, payload.count <= Self.maxFrameBytes else {
            throw ClipveyError.protocolViolation("Недопустимая длина кадра: \(payload.count)")
        }
        var frame = Data()
        withUnsafeBytes(of: UInt32(payload.count).bigEndian) { frame.append(contentsOf: $0) }
        frame.append(payload)
        let connection = self.connection
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            connection.send(content: frame, completion: .contentProcessed { error in
                if let error {
                    continuation.resume(throwing: ClipveyError.connectionFailed(error.localizedDescription))
                } else {
                    continuation.resume()
                }
            })
        }
    }

    private func receiveFrame() async throws -> Data {
        while true {
            if let frame = try extractFrame() {
                return frame
            }
            buffer.append(try await receiveChunk())
        }
    }

    private func extractFrame() throws -> Data? {
        guard buffer.count >= 4 else { return nil }
        let length = buffer.prefix(4).reduce(0) { $0 << 8 | Int($1) }
        guard length > 0, length <= Self.maxFrameBytes else {
            throw ClipveyError.protocolViolation("Недопустимая длина кадра: \(length)")
        }
        guard buffer.count >= 4 + length else { return nil }
        let start = buffer.startIndex
        let frame = buffer.subdata(in: start + 4 ..< start + 4 + length)
        buffer = Data(buffer[(start + 4 + length)...])
        return frame
    }

    private func receiveChunk() async throws -> Data {
        let connection = self.connection
        return try await withCheckedThrowingContinuation { continuation in
            connection.receive(minimumIncompleteLength: 1, maximumLength: 65536) { data, _, isComplete, error in
                if let data, !data.isEmpty {
                    continuation.resume(returning: data)
                } else if let error {
                    continuation.resume(throwing: ClipveyError.connectionFailed(error.localizedDescription))
                } else if isComplete {
                    continuation.resume(throwing: ClipveyError.connectionFailed("соединение закрыто"))
                } else {
                    continuation.resume(returning: Data())
                }
            }
        }
    }
}

/// Продолжение, которое можно безопасно «возобновить» из нескольких мест: сработает только первое.
final class ResumeOnce<Value: Sendable>: @unchecked Sendable {
    private var continuation: CheckedContinuation<Value, Error>?
    private let lock = NSLock()

    init(_ continuation: CheckedContinuation<Value, Error>) {
        self.continuation = continuation
    }

    @discardableResult
    func resume(with result: Result<Value, Error>) -> Bool {
        lock.lock()
        let continuation = self.continuation
        self.continuation = nil
        lock.unlock()
        guard let continuation else { return false }
        continuation.resume(with: result)
        return true
    }
}
