import AppKit
import Network

/// Самопроверка вместе с консольным clipvey-peer. Включается флагами, в обычной работе выключена.
///   --test                 печатать события в stdout («READY …», «CONNECTED …», «CLIP …»)
///   --data DIR             папка с ключом и списком устройств вместо ~/Library/Application Support/Clipvey
///   --pasteboard NAME      работать с именованным буфером вместо общего (macOS не спрашивает разрешения)
///   --name NAME            имя устройства вместо имени Mac (чтобы не путать с настоящим Clipvey)
///   --pair                 открыть режим связывания
///   --auto-confirm         в роли R нажать «Готово», когда другая сторона подтвердит код
///   --pair-with TEXT       в роли I связаться с устройством, в имени которого есть TEXT
///   --pair-address H:P     для --pair-with: подключиться по адресу, не дожидаясь Bonjour
///   --code-file FILE       взять код для роли I из файла со строкой «PAIRING_CODE 123456»
///   --send TEXT            после подключения отправить текст (--send-delay N — подождать ещё N с)
///   --exit-after N         выйти через N секунд
///   --update-url URL       адрес releases/latest вместо GitHub (допускается http://127.0.0.1)
///   --update-public-key B64  открытый ключ подписи релизов вместо зашитого (одноразовый ключ проверок)
///   --update-now           сразу проверить обновления и, если есть новая версия, установить без вопроса
///   --update-check-delay N первая автоматическая проверка через N секунд вместо 60
///   --images off           выключить «Передавать картинки» (настройка не сохраняется)
///   --rename-after N NAME  через N секунд после запуска сменить своё имя
///   --files off            выключить «Передавать файлы» (настройка не сохраняется)
///   --send-files ПУТЬ…     после подключения (и --send-delay) отправить описание файлов и папок (пути — до следующего «--…»)
///   --save-files DIR       каждое пришедшее описание скачивать целиком в DIR/<id>/
///   --save-delay N         начинать скачивание через N секунд после описания
///   --cancel-files-after N отменить скачивание, когда получено N байт
///                          (--save-files заменяет обычное получение файлов: без кэша, буфера и окошек)
///   --downloads DIR        куда «Загрузить» кладёт большие файлы (по умолчанию — DATA/Downloads, не настоящие Загрузки)
///   --auto-download        в окошке «Загрузить» нажать кнопку (настоящими событиями мыши, через секунду)
///   --auto-cancel N        через N секунд после начала загрузки нажать «Отмена»
///   --slow-files MS        после каждого полученного куска файла ждать MS мс (виден прогресс, успевает отмена)
///   --files-off-after N    через N секунд выключить «Передавать файлы», как переключателем в настройках
///   --toast-shots DIR      снимать окошко при каждой смене вида: DIR/NN-вид.png
///   --toast-hover N        окошки «Готово» и сообщения первые N секунд считаются «под мышью»
///   --toast-demo           показать все виды окошка без сети (снимки — в --toast-shots) и выйти
///   --appearance dark|light  оформление программы (для снимков)
/// Узел печатает также «INFO имя os=… form=… caps=…», «RENAMED старое новое», «IMAGE от размер sha256hex»,
/// «FILE_OFFER от id элементов байт»; сценарий файлов — «FILES_SENT получателей id элементов байт», «FILES_REFUSED код»,
/// «FILES_DONE id файлов байт мс», «FILES_FAILED id код». Файлы через буфер: «FILES_SENT …», «FILES_REFUSED код»,
/// «FILES_READY id элементов pasteboard|saved» (скачано; положено в буфер или нет — буфер уже сменился),
/// «FILES_FAILED id код»; окошко — «TOAST вид подробности» (offer id, progress id, done id pasteboard|saved,
/// failed код, notice код, hidden), «TOAST_CLICK кнопка ok|failed», «FILES_ENABLED 0».
@MainActor
enum TestHooks {
    static private(set) var enabled = false
    static private(set) var autoConfirm = false
    static private(set) var imagesEnabled = true
    static private(set) var filesEnabled = true
    private static var sendFiles: [String] = []
    private static var saveFiles: String?
    private static var saveDelay: Double = 0
    private static var cancelFilesAfter: Int64?
    private static var renameAfter: (seconds: Double, name: String)?
    static private(set) var dataDirectory: URL?
    static private(set) var pasteboardName: String?
    static private(set) var deviceName: String?
    private static var startPairing = false
    private static var pairWith: String?
    private static var pairAddress: String?
    private static var codeFile: String?
    private static var sendText: String?
    private static var sendDelay: Double = 0
    private static var exitAfter: Double?
    private static var probeWindow = false
    private static var probeShots: String?
    static private(set) var updateURL: URL?
    static private(set) var updatePublicKey: String?
    static private(set) var updateNow = false
    static private(set) var updateCheckDelay: Double?
    static private(set) var downloadsDirectory: URL?
    static private(set) var fileChunkDelay: Duration?
    private static var autoDownload = false
    private static var autoCancel: Double?
    private static var toastShots: String?
    private static var toastDemo = false
    private static var appearance: String?
    private static var filesOffAfter: Double?
    private static var toastHoverSeconds: Double?
    /// Окошко считается «под мышью» (--toast-hover).
    static private(set) var toastHovered = false
    private static var shotNumber = 0

    /// Папка для файлов режима проверки: --data или временная (настоящие папки пользователя не трогаются).
    static var scratchDirectory: URL {
        dataDirectory ?? FileManager.default.temporaryDirectory.appendingPathComponent("clipvey-test", isDirectory: true)
    }

    static func parse(_ arguments: [String]) {
        func value(_ flag: String) -> String? {
            guard let index = arguments.firstIndex(of: flag), index + 1 < arguments.count else { return nil }
            return arguments[index + 1]
        }
        enabled = arguments.contains("--test")
        guard enabled else { return }
        autoConfirm = arguments.contains("--auto-confirm")
        startPairing = arguments.contains("--pair")
        dataDirectory = value("--data").map { URL(fileURLWithPath: $0) }
        pasteboardName = value("--pasteboard")
        deviceName = value("--name")
        pairWith = value("--pair-with")
        pairAddress = value("--pair-address")
        codeFile = value("--code-file")
        sendText = value("--send")
        sendDelay = value("--send-delay").flatMap(Double.init) ?? 0
        exitAfter = value("--exit-after").flatMap(Double.init)
        probeWindow = arguments.contains("--probe-window")
        probeShots = value("--probe-shots")
        updateURL = value("--update-url").flatMap(URL.init(string:))
        updatePublicKey = value("--update-public-key")
        updateNow = arguments.contains("--update-now")
        updateCheckDelay = value("--update-check-delay").flatMap(Double.init)
        imagesEnabled = value("--images") != "off"
        filesEnabled = value("--files") != "off"
        if let index = arguments.firstIndex(of: "--send-files") {
            sendFiles = Array(arguments[(index + 1)...].prefix { !$0.hasPrefix("--") })
        }
        saveFiles = value("--save-files")
        saveDelay = value("--save-delay").flatMap(Double.init) ?? 0
        cancelFilesAfter = value("--cancel-files-after").flatMap { Int64($0) }
        downloadsDirectory = value("--downloads").map { URL(fileURLWithPath: $0, isDirectory: true) }
        fileChunkDelay = value("--slow-files").flatMap { Int($0) }.map { .milliseconds($0) }
        autoDownload = arguments.contains("--auto-download")
        autoCancel = value("--auto-cancel").flatMap(Double.init)
        toastShots = value("--toast-shots")
        toastDemo = arguments.contains("--toast-demo")
        appearance = value("--appearance")
        filesOffAfter = value("--files-off-after").flatMap(Double.init)
        toastHoverSeconds = value("--toast-hover").flatMap(Double.init)
        if let index = arguments.firstIndex(of: "--rename-after"), index + 2 < arguments.count,
           let seconds = Double(arguments[index + 1]) {
            renameAfter = (seconds, arguments[index + 2])
        }
    }

    static func emit(_ line: String) {
        print(line.replacingOccurrences(of: "\n", with: "\\n"))
        fflush(stdout)
    }

    static func emitIfEnabled(_ line: String) {
        if enabled {
            emit(line)
        }
    }

    /// До запуска узла: подписаться на события.
    static func prepare(_ node: ClipveyNode) {
        guard enabled else { return }
        node.onEvent = { emit($0) }
        if let saveFiles {
            // Вместо обычного получения (кэш, буфер, окошки): сценарии 24–25 проверяют сам узел.
            node.onFileOffer = { [weak node] offer, deviceID, _ in
                guard let node else { return }
                Task { await save(offer, from: deviceID, node: node, into: URL(fileURLWithPath: saveFiles)) }
            }
        }
    }

    /// --save-files: скачать описание целиком в DIR/<id>/ и напечатать итог.
    private static func save(_ offer: FileOffer, from deviceID: String, node: ClipveyNode, into directory: URL) async {
        try? await Task.sleep(for: .seconds(saveDelay))
        let started = Date()
        let limit = cancelFilesAfter
        let download = Task { @MainActor in
            try await node.downloadFiles(offer: offer, from: deviceID, to: IncomingCache.directory(root: directory, offerID: offer.id)) { total in
                if let limit, total >= limit {
                    cancelCurrentDownload?()
                }
            }
        }
        cancelCurrentDownload = { download.cancel() }
        do {
            _ = try await download.value
            emit("FILES_DONE \(offer.id) \(offer.fileCount) \(offer.total) \(Int(Date().timeIntervalSince(started) * 1000))")
        } catch {
            emit("FILES_FAILED \(offer.id) \(FileTransferFailure(of: error).code)")
        }
    }

    /// Отменить скачивание --save-files (для --cancel-files-after).
    private static var cancelCurrentDownload: (() -> Void)?

    /// --probe-window: открыть окошко значка, сходить в настройки и обратно, печатая положение окна
    /// («WINDOW шаг top=… height=… statusBottom=…»). Для проверки подгонки высоты без мыши.
    static func probe(_ model: AppModel) {
        guard enabled, probeWindow else { return }
        func report(_ step: String) {
            guard let controller = StatusBarController.shared else { return }
            let window = controller.window
            let statusBottom = NSApp.windows.first { String(describing: type(of: $0)).contains("StatusBarWindow") }?.frame.minY ?? -1
            emit("WINDOW \(step) visible=\(controller.isShown) x=\(Int(window.frame.minX)) top=\(Int(window.frame.maxY)) height=\(Int(window.frame.height)) statusBottom=\(Int(statusBottom))")
            // --probe-shots DIR: снимок области окна (с полями) — screencapture считает от левого верхнего угла.
            if let probeShots, let screenTop = NSScreen.screens.first?.frame.maxY {
                let rect = window.frame.insetBy(dx: -12, dy: -12)
                let task = Process()
                task.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
                task.arguments = ["-x", "-R\(Int(rect.minX)),\(Int(screenTop - rect.maxY)),\(Int(rect.width)),\(Int(rect.height))",
                                  "\(probeShots)/\(step).png"]
                try? task.run()
                task.waitUntilExit()
            }
        }
        Task {
            try? await Task.sleep(for: .seconds(1.5))
            StatusBarController.shared?.show()
            try? await Task.sleep(for: .seconds(1))
            report("открыто")
            // Каждое изменение рамки окна — чтобы увидеть промежуточные рывки при смене страницы.
            let start = Date()
            if let window = StatusBarController.shared?.window {
                for name in [NSWindow.didResizeNotification, NSWindow.didMoveNotification] {
                    NotificationCenter.default.addObserver(forName: name, object: window, queue: .main) { note in
                        guard let window = note.object as? NSWindow else { return }
                        emit("FRAME +\(Int(Date().timeIntervalSince(start) * 1000))мс top=\(Int(window.frame.maxY)) height=\(Int(window.frame.height))")
                    }
                }
            }
            for round in 1...2 {
                model.page = .settings
                try? await Task.sleep(for: .seconds(1))
                report("настройки\(round)")
                model.page = .main
                try? await Task.sleep(for: .seconds(1))
                report("главная\(round)")
            }
        }
    }

    /// После запуска узла: выполнить сценарий из флагов.
    static func run(_ node: ClipveyNode) {
        guard enabled else { return }
        if startPairing || pairWith != nil {
            node.startPairingMode()
        }
        if let pairWith, let address = pairAddress,
           let colon = address.lastIndex(of: ":"), let port = NWEndpoint.Port(String(address[address.index(after: colon)...])) {
            // Без поиска Bonjour: сразу по адресу (как --pair-address у clipvey-peer).
            let endpoint = NWEndpoint.hostPort(host: NWEndpoint.Host(String(address[..<colon])), port: port)
            emit("PAIRING_WITH \(pairWith)")
            node.pair(with: ClipveyNode.Candidate(id: "", name: pairWith, type: .unknown, endpoint: endpoint))
        } else if let pairWith {
            Task {
                for _ in 0..<600 {
                    if let candidate = node.candidates.first(where: { $0.name.localizedCaseInsensitiveContains(pairWith) }) {
                        emit("PAIRING_WITH \(candidate.name)")
                        node.pair(with: candidate)
                        return
                    }
                    try? await Task.sleep(for: .milliseconds(200))
                }
            }
        }
        if sendText != nil || !sendFiles.isEmpty {
            Task {
                while !node.devices.contains(where: \.connected) {
                    try? await Task.sleep(for: .milliseconds(200))
                }
                try? await Task.sleep(for: .seconds(sendDelay))
                if let sendText {
                    node.broadcast(sendText)
                    emit("SENT")
                }
                if !sendFiles.isEmpty {
                    switch await node.offerFiles(sendFiles.map { URL(fileURLWithPath: $0) }) {
                    case .offered(let offer, let recipients):
                        emit("FILES_SENT \(recipients) \(offer.id) \(offer.items.count) \(offer.total)")
                    case .failed(let failure):
                        emit("FILES_REFUSED \(failure.code)")
                    case .superseded:
                        emit("FILES_REFUSED Superseded")
                    }
                }
            }
        }
        if let renameAfter {
            Task {
                try? await Task.sleep(for: .seconds(renameAfter.seconds))
                node.setName(renameAfter.name)
                emit("NAME \(node.name)")
            }
        }
        if let exitAfter {
            Task {
                try? await Task.sleep(for: .seconds(exitAfter))
                emit("EXIT")
                NSApplication.shared.terminate(nil)
            }
        }
    }

    // MARK: - Файлы через буфер и окошко

    static func applyAppearance() {
        guard enabled, let appearance else { return }
        NSApp.appearance = NSAppearance(named: appearance == "dark" ? .darkAqua : .aqua)
    }

    /// После запуска: сценарии файлов, которым нужна модель (переключатель, демонстрация окошка).
    static func runFiles(_ model: AppModel) {
        guard enabled else { return }
        if let filesOffAfter {
            Task {
                try? await Task.sleep(for: .seconds(filesOffAfter))
                model.filesEnabled = false
                emit("FILES_ENABLED 0")
            }
        }
        if toastDemo, let transfers = model.transfers {
            Task { await demo(transfers) }
        }
    }

    /// Окошко показало новое: событие, снимок (--toast-shots), нажатие «Загрузить» (--auto-download).
    static func toastShown(_ toast: FileToast, controller: ToastController) {
        guard enabled else { return }
        let detail: String
        switch toast.content {
        case .offer, .downloading:
            detail = toast.offer?.id ?? "-"
        case .done(_, let inPasteboard):
            detail = "\(toast.offer?.id ?? "-") \(inPasteboard ? "pasteboard" : "saved")"
        case .failed(let problem), .notice(let problem):
            detail = problem.code
        }
        emit("TOAST \(toast.content.kind) \(detail)")
        if let toastShots, !toastDemo {
            shotNumber += 1
            let path = "\(toastShots)/\(String(format: "%02d", shotNumber))-\(toast.content.kind).png"
            Task {
                try? await Task.sleep(for: .milliseconds(700))
                capture(controller.panel.frame.insetBy(dx: -18, dy: -18), to: path)
            }
        }
        if autoDownload, case .offer = toast.content {
            Task {
                try? await Task.sleep(for: .seconds(1.2))
                guard controller.stage.toast === toast, case .offer = toast.content else { return }
                emit("TOAST_CLICK download \(click("download", in: controller) ? "ok" : "failed")")
            }
        }
        if let autoCancel, case .downloading = toast.content {
            Task {
                try? await Task.sleep(for: .seconds(autoCancel))
                guard controller.stage.toast === toast, case .downloading = toast.content else { return }
                emit("TOAST_CLICK cancel \(click("cancel", in: controller) ? "ok" : "failed")")
            }
        }
        if let toastHoverSeconds, toast.content.kind == "done" || toast.content.kind == "notice" {
            toastHovered = true
            Task {
                try? await Task.sleep(for: .seconds(toastHoverSeconds))
                toastHovered = false
                emit("TOAST_UNHOVER")
            }
        }
    }

    /// Нажать кнопку окошка настоящими событиями мыши (mouseDown и mouseUp в очередь программы): так проверяется,
    /// что кнопка срабатывает в неключевом окне неактивной программы. Рамку кнопки сообщает само окошко.
    @discardableResult
    static func click(_ name: String, in controller: ToastController) -> Bool {
        let panel = controller.panel
        guard panel.isVisible, let frame = controller.stage.buttonFrames[name], let content = panel.contentView else { return false }
        let point = NSPoint(x: frame.midX, y: content.bounds.height - frame.midY)
        for type in [NSEvent.EventType.leftMouseDown, .leftMouseUp] {
            guard let event = NSEvent.mouseEvent(
                with: type, location: point, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime,
                windowNumber: panel.windowNumber, context: nil, eventNumber: 0, clickCount: 1,
                pressure: type == .leftMouseDown ? 1 : 0) else { return false }
            NSApp.postEvent(event, atStart: false)
        }
        return true
    }

    /// Снимок области экрана (координаты Cocoa) — screencapture считает от левого верхнего угла главного экрана.
    static func capture(_ rect: NSRect, to path: String) {
        guard let screenTop = NSScreen.screens.first?.frame.maxY else { return }
        try? FileManager.default.createDirectory(at: URL(fileURLWithPath: path).deletingLastPathComponent(), withIntermediateDirectories: true)
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
        task.arguments = ["-x", "-R\(Int(rect.minX)),\(Int(screenTop - rect.maxY)),\(Int(rect.width)),\(Int(rect.height))", path]
        try? task.run()
        task.waitUntilExit()
        emit("SHOT \(path)")
    }

    /// --toast-demo: все виды окошка по очереди, без сети; снимки — в --toast-shots; затем выход.
    private static func demo(_ transfers: FileTransfers) async {
        try? await Task.sleep(for: .seconds(1))
        guard let controller = ToastController.shared else { return }
        func shot(_ name: String, withPanel: Bool = false) async {
            try? await Task.sleep(for: .milliseconds(900))
            guard let toastShots else { return }
            var rect = controller.panel.frame.insetBy(dx: -18, dy: -18)
            if withPanel, let status = StatusBarController.shared, status.isShown {
                rect = rect.union(status.window.frame.insetBy(dx: -18, dy: -18))
            }
            capture(rect, to: "\(toastShots)/demo-\(name).png")
        }
        let offer = FileOffer(id: "d0000000000000000000000000000001", items: [
            .file("Отчёт за 2025 год.pdf", size: 1_150_000_000),
            .directory("Фотографии"),
            .file("Фотографии/IMG_0001.HEIC", size: 50_000_000),
            .file("Презентация.key", size: 34_000_000),
        ], total: 1_234_000_000)
        let toast = FileToast(offer: offer, deviceID: nil, deviceName: "OFFICE-PC", content: .offer)
        transfers.pushForDemo(toast)
        await shot("01-offer")
        toast.setForDemo(.downloading, received: 340_000_000, speed: 45_300_000)
        await shot("02-progress")
        toast.setForDemo(.done(urls: [], inPasteboard: true))
        await shot("03-done")
        toast.setForDemo(.done(urls: [], inPasteboard: false))
        await shot("04-done-saved")
        toast.setForDemo(.failed(FileProblem(FileTransferError(.changed), deviceName: "OFFICE-PC", inDownloads: true)))
        await shot("05-failed-changed")
        toast.setForDemo(.failed(FileProblem(FileSaveError.noSpace(needed: 1_234_000_000, available: 310_000_000),
                                             deviceName: "OFFICE-PC", inDownloads: true)))
        await shot("06-failed-space")
        toast.setForDemo(.failed(FileProblem(FileSaveError.noAccess(""), deviceName: "OFFICE-PC", inDownloads: true)))
        await shot("07-failed-access")
        transfers.clearForDemo()
        transfers.pushForDemo(FileToast(offer: nil, deviceID: nil, deviceName: "", content: .notice(.sendFailed(.unreadable))))
        await shot("08-notice-access")
        transfers.clearForDemo()
        transfers.pushForDemo(FileToast(offer: nil, deviceID: nil, deviceName: "", content: .notice(.sendFailed(.tooLarge))))
        await shot("09-notice-large")
        transfers.clearForDemo()
        let receiveProblem = FileProblem(FileTransferError(.deviceUnavailable), deviceName: "OFFICE-PC", inDownloads: true)
        transfers.pushForDemo(FileToast(offer: nil, deviceID: nil, deviceName: "", content: .notice(receiveProblem.receiveNotice(from: "OFFICE-PC"))))
        await shot("10-notice-receive")
        transfers.clearForDemo()
        let long = FileOffer(id: "d0000000000000000000000000000002", items: [
            .file("Очень длинное название документа, которое не помещается в одну строку — версия 3 (финальная).docx", size: 81_000_000),
        ] + (1...12).map { .file("файл \($0).txt", size: 1_000_000) }, total: 93_000_000)
        transfers.pushForDemo(FileToast(offer: long, deviceID: nil, deviceName: "Компьютер-в-переговорной", content: .offer))
        await shot("11-offer-long")
        transfers.clearForDemo()
        let folder = FileOffer(id: "d0000000000000000000000000000003", items: [.directory("Проект"), .file("Проект/данные.bin", size: 734_000_000)], total: 734_000_000)
        let folderToast = FileToast(offer: folder, deviceID: nil, deviceName: "OFFICE-PC", content: .offer)
        transfers.pushForDemo(folderToast)
        await shot("12-offer-folder")
        StatusBarController.shared?.show()
        // Значок проверочного экземпляра может прятаться за «чёлкой», и окно значка встаёт у левого края.
        // Чтобы проверить окошко рядом с окном значка, окно значка ставится туда, где оно было бы у значка справа.
        if let status = StatusBarController.shared, let visible = status.menuBarScreen?.visibleFrame ?? NSScreen.main?.visibleFrame {
            status.window.setFrameOrigin(NSPoint(x: visible.maxX - StatusBarController.width - 90, y: status.window.frame.minY))
            status.onLayoutChange?()
        }
        await shot("13-with-panel", withPanel: true)
        folderToast.setForDemo(.downloading, received: 1_200_000, speed: 9_800_000)
        await shot("14-progress-with-panel", withPanel: true)
        StatusBarController.shared?.close()
        await shot("15-after-panel")
        transfers.clearForDemo()
        try? await Task.sleep(for: .seconds(1))
        emit("DEMO_DONE")
        NSApplication.shared.terminate(nil)
    }

    /// Роль I: взять код из файла, где его напечатал другой экземпляр.
    static func provideCode(_ submit: @escaping @MainActor (String) -> Void) {
        guard enabled, let codeFile else { return }
        Task {
            for _ in 0..<600 {
                if let text = try? String(contentsOfFile: codeFile, encoding: .utf8),
                   let match = text.firstMatch(of: /PAIRING_CODE (\d{6})/) {
                    submit(String(match.1))
                    return
                }
                try? await Task.sleep(for: .milliseconds(200))
            }
        }
    }
}
