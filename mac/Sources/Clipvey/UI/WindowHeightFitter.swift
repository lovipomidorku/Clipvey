import AppKit
import SwiftUI

extension View {
    /// Держит высоту окошка MenuBarExtra равной высоте содержимого.
    ///
    /// Окно MenuBarExtra само умеет только расти: после возврата из настроек оно оставалось
    /// высоким, а содержимое съезжало вниз. Здесь высота окна выставляется прямо равной высоте
    /// содержимого, а верхний край привязывается к строке меню.
    func fitsWindowHeight() -> some View {
        fixedSize(horizontal: false, vertical: true)
            .background(GeometryReader { proxy in
                // В `--render-preview` окна нет, а NSView рисуется заглушкой.
                if AppInfo.isBundled {
                    WindowHeightFitter(contentHeight: proxy.size.height)
                }
            })
    }
}

/// Ставит высоту окна равной contentHeight, верхний край — сразу под строкой меню.
private struct WindowHeightFitter: NSViewRepresentable {
    let contentHeight: CGFloat

    /// Окно MenuBarExtra стоит на столько точек ниже строки меню.
    private static let gapBelowMenuBar: CGFloat = 2

    func makeNSView(context: Context) -> NSView {
        NSView()
    }

    func updateNSView(_ view: NSView, context: Context) {
        let height = contentHeight
        guard height > 0 else { return }
        // Сразу и ещё раз чуть позже: MenuBarExtra после смены содержимого может сам вернуть окну прежний размер.
        for delay in [0.0, 0.15] {
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak view] in
                guard let window = view?.window else { return }
                Self.fit(window, height: height)
            }
        }
    }

    @MainActor
    private static func fit(_ window: NSWindow, height: CGFloat) {
        let statusBar = NSApp.windows.first { String(describing: type(of: $0)).contains("StatusBarWindow") }
        let top = statusBar.map { $0.frame.minY - gapBelowMenuBar } ?? window.frame.maxY
        var frame = window.frame
        guard abs(frame.height - height) > 0.5 || abs(frame.maxY - top) > 0.5 else { return }
        frame.size.height = height
        frame.origin.y = top - height
        window.setFrame(frame, display: true)
        Log.app.debug("Окно: высота \(height, format: .fixed(precision: 0)), верх \(top, format: .fixed(precision: 0))")
    }
}
