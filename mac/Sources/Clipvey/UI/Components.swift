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
            item(1, L("Нажмите «Связать» на обоих", "Click Pair on both"))
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

extension View {
    /// Общий вид карточек: отступы и полупрозрачная подложка.
    func cardStyle() -> some View {
        padding(10)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 8))
    }
}
