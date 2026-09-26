import AppKit

/// Самопроверка вместе с консольным clipvey-peer. Включается флагами, в обычной работе выключена.
///   --test                 печатать события в stdout («READY …», «CONNECTED …», «CLIP …»)
///   --data DIR             папка с ключом и списком устройств вместо ~/Library/Application Support/Clipvey
///   --pasteboard NAME      работать с именованным буфером вместо общего (macOS не спрашивает разрешения)
///   --name NAME            имя устройства вместо имени Mac (чтобы не путать с настоящим Clipvey)
///   --pair                 открыть режим связывания
///   --auto-confirm         в роли R нажать «Готово», когда другая сторона подтвердит код
///   --pair-with TEXT       в роли I связаться с устройством, в имени которого есть TEXT
///   --code-file FILE       взять код для роли I из файла со строкой «PAIRING_CODE 123456»
///   --send TEXT            после подключения отправить текст (--send-delay N — подождать ещё N с)
///   --exit-after N         выйти через N секунд
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
    private static var codeFile: String?
    private static var sendText: String?
    private static var sendDelay: Double = 0
    private static var exitAfter: Double?

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
        codeFile = value("--code-file")
        sendText = value("--send")
        sendDelay = value("--send-delay").flatMap(Double.init) ?? 0
        exitAfter = value("--exit-after").flatMap(Double.init)
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

    /// До запуска узла: подписаться на события.
    static func prepare(_ node: ClipveyNode) {
        guard enabled else { return }
        node.onEvent = { emit($0) }
    }

    /// После запуска узла: выполнить сценарий из флагов.
    static func run(_ node: ClipveyNode) {
        guard enabled else { return }
        if startPairing || pairWith != nil {
            node.startPairingMode()
        }
        if let pairWith {
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
