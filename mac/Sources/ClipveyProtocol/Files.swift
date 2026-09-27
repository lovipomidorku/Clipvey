import Foundation

/// Файлы и папки по docs/protocol.md, «Файлы»: описание, проверки, двоичные куски, имена на диске.
/// Совпадает с Clipvey.Core (Files.cs); проверяется clipvey-checks по tests/vectors.json.

/// Элемент описания: файл (size ≥ 0) или папка (size = nil).
public struct FileItem: Equatable, Sendable {
    /// Относительный путь, части через «/».
    public let path: String
    /// Размер файла в байтах; nil — папка.
    public let size: Int64?

    public init(path: String, size: Int64?) {
        self.path = path
        self.size = size
    }

    public static func file(_ path: String, size: Int64) -> FileItem { FileItem(path: path, size: size) }
    public static func directory(_ path: String) -> FileItem { FileItem(path: path, size: nil) }

    public var isDirectory: Bool { size == nil }
}

/// Описание скопированных файлов и папок (file_offer). Создаётся только проверенным: parse и FileTree.
public struct FileOffer: Equatable, Sendable, Identifiable {
    public let id: String
    /// В порядке обхода: папка раньше своего содержимого.
    public let items: [FileItem]
    /// Сумма размеров файлов.
    public let total: Int64

    /// Без проверок — для своих описаний (FileTree уже проверил) и для проверок протокола.
    public init(id: String, items: [FileItem], total: Int64) {
        self.id = id
        self.items = items
        self.total = total
    }

    /// Сколько в описании файлов (без папок).
    public var fileCount: Int { items.lazy.filter { !$0.isDirectory }.count }

    /// Элементы верхнего уровня — то, что скопировал пользователь.
    public var topLevel: [FileItem] { items.filter { !$0.path.contains("/") } }

    /// Почему описание нельзя принять (для журнала, по-русски); nil — можно. Правила — docs/protocol.md, «Описание».
    public static func refusal(id: String, items: [FileItem], total: Int64) -> String? {
        guard isValidID(id) else { return "id — не 32 шестнадцатеричных символа" }
        guard !items.isEmpty else { return "пустой список элементов" }
        guard items.count <= ProtocolLimits.maxFileItems else { return "элементов больше \(ProtocolLimits.maxFileItems)" }
        /// Путь → папка ли это.
        var seen: [String: Bool] = [:]
        seen.reserveCapacity(items.count)
        var sum: Int64 = 0
        for item in items {
            if let problem = FileNames.pathProblem(item.path) {
                return "путь «\(item.path)»: \(problem)"
            }
            guard seen[item.path] == nil else { return "путь «\(item.path)» повторяется" }
            if let slash = item.path.lastIndex(of: "/") {
                let parent = String(item.path[..<slash])
                guard seen[parent] == true else { return "у «\(item.path)» нет родительской папки выше по списку" }
            }
            if let size = item.size {
                guard size >= 0 else { return "размер «\(item.path)» меньше нуля" }
                sum += size
                guard sum <= ProtocolLimits.maxFileTotalBytes else { return "всего больше 10 ГиБ" }
            }
            seen[item.path] = item.isDirectory
        }
        guard total == sum else { return "total \(total) не равен сумме размеров \(sum)" }
        return nil
    }

    /// id — 16 байт в hex строчными (32 символа): он становится именем папки (Incoming/<id>).
    public static func isValidID(_ id: String) -> Bool {
        id.utf8.count == 32 && id.utf8.allSatisfy { (0x30...0x39).contains($0) || (0x61...0x66).contains($0) }
    }

    /// Разбор полей file_offer с проверками. Ошибка — описание пропускается целиком (сеанс продолжается).
    static func parse(_ object: [String: Any]) -> Result<FileOffer, FileMessageError> {
        guard let id = object["id"] as? String else { return .failure(FileMessageError("нет id")) }
        guard let rawItems = object["items"] as? [Any] else { return .failure(FileMessageError("нет items")) }
        guard rawItems.count <= ProtocolLimits.maxFileItems else {
            return .failure(FileMessageError("элементов больше \(ProtocolLimits.maxFileItems)"))
        }
        var items: [FileItem] = []
        items.reserveCapacity(rawItems.count)
        for raw in rawItems {
            guard let entry = raw as? [String: Any], let path = entry["path"] as? String else {
                return .failure(FileMessageError("элемент без пути"))
            }
            if isTrue(entry["dir"]) {
                guard entry["size"] == nil else { return .failure(FileMessageError("у папки «\(path)» есть size")) }
                items.append(.directory(path))
            } else {
                guard let size = JSONNumbers.integer(entry["size"]), size >= 0 else {
                    return .failure(FileMessageError("у файла «\(path)» нет целого size ≥ 0"))
                }
                items.append(.file(path, size: size))
            }
        }
        guard let total = JSONNumbers.integer(object["total"]) else { return .failure(FileMessageError("нет целого total")) }
        if let refusal = refusal(id: id, items: items, total: total) {
            return .failure(FileMessageError(refusal))
        }
        return .success(FileOffer(id: id, items: items, total: total))
    }

    func jsonItems() -> [[String: Any]] {
        items.map { item in
            if let size = item.size {
                return ["path": item.path, "size": size]
            }
            return ["path": item.path, "dir": true]
        }
    }

    private static func isTrue(_ value: Any?) -> Bool {
        guard let number = value as? NSNumber, CFGetTypeID(number) == CFBooleanGetTypeID() else { return false }
        return number.boolValue
    }
}

/// Сообщение о файлах не прошло проверку: пропускается и пишется в журнал, сеанс продолжается.
public struct FileMessageError: Error, Equatable, Sendable {
    public let reason: String

    public init(_ reason: String) {
        self.reason = reason
    }
}

/// Целые числа из JSONSerialization: дробное, строка, логическое или отсутствие — nil.
enum JSONNumbers {
    static func integer(_ value: Any?) -> Int64? {
        guard let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID() else { return nil }
        let double = number.doubleValue
        guard double == double.rounded(), abs(double) < 9e15 else { return nil }
        return number.int64Value
    }
}

/// Двоичный кусок файла: открытый текст шифрованного кадра `00 ‖ big-endian uint32 req ‖ данные`.
public enum FileChunk {
    /// Байт 0x00 и req.
    public static let headerBytes = 5

    /// Заголовок куска.
    public static func header(req: UInt32) -> Data {
        var header = Data(count: headerBytes)
        writeHeader(req: req, into: &header)
        return header
    }

    /// Записать заголовок в первые 5 байт буфера (буфер уже содержит место под них).
    public static func writeHeader(req: UInt32, into buffer: inout Data) {
        let base = buffer.startIndex
        buffer[base] = 0
        buffer[base + 1] = UInt8(truncatingIfNeeded: req >> 24)
        buffer[base + 2] = UInt8(truncatingIfNeeded: req >> 16)
        buffer[base + 3] = UInt8(truncatingIfNeeded: req >> 8)
        buffer[base + 4] = UInt8(truncatingIfNeeded: req)
    }

    /// Открытый текст куска целиком (с копированием данных; для проверок и редких случаев).
    public static func plaintext(req: UInt32, data: Data) -> Data {
        var result = Data(capacity: headerBytes + data.count)
        result.append(header(req: req))
        result.append(data)
        return result
    }

    /// Разбор открытого текста, который начинается с 0x00. Данные — срез без копирования.
    /// Ошибка — кадр пропускается (запрос, если он известен, считается неудачным).
    public static func parse(_ plaintext: Data) -> Result<(req: UInt32, data: Data), FileMessageError> {
        let base = plaintext.startIndex
        guard plaintext.count > headerBytes, plaintext[base] == 0 else {
            return .failure(FileMessageError("двоичный кадр короче \(headerBytes + 1) байт"))
        }
        let req = UInt32(plaintext[base + 1]) << 24 | UInt32(plaintext[base + 2]) << 16
            | UInt32(plaintext[base + 3]) << 8 | UInt32(plaintext[base + 4])
        let data = plaintext[(base + headerBytes)...]
        guard data.count <= ProtocolLimits.fileChunkBytes else {
            return .failure(FileMessageError("кусок \(data.count) байт больше 1 МиБ"))
        }
        return .success((req, data))
    }
}

/// Имена файлов: что отправлять в описании и как назвать полученное на диске.
/// Не часть протокола (docs/protocol.md, «Поведение сторон»), но одинаково на Mac и Windows.
public enum FileNames {
    /// Что не так с путём из описания; nil — всё в порядке.
    public static func pathProblem(_ path: String) -> String? {
        guard path.utf8.count <= ProtocolLimits.maxFilePathBytes else { return "длиннее \(ProtocolLimits.maxFilePathBytes) байт" }
        for part in path.split(separator: "/", omittingEmptySubsequences: false) {
            if let problem = nameProblem(part) {
                return problem
            }
        }
        return nil
    }

    /// Что не так с частью пути; nil — всё в порядке.
    public static func nameProblem<S: StringProtocol>(_ name: S) -> String? {
        if name.isEmpty { return "пустая часть" }
        if name == "." || name == ".." { return "часть «\(name)»" }
        if name.utf8.count > ProtocolLimits.maxFileNameBytes { return "часть длиннее \(ProtocolLimits.maxFileNameBytes) байт" }
        if name.utf8.contains(where: { $0 == 0x2F || $0 == 0x5C || $0 == 0x3A || $0 == 0 }) { return "недопустимый символ в «\(name)»" }
        return nil
    }

    /// Имя для описания: NFC (на Mac имена бывают в NFD), «\», «:» и символ 0 — на «_».
    /// «:» в пути Mac — это «/» в имени, которое видно в Finder.
    public static func wireName(_ name: String) -> String {
        let normalized = name.precomposedStringWithCanonicalMapping
        guard normalized.unicodeScalars.contains(where: { $0 == "\\" || $0 == ":" || $0 == "\0" || $0 == "/" }) else {
            return normalized
        }
        return String(String.UnicodeScalarView(normalized.unicodeScalars.map {
            $0 == "\\" || $0 == ":" || $0 == "\0" || $0 == "/" ? "_" : $0
        }))
    }

    /// Имя с номером: «отчёт (2).pdf», «Папка (3)». Расширение — после последней точки, если она не первая.
    public static func numbered(_ name: String, _ number: Int, isDirectory: Bool) -> String {
        if !isDirectory, let dot = name.lastIndex(of: "."), dot != name.startIndex {
            return "\(name[..<dot]) (\(number))\(name[dot...])"
        }
        return "\(name) (\(number))"
    }

    /// Пути на диске для элементов описания (относительные, через «/»), по порядку items.
    /// windows = true — дополнительно заменить то, что нельзя в именах Windows (docs/protocol.md, «Поведение сторон»).
    /// Совпадающие без учёта регистра имена в одной папке получают номер: «a (2).txt».
    public static func localPaths(_ items: [FileItem], windows: Bool) -> [String] {
        var mapped: [String: String] = [:]
        var used: [String: Set<String>] = [:]
        var result: [String] = []
        result.reserveCapacity(items.count)
        for item in items {
            let slash = item.path.lastIndex(of: "/")
            let parent = slash.map { String(item.path[..<$0]) } ?? ""
            let name = slash.map { String(item.path[item.path.index(after: $0)...]) } ?? item.path
            let localParent = parent.isEmpty ? "" : (mapped[parent] ?? parent)
            var local = windows ? windowsName(name) : name
            var taken = used[localParent] ?? []
            var number = 2
            let base = local
            while taken.contains(local.lowercased()) {
                local = numbered(base, number, isDirectory: item.isDirectory)
                number += 1
            }
            taken.insert(local.lowercased())
            used[localParent] = taken
            let path = localParent.isEmpty ? local : "\(localParent)/\(local)"
            mapped[item.path] = path
            result.append(path)
        }
        return result
    }

    /// Имя, допустимое в Windows: < > " | ? * и управляющие символы — на «_», точка или пробел в конце — на «_»,
    /// зарезервированные имена устройств (CON, NUL, COM1…) — с «_» в начале.
    public static func windowsName(_ name: String) -> String {
        var scalars = name.unicodeScalars.map { scalar -> Unicode.Scalar in
            scalar.value < 0x20 || "<>\"|?*".unicodeScalars.contains(scalar) ? "_" : scalar
        }
        var index = scalars.count - 1
        while index >= 0, scalars[index] == "." || scalars[index] == " " {
            scalars[index] = "_"
            index -= 1
        }
        var result = String(String.UnicodeScalarView(scalars))
        let stem = (result.split(separator: ".", maxSplits: 1, omittingEmptySubsequences: false).first.map(String.init) ?? result)
            .trimmingCharacters(in: .whitespaces).uppercased()
        let reserved: Set<String> = ["CON", "PRN", "AUX", "NUL"]
        let numbered = (stem.hasPrefix("COM") || stem.hasPrefix("LPT")) && stem.count == 4
            && ("1"..."9").contains(String(stem.last!))
        if reserved.contains(stem) || numbered {
            result = "_" + result
        }
        return result
    }
}

/// Почему не удалось отправить описание. Код совпадает с FileOfferFailure в C#.
public enum FileOfferFailure: String, Error, Sendable, CaseIterable {
    /// Нечего отправлять: ничего не выбрано или всё пропущено (ссылки, особые файлы).
    case empty
    /// Больше 10 000 элементов.
    case tooManyItems
    /// Больше 10 ГиБ.
    case tooLarge
    /// Имя длиннее 255 байт UTF-8 или путь длиннее 1024 байт.
    case nameTooLong
    /// Не удалось прочитать папку или свойства файла.
    case unreadable
    /// «Передавать файлы» выключено.
    case disabled

    /// Машинное имя для журнала и событий самопроверки.
    public var code: String {
        switch self {
        case .empty: "Empty"
        case .tooManyItems: "TooManyItems"
        case .tooLarge: "TooLarge"
        case .nameTooLong: "NameTooLong"
        case .unreadable: "Unreadable"
        case .disabled: "Disabled"
        }
    }
}

/// Почему не удалось скачать. Код для интерфейса, совпадает с FileTransferFailure в C#.
public enum FileTransferFailure: String, Sendable, CaseIterable {
    /// Нет сеанса с устройством-источником или он оборвался: «устройство недоступно».
    case deviceUnavailable
    /// Источник не знает описание или элемент (устарело — больше часа, или там выключены файлы).
    case notFound
    /// Файл на источнике изменился или пропал после копирования.
    case changed
    /// Источник не смог прочитать файл.
    case unavailable
    /// Скачивание отменено.
    case cancelled
    /// Не удалось записать на диск.
    case writeFailed
    /// Данные пришли не те, что обещаны (не та длина и т. п.).
    case protocolError

    public var code: String {
        switch self {
        case .deviceUnavailable: "DeviceUnavailable"
        case .notFound: "NotFound"
        case .changed: "Changed"
        case .unavailable: "Unavailable"
        case .cancelled: "Cancelled"
        case .writeFailed: "WriteFailed"
        case .protocolError: "ProtocolError"
        }
    }

    /// По reason из file_error; незнакомая причина — unavailable.
    public init(peerReason reason: String) {
        switch reason {
        case "not_found": self = .notFound
        case "changed": self = .changed
        default: self = .unavailable
        }
    }

    /// Код для любой ошибки скачивания: CancellationError — отмена, прочее не FileTransferError — нет соединения.
    public init(of error: Error) {
        if let error = error as? FileTransferError {
            self = error.failure
        } else if error is CancellationError {
            self = .cancelled
        } else {
            self = .deviceUnavailable
        }
    }
}

/// Ошибка скачивания: код для интерфейса и подробность для журнала (по-русски).
public struct FileTransferError: LocalizedError, Sendable {
    public let failure: FileTransferFailure
    public let detail: String

    public init(_ failure: FileTransferFailure, _ detail: String = "") {
        self.failure = failure
        self.detail = detail
    }

    public var errorDescription: String? {
        let base: String = switch failure {
        case .deviceUnavailable: "Устройство недоступно"
        case .notFound: "На другом устройстве этих файлов уже нет"
        case .changed: "Файл изменился после копирования"
        case .unavailable: "Другое устройство не смогло прочитать файл"
        case .cancelled: "Скачивание отменено"
        case .writeFailed: "Не удалось записать файл"
        case .protocolError: "Получены не те данные"
        }
        return detail.isEmpty ? base : "\(base): \(detail)"
    }
}

/// Кэш полученных файлов: <root>/<id>/ (docs/protocol.md, «Поведение сторон»). Папку root выбирает приложение.
public enum IncomingCache {
    /// Сколько хранить: старше суток удаляются.
    public static let maxAge: TimeInterval = 24 * 60 * 60

    /// Папка для описания id. id проверен при разборе (32 символа hex), так что за пределы root не выйти.
    public static func directory(root: URL, offerID: String) -> URL {
        root.appendingPathComponent(offerID, isDirectory: true)
    }

    /// Удалить из root всё, что изменялось раньше, чем maxAge назад. Возвращает, сколько удалено.
    @discardableResult
    public static func clean(root: URL, maxAge: TimeInterval = maxAge, now: Date = Date()) -> Int {
        let keys: [URLResourceKey] = [.contentModificationDateKey, .creationDateKey]
        guard let entries = try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: keys) else { return 0 }
        var removed = 0
        for entry in entries {
            let values = try? entry.resourceValues(forKeys: Set(keys))
            let date = max(values?.contentModificationDate ?? .distantPast, values?.creationDate ?? .distantPast)
            if now.timeIntervalSince(date) > maxAge, (try? FileManager.default.removeItem(at: entry)) != nil {
                removed += 1
            }
        }
        return removed
    }
}
