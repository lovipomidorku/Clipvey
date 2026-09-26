import Foundation

/// Связанное устройство и последний адрес, по которому с ним удалось соединиться.
/// enabled = false — связь сохранена, но синхронизация с устройством выключена.
/// os, form и alias появились после 0.1.0: в старых файлах их нет, тогда они nil.
struct StoredDevice: Codable, Equatable, Sendable {
    let deviceID: String
    /// Имя, которое устройство сообщает о себе (pair_*, ready, info).
    var name: String
    let publicKey: Data
    var lastHost: String?
    var lastPort: Int
    var enabled: Bool
    var os: String?
    var form: String?
    /// Локальный псевдоним, заданный пользователем этого Mac; nil — показывать name.
    var alias: String?

    var type: DeviceType { DeviceType(os: os, form: form) }
}

/// Список связанных устройств в devices.json.
@MainActor
final class DeviceStore {
    private let url: URL
    private(set) var devices: [StoredDevice]

    init(directory: URL) {
        url = directory.appendingPathComponent("devices.json")
        devices = (try? JSONDecoder().decode([StoredDevice].self, from: Data(contentsOf: url))) ?? []
    }

    func device(id: String) -> StoredDevice? {
        devices.first { $0.deviceID == id }
    }

    func upsert(_ device: StoredDevice) {
        devices.removeAll { $0.deviceID == device.deviceID }
        devices.append(device)
        save()
    }

    func remove(id: String) {
        devices.removeAll { $0.deviceID == id }
        save()
    }

    func setEnabled(_ enabled: Bool, id: String) {
        update(id: id) { $0.enabled = enabled }
    }

    /// alias = nil — сбросить псевдоним.
    func setAlias(_ alias: String?, id: String) {
        update(id: id) { $0.alias = alias }
    }

    /// Имя и тип из ready или info. nil — поле не пришло, оставить прежнее.
    func updateInfo(id: String, name: String?, type: DeviceType) {
        update(id: id) { device in
            if let name, !name.isEmpty { device.name = name }
            if let os = type.os { device.os = os }
            if let form = type.form { device.form = form }
        }
    }

    /// port = nil — оставить прежний (у входящего соединения порт слушателя другой стороны неизвестен).
    func updateEndpoint(id: String, host: String, port: Int?) {
        update(id: id) { device in
            device.lastHost = host
            if let port {
                device.lastPort = port
            }
        }
    }

    private func update(id: String, _ change: (inout StoredDevice) -> Void) {
        guard let index = devices.firstIndex(where: { $0.deviceID == id }) else { return }
        var device = devices[index]
        change(&device)
        guard device != devices[index] else { return }
        devices[index] = device
        save()
    }

    private func save() {
        do {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            try encoder.encode(devices).write(to: url, options: .atomic)
        } catch {
            Log.app.error("Не удалось сохранить список устройств: \(error.localizedDescription, privacy: .public)")
        }
    }
}
