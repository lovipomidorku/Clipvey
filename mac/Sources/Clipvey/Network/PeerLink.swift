import Foundation
import Network

/// Соединение с другим устройством: кадры `u32 длина + нагрузка` поверх LinkTransport (исходящее NWConnection
/// или принятый сокет), после рукопожатия — шифрование. Шифрование кадра и постановка его в очередь транспорта
/// идут без точек приостановки, поэтому порядок кадров на проводе совпадает с порядком счётчиков.
///
/// Куски файлов (и file_end, file_error, pong) уходят «окном»: кадр ставится в очередь транспорта сразу,
/// а ждать приходится, только когда там уже больше fileSendWindow байт, которые он ещё не забрал: следующий
/// кусок читается с диска, пока предыдущие в пути.
actor PeerLink {
    static let maxFrameBytes = 4 * 1024 * 1024
    /// Сколько байт кусков файлов держать в очереди отправки. Больше — дольше ждут своей очереди clip и ping
    /// между кусками; меньше — на медленной сети между кусками снова бывает пусто.
    static let fileSendWindow = 4 * 1024 * 1024

    nonisolated let transport: LinkTransport
    private var codec: SecureCodec?
    private nonisolated let window = SendWindow(limit: fileSendWindow)

    init(transport: LinkTransport) {
        self.transport = transport
    }

    /// Исходящее соединение.
    init(connection: NWConnection) {
        self.init(transport: NWTransport(connection))
    }

    /// Дождаться готовности соединения (исходящее при этом подключается).
    /// Если за timeout не вышло, соединение отменяется.
    func start(timeout: TimeInterval) async throws {
        try await transport.start(timeout: timeout)
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

    /// Двоичный кусок файла. plaintext уже содержит заголовок (FileChunk.writeHeader) и данные:
    /// он шифруется как есть, без копирования в новое сообщение (plaintext можно менять сразу после возврата).
    /// Ждёт, только пока в очереди отправки больше fileSendWindow байт; окончания отправки не ждёт — ошибку
    /// отправки вернёт следующий вызов (и сеанс оборвётся на приёме).
    func sendFileChunk(plaintext: Data) async throws {
        guard codec != nil else {
            throw ClipveyError.protocolViolation("Двоичный кадр до шифрования")
        }
        try await window.wait()
        // После ожидания — шифрование и постановка в очередь без точек приостановки (счётчики по порядку).
        guard var codec else {
            throw ClipveyError.protocolViolation("Двоичный кадр до шифрования")
        }
        let payload = try codec.seal(plaintext)
        self.codec = codec
        try enqueue(payload)
    }

    /// Сообщение без ожидания отправки — вслед за кусками файлов: file_end и file_error (иначе между файлами
    /// труба пустеет), pong из цикла приёма (иначе приём стоит, пока уходят куски). Очередь окна не ждёт.
    func post(_ message: WireMessage) throws {
        var payload = message.encode()
        if var codec {
            payload = try codec.seal(payload)
            self.codec = codec
        }
        try enqueue(payload)
    }

    /// Следующее сообщение. После установки шифрования открытый текст с 0x00 в начале — двоичный кусок файла.
    func receive() async throws -> WireMessage {
        let frame = try await receiveFrame()
        guard var codec else {
            return try WireMessage.decode(frame)
        }
        let plaintext = try codec.open(frame)
        self.codec = codec
        return try WireMessage.decodeSession(plaintext)
    }

    /// Закрыть соединение. Ждущие окна отправки получают ошибку сразу, не полагаясь на завершения транспорта.
    nonisolated func cancel() {
        window.fail(ClipveyError.connectionFailed("соединение закрыто"))
        transport.cancel()
    }

    /// Адрес другой стороны (для запоминания последнего удачного адреса).
    nonisolated var remoteEndpoint: (host: String, port: Int)? {
        transport.remoteEndpoint
    }

    // MARK: - Кадры

    /// Нагрузка больше этого (куски файлов) уходит отдельной отправкой после заголовка, чтобы не копировать её
    /// ради склейки; меньшая — одним куском вместе с заголовком: соединения без Нейгла (TCP_NODELAY), и отдельный
    /// заголовок ушёл бы лишним маленьким пакетом.
    private static let separateSendThreshold = 64 * 1024

    /// Кадр для отправки: заголовок (с нагрузкой, если она небольшая) и отдельно большая нагрузка.
    private static func frame(_ payload: Data) throws -> (head: Data, body: Data?) {
        guard !payload.isEmpty, payload.count <= Self.maxFrameBytes else {
            throw ClipveyError.protocolViolation("Недопустимая длина кадра: \(payload.count)")
        }
        let length = UInt32(payload.count)
        var header = Data([UInt8(truncatingIfNeeded: length >> 24), UInt8(truncatingIfNeeded: length >> 16),
                           UInt8(truncatingIfNeeded: length >> 8), UInt8(truncatingIfNeeded: length)])
        if payload.count > Self.separateSendThreshold {
            return (header, payload)
        }
        header.append(payload)
        return (header, nil)
    }

    /// Заголовок и нагрузка ставятся в очередь транспорта без точки приостановки между ними; ждёт, пока
    /// транспорт их заберёт.
    private func sendFrame(_ payload: Data) async throws {
        let (head, body) = try Self.frame(payload)
        let transport = self.transport
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            transport.send(head: head, body: body) { error in
                if let error {
                    continuation.resume(throwing: error)
                } else {
                    continuation.resume()
                }
            }
        }
    }

    /// Поставить кадр в очередь транспорта, не дожидаясь отправки: байты считаются в окне, пока транспорт их
    /// не заберёт. Ошибка прежней отправки (или закрытое соединение) — исключение.
    private func enqueue(_ payload: Data) throws {
        let (head, body) = try Self.frame(payload)
        let bytes = head.count + (body?.count ?? 0)
        try window.add(bytes)
        let window = self.window
        transport.send(head: head, body: body) { error in
            window.done(bytes, error: error)
        }
    }

    /// Кадр целиком: сначала 4 байта длины, затем ровно столько байт нагрузки (одним приёмом, если он пришёл весь).
    private func receiveFrame() async throws -> Data {
        let header = try await receiveExactly(4)
        let length = header.reduce(0) { $0 << 8 | Int($1) }
        guard length > 0, length <= Self.maxFrameBytes else {
            throw ClipveyError.protocolViolation("Недопустимая длина кадра: \(length)")
        }
        return try await receiveExactly(length)
    }

    private func receiveExactly(_ count: Int) async throws -> Data {
        var result = Data()
        while result.count < count {
            let chunk = try await receiveChunk(count - result.count)
            if result.isEmpty {
                result = chunk
            } else {
                result.append(chunk)
            }
        }
        return result
    }

    private func receiveChunk(_ count: Int) async throws -> Data {
        try await transport.receive(upTo: count)
    }
}

/// Окно отправки PeerLink: сколько байт поставлено в очередь NWConnection без ожидания и ещё не забрано стеком,
/// и первая ошибка отправки. Завершения NWConnection приходят на её очереди, поэтому состояние — под замком.
final class SendWindow: @unchecked Sendable {
    private let limit: Int
    private let lock = NSLock()
    private var queued = 0
    private var failure: Error?
    private var waiters: [CheckedContinuation<Void, Error>] = []

    init(limit: Int) {
        self.limit = limit
    }

    /// Дождаться, пока в очереди меньше limit байт. Ошибка отправки или закрытие — исключение.
    func wait() async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            lock.lock()
            if let failure {
                lock.unlock()
                continuation.resume(throwing: failure)
            } else if queued < limit {
                lock.unlock()
                continuation.resume()
            } else {
                waiters.append(continuation)
                lock.unlock()
            }
        }
    }

    /// Байты поставлены в очередь. Если отправка уже не удалась — исключение, в очередь ставить нечего.
    func add(_ bytes: Int) throws {
        lock.lock()
        defer { lock.unlock() }
        if let failure {
            throw failure
        }
        queued += bytes
    }

    /// Стек забрал байты (или отправка не удалась — error).
    func done(_ bytes: Int, error: Error?) {
        lock.lock()
        queued -= bytes
        if let error, failure == nil {
            failure = error
        }
        var ready: [CheckedContinuation<Void, Error>] = []
        if failure != nil || queued < limit {
            ready = waiters
            waiters.removeAll()
        }
        let failure = self.failure
        lock.unlock()
        for waiter in ready {
            if let failure {
                waiter.resume(throwing: failure)
            } else {
                waiter.resume()
            }
        }
    }

    /// Соединение закрыто: ждущим и следующим — ошибка.
    func fail(_ error: Error) {
        lock.lock()
        if failure == nil {
            failure = error
        }
        let ready = waiters
        waiters.removeAll()
        let failure = self.failure!
        lock.unlock()
        for waiter in ready {
            waiter.resume(throwing: failure)
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
