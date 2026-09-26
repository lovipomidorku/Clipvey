import AppKit
import SwiftUI

/// Состояние синхронизации для значка в строке меню.
enum SyncState {
    /// Нет подключений (или ещё нет связанных устройств): контур.
    case idle
    /// Подключено хотя бы одно устройство: заполненный значок.
    case connected
    /// Все связанные устройства выключены: перечёркнутый контур.
    case disabled
}

/// Значок строки меню. Все три состояния рисуются одинаково — одним размером и одним
/// шаблоном, — чтобы значок не прыгал при смене состояния. Перечёркнутого варианта
/// doc.on.clipboard в SF Symbols нет, поэтому черта дорисовывается поверх контура.
@MainActor
enum MenuBarIcon {
    private static let outline = "doc.on.clipboard"
    private static let filled = "doc.on.clipboard.fill"
    /// Как у системных значков строки меню.
    nonisolated private static let pointSize: CGFloat = 14

    private static var cache: [SyncState: NSImage] = [:]

    static func image(for state: SyncState) -> NSImage {
        if let cached = cache[state] {
            return cached
        }
        let image = draw(state)
        cache[state] = image
        return image
    }

    private static func draw(_ state: SyncState) -> NSImage {
        let canvas = symbol(outline)?.size ?? NSSize(width: 18, height: 18)
        let name = state == .connected ? filled : outline
        let slashed = state == .disabled
        let image = NSImage(size: canvas, flipped: false) { rect in
            // Символ создаётся здесь же: так замыкание не держит NSImage снаружи.
            guard let symbol = MenuBarIcon.symbol(name) else { return false }
            let origin = NSPoint(x: (rect.width - symbol.size.width) / 2, y: (rect.height - symbol.size.height) / 2)
            symbol.draw(in: NSRect(origin: origin, size: symbol.size))
            if slashed {
                MenuBarIcon.drawSlash(in: rect)
            }
            return true
        }
        image.isTemplate = true
        image.accessibilityDescription = "Clipvey"
        return image
    }

    /// Черта из левого верхнего угла в правый нижний, как у системных *.slash:
    /// сначала вырезается широкая полоса, затем поверх рисуется тонкая линия.
    nonisolated private static func drawSlash(in rect: NSRect) {
        let inset: CGFloat = 1.5
        let path = NSBezierPath()
        path.move(to: NSPoint(x: rect.minX + inset, y: rect.maxY - inset))
        path.line(to: NSPoint(x: rect.maxX - inset, y: rect.minY + inset))
        path.lineCapStyle = .round

        guard let context = NSGraphicsContext.current else { return }
        context.saveGraphicsState()
        context.compositingOperation = .destinationOut
        path.lineWidth = 4
        NSColor.black.setStroke()
        path.stroke()
        context.restoreGraphicsState()

        path.lineWidth = 1.5
        NSColor.black.setStroke()
        path.stroke()
    }

    nonisolated private static func symbol(_ name: String) -> NSImage? {
        NSImage(systemSymbolName: name, accessibilityDescription: nil)?
            .withSymbolConfiguration(NSImage.SymbolConfiguration(pointSize: pointSize, weight: .regular))
    }
}

/// Значок в строке меню: меняется вместе с состоянием синхронизации.
struct MenuBarLabel: View {
    let model: AppModel

    var body: some View {
        Image(nsImage: MenuBarIcon.image(for: model.syncState))
            .accessibilityLabel(model.statusSummary)
    }
}

/// Подсказка при наведении на значок. У MenuBarExtra нет своего API для неё,
/// поэтому ищем кнопку значка в окне строки меню нашего процесса.
@MainActor
enum StatusItemTooltip {
    /// false — кнопка значка ещё не создана.
    static func set(_ text: String) -> Bool {
        for window in NSApp.windows where String(describing: type(of: window)).contains("StatusBarWindow") {
            if let button = findButton(in: window.contentView) {
                button.toolTip = text
                return true
            }
        }
        return false
    }

    private static func findButton(in view: NSView?) -> NSButton? {
        guard let view else { return nil }
        if let button = view as? NSButton {
            return button
        }
        for subview in view.subviews {
            if let button = findButton(in: subview) {
                return button
            }
        }
        return nil
    }
}
