import AppKit
import SwiftUI

extension View {
    /// Держит высоту окошка MenuBarExtra равной высоте содержимого.
    ///
    /// Окно MenuBarExtra само умеет только расти: после возврата из настроек оно оставалось
    /// высоким, а содержимое съезжало вниз. Здесь содержимое прижимается к верху, а разница
    /// между высотой окна и высотой содержимого убирается изменением размера окна.
    func fitsWindowHeight() -> some View {
        fixedSize(horizontal: false, vertical: true)
            .background(GeometryReader { proxy in
                Color.clear.preference(key: ContentHeightKey.self, value: proxy.size.height)
            })
            .frame(maxHeight: .infinity, alignment: .top)
            .backgroundPreferenceValue(ContentHeightKey.self) { contentHeight in
                GeometryReader { proxy in
                    // В `--render-preview` окна нет, а NSView рисуется заглушкой.
                    if AppInfo.isBundled && contentHeight > 0 {
                        WindowHeightFitter(excessHeight: proxy.size.height - contentHeight)
                    }
                }
            }
    }
}

private struct ContentHeightKey: PreferenceKey {
    static let defaultValue: CGFloat = 0

    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) {
        value = max(value, nextValue())
    }
}

/// Меняет высоту окна на excessHeight (положительное — окно выше содержимого),
/// оставляя верхний край на месте, у строки меню.
private struct WindowHeightFitter: NSViewRepresentable {
    let excessHeight: CGFloat

    func makeNSView(context: Context) -> NSView {
        NSView()
    }

    func updateNSView(_ view: NSView, context: Context) {
        guard abs(excessHeight) > 0.5, let window = view.window else { return }
        // Целевую высоту считаем сразу: повторные вызовы до применения дадут ту же цель,
        // и окно не ужмётся дважды.
        let targetHeight = window.frame.height - excessHeight
        Task { @MainActor [weak window] in
            guard let window, abs(window.frame.height - targetHeight) > 0.5 else { return }
            var frame = window.frame
            frame.origin.y += frame.height - targetHeight
            frame.size.height = targetHeight
            window.setFrame(frame, display: true)
            Log.app.debug("Высота окна подогнана под содержимое: \(targetHeight, format: .fixed(precision: 0))")
        }
    }
}
