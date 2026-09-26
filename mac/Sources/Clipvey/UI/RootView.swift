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
                .help(L("Назад", "Back"))
                Text(L("Настройки", "Settings"))
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
                .help(L("Настройки", "Settings"))
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }

    private var footer: some View {
        HStack {
            Spacer()
            Button(L("Выйти", "Quit")) {
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
                if let lastSync = model.lastSyncText {
                    Label {
                        Text("\(L("Последняя синхронизация", "Last sync")): \(lastSync)")
                    } icon: {
                        Image(systemName: "arrow.left.arrow.right")
                    }
                    .font(.caption)
                    .foregroundStyle(.secondary)
                }
                PairingSection(node: node)
                if model.bridge.needsAccessPermission && node.devices.contains(where: \.enabled) {
                    AccessHint()
                }
            }
        } else {
            Text(model.startupError.map { L("Не удалось загрузить ключ устройства: \($0)", "Couldn’t load the device key: \($0)") }
                 ?? L("Не удалось запуститься", "Couldn’t start"))
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
                Text(L("Свяжите этот Mac с другим компьютером, и текст, скопированный на одном, можно будет вставить на другом.",
                       "Pair this Mac with another computer to copy text on one and paste it on the other."))
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
        guard total > 0 else { return L("Устройства", "Devices") }
        let connected = node.devices.filter(\.connected).count
        return L("Устройства: подключено \(connected) из \(total)", "Devices: \(connected) of \(total) connected")
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
                Toggle(L("Синхронизация", "Sync"), isOn: Binding(
                    get: { device.enabled },
                    set: { node.setEnabled($0, deviceID: device.id) }
                ))
                .labelsHidden()
                .toggleStyle(.switch)
                .controlSize(.mini)
                .help(device.enabled
                      ? L("Выключить синхронизацию с этим устройством", "Turn off sync with this device")
                      : L("Включить синхронизацию", "Turn on sync"))
                Button {
                    model.confirmUnpairID = device.id
                } label: {
                    Image(systemName: "xmark.circle")
                }
                .buttonStyle(.borderless)
                .help(L("Разорвать связь", "Unpair"))
            }
            if model.confirmUnpairID == device.id {
                HStack {
                    Text(L("Разорвать связь?", "Unpair this device?"))
                        .font(.caption)
                    Spacer()
                    Button(L("Разорвать", "Unpair")) {
                        node.unpair(deviceID: device.id)
                        model.confirmUnpairID = nil
                    }
                    Button(L("Нет", "Cancel")) {
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
        if !device.enabled { return L("Синхронизация выключена", "Sync is off") }
        if device.connected { return L("Подключено", "Connected") }
        return device.problem ?? L("Не в сети", "Offline")
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
                Button(L("Связать новое устройство", "Pair New Device…")) {
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

    /// Общая рамка связывания: заголовок с обратным отсчётом, шаги 1–2–3 и содержимое шага.
    private func pairingCard<Content: View>(title: String, step: Int, showsCountdown: Bool = true, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .firstTextBaseline) {
                SectionHeader(title: title)
                Spacer()
                if showsCountdown, let deadline = node.pairingDeadline {
                    PairingCountdown(deadline: deadline, style: .remaining)
                }
            }
            PairingSteps(current: step)
            content()
        }
        .cardStyle()
    }

    /// Роль R: этот Mac показывает код.
    private func incomingView(_ incoming: ClipveyNode.IncomingPairing) -> some View {
        pairingCard(title: L("Связывание с «\(incoming.peerName)»", "Pairing with “\(incoming.peerName)”"),
                    step: incoming.verified ? 3 : 2,
                    showsCountdown: incoming.code == nil) {
            if let code = incoming.code {
                CodeBlock(code: code, deadline: node.pairingDeadline)
            } else {
                progress(L("Подключение…", "Connecting…"))
            }
            Text(incoming.verified
                 ? L("«\(incoming.peerName)» подтвердил код ✓ Нажмите «Готово».", "“\(incoming.peerName)” confirmed the code ✓ Click Done.")
                 : L("Введите этот код на «\(incoming.peerName)».", "Enter this code on “\(incoming.peerName)”."))
                .font(.callout)
                .fixedSize(horizontal: false, vertical: true)
            HStack {
                Button(L("Готово", "Done")) { node.confirmIncoming() }
                    .disabled(!incoming.verified)
                    .keyboardShortcut(.defaultAction)
                Button(L("Отмена", "Cancel")) { node.cancelIncoming() }
            }
        }
    }

    /// Роль I: пользователь вводит код с экрана другого устройства.
    private func outgoingView(_ outgoing: ClipveyNode.OutgoingPairing) -> some View {
        let name: String
        let step: Int
        switch outgoing {
        case .connecting(let peer), .enterCode(let peer):
            name = peer
            step = 2
        case .waitingConfirmation(let peer):
            name = peer
            step = 3
        }
        return pairingCard(title: L("Связывание с «\(name)»", "Pairing with “\(name)”"), step: step) {
            switch outgoing {
            case .connecting:
                progress(L("Подключение…", "Connecting…"))
            case .enterCode:
                Text(L("Введите код с экрана «\(name)»:", "Enter the code shown on “\(name)”:"))
                    .font(.callout)
                    .fixedSize(horizontal: false, vertical: true)
                HStack {
                    TextField("000 000", text: Binding(
                        get: { model.codeInput },
                        set: { model.codeInput = String($0.filter { $0.isASCII && $0.isNumber }.prefix(6)) }
                    ))
                    .font(.system(size: 24, weight: .semibold, design: .monospaced))
                    .frame(width: 130)
                    .onSubmit(submit)
                    Button(L("Подтвердить", "Confirm"), action: submit)
                        .disabled(model.codeInput.count != 6)
                        .keyboardShortcut(.defaultAction)
                }
            case .waitingConfirmation:
                Text(L("Код верный ✓ Нажмите «Готово» на «\(name)».", "Code is correct ✓ Click Done on “\(name)”."))
                    .font(.callout)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Button(L("Отмена", "Cancel")) { node.cancelOutgoing() }
        }
    }

    private var pairingModeView: some View {
        pairingCard(title: L("Режим связывания", "Pairing"), step: node.candidates.isEmpty ? 1 : 2) {
            Text(L("Нажмите «Связать» и на другом компьютере. Затем выберите его здесь или этот Mac — там.",
                    "Click Pair on the other computer too. Then choose it here, or choose this Mac there."))
                .font(.callout)
                .fixedSize(horizontal: false, vertical: true)
            if node.candidates.isEmpty {
                progress(L("Поиск устройств…", "Looking for devices…"))
            } else {
                ForEach(node.candidates) { candidate in
                    HStack {
                        Text(candidate.name)
                        Spacer()
                        Button(L("Связать", "Pair")) {
                            model.codeInput = ""
                            node.pair(with: candidate)
                        }
                    }
                }
            }
            Button(L("Закрыть", "Close")) { node.stopPairingMode() }
        }
    }

    private func progress(_ text: String) -> some View {
        HStack(spacing: 6) {
            ProgressView().controlSize(.small)
            Text(text).font(.callout).foregroundStyle(.secondary)
        }
    }

    private func submit() {
        guard model.codeInput.count == 6 else { return }
        node.submitCode(model.codeInput)
        model.codeInput = ""
    }
}

private struct AccessHint: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(L("macOS спрашивает разрешение, когда программа читает буфер обмена. Чтобы вопрос не появлялся при каждом копировании, выберите для Clipvey «Всегда разрешать» в Системных настройках → «Конфиденциальность и безопасность».",
                    "macOS asks for permission when an app reads the clipboard. To stop the prompt on every copy, choose Always Allow for Clipvey in System Settings → Privacy & Security."))
                .font(.caption)
                .foregroundStyle(.orange)
                .fixedSize(horizontal: false, vertical: true)
            Button(L("Открыть настройки конфиденциальности", "Open Privacy Settings")) {
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
                SettingsToggle(title: L("Запускать при входе в систему", "Open at login"), isOn: Binding(
                    get: { launchAtLogin.isEnabled },
                    set: { launchAtLogin.setEnabled($0) }
                ))
                .disabled(!launchAtLogin.isAvailable)
                if launchAtLogin.requiresApproval {
                    Text(L("Нужно разрешить в Системных настройках → Основные → Объекты входа.", "Allow it in System Settings → General → Login Items."))
                        .font(.caption)
                        .foregroundStyle(.orange)
                }
                if let error = launchAtLogin.lastError {
                    Text(L("Не удалось изменить автозапуск: \(error)", "Couldn’t change the login item: \(error)"))
                        .font(.caption)
                        .foregroundStyle(.red)
                }
                SettingsToggle(title: L("Не давать Mac засыпать, пока подключены устройства", "Keep Mac awake while devices are connected"), isOn: $model.keepAwake)
                LanguagePicker()
            }
            .cardStyle()

            if let node = model.node {
                VStack(alignment: .leading, spacing: 4) {
                    Text(L("Этот Mac: \(node.name)", "This Mac: \(node.name)"))
                    Text("ID: \(node.identity.deviceID.prefix(8))…  \(L("Порт", "Port")): \(node.port.map(String.init) ?? "—")")
                        .foregroundStyle(.secondary)
                }
                .font(.caption)
            }
        }
        .onAppear { launchAtLogin.refresh() }
    }
}
