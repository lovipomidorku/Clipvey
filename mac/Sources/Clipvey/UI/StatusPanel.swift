import AppKit
import SwiftUI

/// Значок в строке меню и окошко под ним.
///
/// Своё окно вместо MenuBarExtra: у MenuBarExtra размер окна меняет система, причём вокруг центра,
/// поэтому при переходе в настройки и обратно окно дёргалось, а углы на разных страницах отличались.
/// Здесь высоту окна меняет только этот контроллер, одним движением: верхний край стоит под строкой меню,
/// содержимое прижато к верху.
@MainActor
final class StatusBarController: NSObject, NSWindowDelegate {
    private(set) static var shared: StatusBarController?

    /// Ширина содержимого (RootView).
    static let width: CGFloat = 340
    /// Радиус скругления окна.
    private static let cornerRadius: CGFloat = 12
    /// Зазор между строкой меню и окном.
    private static let gapBelowMenuBar: CGFloat = 2

    private let model: AppModel
    private let statusItem: NSStatusItem
    private let panel: StatusPanel
    private var contentHeight: CGFloat = 200
    /// Когда окно закрылось: клик по значку сразу после закрытия (окно потеряло фокус из-за этого же клика)
    /// не должен открыть его снова.
    private var closedAt = Date.distantPast
    private var outsideClickMonitor: Any?

    var isShown: Bool { panel.isVisible }
    var window: NSWindow { panel }

    init(model: AppModel) {
        self.model = model
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        panel = StatusPanel(
            contentRect: NSRect(x: 0, y: 0, width: Self.width, height: contentHeight),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: true)
        super.init()
        Self.shared = self
        setUpPanel()
        setUpButton()
        observeIcon()
    }

    // MARK: - Окно

    private func setUpPanel() {
        panel.isFloatingPanel = true
        panel.level = .popUpMenu
        panel.hidesOnDeactivate = false
        panel.isMovable = false
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.animationBehavior = .utilityWindow
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .transient, .ignoresCycle]
        panel.delegate = self
        panel.onCancel = { [weak self] in self?.close() }

        // Фон как у системных окошек строки меню; скругление — маской, одинаковое при любой высоте.
        let background = NSVisualEffectView()
        background.material = .popover
        background.blendingMode = .behindWindow
        background.state = .active
        background.maskImage = Self.roundedMask(radius: Self.cornerRadius)

        let content = RootView()
            .environment(model)
            .environment(LaunchAtLogin.shared)
            .fixedSize(horizontal: false, vertical: true)
            .background(GeometryReader { proxy in
                Color.clear.preference(key: ContentHeightKey.self, value: proxy.size.height)
            })
            .onPreferenceChange(ContentHeightKey.self) { [weak self] height in
                MainActor.assumeIsolated { self?.contentHeightChanged(height) }
            }
        // Корень всегда размером с окно, содержимое — поверх, у верхнего края. Если корень больше окна
        // (новая страница длиннее, а окно ещё не выросло), NSHostingView ставит его по центру — была видна
        // середина страницы настроек, а потом она прыгала на место.
        let root = Color.clear.overlay(alignment: .top) { content }
        let hosting = NSHostingView(rootView: root)
        // Размер окна задаёт контроллер; своих ограничений размера у хоста нет.
        hosting.sizingOptions = []
        hosting.frame = background.bounds
        hosting.autoresizingMask = [.width, .height]
        background.addSubview(hosting)
        panel.contentView = background
    }

    private func contentHeightChanged(_ height: CGFloat) {
        guard height > 0, abs(height - contentHeight) > 0.5 else { return }
        contentHeight = height
        guard panel.isVisible else { return }
        // Сразу, в том же обновлении, что и новое содержимое: тогда оба попадают в один кадр.
        panel.setFrame(frame(), display: true)
        panel.invalidateShadow()
    }

    /// Рамка окна: ширина содержимого, высота содержимого, верх — под значком.
    private func frame() -> NSRect {
        let height = ceil(contentHeight)
        guard let button = statusItem.button, let buttonWindow = button.window else {
            return NSRect(x: 0, y: 0, width: Self.width, height: height)
        }
        let item = buttonWindow.convertToScreen(button.convert(button.bounds, to: nil))
        let visible = (buttonWindow.screen ?? NSScreen.main)?.visibleFrame ?? item
        // Левый край — под значком, но не за краем экрана.
        let x = min(max(item.minX, visible.minX + 4), visible.maxX - Self.width - 4)
        let top = buttonWindow.frame.minY - Self.gapBelowMenuBar
        return NSRect(x: x, y: top - height, width: Self.width, height: height)
    }

    func show() {
        guard !panel.isVisible else { return }
        panel.setFrame(frame(), display: false)
        panel.makeKeyAndOrderFront(nil)
        panel.invalidateShadow()
        // Без этого система ставит фокус клавиатуры на первую кнопку (шестерёнку) и рисует вокруг неё кольцо.
        // Tab по-прежнему переводит фокус на элементы окна.
        panel.makeFirstResponder(nil)
        DispatchQueue.main.async { [weak self] in
            self?.panel.makeFirstResponder(nil)
        }
        statusItem.button?.highlight(true)
        // Клик в другой программе закрывает окно (окно без активации приложения может и не потерять фокус).
        outsideClickMonitor = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown]) { [weak self] _ in
            MainActor.assumeIsolated { self?.close() }
        }
    }

    func close() {
        guard panel.isVisible else { return }
        panel.orderOut(nil)
        closedAt = Date()
        statusItem.button?.highlight(false)
        if let outsideClickMonitor {
            NSEvent.removeMonitor(outsideClickMonitor)
        }
        outsideClickMonitor = nil
    }

    @objc private func toggle() {
        if panel.isVisible {
            close()
        } else if Date().timeIntervalSince(closedAt) > 0.3 {
            show()
        }
    }

    func windowDidResignKey(_ notification: Notification) {
        close()
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

    // MARK: - Значок

    private func setUpButton() {
        guard let button = statusItem.button else { return }
        button.target = self
        button.action = #selector(toggle)
        // Как у системных значков: окно открывается по нажатию, а не по отпусканию.
        button.sendAction(on: [.leftMouseDown, .rightMouseDown])
    }

    /// Значок и подсказка обновляются при каждом изменении того, из чего они складываются.
    private func observeIcon() {
        let (image, tooltip, summary) = withObservationTracking {
            (MenuBarIcon.image(for: model.syncState, badge: Updater.shared.showsBanner), model.tooltip, model.statusSummary)
        } onChange: { [weak self] in
            Task { @MainActor in self?.observeIcon() }
        }
        guard let button = statusItem.button else { return }
        button.image = image
        button.toolTip = tooltip
        button.setAccessibilityLabel(summary)
    }
}

/// Панель без рамки, которая может принимать ввод с клавиатуры (поля имени, кода).
final class StatusPanel: NSPanel {
    var onCancel: (() -> Void)?

    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }

    /// Esc закрывает окно.
    override func cancelOperation(_ sender: Any?) {
        onCancel?()
    }
}

private struct ContentHeightKey: PreferenceKey {
    static let defaultValue: CGFloat = 0

    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) {
        value = max(value, nextValue())
    }
}
