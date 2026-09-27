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
/// Узел печатает также «INFO имя os=… form=… caps=…», «RENAMED старое новое», «IMAGE от размер sha256hex».
@MainActor
enum TestHooks {
    static private(set) var enabled = false
    static private(set) var autoConfirm = false
    static private(set) var imagesEnabled = true
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
    }

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
        if let sendText {
            Task {
                while !node.devices.contains(where: \.connected) {
                    try? await Task.sleep(for: .milliseconds(200))
                }
                try? await Task.sleep(for: .seconds(sendDelay))
                node.broadcast(sendText)
                emit("SENT")
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
