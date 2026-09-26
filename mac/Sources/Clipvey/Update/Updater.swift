import AppKit
import Foundation

/// Проверка и установка обновлений по docs/releases.md.
///
/// Проверка — через минуту после запуска и затем раз в сутки (время последней проверки хранится,
/// чтобы перезапуски не учащали проверки), если включена настройка «Проверять обновления автоматически».
/// Новая версия показывается полосой в окошке и точкой на значке; скачивается только после «Обновить».
/// Установка: SHA256SUMS с подписью → SHA-256 архива → ditto -x -k → проверка Clipvey.app →
/// отсоединённый /bin/sh ждёт выхода приложения, подменяет .app и открывает новый.
@MainActor
@Observable
final class Updater {
    static let shared = Updater()

    enum Phase: Equatable {
        case idle
        case checking
        case downloading
        case installing
    }

    /// Итог последнего действия пользователя, который нужно показать словами.
    enum Notice: Equatable {
        case upToDate
        case checkFailed
        case notBundled
        case notWritable(String)
        case downloadFailed
        case verificationFailed
        case badPackage
        case launchFailed

        @MainActor var text: String {
            switch self {
            case .upToDate:
                L("Установлена последняя версия", "You’re up to date")
            case .checkFailed:
                L("Не удалось проверить обновления", "Couldn’t check for updates")
            case .notBundled:
                L("Обновление устанавливается только в Clipvey.app", "Updates can only be installed into Clipvey.app")
            case .notWritable(let folder):
                L("Нет прав на запись в папку «\(folder)». Скачайте новую версию вручную.",
                  "No permission to write to “\(folder)”. Download the new version manually.")
            case .downloadFailed:
                L("Не удалось скачать обновление", "Couldn’t download the update")
            case .verificationFailed:
                L("Обновление не прошло проверку подписи и отменено", "The update failed the signature check and was cancelled")
            case .badPackage:
                L("Архив обновления повреждён, установка отменена", "The update archive is damaged; installation cancelled")
            case .launchFailed:
                L("Не удалось запустить установку", "Couldn’t start the installation")
            }
        }

        var isError: Bool { self != .upToDate }
    }

    /// Имена файлов релиза (docs/releases.md).
    private static let archiveName = "Clipvey-mac.zip"
    private static let sumsName = "SHA256SUMS"
    private static let signatureName = "SHA256SUMS.sig"
    private static let defaultURL = URL(string: "https://api.github.com/repos/lovipomidorku/Clipvey/releases/latest")!
    private static let checkInterval: TimeInterval = 24 * 60 * 60

    let currentVersion: ReleaseVersion?
    private(set) var phase: Phase = .idle
    /// Найденная новая версия (nil — не найдена или ещё не проверяли).
    private(set) var available: ReleaseInfo?
    /// «Позже»: полоса и точка на значке скрыты до следующей проверки.
    private(set) var dismissed = false
    private(set) var notice: Notice?

    var checksAutomatically: Bool {
        didSet { UpdateSettings.checksAutomatically = checksAutomatically }
    }

    /// Показывать полосу «Доступна версия» и точку на значке.
    var showsBanner: Bool { available != nil && !dismissed }

    @ObservationIgnored private var started = false
    @ObservationIgnored private lazy var network = UpdateNetwork(allowLoopbackHTTP: TestHooks.enabled)

    private init() {
        currentVersion = (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String).flatMap(ReleaseVersion.init)
        checksAutomatically = UpdateSettings.checksAutomatically
    }

    private var releaseURL: URL { TestHooks.updateURL ?? Self.defaultURL }
    private var publicKey: String { TestHooks.updatePublicKey ?? ReleaseVerifier.publicKeyBase64 }

    // MARK: - Расписание

    func start() {
        guard !started else { return }
        started = true
        guard let currentVersion else {
            Log.app.info("Обновления: нет версии приложения (запуск не из .app) — проверка отключена")
            return
        }
        Log.app.info("Обновления: текущая версия \(currentVersion.description, privacy: .public), адрес \(self.releaseURL.absoluteString, privacy: .public)")
        if TestHooks.updateNow {
            Task {
                await check(manual: true)
                if available != nil {
                    await install()
                }
            }
        }
        Task {
            try? await Task.sleep(for: .seconds(TestHooks.updateCheckDelay ?? 60))
            while true {
                if checksAutomatically && isDue {
                    await check(manual: false)
                }
                // Раз в час смотрим, не пора ли: так расписание переживает сон Mac и смену настройки.
                try? await Task.sleep(for: .seconds(60 * 60))
            }
        }
    }

    private var isDue: Bool {
        guard let last = UpdateSettings.lastCheck else { return true }
        let elapsed = Date().timeIntervalSince(last)
        // Часы переведены назад — считаем, что пора.
        return elapsed >= Self.checkInterval || elapsed < 0
    }

    // MARK: - Проверка

    /// manual — кнопка «Проверить сейчас»: итог показывается словами. Автоматическая проверка молчит при ошибках.
    func check(manual: Bool) async {
        guard let currentVersion, phase == .idle else { return }
        phase = .checking
        if manual {
            notice = nil
        }
        defer {
            if phase == .checking {
                phase = .idle
            }
        }
        UpdateSettings.lastCheck = Date()
        let kind = manual ? "ручная" : "автоматическая"
        do {
            let release = try await network.latestRelease(url: releaseURL, userAgent: "Clipvey/\(currentVersion)")
            if release.version > currentVersion {
                Log.app.info("Обновления (\(kind, privacy: .public)): доступна \(release.version.description, privacy: .public)")
                available = release
                // Каждая проверка (то есть раз в сутки) снова напоминает, даже если нажимали «Позже».
                dismissed = false
                TestHooks.emitIfEnabled("UPDATE_AVAILABLE \(release.version)")
            } else {
                Log.app.info("Обновления (\(kind, privacy: .public)): последняя версия \(release.version.description, privacy: .public), установлена \(currentVersion.description, privacy: .public)")
                available = nil
                if manual {
                    notice = .upToDate
                }
                TestHooks.emitIfEnabled("UPDATE_NONE \(release.version)")
            }
        } catch {
            Log.app.info("Обновления (\(kind, privacy: .public)): не удалось проверить — \(String(describing: error), privacy: .public)")
            if manual {
                notice = .checkFailed
            }
            TestHooks.emitIfEnabled("UPDATE_CHECK_FAILED \(manual ? "manual" : "auto") \(String(describing: error))")
        }
    }

    /// «Позже».
    func dismiss() {
        dismissed = true
    }

    // MARK: - Установка

    func install() async {
        guard let release = available, phase == .idle else { return }
        notice = nil

        let appURL = Bundle.main.bundleURL
        guard appURL.pathExtension == "app", Bundle.main.bundleIdentifier != nil else {
            fail(.notBundled, "приложение запущено не из .app (\(appURL.path))")
            return
        }
        let folder = appURL.deletingLastPathComponent()
        guard FileManager.default.isWritableFile(atPath: folder.path) else {
            fail(.notWritable(folder.path), "нет прав на запись в \(folder.path)")
            return
        }
        guard let archiveURL = release.assets[Self.archiveName],
              let sumsURL = release.assets[Self.sumsName],
              let signatureURL = release.assets[Self.signatureName] else {
            fail(.downloadFailed, "в релизе \(release.tag) нет \(Self.archiveName), \(Self.sumsName) или \(Self.signatureName)")
            return
        }

        phase = .downloading
        TestHooks.emitIfEnabled("UPDATE_DOWNLOADING \(release.version)")
        let work = FileManager.default.temporaryDirectory.appendingPathComponent("ClipveyUpdate-\(UUID().uuidString)")
        let newApp: URL
        do {
            try FileManager.default.createDirectory(at: work, withIntermediateDirectories: true)
            newApp = try await prepare(release: release, archive: archiveURL, sums: sumsURL, signature: signatureURL, in: work)
        } catch let error as UpdateFailure {
            try? FileManager.default.removeItem(at: work)
            phase = .idle
            fail(error.notice, error.detail)
            return
        } catch {
            try? FileManager.default.removeItem(at: work)
            phase = .idle
            fail(.downloadFailed, String(describing: error))
            return
        }

        phase = .installing
        do {
            try launchInstaller(currentApp: appURL, newApp: newApp, work: work)
        } catch {
            try? FileManager.default.removeItem(at: work)
            phase = .idle
            fail(.launchFailed, "не запустился скрипт установки: \(error.localizedDescription)")
            return
        }
        Log.app.info("Обновления: установка \(release.version.description, privacy: .public) — приложение завершается")
        TestHooks.emitIfEnabled("UPDATE_INSTALLING \(release.version)")
        NSApplication.shared.terminate(nil)
    }

    private func fail(_ notice: Notice, _ detail: String) {
        Log.app.error("Обновления: \(detail, privacy: .public)")
        self.notice = notice
        TestHooks.emitIfEnabled("UPDATE_FAILED \(detail)")
    }

    /// Скачать и проверить файлы, распаковать архив. Возвращает проверенный Clipvey.app во временной папке.
    private func prepare(release: ReleaseInfo, archive: URL, sums: URL, signature: URL, in work: URL) async throws -> URL {
        let sumsData: Data
        let signatureData: Data
        let archiveData: Data
        do {
            sumsData = try await network.download(sums)
            signatureData = try await network.download(signature)
            archiveData = try await network.download(archive)
        } catch {
            throw UpdateFailure(.downloadFailed, "скачивание: \(error)")
        }
        do {
            let checksums = try ReleaseVerifier.verifiedChecksums(sums: sumsData, signature: signatureData, publicKeyBase64: publicKey)
            try ReleaseVerifier.verifyFile(archiveData, name: Self.archiveName, checksums: checksums)
        } catch {
            throw UpdateFailure(.verificationFailed, "проверка: \(error)")
        }
        Log.app.info("Обновления: подпись и SHA-256 \(Self.archiveName, privacy: .public) верны")

        let zip = work.appendingPathComponent(Self.archiveName)
        let unpacked = work.appendingPathComponent("unpacked")
        try archiveData.write(to: zip)
        let status = await Self.run("/usr/bin/ditto", ["-x", "-k", zip.path, unpacked.path])
        try? FileManager.default.removeItem(at: zip)
        guard status == 0 else {
            throw UpdateFailure(.badPackage, "ditto -x -k завершился с кодом \(status)")
        }

        let app = unpacked.appendingPathComponent("Clipvey.app")
        let info = NSDictionary(contentsOf: app.appendingPathComponent("Contents/Info.plist"))
        let identifier = info?["CFBundleIdentifier"] as? String
        let version = info?["CFBundleShortVersionString"] as? String
        let executable = (info?["CFBundleExecutable"] as? String).map { app.appendingPathComponent("Contents/MacOS/\($0)") }
        guard let identifier, identifier == Bundle.main.bundleIdentifier else {
            throw UpdateFailure(.badPackage, "в архиве не тот Clipvey.app: CFBundleIdentifier \(identifier ?? "нет")")
        }
        guard version == release.version.description else {
            throw UpdateFailure(.badPackage, "версия Clipvey.app в архиве \(version ?? "нет"), а тег \(release.tag)")
        }
        guard let executable, FileManager.default.isExecutableFile(atPath: executable.path) else {
            throw UpdateFailure(.badPackage, "в Clipvey.app нет исполняемого файла")
        }
        return app
    }

    /// Отсоединённый /bin/sh: ждёт выхода этого процесса, подменяет .app и открывает новый.
    private func launchInstaller(currentApp: URL, newApp: URL, work: URL) throws {
        let script = work.appendingPathComponent("install.sh")
        try Self.installScript.write(to: script, atomically: true, encoding: .utf8)
        var arguments = [script.path, String(ProcessInfo.processInfo.processIdentifier), currentApp.path, newApp.path, work.path]
        // В режиме проверки новая копия запускается с теми же флагами (своя папка данных и буфер),
        // иначе она взяла бы настоящие данные пользователя.
        if TestHooks.enabled {
            arguments += CommandLine.arguments.dropFirst().filter { $0 != "--update-now" }
        }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/sh")
        process.arguments = arguments
        process.standardInput = FileHandle.nullDevice
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try process.run()
    }

    /// $1 — PID, $2 — текущий .app, $3 — новый .app, $4 — временная папка, дальше — флаги для запуска (режим проверки).
    private static let installScript = #"""
    #!/bin/sh
    # Установка обновления Clipvey (запускается приложением, работает после его выхода).
    pid="$1"; old="$2"; new="$3"; work="$4"; shift 4
    log() { /usr/bin/logger -t clipvey-update "$1"; }
    case "$old" in
        *.app) ;;
        *) log "не .app: $old"; exit 1 ;;
    esac
    if [ ! -d "$old" ] || [ ! -d "$new" ]; then
        log "нет $old или $new"
        exit 1
    fi
    # Ждём выхода приложения, не дольше 30 с.
    n=0
    while kill -0 "$pid" 2>/dev/null; do
        n=$((n + 1))
        if [ "$n" -gt 300 ]; then
            log "приложение не завершилось"
            rm -rf "$work"
            exit 1
        fi
        sleep 0.1
    done
    backup="$old.update-old-$$"
    if ! mv "$old" "$backup"; then
        log "не удалось переименовать $old"
        rm -rf "$work"
        open "$old"
        exit 1
    fi
    if mv "$new" "$old"; then
        rm -rf "$backup"
        log "установлено: $old"
    else
        log "не удалось переместить новую версию, возвращаю старую"
        rm -rf "$old"
        mv "$backup" "$old"
    fi
    /usr/bin/xattr -dr com.apple.quarantine "$old" 2>/dev/null
    rm -rf "$work"
    if [ "$#" -gt 0 ]; then
        open -n "$old" --args "$@"
    else
        open "$old"
    fi
    """#

    private static func run(_ tool: String, _ arguments: [String]) async -> Int32 {
        await Task.detached {
            let process = Process()
            process.executableURL = URL(fileURLWithPath: tool)
            process.arguments = arguments
            process.standardInput = FileHandle.nullDevice
            process.standardOutput = FileHandle.nullDevice
            process.standardError = FileHandle.nullDevice
            do {
                try process.run()
            } catch {
                return -1
            }
            process.waitUntilExit()
            return process.terminationStatus
        }.value
    }
}

private struct UpdateFailure: Error {
    let notice: Updater.Notice
    let detail: String

    init(_ notice: Updater.Notice, _ detail: String) {
        self.notice = notice
        self.detail = detail
    }
}

/// Настройки обновлений. В режиме проверки — только в памяти: у копии тот же Bundle ID,
/// и UserDefaults.standard — настоящие настройки пользователя.
@MainActor
enum UpdateSettings {
    private static var memoryAutomatic = true
    private static var memoryLastCheck: Date?

    static var checksAutomatically: Bool {
        get { TestHooks.enabled ? memoryAutomatic : UserDefaults.standard.object(forKey: "checkUpdatesAutomatically") as? Bool ?? true }
        set {
            if TestHooks.enabled {
                memoryAutomatic = newValue
            } else {
                UserDefaults.standard.set(newValue, forKey: "checkUpdatesAutomatically")
            }
        }
    }

    static var lastCheck: Date? {
        get { TestHooks.enabled ? memoryLastCheck : UserDefaults.standard.object(forKey: "lastUpdateCheck") as? Date }
        set {
            if TestHooks.enabled {
                memoryLastCheck = newValue
            } else {
                UserDefaults.standard.set(newValue, forKey: "lastUpdateCheck")
            }
        }
    }
}
