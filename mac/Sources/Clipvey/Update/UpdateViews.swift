import SwiftUI

/// Полоса «Доступна версия X — обновить?» вверху главной страницы.
struct UpdateBanner: View {
    var body: some View {
        let updater = Updater.shared
        if updater.showsBanner, let release = updater.available {
            VStack(alignment: .leading, spacing: 8) {
                Label {
                    Text(L("Доступна версия \(release.version.description) — обновить?",
                           "Version \(release.version.description) is available. Update?"))
                        .fixedSize(horizontal: false, vertical: true)
                } icon: {
                    Image(systemName: "arrow.down.circle.fill")
                        .foregroundStyle(Color.accentColor)
                }
                switch updater.phase {
                case .downloading, .installing:
                    HStack(spacing: 6) {
                        ProgressView().controlSize(.small)
                        Text(updater.phase == .downloading
                             ? L("Загрузка и проверка…", "Downloading and verifying…")
                             : L("Установка…", "Installing…"))
                            .font(.callout)
                            .foregroundStyle(.secondary)
                    }
                case .idle, .checking:
                    HStack {
                        Button(L("Обновить", "Update")) {
                            Task { await updater.install() }
                        }
                        .disabled(updater.phase != .idle)
                        Button(L("Позже", "Later")) { updater.dismiss() }
                    }
                    .controlSize(.small)
                }
                UpdateNoticeText()
            }
            .padding(10)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Color.accentColor.opacity(0.12), in: RoundedRectangle(cornerRadius: 8))
            .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(Color.accentColor.opacity(0.35)))
        }
    }
}

/// Пункты настроек: автоматическая проверка и «Проверить сейчас».
struct UpdateSettingsSection: View {
    var body: some View {
        @Bindable var updater = Updater.shared
        VStack(alignment: .leading, spacing: 8) {
            SettingsToggle(title: L("Проверять обновления автоматически", "Check for updates automatically"),
                           isOn: $updater.checksAutomatically)
            HStack {
                Text(updater.currentVersion.map { L("Версия \($0.description)", "Version \($0.description)") }
                     ?? L("Версия неизвестна", "Unknown version"))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Spacer()
                if updater.phase == .checking {
                    ProgressView().controlSize(.small)
                }
                Button(L("Проверить сейчас", "Check Now")) {
                    Task { await updater.check(manual: true) }
                }
                .controlSize(.small)
                .disabled(updater.phase != .idle || updater.currentVersion == nil)
            }
            if let release = updater.available, updater.dismissed {
                Button(L("Обновить до \(release.version.description)", "Update to \(release.version.description)")) {
                    Task { await updater.install() }
                }
                .controlSize(.small)
                .disabled(updater.phase != .idle)
            }
            if !updater.showsBanner {
                UpdateNoticeText()
            }
        }
    }
}

/// Итог проверки или установки одной строкой.
private struct UpdateNoticeText: View {
    var body: some View {
        if let notice = Updater.shared.notice {
            Text(notice.text)
                .font(.caption)
                .foregroundStyle(notice.isError ? Color.red : Color.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }
}
