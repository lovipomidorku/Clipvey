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

extension View {
    /// Общий вид карточек: отступы и полупрозрачная подложка.
    func cardStyle() -> some View {
        padding(10)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 8))
    }
}
