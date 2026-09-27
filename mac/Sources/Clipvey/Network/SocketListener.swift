import Foundation
import dnssd

/// Слушатель TCP на сокете ядра (двойной стек IPv6/IPv4): принятые соединения идут в SocketTransport.
/// Не NWListener — почему, см. LinkTransport. Объявление в Bonjour — отдельно, BonjourAdvertiser.
@MainActor
final class SocketListener {
    let port: UInt16
    private let descriptor: Int32
    private let source: DispatchSourceRead

    /// port 0 — любой свободный. Порт занят или сокет не открылся — ошибка (errno текстом).
    init(port: UInt16, onAccept: @escaping @MainActor (Int32) -> Void) throws {
        let descriptor = socket(AF_INET6, SOCK_STREAM, IPPROTO_TCP)
        guard descriptor >= 0 else {
            throw ClipveyError.connectionFailed(String(cString: strerror(errno)))
        }
        var on: Int32 = 1
        var off: Int32 = 0
        let size = socklen_t(MemoryLayout<Int32>.size)
        // SO_REUSEADDR — как allowLocalEndpointReuse у NWListener: порт свободен сразу после перезапуска. Без
        // SO_REUSEPORT: занятый другим Clipvey порт должен дать ошибку, а не делить с ним соединения.
        setsockopt(descriptor, SOL_SOCKET, SO_REUSEADDR, &on, size)
        setsockopt(descriptor, IPPROTO_IPV6, IPV6_V6ONLY, &off, size)
        var address = sockaddr_in6()
        address.sin6_len = UInt8(MemoryLayout<sockaddr_in6>.size)
        address.sin6_family = sa_family_t(AF_INET6)
        address.sin6_port = port.bigEndian
        address.sin6_addr = in6addr_any
        let bound = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(descriptor, $0, socklen_t(MemoryLayout<sockaddr_in6>.size)) }
        }
        guard bound == 0, listen(descriptor, 16) == 0 else {
            let text = String(cString: strerror(errno))
            close(descriptor)
            throw ClipveyError.connectionFailed(text)
        }
        let flags = fcntl(descriptor, F_GETFL)
        _ = fcntl(descriptor, F_SETFL, flags | O_NONBLOCK)
        var actual = sockaddr_in6()
        var length = socklen_t(MemoryLayout<sockaddr_in6>.size)
        _ = withUnsafeMutablePointer(to: &actual) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(descriptor, $0, &length) }
        }
        self.port = UInt16(bigEndian: actual.sin6_port)
        self.descriptor = descriptor
        let source = DispatchSource.makeReadSource(fileDescriptor: descriptor, queue: .main)
        source.setEventHandler {
            MainActor.assumeIsolated {
                while true {
                    let accepted = accept(descriptor, nil, nil)
                    if accepted < 0 {
                        if errno == EINTR { continue }
                        break
                    }
                    onAccept(accepted)
                }
            }
        }
        source.setCancelHandler {
            close(descriptor)
        }
        self.source = source
        source.resume()
    }

    func cancel() {
        source.cancel()
    }
}

/// Объявление своего сервиса в Bonjour через dns_sd (DNSServiceRegister): имя, порт и TXT. Смена имени —
/// перерегистрация, смена только TXT — DNSServiceUpdateRecord. Конфликт имён Bonjour решает сам («Имя (2)»).
/// Если mDNSResponder перезапустился или отказал — повтор через 5 с.
@MainActor
final class BonjourAdvertiser {
    private let type: String
    private var service: DNSServiceRef?
    private var name = ""
    private var port: UInt16 = 0
    private var txt: [String: String] = [:]
    private var retry: Task<Void, Never>?

    init(type: String) {
        self.type = type
    }

    func advertise(name: String, port: UInt16, txt: [String: String]) {
        if service != nil, name == self.name, port == self.port {
            if txt != self.txt {
                self.txt = txt
                let record = Self.encode(txt)
                let result = record.withUnsafeBytes {
                    DNSServiceUpdateRecord(service, nil, 0, UInt16(record.count), $0.baseAddress, 0)
                }
                if result != kDNSServiceErr_NoError {
                    Log.network.error("Bonjour: TXT не обновлён (\(result)) — объявляю заново")
                    register()
                }
            }
            return
        }
        self.name = name
        self.port = port
        self.txt = txt
        register()
    }

    func stop() {
        retry?.cancel()
        retry = nil
        if let service {
            DNSServiceRefDeallocate(service)
        }
        service = nil
    }

    private func register() {
        stop()
        let record = Self.encode(txt)
        var reference: DNSServiceRef?
        let context = Unmanaged.passUnretained(self).toOpaque()
        let result = record.withUnsafeBytes { bytes in
            DNSServiceRegister(&reference, 0, 0, name, type, nil, nil, port.bigEndian, UInt16(record.count), bytes.baseAddress,
                               { _, _, error, registeredName, _, _, context in
                                   guard let context else { return }
                                   let advertiser = Unmanaged<BonjourAdvertiser>.fromOpaque(context).takeUnretainedValue()
                                   let registered = registeredName.map { String(cString: $0) } ?? ""
                                   MainActor.assumeIsolated { advertiser.registered(error: error, name: registered) }
                               }, context)
        }
        guard result == kDNSServiceErr_NoError, let reference else {
            Log.network.error("Bonjour: не удалось объявить «\(self.name, privacy: .public)» (\(result))")
            scheduleRetry()
            return
        }
        DNSServiceSetDispatchQueue(reference, .main)
        service = reference
    }

    private func registered(error: DNSServiceErrorType, name registered: String) {
        if error == kDNSServiceErr_NoError {
            Log.network.info("Bonjour: объявлено «\(registered, privacy: .public)» на порту \(self.port)")
        } else {
            Log.network.error("Bonjour: объявление прервано (\(error)) — повтор через 5 с")
            scheduleRetry()
        }
    }

    private func scheduleRetry() {
        if let service {
            DNSServiceRefDeallocate(service)
        }
        service = nil
        retry?.cancel()
        retry = Task { [weak self] in
            try? await Task.sleep(for: .seconds(5))
            guard !Task.isCancelled, let self, self.service == nil else { return }
            self.register()
        }
    }

    /// TXT: записи «ключ=значение», каждая с байтом длины впереди (не длиннее 255 байт).
    private static func encode(_ txt: [String: String]) -> [UInt8] {
        var record: [UInt8] = []
        for (key, value) in txt.sorted(by: { $0.key < $1.key }) {
            let entry = Array("\(key)=\(value)".utf8.prefix(255))
            record.append(UInt8(entry.count))
            record.append(contentsOf: entry)
        }
        return record
    }
}
