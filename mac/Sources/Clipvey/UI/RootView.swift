import AppKit
import SwiftUI

/// Окошко в строке меню: шапка, главная страница или настройки, кнопка выхода.
struct RootView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            Group {
                switch model.page {
                case .main: MainView()
                case .settings: SettingsView()
                }
            }
            .padding(12)
            Divider()
            footer
        }
        .frame(width: 340)
        .fitsWindowHeight()
    }

    private var header: some View {
        HStack(spacing: 6) {
            if model.page == .settings {
                Button {
                    model.page = .main
                } label: {
                    Image(systemName: "chevron.left")
                }
                .buttonStyle(.borderless)
                .help("Назад")
                Text("Настройки")
                    .font(.headline)
            } else {
                Text(AppInfo.displayName)
                    .font(.headline)
            }
            Spacer()
            if model.page == .main {
                Button {
                    model.page = .settings
                } label: {
                    Image(systemName: "gearshape")
                }
                .buttonStyle(.borderless)
                .help("Настройки")
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }

    private var footer: some View {
        HStack {
            Spacer()
            Button("Выйти") {
                NSApplication.shared.terminate(nil)
            }
            .buttonStyle(.borderless)
            .keyboardShortcut("q")
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }
}

/// Главная страница: устройства, связывание, подсказка про доступ к буферу.
struct MainView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        if let node = model.node {
            VStack(alignment: .leading, spacing: 12) {
                DevicesSection(node: node)
                PairingSection(node: node)
                if model.bridge.needsAccessPermission && node.devices.contains(where: \.enabled) {
                    AccessHint()
                }
            }
        } else {
            Text(model.startupError ?? "Не удалось запуститься")
                .foregroundStyle(.red)
        }
    }
}

private struct DevicesSection: View {
    let node: ClipveyNode
    @Environment(AppModel.self) private var model

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            SectionHeader(title: summary)
            if node.devices.isEmpty {
                Text("Свяжите этот Mac с другим компьютером, и текст, скопированный на одном, можно будет вставить на другом.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            } else {
                VStack(alignment: .leading, spacing: 10) {
                    ForEach(node.devices) { device in
                        DeviceRow(node: node, device: device)
                    }
                }
                .cardStyle()
            }
        }
    }

    private var summary: String {
        let total = node.devices.count
        guard total > 0 else { return "Устройства" }
        let connected = node.devices.filter(\.connected).count
        return "Устройства: подключено \(connected) из \(total)"
    }
}

private struct DeviceRow: View {
    let node: ClipveyNode
    let device: ClipveyNode.DeviceStatus
    @Environment(AppModel.self) private var model

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 8) {
                Circle()
                    .fill(indicatorColor)
                    .frame(width: 8, height: 8)
                VStack(alignment: .leading, spacing: 1) {
                    Text(device.name)
                    Text(statusText)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Spacer()
                Toggle("Синхронизация", isOn: Binding(
                    get: { device.enabled },
                    set: { node.setEnabled($0, deviceID: device.id) }
                ))
                .labelsHidden()
                .toggleStyle(.switch)
                .controlSize(.mini)
                .help(device.enabled ? "Выключить синхронизацию с этим устройством" : "Включить синхронизацию")
                Button {
                    model.confirmUnpairID = device.id
                } label: {
                    Image(systemName: "xmark.circle")
                }
                .buttonStyle(.borderless)
                .help("Разорвать связь")
            }
            if model.confirmUnpairID == device.id {
                HStack {
                    Text("Разорвать связь?")
                        .font(.caption)
                    Spacer()
                    Button("Разорвать") {
                        node.unpair(deviceID: device.id)
                        model.confirmUnpairID = nil
                    }
                    Button("Нет") {
                        model.confirmUnpairID = nil
                    }
                }
                .controlSize(.small)
            }
        }
    }

    private var indicatorColor: Color {
        if device.connected { return .green }
        if !device.enabled { return .gray.opacity(0.5) }
        return device.problem == nil ? .gray : .orange
    }

    private var statusText: String {
        if !device.enabled { return "Синхронизация выключена" }
        if device.connected { return "Подключено" }
        return device.problem ?? "Не в сети"
    }
}

private struct PairingSection: View {
    let node: ClipveyNode
    @Environment(AppModel.self) private var model

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            if let incoming = node.incoming {
                incomingView(incoming)
            } else if let outgoing = node.outgoing {
                outgoingView(outgoing)
            } else if node.isPairingMode {
                pairingModeView
            } else {
                Button("Связать новое устройство") {
                    model.codeInput = ""
                    node.startPairingMode()
                }
            }
            if let result = node.pairingResult {
                Text(result)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    /// Роль R: этот Mac показывает код.
    private func incomingView(_ incoming: ClipveyNode.IncomingPairing) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            SectionHeader(title: "Связывание с «\(incoming.peerName)»")
            if let code = incoming.code {
                Text(Self.formatted(code))
                    .font(.system(size: 30, weight: .semibold, design: .monospaced))
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity)
            }
            Text(incoming.verified
                 ? "«\(incoming.peerName)» подтвердил код ✓ Нажмите «Готово»."
                 : "Введите этот код на «\(incoming.peerName)».")
                .font(.callout)
                .fixedSize(horizontal: false, vertical: true)
            HStack {
                Button("Готово") { node.confirmIncoming() }
                    .disabled(!incoming.verified)
                    .keyboardShortcut(.defaultAction)
                Button("Отмена") { node.cancelIncoming() }
            }
        }
        .cardStyle()
    }

    /// Роль I: пользователь вводит код с экрана другого устройства.
    @ViewBuilder
    private func outgoingView(_ outgoing: ClipveyNode.OutgoingPairing) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            switch outgoing {
            case .connecting(let name):
                SectionHeader(title: "Связывание с «\(name)»")
                Text("Подключение…").font(.callout)
            case .enterCode(let name):
                SectionHeader(title: "Связывание с «\(name)»")
                Text("Введите код с экрана «\(name)»:").font(.callout)
                HStack {
                    TextField("000000", text: Binding(
                        get: { model.codeInput },
                        set: { model.codeInput = String($0.filter { $0.isASCII && $0.isNumber }.prefix(6)) }
                    ))
                    .font(.system(size: 20, design: .monospaced))
                    .frame(width: 110)
                    .onSubmit(submit)
                    Button("Подтвердить", action: submit)
                        .disabled(model.codeInput.count != 6)
                        .keyboardShortcut(.defaultAction)
                }
            case .waitingConfirmation(let name):
                SectionHeader(title: "Связывание с «\(name)»")
                Text("Код верный ✓ Нажмите «Готово» на «\(name)».").font(.callout)
            }
            Button("Отмена") { node.cancelOutgoing() }
        }
        .cardStyle()
    }

    private var pairingModeView: some View {
        VStack(alignment: .leading, spacing: 8) {
            SectionHeader(title: "Режим связывания")
            Text("Нажмите «Связать» и на другом компьютере. Затем выберите его здесь или этот Mac — там.")
                .font(.callout)
                .fixedSize(horizontal: false, vertical: true)
            if node.candidates.isEmpty {
                HStack(spacing: 6) {
                    ProgressView().controlSize(.small)
                    Text("Поиск устройств…").font(.callout).foregroundStyle(.secondary)
                }
            } else {
                ForEach(node.candidates) { candidate in
                    HStack {
                        Text(candidate.name)
                        Spacer()
                        Button("Связать") {
                            model.codeInput = ""
                            node.pair(with: candidate)
                        }
                    }
                }
            }
            Button("Закрыть") { node.stopPairingMode() }
        }
        .cardStyle()
    }

    private func submit() {
        guard model.codeInput.count == 6 else { return }
        node.submitCode(model.codeInput)
        model.codeInput = ""
    }

    private static func formatted(_ code: String) -> String {
        "\(code.prefix(3)) \(code.suffix(3))"
    }
}

private struct AccessHint: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("macOS спрашивает разрешение, когда программа читает буфер обмена. Чтобы вопрос не появлялся при каждом копировании, выберите для Clipvey «Всегда разрешать» в Системных настройках → «Конфиденциальность и безопасность».")
                .font(.caption)
                .foregroundStyle(.orange)
                .fixedSize(horizontal: false, vertical: true)
            Button("Открыть настройки конфиденциальности") {
                if let url = URL(string: "x-apple.systempreferences:com.apple.settings.PrivacySecurity.extension") {
                    NSWorkspace.shared.open(url)
                }
            }
            .buttonStyle(.link)
            .font(.caption)
        }
    }
}

/// Настройки приложения.
struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @Environment(LaunchAtLogin.self) private var launchAtLogin

    var body: some View {
        @Bindable var model = model
        VStack(alignment: .leading, spacing: 10) {
            VStack(alignment: .leading, spacing: 8) {
                SettingsToggle(title: "Запускать при входе в систему", isOn: Binding(
                    get: { launchAtLogin.isEnabled },
                    set: { launchAtLogin.setEnabled($0) }
                ))
                .disabled(!launchAtLogin.isAvailable)
                if launchAtLogin.requiresApproval {
                    Text("Нужно разрешить в Системных настройках → Основные → Объекты входа.")
                        .font(.caption)
                        .foregroundStyle(.orange)
                }
                if let error = launchAtLogin.lastError {
                    Text("Не удалось изменить автозапуск: \(error)")
                        .font(.caption)
                        .foregroundStyle(.red)
                }
                SettingsToggle(title: "Не давать Mac засыпать, пока подключены устройства", isOn: $model.keepAwake)
            }
            .cardStyle()

            if let node = model.node {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Этот Mac: \(node.name)")
                    Text("ID: \(node.identity.deviceID.prefix(8))…  Порт: \(node.port.map(String.init) ?? "—")")
                        .foregroundStyle(.secondary)
                }
                .font(.caption)
            }
        }
        .onAppear { launchAtLogin.refresh() }
    }
}
