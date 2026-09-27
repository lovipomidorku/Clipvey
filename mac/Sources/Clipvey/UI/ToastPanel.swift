import AppKit
import SwiftUI
import UniformTypeIdentifiers

/// Что сейчас нарисовано в окошке. Отдельно от стопки FileTransfers: пока окошко исчезает, в нём остаётся прежнее.
@MainActor
@Observable
final class ToastStage {
    var toast: FileToast?
    /// Рамки кнопок в координатах окна (сверху вниз) — для нажатий в режиме проверки.
    @ObservationIgnored var buttonFrames: [String: CGRect] = [:]
}

/// Всплывающее окошко передачи файлов — в правом верхнем углу экрана под строкой меню, как уведомления macOS.
/// Вид как у окна значка (StatusBarController): NSVisualEffectView с маской скругления, без рамки.
/// - Не активирует программу и не забирает фокус (панель не становится ключевой), кнопки срабатывают с первого нажатия.
/// - Одно на экране: показывает верхнее окошко стопки FileTransfers.
/// - Если открыто окно значка и они пересекаются, окошко стоит под ним и ездит вместе с ним.
/// - «Готово» и сообщения исчезают сами, но не пока на окошко наведена мышь.
@MainActor
final class ToastController {
    private(set) static var shared: ToastController?

    static let width: CGFloat = 340
    private static let cornerRadius: CGFloat = 12
    /// Отступ от правого края экрана и от строки меню.
    private static let edgeMargin: CGFloat = 10
    /// Зазор между окном значка и окошком под ним.
    private static let gapBelowPanel: CGFloat = 8
    /// На сколько окошко выезжает справа при появлении.
    private static let slide: CGFloat = 24

    let stage = ToastStage()
    let panel: ToastPanel
    private let transfers: FileTransfers
    private var contentHeight: CGFloat = 0
    /// Что показано (id и вид) — чтобы не повторять события и не перезапускать таймер без изменений.
    private var shown: (id: UUID, kind: String)?
    private var hiding = false
    private var presentPending = false
    private var autoHide: Task<Void, Never>?

    init(transfers: FileTransfers) {
        self.transfers = transfers
        panel = ToastPanel(
            contentRect: NSRect(x: 0, y: 0, width: Self.width, height: 80),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: true)
        Self.shared = self
        setUpPanel()
        StatusBarController.shared?.onLayoutChange = { [weak self] in self?.reposition(animated: true) }
        NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.reposition(animated: false) }
        }
        observe()
    }

    private func setUpPanel() {
        panel.isFloatingPanel = true
        // Ниже окна значка (popUpMenu): если они всё же пересекутся, окно значка — сверху.
        panel.level = .statusBar
        panel.hidesOnDeactivate = false
        panel.isMovable = false
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.becomesKeyOnlyIfNeeded = true
        panel.isReleasedWhenClosed = false
        panel.animationBehavior = .none
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .transient, .ignoresCycle]

        let background = NSVisualEffectView()
        background.material = .popover
        background.blendingMode = .behindWindow
        background.state = .active
        background.maskImage = Self.roundedMask(radius: Self.cornerRadius)

        let content = ToastRootView(stage: stage, transfers: transfers)
            .fixedSize(horizontal: false, vertical: true)
            .background(GeometryReader { proxy in
                Color.clear.preference(key: ToastHeightKey.self, value: proxy.size.height)
            })
            .onPreferenceChange(ToastHeightKey.self) { [weak self] height in
                MainActor.assumeIsolated { self?.contentHeightChanged(height) }
            }
        let hosting = FirstClickHostingView(rootView: Color.clear.overlay(alignment: .top) { content })
        hosting.sizingOptions = []
        hosting.frame = background.bounds
        hosting.autoresizingMask = [.width, .height]
        background.addSubview(hosting)
        panel.contentView = background
    }

    // MARK: - Что показывать

    private func observe() {
        let top = withObservationTracking {
            let toast = transfers.visibleToast
            _ = toast?.content.kind
            return toast
        } onChange: { [weak self] in
            Task { @MainActor in self?.observe() }
        }
        update(top)
    }

    private func update(_ top: FileToast?) {
        guard let top else {
            hide()
            return
        }
        let kind = top.content.kind
        if let shown, shown.id == top.id, shown.kind == kind, !hiding {
            return
        }
        shown = (top.id, kind)
        stage.toast = top
        TestHooks.toastShown(top, controller: self)
        scheduleAutoHide(top)
        if !panel.isVisible || hiding {
            // Сначала SwiftUI должен разложить новое содержимое — тогда известна высота.
            presentPending = true
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.03) { [weak self] in
                MainActor.assumeIsolated { self?.present() }
            }
        }
    }

    private func present() {
        guard presentPending else { return }
        presentPending = false
        let target = frame()
        if hiding, panel.isVisible {
            // Исчезало — вернуть на место.
            hiding = false
            animate(to: target, alpha: 1, duration: 0.2)
            return
        }
        hiding = false
        panel.alphaValue = 0
        panel.setFrame(target.offsetBy(dx: Self.slide, dy: 0), display: false)
        panel.orderFrontRegardless()
        panel.invalidateShadow()
        animate(to: target, alpha: 1, duration: 0.28)
    }

    private func hide() {
        autoHide?.cancel()
        presentPending = false
        guard panel.isVisible, !hiding else { return }
        hiding = true
        shown = nil
        TestHooks.emitIfEnabled("TOAST hidden")
        NSAnimationContext.runAnimationGroup({ context in
            context.duration = 0.22
            context.timingFunction = CAMediaTimingFunction(name: .easeIn)
            panel.animator().alphaValue = 0
            panel.animator().setFrame(panel.frame.offsetBy(dx: Self.slide * 0.6, dy: 0), display: true)
        }, completionHandler: { [weak self] in
            MainActor.assumeIsolated {
                guard let self, self.hiding else { return }
                self.hiding = false
                self.panel.orderOut(nil)
                self.stage.toast = nil
            }
        })
    }

    private func animate(to frame: NSRect, alpha: CGFloat, duration: TimeInterval) {
        NSAnimationContext.runAnimationGroup { context in
            context.duration = duration
            context.timingFunction = CAMediaTimingFunction(name: .easeOut)
            panel.animator().alphaValue = alpha
            panel.animator().setFrame(frame, display: true)
        }
    }

    // MARK: - Где

    private func contentHeightChanged(_ height: CGFloat) {
        guard height > 0, abs(height - contentHeight) > 0.5 else { return }
        contentHeight = height
        guard panel.isVisible, !hiding, !presentPending else { return }
        // Высота меняется в том же обновлении, что и содержимое; верхний край на месте.
        var frame = frame()
        frame.origin.x = panel.frame.minX
        panel.setFrame(frame, display: true)
        panel.invalidateShadow()
    }

    /// Окно значка открылось, закрылось или изменило высоту; сменились экраны.
    private func reposition(animated: Bool) {
        guard panel.isVisible, !hiding, !presentPending else { return }
        let target = frame()
        guard target != panel.frame else { return }
        if animated {
            animate(to: target, alpha: 1, duration: 0.2)
        } else {
            panel.setFrame(target, display: true)
        }
    }

    /// Правый верхний угол экрана со строкой меню, где значок Clipvey; под окном значка, если оно открыто и мешает.
    private func frame() -> NSRect {
        let height = ceil(max(contentHeight, 40))
        let screen = StatusBarController.shared?.menuBarScreen ?? NSScreen.main ?? NSScreen.screens.first
        guard let visible = screen?.visibleFrame else {
            return NSRect(x: 0, y: 0, width: Self.width, height: height)
        }
        let x = visible.maxX - Self.width - Self.edgeMargin
        var top = visible.maxY - Self.edgeMargin
        if let status = StatusBarController.shared, status.isShown {
            let main = status.window.frame
            if main.minX < x + Self.width, main.maxX > x {
                top = min(top, main.minY - Self.gapBelowPanel)
            }
        }
        return NSRect(x: x, y: top - height, width: Self.width, height: height)
    }

    // MARK: - Само исчезает

    private func scheduleAutoHide(_ toast: FileToast) {
        autoHide?.cancel()
        let delay: TimeInterval
        switch toast.content {
        case .done: delay = FileTransfers.doneDuration
        case .received: delay = FileTransfers.receivedDuration
        case .notice: delay = FileTransfers.noticeDuration
        default: return
        }
        autoHide = Task { [weak self, weak toast] in
            try? await Task.sleep(for: .seconds(delay))
            // Пока мышь на окошке — ждём; ушла — ещё полторы секунды, вдруг вернётся.
            while let self, self.isMouseInside {
                while self.isMouseInside {
                    try? await Task.sleep(for: .milliseconds(300))
                    if Task.isCancelled { return }
                }
                try? await Task.sleep(for: .seconds(1.5))
            }
            guard !Task.isCancelled, let self, let toast else { return }
            self.transfers.dismiss(toast)
        }
    }

    /// Наведена ли мышь. В режиме проверки настоящая мышь не считается (сценарии не зависят от того, где она
    /// лежит), «наведена» — только по флагу --toast-hover.
    var isMouseInside: Bool {
        guard panel.isVisible, !hiding else { return false }
        if TestHooks.enabled {
            return TestHooks.toastHovered
        }
        return NSMouseInRect(NSEvent.mouseLocation, panel.frame, false)
    }

    private static func roundedMask(radius: CGFloat) -> NSImage {
        let edge = radius * 2 + 1
        let image = NSImage(size: NSSize(width: edge, height: edge), flipped: false) { rect in
            NSColor.black.setFill()
            NSBezierPath(roundedRect: rect, xRadius: radius, yRadius: radius).fill()
            return true
        }
        image.capInsets = NSEdgeInsets(top: radius, left: radius, bottom: radius, right: radius)
        image.resizingMode = .stretch
        return image
    }
}

/// Панель окошка: никогда не становится ключевой — фокус остаётся там, где был.
final class ToastPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

/// Кнопки срабатывают с первого нажатия, хотя окно не ключевое и программа не активна.
final class FirstClickHostingView<Content: View>: NSHostingView<Content> {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

private struct ToastHeightKey: PreferenceKey {
    static let defaultValue: CGFloat = 0

    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) {
        value = max(value, nextValue())
    }
}

// MARK: - Содержимое

private struct ToastRootView: View {
    let stage: ToastStage
    let transfers: FileTransfers

    var body: some View {
        Group {
            if let toast = stage.toast {
                ToastCard(toast: toast, transfers: transfers)
                    .onPreferenceChange(ToastButtonFramesKey.self) { frames in
                        MainActor.assumeIsolated { stage.buttonFrames = frames }
                    }
            }
        }
        .frame(width: ToastController.width)
        // Клавиатура окошку не нужна (окно не ключевое), кольцо фокуса вокруг кнопки — лишнее.
        .focusEffectDisabled()
    }
}

private struct ToastCard: View {
    let toast: FileToast
    let transfers: FileTransfers

    var body: some View {
        HStack(alignment: .top, spacing: 11) {
            ToastIcon(toast: toast)
            VStack(alignment: .leading, spacing: 7) {
                details
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(.leading, 13)
        .padding(.trailing, 12)
        .padding(.vertical, 12)
        .overlay(alignment: .topTrailing) {
            if showsClose {
                CloseButton { transfers.dismiss(toast) }
                    .reportFrame("close")
                    .padding(7)
            }
        }
    }

    private var showsClose: Bool {
        switch toast.content {
        case .offer, .notice, .done, .received: true
        case .downloading, .receiving, .failed: false
        }
    }

    @ViewBuilder
    private var details: some View {
        switch toast.content {
        case .offer:
            Text(offerText)
                .font(.system(size: 13))
                .fixedSize(horizontal: false, vertical: true)
                .padding(.trailing, 16)
            HStack {
                Spacer()
                Button(L("Загрузить", "Download")) { transfers.download(toast) }
                    .buttonStyle(ToastButtonStyle(prominent: true))
                    .reportFrame("download")
            }
        case .downloading, .receiving:
            Text(progressTitle)
                .font(.system(size: 13, weight: .semibold))
                .lineLimit(2)
                .fixedSize(horizontal: false, vertical: true)
            ToastProgressBar(fraction: toast.total > 0 ? Double(min(toast.received, toast.total)) / Double(toast.total) : 0)
            HStack(alignment: .center) {
                Text(progressText)
                    .font(.system(size: 11).monospacedDigit())
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                Spacer(minLength: 8)
                Button(L("Отмена", "Cancel")) { transfers.cancel(toast) }
                    .buttonStyle(ToastButtonStyle())
                    .reportFrame("cancel")
            }
        case .received:
            VStack(alignment: .leading, spacing: 2) {
                Text(L("Готово — можно вставлять", "Done — ready to paste"))
                    .font(.system(size: 13, weight: .semibold))
                    .padding(.trailing, 16)
                Text("\(quotedName)\(more)")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .padding(.trailing, 16)
            }
        case .done(_, let inPasteboard):
            VStack(alignment: .leading, spacing: 2) {
                Text(L("Готово: \(quotedName)\(more)", "Done: \(quotedName)\(more)"))
                    .font(.system(size: 13, weight: .semibold))
                    .lineLimit(2)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.trailing, 16)
                Text(inPasteboard
                     ? L("Скопировано — можно вставлять", "Copied — ready to paste")
                     : L("Сохранено в «Загрузки» → Clipvey", "Saved to Downloads → Clipvey"))
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
            }
            HStack {
                Spacer()
                Button(L("Показать в Finder", "Show in Finder")) { transfers.showInFinder(toast) }
                    .buttonStyle(ToastButtonStyle())
                    .reportFrame("finder")
            }
        case .failed(let problem):
            VStack(alignment: .leading, spacing: 2) {
                Text(problem.title.isEmpty ? L("Не удалось загрузить \(quotedName)", "Couldn’t download \(quotedName)") : problem.title)
                    .font(.system(size: 13, weight: .semibold))
                    .lineLimit(2)
                    .fixedSize(horizontal: false, vertical: true)
                Text(problem.message)
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            HStack {
                if problem.opensPrivacySettings {
                    Button(L("Открыть настройки", "Open Settings")) { transfers.openPrivacySettings() }
                        .buttonStyle(ToastButtonStyle())
                        .reportFrame("settings")
                }
                Spacer()
                Button(L("Закрыть", "Close")) { transfers.dismiss(toast) }
                    .buttonStyle(ToastButtonStyle())
                    .reportFrame("dismiss")
            }
        case .notice(let problem):
            VStack(alignment: .leading, spacing: 2) {
                Text(problem.title)
                    .font(.system(size: 13, weight: .semibold))
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.trailing, 16)
                Text(problem.message)
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            if problem.opensPrivacySettings {
                Button(L("Открыть настройки", "Open Settings")) { transfers.openPrivacySettings() }
                    .buttonStyle(ToastButtonStyle())
                    .reportFrame("settings")
            }
        }
    }

    /// «OFFICE-PC скопировал «отчёт.pdf» (+2 ещё), 1,2 ГБ» — имя устройства жирным.
    private var offerText: AttributedString {
        var device = AttributedString(toast.deviceName)
        device.font = .system(size: 13, weight: .semibold)
        let size = ByteText.string(toast.total)
        return device + AttributedString(L(" скопировал \(quotedName)\(more), \(size)", " copied \(quotedName)\(more), \(size)"))
    }

    private var quotedName: String {
        let name = Self.shortened(toast.firstItem?.path ?? "")
        return L("«\(name)»", "“\(name)”")
    }

    private var more: String {
        toast.moreCount > 0 ? L(" (+\(toast.moreCount) ещё)", " (+\(toast.moreCount) more)") : ""
    }

    /// «Загрузка «отчёт.pdf»» после «Загрузить»; «Получение «отчёт.pdf» с OFFICE-PC» — тихое скачивание.
    private var progressTitle: String {
        if case .receiving = toast.content {
            return L("Получение \(quotedName)\(more) с \(toast.deviceName)", "Receiving \(quotedName)\(more) from \(toast.deviceName)")
        }
        return L("Загрузка \(quotedName)\(more)", "Downloading \(quotedName)\(more)")
    }

    /// «340 МБ из 1,2 ГБ · 45 МБ/с».
    private var progressText: String {
        var text = L("\(ByteText.string(toast.received)) из \(ByteText.string(toast.total))",
                     "\(ByteText.string(toast.received)) of \(ByteText.string(toast.total))")
        if let speed = toast.speed, speed > 0 {
            text += " · \(ByteText.speed(speed))"
        }
        return text
    }

    /// Длинное имя — с многоточием в середине, расширение остаётся видно.
    static func shortened(_ name: String, limit: Int = 44) -> String {
        guard name.count > limit else { return name }
        let tail = min(12, limit / 3)
        return "\(name.prefix(limit - tail - 1))…\(name.suffix(tail))"
    }
}

/// Значок файла или папки (как в Finder); у готового — зелёная галочка, у ошибки — восклицательный знак.
private struct ToastIcon: View {
    let toast: FileToast

    var body: some View {
        Group {
            if case .notice = toast.content {
                Image(systemName: "exclamationmark.triangle.fill")
                    .font(.system(size: 24))
                    .foregroundStyle(.orange)
            } else {
                Image(nsImage: fileIcon)
                    .resizable()
                    .interpolation(.high)
                    .overlay(alignment: .bottomTrailing) { badge.offset(x: 3, y: 2) }
            }
        }
        .frame(width: 34, height: 34)
        .accessibilityHidden(true)
    }

    @ViewBuilder
    private var badge: some View {
        switch toast.content {
        case .done, .received:
            Image(systemName: "checkmark.circle.fill")
                .symbolRenderingMode(.palette)
                .foregroundStyle(.white, .green)
                .font(.system(size: 15))
        case .failed:
            Image(systemName: "exclamationmark.circle.fill")
                .symbolRenderingMode(.palette)
                .foregroundStyle(.white, .orange)
                .font(.system(size: 15))
        default:
            EmptyView()
        }
    }

    private var fileIcon: NSImage {
        guard let item = toast.firstItem else { return NSWorkspace.shared.icon(for: .data) }
        if item.isDirectory {
            return NSWorkspace.shared.icon(for: .folder)
        }
        let type = UTType(filenameExtension: (item.path as NSString).pathExtension) ?? .data
        return NSWorkspace.shared.icon(for: type)
    }
}

/// Кнопки окошка. Окно никогда не становится ключевым, и системные кнопки в нём рисовались бы серыми, как
/// в неактивном окне (главная — тоже), поэтому стиль свой: главная — цвета акцента, остальные — светлая подложка.
private struct ToastButtonStyle: ButtonStyle {
    var prominent = false

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 12, weight: prominent ? .semibold : .regular))
            .foregroundStyle(prominent ? AnyShapeStyle(Color.white) : AnyShapeStyle(.primary))
            .lineLimit(1)
            .padding(.horizontal, 10)
            .frame(minHeight: 22)
            .background(
                RoundedRectangle(cornerRadius: 6, style: .continuous)
                    .fill(prominent
                          ? AnyShapeStyle(Color.accentColor.opacity(configuration.isPressed ? 0.75 : 1))
                          : AnyShapeStyle(Color.primary.opacity(configuration.isPressed ? 0.16 : 0.09))))
            .contentShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
    }
}

/// Полоса прогресса цвета акцента (системная в неключевом окне серая).
private struct ToastProgressBar: View {
    let fraction: Double

    var body: some View {
        GeometryReader { proxy in
            ZStack(alignment: .leading) {
                Capsule().fill(Color.primary.opacity(0.12))
                Capsule().fill(Color.accentColor)
                    .frame(width: max(5, proxy.size.width * min(max(fraction, 0), 1)))
            }
        }
        .frame(height: 5)
        .animation(.linear(duration: 0.25), value: fraction)
        .accessibilityElement()
        .accessibilityLabel(L("Загрузка", "Download progress"))
        .accessibilityValue("\(Int(fraction * 100))%")
    }
}

/// Круглая кнопка «×», как у уведомлений.
private struct CloseButton: View {
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: "xmark")
                .font(.system(size: 8, weight: .bold))
                .foregroundStyle(.secondary)
                .frame(width: 18, height: 18)
                .background(Circle().fill(Color.primary.opacity(0.08)))
                .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .help(L("Закрыть", "Close"))
        .accessibilityLabel(L("Закрыть", "Close"))
    }
}

/// Рамки кнопок в координатах окна: по ним режим проверки «нажимает» кнопки настоящими событиями мыши.
private struct ToastButtonFramesKey: PreferenceKey {
    static let defaultValue: [String: CGRect] = [:]

    static func reduce(value: inout [String: CGRect], nextValue: () -> [String: CGRect]) {
        value.merge(nextValue()) { $1 }
    }
}

private extension View {
    func reportFrame(_ name: String) -> some View {
        background(GeometryReader { proxy in
            Color.clear.preference(key: ToastButtonFramesKey.self, value: [name: proxy.frame(in: .global)])
        })
    }
}
