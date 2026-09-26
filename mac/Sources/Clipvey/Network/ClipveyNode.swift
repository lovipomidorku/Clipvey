import CryptoKit
import Foundation
import Network
import Observation

/// Узел Clipvey:
/// - слушает TCP и объявляет себя через Bonjour, ищет другие устройства;
/// - держит с каждым связанным включённым устройством не больше одного сеанса;
/// - пересылает фрагменты буфера между устройствами;
/// - ведёт связывание в обеих ролях.
/// Правила — docs/protocol.md; логика совпадает с Clipvey.Core на C#.
@MainActor
@Observable
final class ClipveyNode {
    struct DeviceStatus: Identifiable, Equatable {
        let id: String
        let name: String
        let enabled: Bool
        let connected: Bool
        let problem: String?
    }

    struct Candidate: Identifiable, Equatable {
        let id: String
        let name: String
        let endpoint: NWEndpoint
    }

    /// Входящее связывание (роль R): этот Mac показывает код.
    struct IncomingPairing: Equatable {
        let peerName: String
        var code: String?
        var verified = false
    }

    /// Исходящее связывание (роль I): пользователь вводит код с экрана другого устройства.
    enum OutgoingPairing: Equatable {
        case connecting(String)
        case enterCode(String)
        case waitingConfirmation(String)
    }

    static let serviceType = "_clipvey._tcp"
    static let defaultPort: UInt16 = 48620
    private static let maxClipBytes = 1024 * 1024
    private static let maxHops = 8
    private static let seenClipsCapacity = 500
    private static let pairingDuration: TimeInterval = 120
    private static let secondaryConnectDelay: TimeInterval = 5
    private static let rejectedRetryDelay: TimeInterval = 30

    private(set) var devices: [DeviceStatus] = []
    private(set) var isPairingMode = false
    private(set) var candidates: [Candidate] = []
    private(set) var incoming: IncomingPairing?
    private(set) var outgoing: OutgoingPairing?
    /// Итог последнего связывания: успех или причина неудачи.
    private(set) var pairingResult: String?
    private(set) var port: UInt16?

    let identity: DeviceIdentity
    let name: String

    /// Пришёл текст с другого устройства (переводы строк — \n).
    @ObservationIgnored var onClipReceived: ((String) -> Void)?
    /// Изменилось число подключённых устройств.
    @ObservationIgnored var onConnectionsChanged: ((Int) -> Void)?
    /// Для самопроверки: события строками вида «CONNECTED имя».
    @ObservationIgnored var onEvent: ((String) -> Void)?

    @ObservationIgnored private let store: DeviceStore
    @ObservationIgnored private var listener: NWListener?
    @ObservationIgnored private var listenerUsesDefaultPort = true
    @ObservationIgnored private var browser: NWBrowser?
    @ObservationIgnored private var sessions: [String: ActiveSession] = [:]
    @ObservationIgnored private var browseResults: [String: (name: String, endpoint: NWEndpoint, pairing: Bool)] = [:]
    @ObservationIgnored private var disconnectedSince: [String: Date] = [:]
    @ObservationIgnored private var retryAfter: [String: Date] = [:]
    @ObservationIgnored private var problems: [String: String] = [:]
    @ObservationIgnored private var connecting: Set<String> = []
    @ObservationIgnored private var seenClips: Set<String> = []
    @ObservationIgnored private var seenOrder: [String] = []
    @ObservationIgnored private var pairingDeadline: Date?
    @ObservationIgnored private var pairingBusy = false
    @ObservationIgnored private var incomingLink: PeerLink?
    @ObservationIgnored private var incomingDecision: CheckedContinuation<Bool, Never>?
    @ObservationIgnored private var outgoingLink: PeerLink?
    @ObservationIgnored private var outgoingCode: CheckedContinuation<String?, Never>?
    @ObservationIgnored private var maintenance: Task<Void, Never>?

    init(identity: DeviceIdentity, name: String, store: DeviceStore) {
        self.identity = identity
        self.name = name
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

    // MARK: - Устройства

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

    private func refreshDevices() {
        let list = store.devices.map { device in
            DeviceStatus(
                id: device.deviceID,
                name: device.name,
                enabled: device.enabled,
                connected: sessions[device.deviceID] != nil,
                problem: problems[device.deviceID])
        }
        if list != devices {
            devices = list
        }
        onConnectionsChanged?(sessions.count)
    }

    // MARK: - Буфер обмена

    /// Отправить текст, скопированный на этом Mac, всем подключённым устройствам.
    func broadcast(_ text: String) {
        guard text.utf8.count <= Self.maxClipBytes else {
            Log.clipboard.notice("Текст больше 1 МиБ — не отправлен")
            return
        }
        let id = ClipveyCrypto.randomNonce().prefix(16).map { String(format: "%02x", $0) }.joined()
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
        onClipReceived?(clip.text)
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

    /// true — фрагмент новый; false — уже был (пришёл другим путём).
    private func markSeen(_ id: String) -> Bool {
        guard seenClips.insert(id).inserted else { return false }
        seenOrder.append(id)
        if seenOrder.count > Self.seenClipsCapacity {
            seenClips.remove(seenOrder.removeFirst())
        }
        return true
    }

    // MARK: - Объявление и поиск

    private func startListener(port: NWEndpoint.Port) {
        do {
            let parameters = NWParameters.tcp
            parameters.allowLocalEndpointReuse = true
            parameters.includePeerToPeer = false
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
        NWListener.Service(
            name: name,
            type: Self.serviceType,
            domain: nil,
            txtRecord: NWTXTRecord(["id": identity.deviceID, "v": "1", "pair": isPairingMode ? "1" : "0"]))
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
        var found: [String: (name: String, endpoint: NWEndpoint, pairing: Bool)] = [:]
        for result in results {
            guard case .service(let serviceName, _, _, _) = result.endpoint,
                  case .bonjour(let txt) = result.metadata,
                  let id = txt["id"], id != identity.deviceID,
                  found[id] == nil else { continue }
            found[id] = (serviceName, result.endpoint, txt["pair"] == "1")
        }
        browseResults = found
        updateCandidates()
        Task { await maintain() }
    }

    private func updateCandidates() {
        let list = isPairingMode
            ? browseResults
                .filter { $0.value.pairing && store.device(id: $0.key) == nil }
                .map { Candidate(id: $0.key, name: $0.value.name, endpoint: $0.value.endpoint) }
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
            case .pairHello(let peerName, let peerKey):
                await respondToPairing(link: link, peerName: peerName, peerKey: peerKey)
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

        let peerName = try await exchangeReady(link, fallbackName: peer.name)
        adopt(link: link, peer: peer, peerName: peerName, initiatorID: peer.deviceID, endpoint: link.remoteEndpoint, keepPort: true)
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
            for endpoint in endpoints {
                do {
                    try await initiateSession(with: device, via: endpoint)
                    break
                } catch let error as ClipveyError where error.isRejection {
                    Log.network.notice("«\(device.name, privacy: .public)»: \(error.localizedDescription, privacy: .public)")
                    problems[device.deviceID] = error.localizedDescription
                    retryAfter[device.deviceID] = Date().addingTimeInterval(Self.rejectedRetryDelay)
                    refreshDevices()
                    break
                } catch {
                    Log.network.info("Подключение к «\(device.name, privacy: .public)»: \(error.localizedDescription, privacy: .public)")
                }
            }
            connecting.remove(device.deviceID)
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

    private func endpoints(for device: StoredDevice) -> [NWEndpoint] {
        var endpoints: [NWEndpoint] = []
        if let found = browseResults[device.deviceID] {
            endpoints.append(found.endpoint)
        }
        if let host = device.lastHost, device.lastPort > 0, let port = NWEndpoint.Port(rawValue: UInt16(device.lastPort)) {
            endpoints.append(.hostPort(host: NWEndpoint.Host(host), port: port))
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
                throw ClipveyError.protocolViolation("Ответило не то устройство, с которым было связывание")
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

            let peerName = try await exchangeReady(link, fallbackName: device.name)
            adopt(link: link, peer: device, peerName: peerName, initiatorID: identity.deviceID, endpoint: link.remoteEndpoint, keepPort: false)
        } catch {
            link.cancel()
            throw error
        }
    }

    private static func outgoingParameters() -> NWParameters {
        let parameters = NWParameters.tcp
        parameters.includePeerToPeer = false
        return parameters
    }

    // MARK: - Сеансы

    private func exchangeReady(_ link: PeerLink, fallbackName: String) async throws -> String {
        try await link.send(.ready(name: name))
        let message = try await link.receive()
        guard case .ready(let peerName) = message else {
            throw message.unexpected(expecting: "ready")
        }
        return peerName ?? fallbackName
    }

    private func adopt(link: PeerLink, peer: StoredDevice, peerName: String, initiatorID: String, endpoint: (host: String, port: Int)?, keepPort: Bool) {
        let peerID = peer.deviceID
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
        let session = ActiveSession(link: link, peerID: peerID, peerName: peerName, initiatorID: initiatorID)
        sessions[peerID] = session
        disconnectedSince[peerID] = nil
        problems[peerID] = nil
        if let endpoint {
            store.updateEndpoint(id: peerID, name: peerName, host: endpoint.host, port: keepPort ? nil : endpoint.port)
        }
        let direction = initiatorID == identity.deviceID ? "исходящий" : "входящий"
        Log.network.notice("Сеанс с «\(peerName, privacy: .public)» установлен (\(direction, privacy: .public))")
        onEvent?("CONNECTED \(peerName)")
        Task { await runSession(session) }
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
        if sessions[session.peerID] === session {
            sessions[session.peerID] = nil
            disconnectedSince[session.peerID] = Date()
            onEvent?("DISCONNECTED \(session.peerName)")
            refreshDevices()
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

    private func respondToPairing(link: PeerLink, peerName: String, peerKey: Data) async {
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
        incoming = IncomingPairing(peerName: peerName)
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
                commit: ClipveyCrypto.commitment(responderKey: identity.publicKey, initiatorKey: peerKey, responderNonce: nonce)))
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
            savePaired(deviceID: ClipveyCrypto.deviceID(for: peerKey), name: peerName, publicKey: peerKey, endpoint: link.remoteEndpoint, port: Int(Self.defaultPort))
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
            try await link.send(.pairHello(name: name, key: identity.publicKey))
            let commitMessage = try await link.receive()
            guard case .pairCommit(let peerName, let peerKey, let commitment) = commitMessage else {
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
                throw ClipveyError.protocolViolation("Проверка связывания не прошла — возможно, соединение перехвачено")
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
            savePaired(deviceID: ClipveyCrypto.deviceID(for: peerKey), name: peerName, publicKey: peerKey, endpoint: endpoint, port: endpoint?.port)
        } catch {
            link.cancel()
            pairingFailed(error)
        }
    }

    private func savePaired(deviceID: String, name peerName: String, publicKey: Data, endpoint: (host: String, port: Int)?, port: Int?) {
        store.upsert(StoredDevice(
            deviceID: deviceID,
            name: peerName,
            publicKey: publicKey,
            lastHost: endpoint?.host,
            lastPort: port ?? Int(Self.defaultPort),
            enabled: true))
        disconnectedSince[deviceID] = Date()
        problems[deviceID] = nil
        pairingResult = "Связано с «\(peerName)»"
        Log.network.notice("Связывание с «\(peerName, privacy: .public)» завершено")
        onEvent?("PAIRED \(peerName)")
        stopPairingMode(cancelFlows: false)
        refreshDevices()
        Task { await maintain() }
    }

    private func pairingFailed(_ error: Error) {
        let message = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        pairingResult = message
        Log.network.notice("Связывание не удалось: \(message, privacy: .public)")
        onEvent?("PAIRING_FAILED \(message)")
    }
}

/// Сеанс с устройством. Живёт на главном потоке вместе с узлом.
@MainActor
private final class ActiveSession {
    let link: PeerLink
    let peerID: String
    let peerName: String
    let initiatorID: String
    var lastReceived = Date()

    init(link: PeerLink, peerID: String, peerName: String, initiatorID: String) {
        self.link = link
        self.peerID = peerID
        self.peerName = peerName
        self.initiatorID = initiatorID
    }
}
