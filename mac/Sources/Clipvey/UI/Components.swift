import SwiftUI

/// Заголовок группы.
struct SectionHeader: View {
    let title: String

    var body: some View {
        Text(title)
            .font(.subheadline.weight(.semibold))
            .foregroundStyle(.secondary)
    }
}

/// Строка настроек: подпись слева, переключатель справа.
struct SettingsToggle: View {
    let title: String
    let isOn: Binding<Bool>

    var body: some View {
        HStack {
            Text(title)
                .fixedSize(horizontal: false, vertical: true)
            Spacer()
            Toggle(title, isOn: isOn)
                .labelsHidden()
                .toggleStyle(.switch)
                .controlSize(.small)
        }
    }
}

/// Строка настроек «Язык»: смена применяется сразу, без перезапуска.
struct LanguagePicker: View {
    var body: some View {
        @Bindable var language = Language.shared
        HStack {
            Text(L("Язык", "Language"))
            Spacer()
            Picker(L("Язык", "Language"), selection: $language.choice) {
                ForEach(AppLanguage.allCases) { option in
                    Text(option.title).tag(option)
                }
            }
            .labelsHidden()
            .pickerStyle(.menu)
            .controlSize(.small)
            .fixedSize()
        }
    }
}

/// Шаги связывания 1–2–3; текущий выделен, пройденные отмечены галочкой.
struct PairingSteps: View {
    /// Номер текущего шага, 1…3.
    let current: Int

    var body: some View {
        HStack(alignment: .top, spacing: 6) {
            item(1, L("«Добавить устройство» на обоих", "Add Device on both"))
            item(2, L("Выберите устройство или введите код", "Choose a device or enter the code"))
            item(3, L("Подтвердите", "Confirm"))
        }
    }

    private func item(_ number: Int, _ title: String) -> some View {
        let isCurrent = number == current
        let isDone = number < current
        return VStack(spacing: 4) {
            ZStack {
                Circle()
                    .fill(isCurrent ? Color.accentColor : isDone ? Color.accentColor.opacity(0.2) : Color.clear)
                Circle()
                    .strokeBorder(isCurrent || isDone ? Color.clear : Color.secondary.opacity(0.5))
                if isDone {
                    Image(systemName: "checkmark")
                        .font(.system(size: 9, weight: .bold))
                        .foregroundStyle(Color.accentColor)
                } else {
                    Text("\(number)")
                        .font(.system(size: 11, weight: .semibold).monospacedDigit())
                        .foregroundStyle(isCurrent ? Color.white : Color.secondary)
                }
            }
            .frame(width: 20, height: 20)
            Text(title)
                .font(.caption2.weight(isCurrent ? .semibold : .regular))
                .foregroundStyle(isCurrent ? .primary : .secondary)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, alignment: .top)
        .accessibilityElement(children: .combine)
    }
}

/// Обратный отсчёт до закрытия режима связывания. @State без Xcode недоступен,
/// поэтому время берётся из TimelineView, а срок — из узла.
struct PairingCountdown: View {
    enum Style {
        /// «Осталось 1:45» — в заголовке.
        case remaining
        /// «Код действует ещё 1:45» — под кодом.
        case codeValidity
    }

    let deadline: Date
    let style: Style

    var body: some View {
        TimelineView(.periodic(from: .now, by: 1)) { context in
            // Узел закрывает режим с опозданием до 2 с, поэтому ниже нуля не опускаемся.
            let seconds = max(0, Int(deadline.timeIntervalSince(context.date).rounded(.up)))
            let time = String(format: "%d:%02d", seconds / 60, seconds % 60)
            Text(style == .remaining
                 ? L("Осталось \(time)", "\(time) left")
                 : L("Код действует ещё \(time)", "Code expires in \(time)"))
                .font(.caption.monospacedDigit())
                .foregroundStyle(seconds <= 20 ? Color.orange : Color.secondary)
        }
    }
}

/// Код связывания крупно, моноширинным шрифтом, на выделенной подложке.
struct CodeBlock: View {
    let code: String
    let deadline: Date?

    var body: some View {
        VStack(spacing: 4) {
            Text("\(code.prefix(3)) \(code.suffix(3))")
                .font(.system(size: 34, weight: .semibold, design: .monospaced))
                .kerning(2)
                .textSelection(.enabled)
            if let deadline {
                PairingCountdown(deadline: deadline, style: .codeValidity)
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 10)
        .background(Color.accentColor.opacity(0.12), in: RoundedRectangle(cornerRadius: 8))
        .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(Color.accentColor.opacity(0.35)))
    }
}

// MARK: - Устройства

extension DeviceType {
    /// Значок SF Symbols по os и form (docs/protocol.md, «Тип устройства»).
    /// Незнакомое или отсутствующее значение (например, у версии 0.1.0) — общий значок сетевого устройства:
    /// монитор (display) неотличим от desktopcomputer.
    var symbolName: String {
        if form == "laptop" { return "laptopcomputer" }
        switch os {
        case "mac": return "desktopcomputer"
        case "windows", "linux": return "pc"
        default: return "rectangle.connected.to.line.below"
        }
    }
}

/// Значок типа устройства; если задан цвет, в углу — точка состояния подключения.
struct DeviceIcon: View {
    let type: DeviceType
    var indicator: Color?

    var body: some View {
        Image(systemName: type.symbolName)
            .font(.system(size: 17))
            .foregroundStyle(.secondary)
            .frame(width: 26, height: 22)
            .overlay(alignment: .bottomTrailing) {
                if let indicator {
                    Circle()
                        .fill(indicator)
                        .frame(width: 8, height: 8)
                        // Кольцо цвета фона отделяет точку от контура значка.
                        .overlay(Circle().strokeBorder(Color(nsColor: .windowBackgroundColor), lineWidth: 1.5).padding(-1.5))
                        .offset(x: 2, y: 1)
                }
            }
            .accessibilityHidden(true)
    }
}

/// Имя устройства в списке: псевдоним (или имя) и, если есть псевдоним, мелко — имя, которое сообщает само устройство.
struct DeviceTitle: View {
    let displayName: String
    let name: String
    let hasAlias: Bool

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 5) {
            Text(displayName)
                .lineLimit(1)
                .layoutPriority(1)
            if hasAlias {
                Text(name)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }
        }
        .help(hasAlias
              ? L("Имя на самом устройстве: \(name)", "Name on the device itself: \(name)")
              : name)
    }
}

/// Левая часть строки устройства: значок с точкой состояния, имя и состояние.
/// Отдельно от кнопок, чтобы её можно было отрисовать без узла.
struct DeviceSummary: View {
    let device: ClipveyNode.DeviceStatus
    /// Своя настройка «Передавать картинки». Когда она выключена, пометка «только текст» не показывается:
    /// иначе она перекладывала бы на другое устройство то, что выключено здесь.
    let imagesEnabled: Bool

    var body: some View {
        HStack(spacing: 8) {
            DeviceIcon(type: device.type, indicator: indicatorColor)
            VStack(alignment: .leading, spacing: 1) {
                DeviceTitle(displayName: device.displayName, name: device.name, hasAlias: device.alias != nil)
                Text(statusText)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .help(isTextOnly
                          ? L("Это устройство не принимает картинки: на нём выключена передача картинок или стоит старая версия Clipvey.",
                              "This device doesn’t accept images: image sync is off there, or it runs an older Clipvey.")
                          : "")
            }
        }
    }

    private var isTextOnly: Bool {
        imagesEnabled && device.enabled && device.connected && !device.acceptsImages
    }

    private var indicatorColor: Color {
        if device.connected { return .green }
        if !device.enabled { return .gray.opacity(0.5) }
        return device.problem == nil ? .gray : .orange
    }

    private var statusText: String {
        if !device.enabled { return L("Синхронизация выключена", "Sync is off") }
        if device.connected {
            return isTextOnly ? L("Подключено · только текст", "Connected · text only") : L("Подключено", "Connected")
        }
        return device.problem?.text ?? L("Не в сети", "Offline")
    }
}

/// Строка ввода имени не длиннее 63 байт UTF-8, как у ClipveyNode.normalizedName, но без обрезки
/// пробелов: иначе при наборе нельзя поставить пробел между словами. Пробелы по краям убирает сохранение.
func limitedNameInput(_ raw: String) -> String {
    var result = ""
    for character in raw where character != "\n" && character != "\r" {
        guard result.utf8.count + String(character).utf8.count <= 63 else { break }
        result.append(character)
    }
    return result
}

extension View {
    /// Общий вид карточек: отступы и полупрозрачная подложка.
    func cardStyle() -> some View {
        padding(10)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 8))
    }
}

// MARK: - Тексты для кодов узла

extension FailureReason {
    /// Причина неудачи на языке интерфейса (узел отдаёт только код).
    @MainActor var text: String {
        switch self {
        case .notPairing: L("На другом устройстве не открыт режим связывания", "Pairing isn’t open on the other device")
        case .busy: L("Другое устройство уже связывается с кем-то", "The other device is already pairing with someone")
        case .peerCodeMismatch: L("На другом устройстве введён неверный код", "A wrong code was entered on the other device")
        case .peerCancelled: L("Связывание отменено на другом устройстве", "Pairing was cancelled on the other device")
        case .commitMismatch: L("Проверка связывания не прошла — возможно, соединение перехвачено", "Pairing check failed — the connection may be intercepted")
        case .unknownDevice: L("Другое устройство не знает этот Mac — свяжите заново", "The other device doesn’t know this Mac — pair again")
        case .disabled: L("На другом устройстве синхронизация с этим Mac выключена", "The other device has sync with this Mac turned off")
        case .rejected(let reason): L("Другое устройство отказало: \(reason)", "The other device refused: \(reason)")
        case .codeMismatch: L("Код не совпал", "The code doesn’t match")
        case .cancelled: L("Связывание отменено", "Pairing cancelled")
        case .wrongDevice: L("По адресу ответило другое устройство", "A different device answered at this address")
        case .connectionFailed: L("Нет соединения", "Couldn’t connect")
        case .protocolError: L("Ошибка обмена с другим устройством", "Communication error with the other device")
        }
    }
}

extension ClipveyNode.PairingResult {
    /// Итог связывания на языке интерфейса.
    @MainActor var text: String {
        switch self {
        case .paired(let name): L("Связано с «\(name)»", "Paired with “\(name)”")
        case .failed(let reason): reason.text
        }
    }
}
