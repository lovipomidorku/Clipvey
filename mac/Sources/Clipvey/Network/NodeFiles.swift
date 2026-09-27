import Foundation

/// Файлы и папки (docs/protocol.md, «Файлы»): описание уходит при копировании, содержимое получатель забирает
/// у источника кусками по запросу. Файлы не пересылаются по цепочке. Логика совпадает с NodeFiles.cs.

/// Итог offerFiles: описание и скольким устройствам оно отправлено, или почему не отправлено.
enum FileOfferResult: Sendable {
    case offered(FileOffer, recipients: Int)
    case failed(FileOfferFailure)
    /// Пока обходились папки, содержимое буфера сменилось (isCurrent вернул false): описание не отправлено.
    case superseded
}

/// Своё описание: что отдавать по file_get.
struct OfferRecord {
    let offer: FileOffer
    /// Локальный файл для каждого элемента, по порядку items.
    let urls: [URL]
    /// Когда описание перестало быть последним.
    var supersededAt: Date?
}

extension ClipveyNode {
    /// Сколько file_get держать отправленными наперёд при скачивании (источник всё равно обслуживает их по очереди).
    private static let maxOutstandingRequests = 8
    /// Старые описания (не последнее) обслуживаются не дольше часа после того, как их сменило новое.
    private static let oldOfferLifetime: TimeInterval = 60 * 60
    /// Сколько старых описаний помнить.
    private static let maxOldOffers = 16

    /// Включить или выключить «Передавать файлы»: новый caps в info всем сеансам. Выключено — описания не уходят
    /// и не принимаются, на запросы содержимого — not_found. Начатые скачивания не прерываются. Хранит настройку приложение.
    func setFilesEnabled(_ enabled: Bool) {
        guard enabled != filesEnabled else { return }
        filesEnabled = enabled
        Log.files.notice("Передача файлов \(enabled ? "включена" : "выключена", privacy: .public)")
        if !enabled {
            fileOffers.removeAll()
            latestFileOfferID = nil
        }
        sendInfoToAll()
        refreshDevices()
    }

    // MARK: - Отправитель

    /// Отправить описание выбранных файлов и папок напрямую подключённым включённым устройствам с file в caps
    /// (не по цепочке). Обход папок идёт в фоне. Содержимое потом отдаётся по запросам, пока описание последнее
    /// (и ещё час после этого). isCurrent проверяется после обхода: если в буфере уже другое (скопировали новое,
    /// пришло с другого устройства), устаревшее описание не отправляется и не заменяет более новое содержимое.
    func offerFiles(_ urls: [URL], isCurrent: @MainActor () -> Bool = { true }) async -> FileOfferResult {
        guard filesEnabled else {
            Log.files.info("Передача файлов выключена — файлы не отправлены")
            return .failed(.disabled)
        }
        let built = await Task.detached(priority: .userInitiated) { FileTree.build(urls) }.value
        let tree: FileTree
        switch built {
        case .success(let value):
            tree = value
        case .failure(let failure):
            Log.files.notice("Файлы не отправлены: \(failure.code, privacy: .public)")
            return .failed(failure)
        }
        guard isCurrent() else {
            Log.files.info("Файлы не отправлены: пока обходились папки, буфер изменился")
            return .superseded
        }
        let offer = FileOffer(id: Blob.newID(), items: tree.items, total: tree.total)
        registerOffer(OfferRecord(offer: offer, urls: tree.urls))
        let targets = sessions.values.filter {
            $0.caps.contains(ProtocolLimits.fileCapability) && store.device(id: $0.peerID)?.enabled == true
        }
        var recipients = 0
        for session in targets {
            do {
                try await session.link.send(.fileOffer(offer))
                recipients += 1
            } catch {
                Log.files.error("Не удалось отправить описание файлов «\(session.peerName, privacy: .public)»: \(error.localizedDescription, privacy: .public)")
                session.link.cancel()
            }
        }
        Log.files.info("Описание файлов \(offer.id, privacy: .public): \(offer.items.count) элементов, \(offer.total) байт — отправлено \(recipients) устройствам")
        return .offered(offer, recipients: recipients)
    }

    /// Новое описание становится последним, старые живут час и не больше maxOldOffers.
    private func registerOffer(_ record: OfferRecord) {
        let now = Date()
        if let latest = latestFileOfferID {
            fileOffers[latest]?.supersededAt = now
        }
        fileOffers[record.offer.id] = record
        latestFileOfferID = record.offer.id
        pruneOffers(now: now)
    }

    private func pruneOffers(now: Date) {
        let old = fileOffers.values.compactMap { record in record.supersededAt.map { (record.offer.id, $0) } }
            .sorted { $0.1 < $1.1 }
        for (position, (id, date)) in old.enumerated()
        where now.timeIntervalSince(date) > Self.oldOfferLifetime || old.count - position > Self.maxOldOffers {
            fileOffers[id] = nil
        }
    }

    /// file_get: ответ ставится в очередь сеанса (в том числе not_found — ответы идут в порядке запросов).
    func handleFileGet(id: String, req: UInt32, index: Int, offset: Int64, from session: ActiveSession) async {
        pruneOffers(now: Date())
        let job: FileServer.Job
        if filesEnabled, let record = fileOffers[id], index >= 0, index < record.offer.items.count,
           let size = record.offer.items[index].size, offset >= 0, offset <= size {
            job = FileServer.Job(req: req, url: record.urls[index], size: size, offset: offset, failure: nil)
        } else {
            job = FileServer.Job(req: req, url: nil, size: 0, offset: 0, failure: "not_found")
        }
        await session.files.server.enqueue(job)
    }

    // MARK: - Получатель

    func handleFileOffer(_ offer: FileOffer, from session: ActiveSession) {
        guard filesEnabled else {
            Log.files.info("Описание файлов от «\(session.peerName, privacy: .public)» пропущено: передача файлов выключена")
            return
        }
        Log.files.info("Описание файлов от «\(session.peerName, privacy: .public)»: \(offer.items.count) элементов, \(offer.total) байт")
        onEvent?("FILE_OFFER \(session.peerName) \(offer.id) \(offer.items.count) \(offer.total)")
        onFileOffer?(offer, session.peerID, displayName(session))
    }

    /// Скачать всё описание в directory с сохранением структуры. Возвращает элементы верхнего уровня.
    /// - Пишет во временную скрытую папку в directory и переносит на место только после успеха; при ошибке
    ///   и отмене недокачанное удаляется. Совпадающие имена в directory получают номер: «отчёт (2).pdf».
    /// - Запись на диск идёт не на главном потоке; сеанс не читает из сети быстрее, чем пишет диск.
    /// - progress — сколько байт получено всего, на главном потоке после каждого куска.
    /// - Ошибки — FileTransferError (failure — код для интерфейса); отмена задачи — CancellationError
    ///   (источнику уходит file_cancel). FileTransferFailure(of:) даёт код для любой из них.
    func downloadFiles(offer: FileOffer, from deviceID: String, to directory: URL,
                       progress: @escaping @MainActor (Int64) -> Void = { _ in }) async throws -> [URL] {
        guard let session = sessions[deviceID] else {
            throw FileTransferError(.deviceUnavailable, "нет сеанса с устройством")
        }
        let fileManager = FileManager.default
        let local = FileNames.localPaths(offer.items, windows: false)
        let staging = directory.appendingPathComponent(".clipvey-\(offer.id)-\(Blob.newID().prefix(8)).part", isDirectory: true)
        do {
            try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
            try fileManager.createDirectory(at: staging, withIntermediateDirectories: false)
        } catch {
            throw FileTransferError(.writeFailed, error.localizedDescription)
        }
        defer {
            if fileManager.fileExists(atPath: staging.path) {
                do {
                    try fileManager.removeItem(at: staging)
                } catch {
                    Log.files.error("Не удалось убрать недокачанное \(staging.path, privacy: .public): \(error.localizedDescription, privacy: .public)")
                }
            }
        }

        let started = Date()
        let counter = ProgressCounter(progress)
        var pending: [IncomingFileRequest] = []
        do {
            for (index, item) in offer.items.enumerated() where item.isDirectory {
                do {
                    try fileManager.createDirectory(at: staging.appendingPathComponent(local[index]), withIntermediateDirectories: true)
                } catch {
                    throw FileTransferError(.writeFailed, error.localizedDescription)
                }
            }
            for (index, item) in offer.items.enumerated() {
                guard let size = item.size else { continue }
                try Task.checkCancellation()
                while pending.count >= Self.maxOutstandingRequests {
                    try await pending[0].wait()
                    pending.removeFirst()
                }
                let request = try IncomingFileRequest(url: staging.appendingPathComponent(local[index]), size: size, counter: counter)
                try session.files.register(request)
                pending.append(request)
                try await session.link.send(.fileGet(id: offer.id, req: request.req, index: index, offset: 0))
            }
            while !pending.isEmpty {
                try await pending[0].wait()
                pending.removeFirst()
            }
            var results: [URL] = []
            for (index, item) in offer.items.enumerated() where !item.path.contains("/") {
                results.append(try Self.moveToUnique(staging.appendingPathComponent(local[index]), into: directory,
                                                     name: local[index], isDirectory: item.isDirectory))
            }
            let milliseconds = Int(Date().timeIntervalSince(started) * 1000)
            Log.files.info("Файлы \(offer.id, privacy: .public) от «\(session.peerName, privacy: .public)» скачаны: \(offer.fileCount) файлов, \(counter.total) байт за \(milliseconds) мс")
            return results
        } catch {
            for request in pending {
                await request.cancel()
            }
            Log.files.notice("Файлы \(offer.id, privacy: .public) от «\(session.peerName, privacy: .public)» не скачаны (\(FileTransferFailure(of: error).code, privacy: .public)): \(error.localizedDescription, privacy: .public)")
            if error is CancellationError || error is FileTransferError {
                throw error
            }
            // Запись и перенос дают FileTransferError; остальное — не удалось отправить file_get.
            throw FileTransferError(.deviceUnavailable, error.localizedDescription)
        }
    }

    /// Перенести готовый элемент верхнего уровня в directory; занятое имя — с номером.
    private static func moveToUnique(_ source: URL, into directory: URL, name: String, isDirectory: Bool) throws -> URL {
        let fileManager = FileManager.default
        var number = 1
        while true {
            let candidate = directory.appendingPathComponent(number == 1 ? name : FileNames.numbered(name, number, isDirectory: isDirectory))
            number += 1
            if fileManager.fileExists(atPath: candidate.path) {
                continue
            }
            do {
                try fileManager.moveItem(at: source, to: candidate)
                return candidate
            } catch CocoaError.fileWriteFileExists where number < 1000 {
                continue
            } catch {
                throw FileTransferError(.writeFailed, error.localizedDescription)
            }
        }
    }
}

/// Сколько байт получено всего за скачивание.
@MainActor
final class ProgressCounter {
    private(set) var total: Int64 = 0
    private let report: @MainActor (Int64) -> Void

    init(_ report: @escaping @MainActor (Int64) -> Void) {
        self.report = report
    }

    func add(_ count: Int) {
        total += Int64(count)
        report(total)
    }
}

// MARK: - Сеанс

/// Файлы одного сеанса: приём кусков для своих запросов и обслуживание чужих (server).
@MainActor
final class SessionFiles {
    let server: FileServer
    private var requests: [UInt32: IncomingFileRequest] = [:]
    private var lastReq: UInt32 = 0
    private var stopped = false

    init(link: PeerLink, peerName: String) {
        server = FileServer(link: link, peerName: peerName)
        self.link = link
    }

    let link: PeerLink

    /// Выдать req (от 1, в пределах сеанса не повторяется) и ждать по нему данные.
    func register(_ request: IncomingFileRequest) throws {
        guard !stopped else { throw FileTransferError(.deviceUnavailable, "сеанс завершён") }
        lastReq = lastReq == UInt32.max ? 1 : lastReq + 1
        request.req = lastReq
        request.files = self
        requests[lastReq] = request
    }

    func unregister(_ req: UInt32) {
        requests[req] = nil
    }

    /// Кусок для неизвестного req (отменённый запрос) пропускается. Запись на диск — не на главном потоке;
    /// цикл приёма сеанса ждёт её, так что из сети не читается быстрее, чем пишет диск.
    func chunk(req: UInt32, data: Data) async {
        await requests[req]?.chunk(data)
        // Режим проверки (--slow-files): приём медленнее, чтобы увидеть прогресс и успеть отменить.
        if let delay = TestHooks.fileChunkDelay {
            try? await Task.sleep(for: delay)
        }
    }

    func end(req: UInt32, size: Int64) async {
        guard let request = requests.removeValue(forKey: req) else { return }
        request.expectsData = false
        await request.end(size: size)
    }

    func fail(req: UInt32, reason: String) {
        guard let request = requests.removeValue(forKey: req) else { return }
        request.expectsData = false
        request.finish(.failure(FileTransferError(FileTransferFailure(peerReason: reason), "источник ответил \(reason)")))
        Task { await request.closeWriter() }
    }

    /// Отправить без ожидания (file_cancel): ошибка отправки значит, что сеанс и так рвётся.
    func sendInBackground(_ message: WireMessage) {
        let link = self.link
        Task { try? await link.send(message) }
    }

    /// Сеанс завершён: все ожидания — «устройство недоступно», обслуживание — остановить.
    func stop() async {
        stopped = true
        let pending = requests.values
        requests.removeAll()
        for request in pending {
            request.expectsData = false
            request.finish(.failure(FileTransferError(.deviceUnavailable, "сеанс с устройством оборвался")))
            await request.closeWriter()
        }
        await server.stop()
    }
}

/// Запрос одного файла при скачивании.
@MainActor
final class IncomingFileRequest {
    fileprivate(set) var req: UInt32 = 0
    fileprivate weak var files: SessionFiles?
    /// Источник ещё может прислать данные по req (нет file_end, file_error, обрыва и отмены): при отмене — file_cancel.
    fileprivate var expectsData = true
    private let size: Int64
    private let writer: FileWriter
    private let counter: ProgressCounter
    private var received: Int64 = 0
    private var result: Result<Void, Error>?
    private var continuation: CheckedContinuation<Void, Error>?

    init(url: URL, size: Int64, counter: ProgressCounter) throws {
        self.size = size
        self.counter = counter
        writer = try FileWriter(url: url)
    }

    fileprivate func chunk(_ data: Data) async {
        guard result == nil else { return }
        guard received + Int64(data.count) <= size else {
            await cancel(FileTransferError(.protocolError, "данных больше \(size) байт"))
            return
        }
        do {
            try await writer.write(data)
        } catch {
            await cancel(FileTransferError(.writeFailed, error.localizedDescription))
            return
        }
        guard result == nil else { return }
        received += Int64(data.count)
        counter.add(data.count)
    }

    fileprivate func end(size endSize: Int64) async {
        guard result == nil else { return }
        guard endSize == received, received == size else {
            finish(.failure(FileTransferError(.protocolError, "получено \(received) байт, по file_end \(endSize), ожидалось \(size)")))
            await closeWriter()
            return
        }
        do {
            try await writer.close()
            finish(.success(()))
        } catch {
            finish(.failure(FileTransferError(.writeFailed, error.localizedDescription)))
        }
    }

    /// Отменить: источнику — file_cancel (если он ещё шлёт данные), файл закрывается (папку потом удаляет downloadFiles).
    /// Результат мог уже установить обработчик отмены задачи в wait() — file_cancel нужен и тогда.
    func cancel(_ error: Error = CancellationError()) async {
        finish(.failure(error))
        if expectsData, let files, req != 0 {
            expectsData = false
            files.unregister(req)
            files.sendInBackground(.fileCancel(req: req))
        }
        await closeWriter()
    }

    func closeWriter() async {
        try? await writer.close()
    }

    func finish(_ outcome: Result<Void, Error>) {
        guard result == nil else { return }
        result = outcome
        continuation?.resume(with: outcome)
        continuation = nil
    }

    /// Дождаться file_end (или ошибки). Отмена задачи — CancellationError.
    func wait() async throws {
        if let result { return try result.get() }
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                if let result {
                    continuation.resume(with: result)
                } else {
                    self.continuation = continuation
                }
            }
        } onCancel: {
            Task { @MainActor [weak self] in self?.finish(.failure(CancellationError())) }
        }
    }
}

/// Запись одного файла не на главном потоке.
actor FileWriter {
    private var handle: FileHandle?

    init(url: URL) throws {
        guard FileManager.default.createFile(atPath: url.path, contents: nil) else {
            throw FileTransferError(.writeFailed, "не удалось создать «\(url.lastPathComponent)»")
        }
        handle = try FileHandle(forWritingTo: url)
    }

    func write(_ data: Data) throws {
        try handle?.write(contentsOf: data)
    }

    func close() throws {
        let current = handle
        handle = nil
        try current?.close()
    }
}

/// Обслуживание file_get одного сеанса: запросы по очереди, в порядке поступления; файл читается кусками
/// по 1 МиБ в фоне (pread прямо в буфер открытого текста, без копий). Каждый кусок — отдельный кадр PeerLink,
/// поэтому ping, clip и info проходят между кусками.
actor FileServer {
    /// Запрос: файл url с позиции offset до конца (size — размер на момент описания) или сразу ошибка failure.
    struct Job: Sendable {
        let req: UInt32
        let url: URL?
        let size: Int64
        let offset: Int64
        let failure: String?
    }

    private let link: PeerLink
    private let peerName: String
    private var queue: [Job] = []
    private var pending: Set<UInt32> = []
    private var cancelled: Set<UInt32> = []
    private var running = false
    private var stopped = false
    private var servedFiles = 0
    private var servedBytes: Int64 = 0

    init(link: PeerLink, peerName: String) {
        self.link = link
        self.peerName = peerName
    }

    func enqueue(_ job: Job) {
        guard !stopped else { return }
        queue.append(job)
        pending.insert(job.req)
        if !running {
            running = true
            Task { await drain() }
        }
    }

    /// file_cancel: запрос в очереди выбрасывается, идущий прекращается перед следующим куском. Ответа нет.
    func cancel(_ req: UInt32) {
        if pending.contains(req) {
            cancelled.insert(req)
        }
    }

    func stop() {
        stopped = true
        queue.removeAll()
    }

    private func drain() async {
        while !stopped, !queue.isEmpty {
            let job = queue.removeFirst()
            if !cancelled.contains(job.req) {
                do {
                    try await serve(job)
                } catch {
                    Log.files.error("Отдача файлов «\(self.peerName, privacy: .public)» прервана: \(error.localizedDescription, privacy: .public)")
                    stopped = true
                    link.cancel()
                }
            }
            pending.remove(job.req)
            cancelled.remove(job.req)
        }
        if servedFiles > 0 {
            Log.files.info("Отдано «\(self.peerName, privacy: .public)»: \(self.servedFiles) файлов, \(self.servedBytes) байт")
            servedFiles = 0
            servedBytes = 0
        }
        running = false
        // Пока шёл журнал, могли прийти новые запросы.
        if !stopped, !queue.isEmpty {
            running = true
            Task { await drain() }
        }
    }

    /// Ошибки чтения — file_error; ошибка отправки (сеанс рвётся) уходит выше.
    private func serve(_ job: Job) async throws {
        if let failure = job.failure {
            Log.files.info("Запрос \(job.req) от «\(self.peerName, privacy: .public)»: \(failure, privacy: .public)")
            try await link.send(.fileError(req: job.req, reason: failure))
            return
        }
        guard let url = job.url else { return }
        let descriptor = open(url.path, O_RDONLY)
        guard descriptor >= 0 else {
            let reason = errno == ENOENT || errno == ENOTDIR ? "changed" : "unavailable"
            try await replyError(job, reason, "не открыть «\(url.path)»: \(String(cString: strerror(errno)))")
            return
        }
        defer { close(descriptor) }
        guard Self.size(descriptor) == job.size else {
            try await replyError(job, "changed", "размер «\(url.path)» изменился")
            return
        }
        let capacity = FileChunk.headerBytes + Int(min(Int64(ProtocolLimits.fileChunkBytes), max(job.size - job.offset, 0)))
        var buffer = Data(count: capacity)
        var position = job.offset
        var sent: Int64 = 0
        while position < job.size {
            if cancelled.contains(job.req) || stopped {
                Log.files.info("Запрос \(job.req) от «\(self.peerName, privacy: .public)» отменён после \(sent) байт")
                return
            }
            let length = Int(min(Int64(ProtocolLimits.fileChunkBytes), job.size - position))
            let read = buffer.withUnsafeMutableBytes { raw in
                Self.readFully(descriptor, raw.baseAddress! + FileChunk.headerBytes, length, position)
            }
            if read < 0 {
                try await replyError(job, "unavailable", "ошибка чтения «\(url.path)»: \(String(cString: strerror(errno)))")
                return
            }
            if read < length {
                try await replyError(job, "changed", "«\(url.path)» стал короче")
                return
            }
            FileChunk.writeHeader(req: job.req, into: &buffer)
            // Последний кусок короче — срез того же буфера, без копирования.
            try await link.sendFileChunk(plaintext: length + FileChunk.headerBytes == buffer.count
                                         ? buffer : buffer.prefix(FileChunk.headerBytes + length))
            position += Int64(length)
            sent += Int64(length)
        }
        guard Self.size(descriptor) == job.size else {
            try await replyError(job, "changed", "размер «\(url.path)» изменился во время передачи")
            return
        }
        try await link.send(.fileEnd(req: job.req, size: sent))
        servedFiles += 1
        servedBytes += sent
    }

    private func replyError(_ job: Job, _ reason: String, _ detail: String) async throws {
        Log.files.notice("Запрос \(job.req) от «\(self.peerName, privacy: .public)»: \(reason, privacy: .public) — \(detail, privacy: .public)")
        try await link.send(.fileError(req: job.req, reason: reason))
    }

    private static func size(_ descriptor: Int32) -> Int64 {
        var info = stat()
        return fstat(descriptor, &info) == 0 ? Int64(info.st_size) : -1
    }

    /// Прочитать count байт с позиции; меньше — файл кончился; −1 — ошибка.
    private static func readFully(_ descriptor: Int32, _ destination: UnsafeMutableRawPointer, _ count: Int, _ offset: Int64) -> Int {
        var total = 0
        while total < count {
            let read = pread(descriptor, destination + total, count - total, off_t(offset) + off_t(total))
            if read < 0 {
                if errno == EINTR { continue }
                return -1
            }
            if read == 0 { break }
            total += read
        }
        return total
    }
}
