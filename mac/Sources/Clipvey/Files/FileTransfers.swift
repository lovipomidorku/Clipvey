import AppKit
import Observation

/// Передача файлов со стороны приложения (docs/protocol.md, «Поведение сторон → Файлы в буфере»):
/// - **отправка**: файлы и папки из буфера → описание другим устройствам (узел);
/// - **получение до 50 МиБ**: тихо, в фоне, в кэш (Incoming/<id>), затем настоящие файлы — в буфер;
///   новое содержимое буфера (с другого устройства или скопированное здесь) отменяет незаконченное скачивание;
/// - **получение больше 50 МиБ**: окошко «Загрузить» → ~/Downloads/Clipvey, затем файлы — в буфер.
///
/// Окошки — стопка FileToast, видно верхнее. Новое окошко ложится сверху, поэтому идущая загрузка не теряется:
/// она продолжается под новым окошком и снова видна, когда то закрыто. Описание, которое ждёт «Загрузить»,
/// одно: новое содержимое с другого устройства его убирает (как следующий clip заменяет описание в буфере).
@MainActor
@Observable
final class FileTransfers {
    /// До этого размера (включительно) файлы скачиваются сразу, тихо, без окошка. Больше — по кнопке «Загрузить».
    static let instantLimit: Int64 = 50 * 1024 * 1024
    /// Сколько показывать «Готово» и сообщения об ошибках, если на окошко не навели мышь.
    static let doneDuration: TimeInterval = 6
    static let noticeDuration: TimeInterval = 10

    /// Стопка окошек; видно последнее.
    private(set) var toasts: [FileToast] = []
    var visibleToast: FileToast? { toasts.last }

    /// Файлы переданы: для «Последней синхронизации».
    @ObservationIgnored var onSynced: ((_ direction: AppModel.LastSync.Direction) -> Void)?

    @ObservationIgnored private let node: ClipveyNode
    @ObservationIgnored private let bridge: PasteboardBridge
    /// Кэш для тихих скачиваний: <cacheRoot>/<id>/.
    @ObservationIgnored let cacheRoot: URL
    /// Куда ложатся большие файлы после «Загрузить».
    @ObservationIgnored let downloadsDirectory: URL
    /// Тихое скачивание: одно, новое содержимое буфера его отменяет.
    @ObservationIgnored private var background: (offerID: String, task: Task<Void, Never>)?

    init(node: ClipveyNode, bridge: PasteboardBridge, cacheRoot: URL, downloadsDirectory: URL) {
        self.node = node
        self.bridge = bridge
        self.cacheRoot = cacheRoot
        self.downloadsDirectory = downloadsDirectory
    }

    /// При запуске: удалить из кэша всё старше суток (не на главном потоке).
    func cleanCache() {
        let root = cacheRoot
        Task.detached(priority: .utility) {
            let removed = IncomingCache.clean(root: root)
            if removed > 0 {
                Log.files.info("Из кэша полученных файлов удалено \(removed) старых папок")
            }
        }
    }

    // MARK: - Отправка

    /// В буфере файлы и папки, и их есть кому принять: отправить описание.
    /// Пока проверяется доступ и обходятся папки, содержимое буфера могло смениться — тогда ничего не уходит.
    func send(_ urls: [URL]) {
        let generation = bridge.changeCount
        let bridge = bridge
        Task {
            if let problem = await Self.accessProblem(urls) {
                Log.files.notice("Файлы не отправлены: \(problem, privacy: .public)")
                TestHooks.emitIfEnabled("FILES_REFUSED \(FileOfferFailure.unreadable.code)")
                showNotice(.sendFailed(.unreadable))
                return
            }
            let result = await node.offerFiles(urls) { bridge.changeCount == generation }
            switch result {
            case .offered(let offer, let recipients):
                TestHooks.emitIfEnabled("FILES_SENT \(recipients) \(offer.id) \(offer.items.count) \(offer.total)")
                if recipients > 0 {
                    onSynced?(.sent)
                }
            case .failed(let failure):
                TestHooks.emitIfEnabled("FILES_REFUSED \(failure.code)")
                // Выключено — не ошибка: пользователь сам выключил передачу файлов.
                if failure != .disabled {
                    showNotice(.sendFailed(failure))
                }
            case .superseded:
                TestHooks.emitIfEnabled("FILES_REFUSED Superseded")
            }
        }
    }

    /// Можно ли прочитать выбранные файлы. Файлы на Рабочем столе, в Документах и Загрузках macOS даёт читать
    /// только с разрешения (TCC): первое чтение показывает вопрос, после отказа open возвращает EPERM.
    /// Проверяется здесь, при копировании, а не когда другое устройство уже просит содержимое. Папки проверяет
    /// обход (FileTree: не прочитать папку — unreadable).
    nonisolated private static func accessProblem(_ urls: [URL]) async -> String? {
        await Task.detached(priority: .userInitiated) {
            for url in urls {
                var isDirectory: ObjCBool = false
                guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isDirectory), !isDirectory.boolValue else { continue }
                let descriptor = open(url.path, O_RDONLY)
                if descriptor >= 0 {
                    close(descriptor)
                } else if errno == EPERM || errno == EACCES {
                    return "нет доступа к «\(url.path)»: \(String(cString: strerror(errno)))"
                }
            }
            return nil
        }.value
    }

    // MARK: - Получение

    /// Пришло описание файлов — новое содержимое буфера с другого устройства.
    func receive(_ offer: FileOffer, deviceID: String, from name: String) {
        remoteContentArrived()
        if offer.total <= Self.instantLimit {
            startBackground(offer, deviceID: deviceID, name: name)
        } else {
            Log.files.info("Файлы \(offer.id, privacy: .public) (\(offer.total) байт) больше 50 МиБ — окошко «Загрузить»")
            push(FileToast(offer: offer, deviceID: deviceID, deviceName: name, content: .offer))
        }
    }

    /// Пришли текст, картинка или другое описание: незаконченное тихое скачивание больше не нужно,
    /// окошко «Загрузить» тоже устарело. Загрузки, начатые кнопкой, продолжаются.
    func remoteContentArrived() {
        cancelBackground("пришло новое содержимое с другого устройства")
        dropOffers()
    }

    /// На этом Mac скопировали другое: тихое скачивание отменяется, иначе оно заменило бы новое содержимое буфера.
    func localContentChanged() {
        cancelBackground("на этом Mac скопировано другое")
    }

    /// Выключено «Передавать файлы»: тихое скачивание и окошки «Загрузить» убираются; начатые кнопкой загрузки
    /// продолжаются (пользователь сам попросил).
    func filesDisabled() {
        cancelBackground("передача файлов выключена")
        dropOffers()
    }

    private func cancelBackground(_ reason: String) {
        guard let background else { return }
        Log.files.info("Тихое скачивание \(background.offerID, privacy: .public) отменено: \(reason, privacy: .public)")
        background.task.cancel()
        self.background = nil
    }

    private func dropOffers() {
        toasts.removeAll { if case .offer = $0.content { true } else { false } }
    }

    /// До 50 МиБ: скачать в кэш и, если буфер за это время не менялся, положить в него файлы.
    private func startBackground(_ offer: FileOffer, deviceID: String, name: String) {
        let generation = bridge.changeCount
        let directory = IncomingCache.directory(root: cacheRoot, offerID: offer.id)
        let root = cacheRoot
        let task = Task { [weak self] in
            guard let self else { return }
            do {
                try await Self.prepare(root, needed: offer.total)
                let urls = try await node.downloadFiles(offer: offer, from: deviceID, to: directory)
                try Task.checkCancellation()
                background = nil
                guard bridge.changeCount == generation else {
                    Log.files.info("Файлы \(offer.id, privacy: .public) скачаны, но буфер уже изменился — в буфер не кладутся")
                    TestHooks.emitIfEnabled("FILES_READY \(offer.id) \(urls.count) saved")
                    return
                }
                bridge.writeFiles(urls)
                onSynced?(.received(from: name))
                TestHooks.emitIfEnabled("FILES_READY \(offer.id) \(urls.count) pasteboard")
            } catch {
                if background?.offerID == offer.id {
                    background = nil
                }
                let problem = FileProblem(error, deviceName: name, inDownloads: false)
                TestHooks.emitIfEnabled("FILES_FAILED \(offer.id) \(problem.code)")
                guard problem.code != FileTransferFailure.cancelled.code else { return }
                showNotice(problem.receiveNotice(from: name))
            }
        }
        background = (offer.id, task)
    }

    /// «Загрузить»: скачать в ~/Downloads/Clipvey, прогресс — в том же окошке.
    func download(_ toast: FileToast) {
        guard case .offer = toast.content, let offer = toast.offer, let deviceID = toast.deviceID else { return }
        toast.startProgress()
        let generation = bridge.changeCount
        let directory = downloadsDirectory
        let name = toast.deviceName
        Log.files.info("Загрузка \(offer.id, privacy: .public) в \(directory.path, privacy: .public)")
        toast.task = Task { [weak self, weak toast] in
            guard let self else { return }
            do {
                try await Self.prepare(directory, needed: offer.total)
                let urls = try await node.downloadFiles(offer: offer, from: deviceID, to: directory) { received in
                    toast?.progress(received)
                }
                // Пока шла загрузка, в буфер могли скопировать другое (здесь или на другом устройстве) —
                // его не заменяем: файлы и так лежат в Загрузках, «Показать в Finder» их откроет.
                let inPasteboard = bridge.changeCount == generation
                if inPasteboard {
                    bridge.writeFiles(urls)
                    onSynced?(.received(from: name))
                }
                TestHooks.emitIfEnabled("FILES_READY \(offer.id) \(urls.count) \(inPasteboard ? "pasteboard" : "saved")")
                toast?.finish(.done(urls: urls, inPasteboard: inPasteboard))
            } catch {
                let problem = FileProblem(error, deviceName: name, inDownloads: true)
                TestHooks.emitIfEnabled("FILES_FAILED \(offer.id) \(problem.code)")
                if problem.code == FileTransferFailure.cancelled.code {
                    if let toast { remove(toast) }
                } else {
                    toast?.finish(.failed(problem))
                }
            }
        }
    }

    /// Папка есть (создаётся не на главном потоке: первая запись в Загрузки показывает вопрос macOS) и места хватает.
    nonisolated private static func prepare(_ directory: URL, needed: Int64) async throws {
        try await Task.detached(priority: .userInitiated) {
            let fileManager = FileManager.default
            do {
                try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
            } catch let error as CocoaError where error.code == .fileWriteNoPermission {
                throw FileSaveError.noAccess(error.localizedDescription)
            } catch {
                throw FileTransferError(.writeFailed, error.localizedDescription)
            }
            // Папка могла остаться с прошлого раза, а доступ к Загрузкам с тех пор отозван: проверяем записью.
            let probe = directory.appendingPathComponent(".clipvey-probe-\(Blob.newID().prefix(8))")
            guard fileManager.createFile(atPath: probe.path, contents: nil) else {
                if errno == EPERM || errno == EACCES {
                    throw FileSaveError.noAccess(String(cString: strerror(errno)))
                }
                throw FileTransferError(.writeFailed, "не создать файл в «\(directory.path)»: \(String(cString: strerror(errno)))")
            }
            try? fileManager.removeItem(at: probe)
            let values = try? directory.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey])
            if let available = values?.volumeAvailableCapacityForImportantUsage, available < needed {
                throw FileSaveError.noSpace(needed: needed, available: available)
            }
        }.value
    }

    // MARK: - Окошки

    private func push(_ toast: FileToast) {
        toasts.append(toast)
    }

    private func showNotice(_ problem: FileProblem) {
        // Сообщение одно: новое заменяет прежнее, а не копится под ним.
        toasts.removeAll { if case .notice = $0.content { true } else { false } }
        push(FileToast(offer: nil, deviceID: nil, deviceName: "", content: .notice(problem)))
    }

    /// Закрыть окошко. Идущая загрузка при этом отменяется (как «Отмена»), окошко уберёт её итог.
    func dismiss(_ toast: FileToast) {
        if case .downloading = toast.content {
            toast.task?.cancel()
            return
        }
        remove(toast)
    }

    private func remove(_ toast: FileToast) {
        toasts.removeAll { $0 === toast }
    }

    func cancel(_ toast: FileToast) {
        toast.task?.cancel()
    }

    func showInFinder(_ toast: FileToast) {
        guard case .done(let urls, _) = toast.content else { return }
        if TestHooks.enabled {
            TestHooks.emit("SHOW_IN_FINDER \(urls.map(\.lastPathComponent).joined(separator: "|"))")
        } else {
            NSWorkspace.shared.activateFileViewerSelecting(urls)
        }
        dismiss(toast)
    }

    func openPrivacySettings() {
        if TestHooks.enabled {
            TestHooks.emit("OPEN_PRIVACY_SETTINGS")
            return
        }
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_FilesAndFolders") {
            NSWorkspace.shared.open(url)
        }
    }

    /// Для --toast-demo: показать окошко без сети.
    func pushForDemo(_ toast: FileToast) {
        push(toast)
    }

    func clearForDemo() {
        toasts.removeAll()
    }

    /// Выход из программы: недокачанное (скрытые папки .clipvey-<id>-….part) не остаётся лежать в Загрузках и кэше.
    func removeUnfinished() {
        var offers: [(String, URL)] = []
        if let background {
            offers.append((background.offerID, IncomingCache.directory(root: cacheRoot, offerID: background.offerID)))
        }
        for toast in toasts {
            if case .downloading = toast.content, let offer = toast.offer {
                offers.append((offer.id, downloadsDirectory))
            }
        }
        let fileManager = FileManager.default
        for (id, directory) in offers {
            let entries = (try? fileManager.contentsOfDirectory(atPath: directory.path)) ?? []
            for entry in entries where entry.hasPrefix(".clipvey-\(id)-") && entry.hasSuffix(".part") {
                try? fileManager.removeItem(at: directory.appendingPathComponent(entry))
            }
        }
    }
}

/// Одно окошко: большое описание, загрузка, её итог или сообщение об ошибке.
@MainActor
@Observable
final class FileToast: Identifiable {
    enum Content {
        /// Больше 50 МиБ: «Загрузить» или закрыть.
        case offer
        case downloading
        /// Загружено; inPasteboard — файлы положены в буфер.
        case done(urls: [URL], inPasteboard: Bool)
        /// Загрузка не удалась.
        case failed(FileProblem)
        /// Сообщение: файлы не отправлены или тихое скачивание не удалось.
        case notice(FileProblem)

        /// Имя для событий самопроверки.
        var kind: String {
            switch self {
            case .offer: "offer"
            case .downloading: "progress"
            case .done: "done"
            case .failed: "failed"
            case .notice: "notice"
            }
        }
    }

    let id = UUID()
    let offer: FileOffer?
    let deviceID: String?
    let deviceName: String
    private(set) var content: Content
    /// Получено байт и скорость (байт/с) — обновляются несколько раз в секунду, а не на каждый кусок.
    private(set) var received: Int64 = 0
    private(set) var speed: Double?
    @ObservationIgnored var task: Task<Void, Never>?
    @ObservationIgnored private var latest: Int64 = 0
    @ObservationIgnored private var samples: [(date: Date, bytes: Int64)] = []
    @ObservationIgnored private var ticker: Task<Void, Never>?

    init(offer: FileOffer?, deviceID: String?, deviceName: String, content: Content) {
        self.offer = offer
        self.deviceID = deviceID
        self.deviceName = deviceName
        self.content = content
    }

    /// Первый элемент верхнего уровня (то, что скопировал пользователь) и сколько ещё.
    var firstItem: FileItem? { offer?.topLevel.first }
    var moreCount: Int { max(0, (offer?.topLevel.count ?? 0) - 1) }

    var total: Int64 { offer?.total ?? 0 }

    func startProgress() {
        content = .downloading
        samples = [(Date(), 0)]
        ticker = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(250))
                self?.tick()
            }
        }
    }

    /// На каждый кусок: только запомнить (перерисовка — в tick).
    func progress(_ bytes: Int64) {
        latest = bytes
    }

    func finish(_ result: Content) {
        ticker?.cancel()
        ticker = nil
        tick()
        content = result
    }

    /// Скорость — по последним трём секундам.
    private func tick() {
        let now = Date()
        samples.append((now, latest))
        samples.removeAll { now.timeIntervalSince($0.date) > 3 }
        if let first = samples.first, now.timeIntervalSince(first.date) >= 0.5 {
            speed = Double(latest - first.bytes) / now.timeIntervalSince(first.date)
        }
        if received != latest {
            received = latest
        }
    }

    /// Демонстрация (--toast-demo).
    func setForDemo(_ content: Content, received: Int64 = 0, speed: Double? = nil) {
        self.content = content
        self.received = received
        self.speed = speed
    }
}

/// Что не получилось, на языке интерфейса: заголовок, объяснение, код для журнала и событий самопроверки.
struct FileProblem {
    let code: String
    let title: String
    let message: String
    /// Показать кнопку «Открыть настройки» (Конфиденциальность → Файлы и папки).
    let opensPrivacySettings: Bool

    /// Файлы не отправлены.
    @MainActor
    static func sendFailed(_ failure: FileOfferFailure) -> FileProblem {
        let title = L("Файлы не отправлены", "Files not sent")
        let message: String
        switch failure {
        case .tooLarge:
            message = L("Больше 10 ГБ за раз не передаётся.", "More than 10 GB at once can’t be sent.")
        case .tooManyItems:
            message = L("Больше 10 000 файлов и папок за раз не передаётся.", "More than 10,000 files and folders at once can’t be sent.")
        case .nameTooLong:
            message = L("Слишком длинное имя файла или путь.", "A file name or path is too long.")
        case .empty:
            message = L("Нечего отправлять: ссылки и особые файлы не передаются.", "Nothing to send: links and special files aren’t sent.")
        case .disabled:
            message = L("Передача файлов выключена.", "File sync is off.")
        case .unreadable:
            return FileProblem(
                code: failure.code,
                title: L("Нет доступа к файлам", "No access to the files"),
                message: L("Разрешите Clipvey доступ: Системные настройки → Конфиденциальность и безопасность → Файлы и папки.",
                           "Allow Clipvey access: System Settings → Privacy & Security → Files & Folders."),
                opensPrivacySettings: true)
        }
        return FileProblem(code: failure.code, title: title, message: message, opensPrivacySettings: false)
    }

    /// Ошибка скачивания: FileTransferError, FileSaveError или отмена. inDownloads — скачивалось в Загрузки
    /// (доступ к ним macOS даёт только с разрешения); иначе — в кэш программы.
    @MainActor
    init(_ error: Error, deviceName name: String, inDownloads: Bool) {
        switch error {
        case FileSaveError.noAccess where inDownloads:
            code = "NoAccess"
            title = L("Нет доступа к папке", "No access to the folder")
            message = L("Разрешите Clipvey доступ к папке «Загрузки»: Системные настройки → Конфиденциальность и безопасность → Файлы и папки.",
                        "Allow Clipvey to access Downloads: System Settings → Privacy & Security → Files & Folders.")
            opensPrivacySettings = true
            return
        case FileSaveError.noAccess:
            code = "NoAccess"
            title = ""
            message = L("Не удалось записать файлы на этот Mac: нет доступа к папке.", "Couldn’t save the files on this Mac: no access to the folder.")
            opensPrivacySettings = false
            return
        case FileSaveError.noSpace(let needed, let available):
            code = "NoSpace"
            title = L("Недостаточно места", "Not enough space")
            message = L("Нужно \(ByteText.string(needed)), свободно \(ByteText.string(available)).",
                        "\(ByteText.string(needed)) needed, \(ByteText.string(available)) available.")
            opensPrivacySettings = false
            return
        default:
            break
        }
        let failure = FileTransferFailure(of: error)
        code = failure.code
        title = ""
        opensPrivacySettings = false
        message = switch failure {
        case .deviceUnavailable: L("\(name) недоступен — соединение прервалось.", "\(name) is unavailable — the connection was lost.")
        case .notFound: L("На \(name) этих файлов уже нет. Скопируйте их снова.", "These files are no longer available on \(name). Copy them again.")
        case .changed: L("Файл на \(name) изменился после копирования. Скопируйте его снова.", "A file on \(name) changed after it was copied. Copy it again.")
        case .unavailable: L("\(name) не смог прочитать файл.", "\(name) couldn’t read a file.")
        case .writeFailed: L("Не удалось записать файлы на этот Mac.", "Couldn’t save the files on this Mac.")
        case .protocolError: L("Файлы пришли с ошибкой. Попробуйте ещё раз.", "The files arrived damaged. Try again.")
        case .cancelled: L("Загрузка отменена.", "Download cancelled.")
        }
    }

    private init(code: String, title: String, message: String, opensPrivacySettings: Bool) {
        self.code = code
        self.title = title
        self.message = message
        self.opensPrivacySettings = opensPrivacySettings
    }

    /// Сообщение о неудачном тихом скачивании: заголовок — от кого.
    @MainActor
    func receiveNotice(from name: String) -> FileProblem {
        FileProblem(code: code, title: title.isEmpty ? L("Файлы с \(name) не получены", "Files from \(name) not received") : title,
                    message: message, opensPrivacySettings: opensPrivacySettings)
    }
}

/// Ошибки подготовки папки перед скачиванием (до запросов к другому устройству).
enum FileSaveError: Error {
    /// Нет разрешения писать в папку (Загрузки защищены TCC).
    case noAccess(String)
    case noSpace(needed: Int64, available: Int64)
}

/// Размеры как в Finder: десятичные единицы, «1,2 ГБ», «340 МБ», «45 МБ/с».
enum ByteText {
    @MainActor
    static func string(_ bytes: Int64) -> String {
        let units = Language.shared.isRussian ? ["байт", "КБ", "МБ", "ГБ", "ТБ"] : ["bytes", "KB", "MB", "GB", "TB"]
        var value = Double(bytes)
        var unit = 0
        while value >= 1000, unit < units.count - 1 {
            value /= 1000
            unit += 1
        }
        // Неразрывный пробел: число и единица не расходятся по строкам.
        if unit == 0 {
            return "\(bytes)\u{00A0}\(units[0])"
        }
        // Меньше 10 — с одним знаком после запятой (1,2 ГБ), иначе целое (340 МБ).
        let digits = value < 9.95 ? 1 : 0
        var text = String(format: "%.\(digits)f", value)
        if text.hasSuffix(".0") {
            text.removeLast(2)
        }
        if Language.shared.isRussian {
            text = text.replacingOccurrences(of: ".", with: ",")
        }
        return "\(text)\u{00A0}\(units[unit])"
    }

    @MainActor
    static func speed(_ bytesPerSecond: Double) -> String {
        "\(string(Int64(bytesPerSecond)))\(L("/с", "/s"))"
    }
}
