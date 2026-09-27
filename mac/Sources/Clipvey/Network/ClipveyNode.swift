import CryptoKit
import Foundation
import Network
import Observation

/// Узел Clipvey:
/// - слушает TCP и объявляет себя через Bonjour, ищет другие устройства;
/// - держит с каждым связанным включённым устройством не больше одного сеанса;
/// - пересылает фрагменты буфера (текст и картинки) между устройствами;
/// - передаёт файлы по запросу (NodeFiles.swift);
/// - ведёт связывание в обеих ролях;
/// - держит общие настройки (docs/protocol.md, «Общие настройки») в согласии со всеми устройствами.
/// Правила — docs/protocol.md; логика совпадает с Clipvey.Core на C#.
/// Интерфейсу узел отдаёт коды (FailureReason, PairingResult), а не готовые строки.
@MainActor
@Observable
final class ClipveyNode {
    struct DeviceStatus: Identifiable, Equatable {
        let id: String
        /// Имя, которое устройство сообщает о себе.
        let name: String
        /// Локальный псевдоним (setAlias); nil — нет.
        let alias: String?
        /// os и form, если устройство их сообщало.
        let type: DeviceType
        let enabled: Bool
        let connected: Bool
        /// Есть сеанс, и в его caps есть image: картинки этому устройству отправляются.
        let acceptsImages: Bool
        /// Есть сеанс, и в его caps есть file: описания файлов этому устройству отправляются.
        let acceptsFiles: Bool
        /// Почему не удаётся подключиться (если известно). Текст подбирает интерфейс.
        let problem: FailureReason?

        /// Что показывать: псевдоним или имя.
        var displayName: String { alias ?? name }
    }

    struct Candidate: Identifiable, Equatable {
        let id: String
        let name: String
        let type: DeviceType
        let endpoint: NWEndpoint
    }

    /// Входящее связывание (роль R): этот Mac показывает код.
    struct IncomingPairing: Equatable {
        let peerName: String
        let peerType: DeviceType
        var code: String?
        var verified = false
    }

    /// Исходящее связывание (роль I): пользователь вводит код с экрана другого устройства.
    enum OutgoingPairing: Equatable {
        case connecting(String)
        case enterCode(String)
        case waitingConfirmation(String)
    }

    /// Итог последнего связывания.
    enum PairingResult: Equatable {
        case paired(name: String)
        case failed(FailureReason)
    }

    static let serviceType = "_clipvey._tcp"
    static let defaultPort: UInt16 = 48620
    private static let maxClipBytes = ProtocolLimits.maxClipBytes
    private static let maxHops = ProtocolLimits.maxHops
    private static let seenClipsCapacity = ProtocolLimits.seenCapacity
    /// Сколько картинок может ждать отправки в одном сеансе; при переполнении отбрасывается самая старая.
    private static let maxQueuedImages = 3
    private static let pairingDuration: TimeInterval = 120
    private static let secondaryConnectDelay: TimeInterval = 5
    private static let rejectedRetryDelay: TimeInterval = 30

    private(set) var devices: [DeviceStatus] = []
    private(set) var isPairingMode = false
    private(set) var candidates: [Candidate] = []
    private(set) var incoming: IncomingPairing?
    private(set) var outgoing: OutgoingPairing?
    /// Итог последнего связывания: успех или причина неудачи.
    private(set) var pairingResult: PairingResult?
    private(set) var port: UInt16?

    let identity: DeviceIdentity
    /// Своё имя: TXT, имя экземпляра Bonjour, pair_*, ready и info. Меняется через setName.
    private(set) var name: String
    /// Свои os и form (задаёт приложение).
    let deviceType: DeviceType
    /// «Передавать картинки»: заявлять image в caps, принимать и отправлять картинки. Меняется через setImagesEnabled.
    private(set) var imagesEnabled: Bool
    /// «Передавать файлы»: заявлять file в caps, принимать и отправлять описания файлов. Меняется через setFilesEnabled
    /// (NodeFiles.swift; поэтому не private(set)).
    var filesEnabled: Bool
    /// Общие настройки: меняются через setAutoDownloadMB или приходят более новые с другого устройства.
    /// Хранит их приложение (onSettingsChanged).
    private(set) var sharedSettings: SharedSettings

    /// Пришёл текст с другого устройства (переводы строк — \n) и имя устройства, от которого он пришёл.
    @ObservationIgnored var onClipReceived: ((_ text: String, _ from: String) -> Void)?
    /// Пришла картинка (image/png или image/jpeg, sha256 проверен) и имя устройства, от которого она пришла.
    @ObservationIgnored var onImageReceived: ((_ data: Data, _ mime: String, _ from: String) -> Void)?
    /// Пришло описание файлов: описание, deviceID источника (для downloadFiles) и имя устройства (псевдоним, если задан).
    /// Описание — новое содержимое буфера, как текст.
    @ObservationIgnored var onFileOffer: ((_ offer: FileOffer, _ deviceID: String, _ from: String) -> Void)?
    /// Изменилось число подключённых устройств.
    @ObservationIgnored var onConnectionsChanged: ((Int) -> Void)?
    /// Общие настройки изменились (здесь или пришли новее): приложение сохраняет их целиком, все три поля.
    @ObservationIgnored var onSettingsChanged: ((SharedSettings) -> Void)?
    /// Для самопроверки: события строками вида «CONNECTED имя».
    @ObservationIgnored var onEvent: ((String) -> Void)?

    @ObservationIgnored let store: DeviceStore
    @ObservationIgnored private var listener: NWListener?
    @ObservationIgnored private var listenerUsesDefaultPort = true
    @ObservationIgnored private var browser: NWBrowser?
    @ObservationIgnored var sessions: [String: ActiveSession] = [:]
    /// Свои описания файлов: id → описание и локальные файлы (NodeFiles.swift).
    @ObservationIgnored var fileOffers: [String: OfferRecord] = [:]
    @ObservationIgnored var latestFileOfferID: String?
    @ObservationIgnored private var browseResults: [String: BrowseResult] = [:]
    @ObservationIgnored private var disconnectedSince: [String: Date] = [:]
    @ObservationIgnored private var retryAfter: [String: Date] = [:]
    @ObservationIgnored private var problems: [String: FailureReason] = [:]
    @ObservationIgnored private var connecting: Set<String> = []
    @ObservationIgnored private var seenClips: Set<String> = []
    @ObservationIgnored private var seenOrder: [String] = []
    /// Когда закроется режим связывания; вместе с ним прерывается и идущее связывание.
    private(set) var pairingDeadline: Date?
    @ObservationIgnored private var pairingBusy = false
    @ObservationIgnored private var incomingLink: PeerLink?
    @ObservationIgnored private var incomingDecision: CheckedContinuation<Bool, Never>?
    @ObservationIgnored private var outgoingLink: PeerLink?
    @ObservationIgnored private var outgoingCode: CheckedContinuation<String?, Never>?
    @ObservationIgnored private var maintenance: Task<Void, Never>?

    private struct BrowseResult {
        let name: String
        let endpoint: NWEndpoint
        let pairing: Bool
        let type: DeviceType
    }

    /// sharedSettings — сохранённые общие настройки (nil — по умолчанию: 50 МиБ, changed = 0, by = свой deviceId).
    init(identity: DeviceIdentity, name: String, deviceType: DeviceType, imagesEnabled: Bool, filesEnabled: Bool,
         sharedSettings: SharedSettings? = nil, store: DeviceStore) {
        self.identity = identity
        let normalized = Self.normalizedName(name)
        self.name = normalized.isEmpty ? "Mac" : normalized
        self.deviceType = deviceType
        self.imagesEnabled = imagesEnabled
        self.filesEnabled = filesEnabled
        self.sharedSettings = sharedSettings?.normalized() ?? .default(deviceID: identity.deviceID)
        self.store = store
        refreshDevices()
    }

    func start() {
        for device in store.devices {
            disconnectedSince[device.deviceID] = Date()
        }
        startListener(port: NWEndpoint.Port(rawValue: Self.defaultPort) ?? .any)
        startBrowser()
        maintenance = Task { [weak self] in
            while !Task.isCancelled {
                await self?.maintain()
                let interval: Double = self?.isPairingMode == true ? 2 : 5
                try? await Task.sleep(for: .seconds(interval))
            }
        }
    }

    /// Имя без пробелов по краям и не длиннее 63 байт UTF-8 (предел метки DNS для имени экземпляра).
    static func normalizedName(_ raw: String) -> String {
        var result = ""
        for character in raw.trimmingCharacters(in: .whitespacesAndNewlines) {
            guard result.utf8.count + String(character).utf8.count <= 63 else { break }
            result.append(character)
        }
        return result
    }

    // MARK: - Устройства и настройки

    func setEnabled(_ enabled: Bool, deviceID: String) {
        store.setEnabled(enabled, id: deviceID)
        problems[deviceID] = nil
        retryAfter[deviceID] = nil
        if enabled {
            disconnectedSince[deviceID] = Date()
            Task { await maintain() }
        } else {
            sessions[deviceID]?.link.cancel()
        }
        Log.network.notice("Синхронизация с \(deviceID, privacy: .public) \(enabled ? "включена" : "выключена", privacy: .public)")
        refreshDevices()
    }

    func unpair(deviceID: String) {
        store.remove(id: deviceID)
        sessions[deviceID]?.link.cancel()
        disconnectedSince[deviceID] = nil
        retryAfter[deviceID] = nil
        problems[deviceID] = nil
        Log.network.notice("Связь с \(deviceID, privacy: .public) разорвана")
        refreshDevices()
    }

    /// Задать локальный псевдоним устройства; пустая строка — сбросить. Другим устройствам не передаётся.
    func setAlias(_ alias: String, deviceID: String) {
        let trimmed = alias.trimmingCharacters(in: .whitespacesAndNewlines)
        store.setAlias(trimmed.isEmpty ? nil : trimmed, id: deviceID)
        Log.network.notice("Псевдоним \(deviceID, privacy: .public): «\(trimmed, privacy: .public)»")
        refreshDevices()
    }

    /// Сменить своё имя: TXT и имя экземпляра Bonjour, info всем сеансам. Хранит имя приложение.
    func setName(_ newName: String) {
        let normalized = Self.normalizedName(newName)
        guard !normalized.isEmpty, normalized != name else { return }
        Log.network.notice("Имя устройства: «\(self.name, privacy: .public)» → «\(normalized, privacy: .public)»")
        name = normalized
        // Повторное присваивание service обновляет и имя экземпляра, и TXT.
        listener?.service = advertisedService()
        sendInfoToAll()
    }

    /// Включить или выключить «Передавать картинки»: новый caps в info всем сеансам. Хранит настройку приложение.
    func setImagesEnabled(_ enabled: Bool) {
        guard enabled != imagesEnabled else { return }
        imagesEnabled = enabled
        Log.network.notice("Передача картинок \(enabled ? "включена" : "выключена", privacy: .public)")
        if !enabled {
            for session in sessions.values {
                session.outgoingImages.removeAll()
                if case .receiving(let assembly) = session.incomingBlob {
                    session.incomingBlob = .skipping(assembly.header.id)
                }
            }
        }
        sendInfoToAll()
        refreshDevices()
    }

    // MARK: - Общие настройки

    /// Пользователь изменил «Скачивать автоматически» (МиБ; незнакомое значение — ближайшее допустимое):
    /// changed — сейчас, by — этот Mac; разослать всем сеансам. То же значение — ничего не меняется.
    /// changed не меньше прежнего + 1: изменение здесь новее принятого, даже если часы другого устройства спешат.
    func setAutoDownloadMB(_ mb: Int) {
        let value = SharedSettings.nearest(mb)
        guard value != sharedSettings.autoDownloadMB else { return }
        let now = Int64((Date().timeIntervalSince1970 * 1000).rounded(.down))
        let updated = SharedSettings(autoDownloadMB: value, changed: max(now, sharedSettings.changed + 1), by: identity.deviceID)
        sharedSettings = updated
        Log.network.notice("Скачивать автоматически: до \(value) МиБ (изменено здесь)")
        settingsChanged(updated)
        sendSettings(updated, to: Array(sessions.values))
    }

    /// settings пришли по сеансу: новее своих — принять, сообщить приложению и переслать всем остальным сеансам.
    private func handleSettings(_ incoming: SharedSettings, from session: ActiveSession) {
        guard incoming.isNewer(than: sharedSettings) else {
            if incoming != sharedSettings {
                Log.network.info("Общие настройки от «\(session.peerName, privacy: .public)» старше своих (\(incoming.description, privacy: .public) ≤ \(self.sharedSettings.description, privacy: .public)) — пропущены")
            }
            return
        }
        sharedSettings = incoming
        Log.network.notice("Общие настройки от «\(session.peerName, privacy: .public)»: скачивать автоматически до \(incoming.autoDownloadMB) МиБ (\(incoming.changed), \(incoming.by, privacy: .public))")
        settingsChanged(incoming)
        sendSettings(incoming, to: sessions.values.filter { $0.peerID != session.peerID })
    }

    private func settingsChanged(_ settings: SharedSettings) {
        onEvent?("SETTINGS \(settings.description)")
        onSettingsChanged?(settings)
    }

    private func sendSettings(_ settings: SharedSettings, to targets: [ActiveSession]) {
        for session in targets {
            let link = session.link
            let peerName = session.peerName
            Task {
                do {
                    try await link.send(.settings(settings))
                } catch {
                    Log.network.info("Не удалось отправить общие настройки «\(peerName, privacy: .public)»: \(error.localizedDescription, privacy: .public)")
                }
            }
        }
    }

    private var ownInfo: PeerInfo {
        var caps: [String] = []
        if imagesEnabled { caps.append(ProtocolLimits.imageCapability) }
        if filesEnabled { caps.append(ProtocolLimits.fileCapability) }
        return PeerInfo(name: name, type: deviceType, caps: caps)
    }

    func sendInfoToAll() {
        let info = ownInfo
        for session in sessions.values {
            let link = session.link
            Task { try? await link.send(.info(info)) }
        }
    }

    func refreshDevices() {
        let list = store.devices.map { device in
            DeviceStatus(
                id: device.deviceID,
                name: device.name,
                alias: device.alias,
                type: device.type,
                enabled: device.enabled,
                connected: sessions[device.deviceID] != nil,
                acceptsImages: sessions[device.deviceID]?.caps.contains(ProtocolLimits.imageCapability) ?? false,
                acceptsFiles: sessions[device.deviceID]?.caps.contains(ProtocolLimits.fileCapability) ?? false,
                problem: problems[device.deviceID])
        }
        if list != devices {
            devices = list
        }
        onConnectionsChanged?(sessions.count)
    }

    func displayName(_ session: ActiveSession) -> String {
        store.device(id: session.peerID)?.alias ?? session.peerName
    }

    // MARK: - Текст

    /// Отправить текст, скопированный на этом Mac, всем подключённым устройствам.
    func broadcast(_ text: String) {
        guard text.utf8.count <= Self.maxClipBytes else {
            Log.clipboard.notice("Текст больше 1 МиБ — не отправлен")
            return
        }
        let id = Blob.newID()
        _ = markSeen(id)
        send(ClipPayload(id: id, origin: identity.deviceID, hops: 0, text: text), except: nil)
    }

    private func handleClip(_ clip: ClipPayload, from session: ActiveSession) {
        guard markSeen(clip.id) else {
            Log.clipboard.debug("Повтор фрагмента от «\(session.peerName, privacy: .public)» отброшен")
            return
        }
        Log.clipboard.info("Получено от «\(session.peerName, privacy: .public)»: \(clip.text.count) символов")
        onEvent?("CLIP \(session.peerName) \(clip.text)")
        onClipReceived?(clip.text, displayName(session))
        if clip.hops + 1 < Self.maxHops {
            send(ClipPayload(id: clip.id, origin: clip.origin, hops: clip.hops + 1, text: clip.text), except: session.peerID)
        }
    }

    private func send(_ clip: ClipPayload, except excluded: String?) {
        for session in sessions.values
        where session.peerID != excluded && session.peerID != clip.origin && store.device(id: session.peerID)?.enabled == true {
            let link = session.link
            let peerName = session.peerName
            Task {
                do {
                    try await link.send(.clip(clip))
                    Log.clipboard.info("\(clip.hops == 0 ? "Отправлено" : "Переслано", privacy: .public) «\(peerName, privacy: .public)»: \(clip.text.count) символов")
                } catch {
                    Log.clipboard.error("Не удалось отправить «\(peerName, privacy: .public)»: \(error.localizedDescription, privacy: .public)")
                    link.cancel()
                }
            }
        }
    }

    /// true — фрагмент новый; false — уже был (пришёл другим путём). Список общий для текста и картинок.
    private func markSeen(_ id: String) -> Bool {
        guard seenClips.insert(id).inserted else { return false }
        seenOrder.append(id)
        if seenOrder.count > Self.seenClipsCapacity {
            seenClips.remove(seenOrder.removeFirst())
        }
        return true
    }

    // MARK: - Картинки

    /// Отправить картинку (image/png или image/jpeg) всем подключённым включённым устройствам с image в caps.
    /// Возвращает, скольким устройствам она поставлена в очередь (0 — никому или картинка не подходит).
    /// Данные не копируются: все очереди держат одну и ту же Data.
    @discardableResult
    func sendImage(_ data: Data, mime: String) -> Int {
        guard imagesEnabled else {
            Log.clipboard.info("Передача картинок выключена — картинка не отправлена")
            return 0
        }
        guard ProtocolLimits.imageMimes.contains(mime) else {
            Log.clipboard.notice("Картинка \(mime, privacy: .public) не отправлена: незнакомый mime")
            return 0
        }
        guard !data.isEmpty, data.count <= ProtocolLimits.maxImageBytes else {
            Log.clipboard.notice("Картинка \(data.count) байт больше 20 МиБ — не отправлена")
            return 0
        }
        let header = BlobStart(id: Blob.newID(), origin: identity.deviceID, hops: 0, mime: mime, size: data.count, sha256: Blob.sha256(data))
        _ = markSeen(header.id)
        let recipients = enqueueImage(OutgoingImage(header: header, data: data), except: nil)
        Log.clipboard.info("Картинка \(mime, privacy: .public), \(data.count) байт: в очереди для \(recipients) устройств")
        return recipients
    }

    private func handleBlobStart(_ start: BlobStart, from session: ActiveSession) {
        if case .receiving(let previous) = session.incomingBlob {
            Log.clipboard.notice("Картинка \(previous.header.id, privacy: .public) от «\(session.peerName, privacy: .public)» не закончена — отброшена")
        }
        // Пока не решено иное, картинка пропускается до blob_end.
        session.incomingBlob = .skipping(start.id)
        if seenClips.contains(start.id) {
            Log.clipboard.debug("Повтор картинки от «\(session.peerName, privacy: .public)» пропущен")
            return
        }
        guard imagesEnabled else {
            Log.clipboard.info("Картинка от «\(session.peerName, privacy: .public)» пропущена: передача картинок выключена")
            return
        }
        if let refusal = BlobAssembly.refusal(start) {
            Log.clipboard.notice("Картинка от «\(session.peerName, privacy: .public)» не принята: \(refusal, privacy: .public)")
            onEvent?("IMAGE_REFUSED \(session.peerName)")
            return
        }
        session.incomingBlob = .receiving(BlobAssembly(start))
    }

    private func handleBlobChunk(id: String, seq: Int, data: Data?, from session: ActiveSession) {
        guard case .receiving(var assembly) = session.incomingBlob else { return }
        // Сборка должна остаться единственной ссылкой на данные, иначе append скопирует их целиком.
        session.incomingBlob = .skipping(assembly.header.id)
        guard assembly.header.id == id else {
            Log.clipboard.notice("Картинка от «\(session.peerName, privacy: .public)» отброшена: кусок с другим id")
            return
        }
        do {
            try assembly.append(seq: seq, chunk: data)
            session.incomingBlob = .receiving(assembly)
        } catch {
            Log.clipboard.notice("Картинка от «\(session.peerName, privacy: .public)» отброшена: \(error.localizedDescription, privacy: .public)")
        }
    }

    private func handleBlobEnd(id: String, from session: ActiveSession) {
        switch session.incomingBlob {
        case .receiving(let assembly) where assembly.header.id == id:
            session.incomingBlob = nil
            do {
                deliverImage(assembly.header, try assembly.finish(), from: session)
            } catch {
                Log.clipboard.notice("Картинка от «\(session.peerName, privacy: .public)» отброшена: \(error.localizedDescription, privacy: .public)")
            }
        case .skipping(let skipped) where skipped == id:
            session.incomingBlob = nil
        default:
            Log.clipboard.debug("blob_end без начатой картинки от «\(session.peerName, privacy: .public)»")
        }
    }

    private func deliverImage(_ header: BlobStart, _ data: Data, from session: ActiveSession) {
        guard imagesEnabled else { return }
        // id заносится в список последних только после успешной сборки.
        guard markSeen(header.id) else {
            Log.clipboard.debug("Повтор картинки от «\(session.peerName, privacy: .public)» отброшен")
            return
        }
        Log.clipboard.info("Картинка от «\(session.peerName, privacy: .public)»: \(header.mime, privacy: .public), \(data.count) байт")
        onEvent?("IMAGE \(session.peerName) \(data.count) \(header.sha256.map { String(format: "%02x", $0) }.joined())")
        onImageReceived?(data, header.mime, displayName(session))
        if header.hops + 1 < Self.maxHops {
            let forwarded = enqueueImage(OutgoingImage(header: header.with(hops: header.hops + 1), data: data), except: session.peerID)
            if forwarded > 0 {
                Log.clipboard.info("Картинка пересылается \(forwarded) устройствам")
            }
        }
    }

    /// Поставить картинку в очередь всем подходящим сеансам. В каждом сеансе картинки уходят по одной.
    private func enqueueImage(_ image: OutgoingImage, except excluded: String?) -> Int {
        var recipients = 0
        for session in sessions.values
        where session.peerID != excluded && session.peerID != image.header.origin
            && session.caps.contains(ProtocolLimits.imageCapability)
            && store.device(id: session.peerID)?.enabled == true {
            if session.outgoingImages.count >= Self.maxQueuedImages {
                let dropped = session.outgoingImages.removeFirst()
                Log.clipboard.notice("Очередь картинок для «\(session.peerName, privacy: .public)» полна — \(dropped.header.id, privacy: .public) не отправлена")
            }
            session.outgoingImages.append(image)
            recipients += 1
            if session.imageSender == nil {
                session.imageSender = Task { [weak self] in await self?.drainImages(session) }
            }
        }
        return recipients
    }

    private func drainImages(_ session: ActiveSession) async {
        while !session.outgoingImages.isEmpty, sessions[session.peerID] === session {
            let image = session.outgoingImages.removeFirst()
            do {
                try await sendBlob(image, over: session.link)
                Log.clipboard.info("\(image.header.hops == 0 ? "Отправлена" : "Переслана", privacy: .public) картинка «\(session.peerName, privacy: .public)»: \(image.data.count) байт")
            } catch {
                Log.clipboard.error("Не удалось отправить картинку «\(session.peerName, privacy: .public)»: \(error.localizedDescription, privacy: .public)")
                session.outgoingImages.removeAll()
                session.link.cancel()
            }
        }
        session.imageSender = nil
    }

    /// blob_start, куски по порядку, blob_end. Каждый кадр — отдельная отправка через PeerLink
    /// (шифрование и постановка в очередь без await между ними), поэтому между кусками проходят ping, clip и info.
    private func sendBlob(_ image: OutgoingImage, over link: PeerLink) async throws {
        try await link.send(.blobStart(image.header))
        let base = image.data.startIndex
        for (seq, range) in Blob.chunkRanges(size: image.data.count).enumerated() {
            let chunk = image.data.subdata(in: base + range.lowerBound ..< base + range.upperBound)
            try await link.send(.blobChunk(id: image.header.id, seq: seq, data: chunk))
        }
        try await link.send(.blobEnd(id: image.header.id))
    }

    // MARK: - Объявление и поиск

    private func startListener(port: NWEndpoint.Port) {
        do {
            // Принятые соединения получают параметры слушателя, в том числе TCP_NODELAY.
            let parameters = Self.tcpParameters()
            parameters.allowLocalEndpointReuse = true
            let listener = try NWListener(using: parameters, on: port)
            listener.service = advertisedService()
            listener.newConnectionHandler = { [weak self] connection in
                MainActor.assumeIsolated { self?.accept(connection) }
            }
            listener.stateUpdateHandler = { [weak self] state in
                MainActor.assumeIsolated { self?.listenerStateChanged(state) }
            }
            listener.start(queue: .main)
            self.listener = listener
        } catch {
            Log.network.error("Не удалось открыть порт: \(error.localizedDescription, privacy: .public)")
            if port != .any {
                listenerUsesDefaultPort = false
                startListener(port: .any)
            }
        }
    }

    private func listenerStateChanged(_ state: NWListener.State) {
        switch state {
        case .ready:
            port = listener?.port?.rawValue
            Log.network.notice("«\(self.name, privacy: .public)» (\(self.identity.deviceID, privacy: .public)) слушает порт \(self.port ?? 0)")
            onEvent?("READY \(name) id=\(identity.deviceID) port=\(port ?? 0)")
        case .failed(let error):
            Log.network.error("Слушатель остановлен: \(error.localizedDescription, privacy: .public)")
            listener?.cancel()
            listener = nil
            if listenerUsesDefaultPort {
                listenerUsesDefaultPort = false
                startListener(port: .any)
            }
        default:
            break
        }
    }

    private func advertisedService() -> NWListener.Service {
        var txt = ["id": identity.deviceID, "v": "1", "pair": isPairingMode ? "1" : "0"]
        if let os = deviceType.os { txt["os"] = os }
        if let form = deviceType.form { txt["form"] = form }
        return NWListener.Service(name: name, type: Self.serviceType, domain: nil, txtRecord: NWTXTRecord(txt))
    }

    private func startBrowser() {
        let parameters = NWParameters.tcp
        parameters.includePeerToPeer = false
        let browser = NWBrowser(for: .bonjourWithTXTRecord(type: Self.serviceType, domain: nil), using: parameters)
        browser.browseResultsChangedHandler = { [weak self] results, _ in
            MainActor.assumeIsolated { self?.browseResultsChanged(results) }
        }
        browser.stateUpdateHandler = { state in
            if case .failed(let error) = state {
                Log.network.error("Поиск устройств остановлен: \(error.localizedDescription, privacy: .public)")
            }
        }
        browser.start(queue: .main)
        self.browser = browser
    }

    private func browseResultsChanged(_ results: Set<NWBrowser.Result>) {
        var found: [String: BrowseResult] = [:]
        for result in results {
            guard case .service(let serviceName, _, _, _) = result.endpoint,
                  case .bonjour(let txt) = result.metadata,
                  let id = txt["id"], id != identity.deviceID,
                  found[id] == nil else { continue }
            found[id] = BrowseResult(
                name: serviceName,
                endpoint: result.endpoint,
                pairing: txt["pair"] == "1",
                type: DeviceType(os: txt["os"], form: txt["form"]))
        }
        browseResults = found
        updateCandidates()
        Task { await maintain() }
    }

    private func updateCandidates() {
        let list = isPairingMode
            ? browseResults
                .filter { $0.value.pairing && store.device(id: $0.key) == nil }
                .map { Candidate(id: $0.key, name: $0.value.name, type: $0.value.type, endpoint: $0.value.endpoint) }
                .sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
            : []
        if list != candidates {
            candidates = list
        }
    }

    // MARK: - Входящие соединения

    private func accept(_ connection: NWConnection) {
        let link = PeerLink(connection: connection)
        Task { await handleIncoming(link) }
    }

    private func handleIncoming(_ link: PeerLink) async {
        do {
            try await link.start(timeout: 10)
            let timer = cancel(link, after: 10)
            let first = try await link.receive()
            timer.cancel()
            switch first {
            case .pairHello(let peerName, let peerKey, let peerType):
                await respondToPairing(link: link, peerName: peerName, peerKey: peerKey, peerType: peerType)
            case .hello(let peerID, let ephemeral):
                try await respondToSession(link: link, peerID: peerID, peerEphemeral: ephemeral)
            default:
                Log.network.notice("Непонятное первое сообщение: \(first.type, privacy: .public)")
                link.cancel()
            }
        } catch {
            Log.network.info("Входящее соединение: \(error.localizedDescription, privacy: .public)")
            link.cancel()
        }
    }

    private func respondToSession(link: PeerLink, peerID: String, peerEphemeral: Data) async throws {
        guard let peer = store.device(id: peerID) else {
            try? await link.send(.error(reason: "unknown_device"))
            link.cancel()
            Log.network.notice("Подключилось несвязанное устройство \(peerID, privacy: .public)")
            return
        }
        guard peer.enabled else {
            try? await link.send(.error(reason: "disabled"))
            link.cancel()
            Log.network.info("Сеанс с выключенным устройством «\(peer.name, privacy: .public)» отклонён")
            return
        }
        let timer = cancel(link, after: 10)
        defer { timer.cancel() }

        let ephemeral = P256.KeyAgreement.PrivateKey()
        let ephemeralPublic = ephemeral.publicKey.x963Representation
        try await link.send(.helloAck(id: identity.deviceID, ephemeral: ephemeralPublic))

        let peerStatic = try ClipveyCrypto.publicKey(from: peer.publicKey)
        let peerEphemeralKey = try ClipveyCrypto.publicKey(from: peerEphemeral)
        let keys = ClipveyCrypto.sessionKeys(
            dh1: try ClipveyCrypto.agree(ephemeral, peerEphemeralKey),
            dh2: try ClipveyCrypto.agree(ephemeral, peerStatic),
            dh3: try ClipveyCrypto.agree(identity.privateKey, peerEphemeralKey),
            transcript: ClipveyCrypto.sessionTranscript(
                initiatorKey: peer.publicKey, responderKey: identity.publicKey,
                initiatorEphemeral: peerEphemeral, responderEphemeral: ephemeralPublic))
        await link.enableEncryption(SecureCodec(sendKey: keys.responderToInitiator, receiveKey: keys.initiatorToResponder))

        let info = try await exchangeReady(link)
        adopt(link: link, peer: peer, info: info, initiatorID: peer.deviceID, endpoint: link.remoteEndpoint, keepPort: true)
    }

    // MARK: - Исходящие соединения

    private func maintain() async {
        if let deadline = pairingDeadline, Date() >= deadline {
            stopPairingMode()
        }
        for device in store.devices
        where device.enabled && sessions[device.deviceID] == nil && !connecting.contains(device.deviceID) && shouldInitiate(device.deviceID) {
            let endpoints = endpoints(for: device)
            guard !endpoints.isEmpty else { continue }
            connecting.insert(device.deviceID)
            await connect(to: device, endpoints: endpoints)
            connecting.remove(device.deviceID)
        }
    }

    /// Пробует адреса по очереди. Пауза 30 с — только после disabled. unknown_device или не тот deviceId
    /// по запасному адресу значат, что там теперь другой Clipvey: пробуется следующий адрес.
    private func connect(to device: StoredDevice, endpoints: [(endpoint: NWEndpoint, discovered: Bool)]) async {
        for (endpoint, discovered) in endpoints {
            do {
                try await initiateSession(with: device, via: endpoint)
                return
            } catch let error as ClipveyError where error.reason == .disabled {
                Log.network.notice("«\(device.name, privacy: .public)»: \(error.localizedDescription, privacy: .public)")
                problems[device.deviceID] = .disabled
                retryAfter[device.deviceID] = Date().addingTimeInterval(Self.rejectedRetryDelay)
                refreshDevices()
                return
            } catch {
                let reason = ClipveyError.reason(of: error)
                Log.network.info("Подключение к «\(device.name, privacy: .public)» (\(discovered ? "найден в сети" : "запасной адрес", privacy: .public)): \(error.localizedDescription, privacy: .public)")
                // Устройство, найденное в сети по своему id, нас не знает — надо связать заново.
                if discovered, reason == .unknownDevice, problems[device.deviceID] != reason {
                    problems[device.deviceID] = reason
                    refreshDevices()
                }
            }
        }
    }

    /// Первым подключается меньший deviceId; больший — только если сеанс не появился за 5 секунд.
    private func shouldInitiate(_ peerID: String) -> Bool {
        if let retry = retryAfter[peerID], Date() < retry {
            return false
        }
        if identity.deviceID < peerID {
            return true
        }
        guard let since = disconnectedSince[peerID] else {
            disconnectedSince[peerID] = Date()
            return false
        }
        return Date().timeIntervalSince(since) >= Self.secondaryConnectDelay
    }

    /// Сначала адрес из Bonjour, затем запасной (последний удачный).
    private func endpoints(for device: StoredDevice) -> [(endpoint: NWEndpoint, discovered: Bool)] {
        var endpoints: [(endpoint: NWEndpoint, discovered: Bool)] = []
        if let found = browseResults[device.deviceID] {
            endpoints.append((found.endpoint, true))
        }
        if let host = device.lastHost, device.lastPort > 0, let port = NWEndpoint.Port(rawValue: UInt16(device.lastPort)) {
            endpoints.append((.hostPort(host: NWEndpoint.Host(host), port: port), false))
        }
        return endpoints
    }

    private func initiateSession(with device: StoredDevice, via endpoint: NWEndpoint) async throws {
        let link = PeerLink(connection: NWConnection(to: endpoint, using: Self.outgoingParameters()))
        do {
            try await link.start(timeout: 3)
            let timer = cancel(link, after: 10)
            defer { timer.cancel() }

            let ephemeral = P256.KeyAgreement.PrivateKey()
            let ephemeralPublic = ephemeral.publicKey.x963Representation
            try await link.send(.hello(id: identity.deviceID, ephemeral: ephemeralPublic))
            let ack = try await link.receive()
            guard case .helloAck(let peerID, let peerEphemeral) = ack else {
                throw ack.unexpected(expecting: "hello_ack")
            }
            guard peerID == device.deviceID else {
                throw ClipveyError.wrongDevice
            }

            let peerStatic = try ClipveyCrypto.publicKey(from: device.publicKey)
            let peerEphemeralKey = try ClipveyCrypto.publicKey(from: peerEphemeral)
            let keys = ClipveyCrypto.sessionKeys(
                dh1: try ClipveyCrypto.agree(ephemeral, peerEphemeralKey),
                dh2: try ClipveyCrypto.agree(identity.privateKey, peerEphemeralKey),
                dh3: try ClipveyCrypto.agree(ephemeral, peerStatic),
                transcript: ClipveyCrypto.sessionTranscript(
                    initiatorKey: identity.publicKey, responderKey: device.publicKey,
                    initiatorEphemeral: ephemeralPublic, responderEphemeral: peerEphemeral))
            await link.enableEncryption(SecureCodec(sendKey: keys.initiatorToResponder, receiveKey: keys.responderToInitiator))

            let info = try await exchangeReady(link)
            adopt(link: link, peer: device, info: info, initiatorID: identity.deviceID, endpoint: link.remoteEndpoint, keepPort: false)
        } catch {
            link.cancel()
            throw error
        }
    }

    private static func outgoingParameters() -> NWParameters {
        tcpParameters()
    }

    /// TCP с TCP_NODELAY (docs/protocol.md, «Транспорт»): без алгоритма Нейгла маленькое сообщение после данных
    /// (file_end, file_get) уходит сразу, а не ждёт подтверждения, которое получатель откладывает до 200 мс.
    private static func tcpParameters() -> NWParameters {
        let tcp = NWProtocolTCP.Options()
        tcp.noDelay = true
        let parameters = NWParameters(tls: nil, tcp: tcp)
        parameters.includePeerToPeer = false
        return parameters
    }

    // MARK: - Сеансы

    private func exchangeReady(_ link: PeerLink) async throws -> PeerInfo {
        try await link.send(.ready(ownInfo))
        let message = try await link.receive()
        guard case .ready(let info) = message else {
            throw message.unexpected(expecting: "ready")
        }
        return info
    }

    private func adopt(link: PeerLink, peer: StoredDevice, info: PeerInfo, initiatorID: String, endpoint: (host: String, port: Int)?, keepPort: Bool) {
        let peerID = peer.deviceID
        let peerName = info.name.flatMap { $0.isEmpty ? nil : $0 } ?? peer.name
        if let existing = sessions[peerID] {
            // Два одновременных сеанса: оставляем тот, где подключался меньший deviceId.
            let preferred = identity.deviceID < peerID ? identity.deviceID : peerID
            if existing.initiatorID == preferred || initiatorID != preferred {
                Log.network.info("Лишний сеанс с «\(peerName, privacy: .public)» закрыт")
                link.cancel()
                return
            }
            existing.link.cancel()
        }
        // В ready отсутствие caps значит «ничего» (так у 0.1.0): картинки такому устройству не шлются.
        let session = ActiveSession(link: link, peerID: peerID, peerName: peerName, initiatorID: initiatorID, caps: Set(info.caps ?? []))
        sessions[peerID] = session
        disconnectedSince[peerID] = nil
        problems[peerID] = nil
        store.updateInfo(id: peerID, name: info.name, type: info.type)
        if let endpoint {
            store.updateEndpoint(id: peerID, host: endpoint.host, port: keepPort ? nil : endpoint.port)
        }
        if peer.name != peerName {
            Log.network.notice("«\(peer.name, privacy: .public)» теперь называется «\(peerName, privacy: .public)»")
            onEvent?("RENAMED \(peer.name) \(peerName)")
        }
        let direction = initiatorID == identity.deviceID ? "исходящий" : "входящий"
        Log.network.notice("Сеанс с «\(peerName, privacy: .public)» установлен (\(direction, privacy: .public))")
        onEvent?("CONNECTED \(peerName)")
        emitInfo(session)
        Task { await runSession(session) }
        // Свои общие настройки — сразу после ready (старые версии settings пропускают).
        sendSettings(sharedSettings, to: [session])
        refreshDevices()
    }

    /// «INFO имя os=… form=… caps=…» для самопроверки: что известно о другом устройстве.
    private func emitInfo(_ session: ActiveSession) {
        let type = store.device(id: session.peerID)?.type ?? .unknown
        let caps = session.caps.sorted().joined(separator: ",")
        onEvent?("INFO \(session.peerName) os=\(type.os ?? "-") form=\(type.form ?? "-") caps=\(caps.isEmpty ? "-" : caps)")
    }

    private func handleInfo(_ info: PeerInfo, from session: ActiveSession) {
        if let newName = info.name, !newName.isEmpty, newName != session.peerName {
            Log.network.notice("«\(session.peerName, privacy: .public)» теперь называется «\(newName, privacy: .public)»")
            onEvent?("RENAMED \(session.peerName) \(newName)")
            session.peerName = newName
        }
        if let caps = info.caps {
            session.caps = Set(caps)
            if !session.caps.contains(ProtocolLimits.imageCapability) {
                session.outgoingImages.removeAll()
            }
        }
        store.updateInfo(id: session.peerID, name: info.name, type: info.type)
        emitInfo(session)
        refreshDevices()
    }

    private func runSession(_ session: ActiveSession) async {
        let link = session.link
        let pinger = Task {
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(15))
                guard !Task.isCancelled else { return }
                if Date().timeIntervalSince(session.lastReceived) > 45 {
                    Log.network.notice("«\(session.peerName, privacy: .public)» не отвечает")
                    link.cancel()
                    return
                }
                try? await link.send(.ping)
            }
        }
        defer { pinger.cancel() }

        do {
            while true {
                let message = try await link.receive()
                session.lastReceived = Date()
                switch message {
                case .clip(let clip):
                    handleClip(clip, from: session)
                case .info(let info):
                    handleInfo(info, from: session)
                case .settings(let settings):
                    handleSettings(settings, from: session)
                case .blobStart(let start):
                    handleBlobStart(start, from: session)
                case .blobChunk(let id, let seq, let data):
                    handleBlobChunk(id: id, seq: seq, data: data, from: session)
                case .blobEnd(let id):
                    handleBlobEnd(id: id, from: session)
                case .fileOffer(let offer):
                    handleFileOffer(offer, from: session)
                case .fileGet(let id, let req, let index, let offset):
                    await handleFileGet(id: id, req: req, index: index, offset: offset, from: session)
                case .fileEnd(let req, let size):
                    await session.files.end(req: req, size: size)
                case .fileError(let req, let reason):
                    session.files.fail(req: req, reason: reason)
                case .fileCancel(let req):
                    await session.files.server.cancel(req)
                case .fileChunk(let req, let data):
                    await session.files.chunk(req: req, data: data)
                case .invalidFileMessage(let type, let reason):
                    Log.files.notice("\(type, privacy: .public) от «\(session.peerName, privacy: .public)» пропущено: \(reason, privacy: .public)")
                case .ping:
                    try await link.send(.pong)
                case .pong:
                    break
                default:
                    Log.network.notice("Неожиданное сообщение от «\(session.peerName, privacy: .public)»: \(message.type, privacy: .public)")
                }
            }
        } catch {
            if sessions[session.peerID] === session {
                Log.network.notice("Сеанс с «\(session.peerName, privacy: .public)» завершён: \(error.localizedDescription, privacy: .public)")
            }
        }
        link.cancel()
        session.outgoingImages.removeAll()
        session.incomingBlob = nil
        await session.files.stop()
        if sessions[session.peerID] === session {
            sessions[session.peerID] = nil
            disconnectedSince[session.peerID] = Date()
            onEvent?("DISCONNECTED \(session.peerName)")
            refreshDevices()
            // Как Wake() на Windows: если первым подключается этот Mac, сеанс восстанавливается сразу, а не
            // при следующем обходе (его ждут скачивания файлов, прерванные обрывом).
            Task { await maintain() }
        }
    }

    private func cancel(_ link: PeerLink, after seconds: Double) -> Task<Void, Never> {
        Task {
            try? await Task.sleep(for: .seconds(seconds))
            if !Task.isCancelled {
                link.cancel()
            }
        }
    }

    // MARK: - Связывание

    func startPairingMode() {
        pairingDeadline = Date().addingTimeInterval(Self.pairingDuration)
        isPairingMode = true
        pairingResult = nil
        listener?.service = advertisedService()
        updateCandidates()
        Log.network.notice("Режим связывания открыт")
        onEvent?("PAIRING_MODE")
    }

    /// Закрыть режим связывания. Идущее связывание отменяется, если cancelFlows = true.
    func stopPairingMode(cancelFlows: Bool = true) {
        guard isPairingMode else { return }
        isPairingMode = false
        pairingDeadline = nil
        if cancelFlows {
            cancelIncoming()
            cancelOutgoing()
        }
        listener?.service = advertisedService()
        updateCandidates()
        Log.network.notice("Режим связывания закрыт")
    }

    /// Роль R: пользователь нажал «Готово» после того, как другое устройство подтвердило код.
    func confirmIncoming() {
        guard let decision = incomingDecision else { return }
        incomingDecision = nil
        decision.resume(returning: true)
    }

    func cancelIncoming() {
        if let decision = incomingDecision {
            incomingDecision = nil
            decision.resume(returning: false)
        } else if let link = incomingLink {
            Task {
                try? await link.send(.pairAbort(reason: "cancel"))
                link.cancel()
            }
        }
    }

    /// Роль I: связаться с устройством из списка кандидатов.
    func pair(with candidate: Candidate) {
        guard !pairingBusy else { return }
        pairingBusy = true
        pairingResult = nil
        outgoing = .connecting(candidate.name)
        Task { await runOutgoingPairing(candidate) }
    }

    /// Роль I: пользователь ввёл код с экрана другого устройства.
    func submitCode(_ code: String) {
        guard let continuation = outgoingCode else { return }
        outgoingCode = nil
        continuation.resume(returning: code)
    }

    func cancelOutgoing() {
        if let continuation = outgoingCode {
            outgoingCode = nil
            continuation.resume(returning: nil)
        } else {
            outgoingLink?.cancel()
        }
    }

    private func respondToPairing(link: PeerLink, peerName: String, peerKey: Data, peerType: DeviceType) async {
        guard isPairingMode else {
            try? await link.send(.error(reason: "not_pairing"))
            link.cancel()
            Log.network.notice("Запрос на связывание от «\(peerName, privacy: .public)» отклонён: режим связывания закрыт")
            return
        }
        guard !pairingBusy else {
            try? await link.send(.error(reason: "busy"))
            link.cancel()
            return
        }
        pairingBusy = true
        incomingLink = link
        incoming = IncomingPairing(peerName: peerName, peerType: peerType)
        let deadline = cancel(link, after: Self.pairingDuration)
        defer {
            deadline.cancel()
            pairingBusy = false
            incomingLink = nil
            incoming = nil
            incomingDecision = nil
        }

        do {
            _ = try ClipveyCrypto.publicKey(from: peerKey)
            let nonce = ClipveyCrypto.randomNonce()
            try await link.send(.pairCommit(
                name: name,
                key: identity.publicKey,
                commit: ClipveyCrypto.commitment(responderKey: identity.publicKey, initiatorKey: peerKey, responderNonce: nonce),
                type: deviceType))
            let nonceMessage = try await link.receive()
            guard case .pairNonce(let peerNonce) = nonceMessage else {
                throw nonceMessage.unexpected(expecting: "pair_nonce")
            }
            try await link.send(.pairReveal(nonce))

            let code = ClipveyCrypto.code(initiatorKey: peerKey, responderKey: identity.publicKey, initiatorNonce: peerNonce, responderNonce: nonce)
            incoming?.code = code
            onEvent?("PAIRING_CODE \(code) FROM \(peerName)")

            let verdict = try await link.receive()
            guard case .pairVerified = verdict else {
                throw verdict.unexpected(expecting: "pair_verified")
            }
            incoming?.verified = true
            onEvent?("PAIRING_VERIFIED \(peerName)")

            let confirmed = await withCheckedContinuation { continuation in
                incomingDecision = continuation
                if TestHooks.autoConfirm {
                    confirmIncoming()
                }
            }
            guard confirmed else {
                throw ClipveyError.cancelled
            }
            try await link.send(.pairDone)
            link.cancel()
            // Порт слушателя I через Bonjour здесь неизвестен (NWBrowser не отдаёт порт без разрешения имени),
            // поэтому запасной адрес — с портом по умолчанию. Если там окажется другой Clipvey, узел
            // просто попробует следующий адрес (см. connect), а при исходящем сеансе запомнит настоящий порт.
            savePaired(deviceID: ClipveyCrypto.deviceID(for: peerKey), name: peerName, publicKey: peerKey, type: peerType,
                       endpoint: link.remoteEndpoint, port: Int(Self.defaultPort))
        } catch {
            if case ClipveyError.cancelled = error {
                try? await link.send(.pairAbort(reason: "cancel"))
            }
            link.cancel()
            pairingFailed(error)
        }
    }

    private func runOutgoingPairing(_ candidate: Candidate) async {
        let link = PeerLink(connection: NWConnection(to: candidate.endpoint, using: Self.outgoingParameters()))
        outgoingLink = link
        let deadline = cancel(link, after: Self.pairingDuration)
        defer {
            deadline.cancel()
            pairingBusy = false
            outgoing = nil
            outgoingLink = nil
            outgoingCode = nil
        }

        do {
            try await link.start(timeout: 5)
            try await link.send(.pairHello(name: name, key: identity.publicKey, type: deviceType))
            let commitMessage = try await link.receive()
            guard case .pairCommit(let peerName, let peerKey, let commitment, let peerType) = commitMessage else {
                throw commitMessage.unexpected(expecting: "pair_commit")
            }
            _ = try ClipveyCrypto.publicKey(from: peerKey)
            let nonce = ClipveyCrypto.randomNonce()
            try await link.send(.pairNonce(nonce))
            let reveal = try await link.receive()
            guard case .pairReveal(let peerNonce) = reveal else {
                throw reveal.unexpected(expecting: "pair_reveal")
            }
            guard ClipveyCrypto.commitment(responderKey: peerKey, initiatorKey: identity.publicKey, responderNonce: peerNonce) == commitment else {
                try? await link.send(.pairAbort(reason: "commit"))
                throw ClipveyError.commitMismatch
            }

            let code = ClipveyCrypto.code(initiatorKey: identity.publicKey, responderKey: peerKey, initiatorNonce: nonce, responderNonce: peerNonce)
            outgoing = .enterCode(peerName)
            onEvent?("ENTER_CODE \(peerName)")
            let typed = await withCheckedContinuation { continuation in
                outgoingCode = continuation
                TestHooks.provideCode { [weak self] code in self?.submitCode(code) }
            }
            guard let typed else {
                try? await link.send(.pairAbort(reason: "cancel"))
                throw ClipveyError.cancelled
            }
            guard typed.filter({ $0.isASCII && $0.isNumber }) == code else {
                try? await link.send(.pairAbort(reason: "code"))
                throw ClipveyError.codeMismatch
            }
            try await link.send(.pairVerified)
            outgoing = .waitingConfirmation(peerName)
            onEvent?("CODE_ACCEPTED")

            let done = try await link.receive()
            guard case .pairDone = done else {
                throw done.unexpected(expecting: "pair_done")
            }
            let endpoint = link.remoteEndpoint
            link.cancel()
            savePaired(deviceID: ClipveyCrypto.deviceID(for: peerKey), name: peerName, publicKey: peerKey, type: peerType,
                       endpoint: endpoint, port: endpoint?.port)
        } catch {
            link.cancel()
            pairingFailed(error)
        }
    }

    private func savePaired(deviceID: String, name peerName: String, publicKey: Data, type: DeviceType, endpoint: (host: String, port: Int)?, port: Int?) {
        store.upsert(StoredDevice(
            deviceID: deviceID,
            name: peerName,
            publicKey: publicKey,
            lastHost: endpoint?.host,
            lastPort: port ?? Int(Self.defaultPort),
            enabled: true,
            os: type.os,
            form: type.form,
            // Псевдоним переживает повторное связывание с тем же устройством.
            alias: store.device(id: deviceID)?.alias))
        disconnectedSince[deviceID] = Date()
        problems[deviceID] = nil
        pairingResult = .paired(name: peerName)
        Log.network.notice("Связывание с «\(peerName, privacy: .public)» завершено")
        onEvent?("PAIRED \(peerName)")
        stopPairingMode(cancelFlows: false)
        refreshDevices()
        Task { await maintain() }
    }

    private func pairingFailed(_ error: Error) {
        let reason = ClipveyError.reason(of: error)
        pairingResult = .failed(reason)
        let message = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        Log.network.notice("Связывание не удалось: \(message, privacy: .public)")
        onEvent?("PAIRING_FAILED \(reason.code)")
    }
}

/// Картинка в очереди на отправку. Data общая для всех очередей (копии не создаются).
private struct OutgoingImage {
    let header: BlobStart
    let data: Data
}

/// Что сейчас приходит по сеансу: картинка собирается или пропускается до blob_end.
private enum IncomingBlob {
    case receiving(BlobAssembly)
    case skipping(String)
}

/// Сеанс с устройством. Живёт на главном потоке вместе с узлом.
@MainActor
final class ActiveSession {
    let link: PeerLink
    let peerID: String
    var peerName: String
    let initiatorID: String
    /// Последний известный caps другой стороны (из ready, затем из info).
    var caps: Set<String>
    var lastReceived = Date()
    fileprivate var incomingBlob: IncomingBlob?
    fileprivate var outgoingImages: [OutgoingImage] = []
    var imageSender: Task<Void, Never>?
    /// Файлы этого сеанса: обслуживание file_get и приём кусков (NodeFiles.swift).
    let files: SessionFiles

    init(link: PeerLink, peerID: String, peerName: String, initiatorID: String, caps: Set<String>) {
        self.link = link
        self.peerID = peerID
        self.peerName = peerName
        self.initiatorID = initiatorID
        self.caps = caps
        files = SessionFiles(link: link, peerName: peerName)
    }
}
