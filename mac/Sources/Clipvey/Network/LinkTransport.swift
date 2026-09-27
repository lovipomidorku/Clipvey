import Foundation
import Network

/// Поток байт под PeerLink. Исходящие соединения — NWConnection (NWTransport), принятые — обычный сокет ядра
/// (SocketTransport, его даёт SocketListener).
///
/// Почему принятые не через NWListener: соединение, которое принял NWListener (стек TCP в пространстве
/// процесса, в nettop — «ch»), отдавало на ПК с Windows ~5 МиБ/с при 13–18 МиБ/с у сети — с повторными
/// отправками и пустой трубой, при любом способе отправки и любых параметрах TCP. Сокет ядра, принятый в ту же
/// минуту, отдаёт со скоростью сети; исходящее NWConnection — тоже.
protocol LinkTransport: AnyObject, Sendable {
    /// Дождаться готовности (исходящее при этом подключается). Не вышло за timeout — закрыть, ошибка.
    func start(timeout: TimeInterval) async throws
    /// Отправить head и сразу за ним body (если есть), по порядку вызовов, без чужих байт между ними.
    /// completion — когда транспорт забрал данные (или ошибка: соединение не годится).
    func send(head: Data, body: Data?, completion: @escaping @Sendable (Error?) -> Void)
    /// От 1 до count байт (пустые данные допустимы — тогда просто повторить). Соединение закрыто — ошибка.
    func receive(upTo count: Int) async throws -> Data
    /// Закрыть: ждущие приём и отправку получают ошибку.
    func cancel()
    /// Адрес другой стороны (для запоминания последнего удачного адреса).
    var remoteEndpoint: (host: String, port: Int)? { get }
}

// MARK: - NWConnection

/// Исходящее соединение: NWConnection (подключается к адресу из Bonjour или к запасному).
final class NWTransport: LinkTransport {
    private static let queue = DispatchQueue(label: "io.github.lovipomidorku.clipvey.network")
    let connection: NWConnection

    init(_ connection: NWConnection) {
        self.connection = connection
    }

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

    func send(head: Data, body: Data?, completion: @escaping @Sendable (Error?) -> Void) {
        let done = NWConnection.SendCompletion.contentProcessed { error in
            completion(error.map { ClipveyError.connectionFailed($0.localizedDescription) })
        }
        if let body {
            connection.send(content: head, completion: .contentProcessed { _ in })
            connection.send(content: body, completion: done)
        } else {
            connection.send(content: head, completion: done)
        }
    }

    /// NWConnection отдаёт данные, когда придут все count байт (или соединение закроется).
    func receive(upTo count: Int) async throws -> Data {
        let connection = self.connection
        return try await withCheckedThrowingContinuation { continuation in
            connection.receive(minimumIncompleteLength: count, maximumLength: count) { data, _, isComplete, error in
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

    func cancel() {
        connection.cancel()
    }

    var remoteEndpoint: (host: String, port: Int)? {
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
}

// MARK: - Сокет ядра

/// Принятое соединение: сокет ядра в блокирующем режиме. Запись — на своей последовательной очереди
/// (по порядку вызовов send), чтение — на своей; обе очереди «с гарантированным потоком» (последовательные
/// очереди GCD), поэтому блокирующие вызовы не занимают общий пул. cancel() делает shutdown — блокирующие
/// read и write сразу возвращаются; сам сокет закрывается, когда объект освобождён (после всех блоков очередей),
/// поэтому номер дескриптора не может достаться чужому сокету, пока им пользуются.
final class SocketTransport: LinkTransport, @unchecked Sendable {
    private let descriptor: Int32
    private let writeQueue = DispatchQueue(label: "io.github.lovipomidorku.clipvey.socket.write")
    private let readQueue = DispatchQueue(label: "io.github.lovipomidorku.clipvey.socket.read")
    private let lock = NSLock()
    private var cancelled = false
    let remoteEndpoint: (host: String, port: Int)?

    init(descriptor: Int32) {
        self.descriptor = descriptor
        // Принятый сокет наследует O_NONBLOCK слушателя — здесь нужен блокирующий.
        let flags = fcntl(descriptor, F_GETFL)
        if flags >= 0 {
            _ = fcntl(descriptor, F_SETFL, flags & ~O_NONBLOCK)
        }
        var on: Int32 = 1
        // Как у NWConnection: без Нейгла (docs/protocol.md, «Транспорт»); без SIGPIPE при записи в закрытое.
        setsockopt(descriptor, IPPROTO_TCP, TCP_NODELAY, &on, socklen_t(MemoryLayout<Int32>.size))
        setsockopt(descriptor, SOL_SOCKET, SO_NOSIGPIPE, &on, socklen_t(MemoryLayout<Int32>.size))
        remoteEndpoint = Self.peerAddress(descriptor)
    }

    deinit {
        close(descriptor)
    }

    private var isCancelled: Bool {
        lock.lock()
        defer { lock.unlock() }
        return cancelled
    }

    /// Уже подключено.
    func start(timeout: TimeInterval) async throws {
        if isCancelled {
            throw ClipveyError.connectionFailed("соединение закрыто")
        }
    }

    func send(head: Data, body: Data?, completion: @escaping @Sendable (Error?) -> Void) {
        writeQueue.async { [self] in
            if isCancelled {
                completion(ClipveyError.connectionFailed("соединение закрыто"))
                return
            }
            completion(Self.writeAll(descriptor, head, body ?? Data()))
        }
    }

    func receive(upTo count: Int) async throws -> Data {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Data, Error>) in
            readQueue.async { [self] in
                if isCancelled {
                    continuation.resume(throwing: ClipveyError.connectionFailed("соединение закрыто"))
                    return
                }
                continuation.resume(with: Self.read(descriptor, count))
            }
        }
    }

    func cancel() {
        lock.lock()
        let first = !cancelled
        cancelled = true
        lock.unlock()
        if first {
            shutdown(descriptor, SHUT_RDWR)
        }
    }

    /// Прочитать до count байт: сколько есть, но не меньше одного (кадры большие — стараемся набрать все count,
    /// чтобы не склеивать кусочки выше). 0 — соединение закрыто.
    private static func read(_ descriptor: Int32, _ count: Int) -> Result<Data, Error> {
        var data = Data(count: count)
        var total = 0
        let failure: Error? = data.withUnsafeMutableBytes { raw -> Error? in
            guard let base = raw.baseAddress else { return nil }
            while total < count {
                let got = Darwin.read(descriptor, base + total, count - total)
                if got > 0 {
                    total += got
                    continue
                }
                if got < 0, errno == EINTR { continue }
                if total > 0 { return nil }
                return ClipveyError.connectionFailed(got == 0 ? "соединение закрыто" : String(cString: strerror(errno)))
            }
            return nil
        }
        if let failure {
            return .failure(failure)
        }
        data.count = total
        return .success(data)
    }

    /// Записать head и body целиком одним writev за раз (без отдельного маленького пакета для заголовка).
    private static func writeAll(_ descriptor: Int32, _ head: Data, _ body: Data) -> Error? {
        head.withUnsafeBytes { headBytes in
            body.withUnsafeBytes { bodyBytes in
                var headSent = 0
                var bodySent = 0
                while headSent < headBytes.count || bodySent < bodyBytes.count {
                    var vectors: [iovec] = []
                    if headSent < headBytes.count, let base = headBytes.baseAddress {
                        vectors.append(iovec(iov_base: UnsafeMutableRawPointer(mutating: base + headSent), iov_len: headBytes.count - headSent))
                    }
                    if bodySent < bodyBytes.count, let base = bodyBytes.baseAddress {
                        vectors.append(iovec(iov_base: UnsafeMutableRawPointer(mutating: base + bodySent), iov_len: bodyBytes.count - bodySent))
                    }
                    let written = writev(descriptor, vectors, Int32(vectors.count))
                    if written < 0 {
                        if errno == EINTR { continue }
                        return ClipveyError.connectionFailed(String(cString: strerror(errno)))
                    }
                    let toHead = min(written, headBytes.count - headSent)
                    headSent += toHead
                    bodySent += written - toHead
                }
                return nil
            }
        }
    }

    /// Адрес другой стороны; IPv4 внутри IPv6 (::ffff:a.b.c.d, слушатель двойного стека) — как IPv4.
    private static func peerAddress(_ descriptor: Int32) -> (host: String, port: Int)? {
        var storage = sockaddr_storage()
        var length = socklen_t(MemoryLayout<sockaddr_storage>.size)
        let ok = withUnsafeMutablePointer(to: &storage) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getpeername(descriptor, $0, &length) }
        }
        guard ok == 0 else { return nil }
        var buffer = [CChar](repeating: 0, count: Int(INET6_ADDRSTRLEN))
        func text(_ chars: [CChar]) -> String {
            String(decoding: chars.prefix { $0 != 0 }.map { UInt8(bitPattern: $0) }, as: UTF8.self)
        }
        switch Int32(storage.ss_family) {
        case AF_INET:
            var address = withUnsafePointer(to: &storage) { $0.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { $0.pointee } }
            inet_ntop(AF_INET, &address.sin_addr, &buffer, socklen_t(buffer.count))
            return (text(buffer), Int(UInt16(bigEndian: address.sin_port)))
        case AF_INET6:
            var address = withUnsafePointer(to: &storage) { $0.withMemoryRebound(to: sockaddr_in6.self, capacity: 1) { $0.pointee } }
            let port = Int(UInt16(bigEndian: address.sin6_port))
            let bytes = withUnsafeBytes(of: address.sin6_addr) { Array($0) }
            if bytes[0..<10].allSatisfy({ $0 == 0 }), bytes[10] == 0xff, bytes[11] == 0xff {
                return ("\(bytes[12]).\(bytes[13]).\(bytes[14]).\(bytes[15])", port)
            }
            inet_ntop(AF_INET6, &address.sin6_addr, &buffer, socklen_t(buffer.count))
            return (text(buffer), port)
        default:
            return nil
        }
    }
}
