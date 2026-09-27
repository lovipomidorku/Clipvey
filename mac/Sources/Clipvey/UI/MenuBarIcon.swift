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

    private struct Key: Hashable {
        let state: SyncState
        let badge: Bool
    }

    private static var cache: [Key: NSImage] = [:]

    /// badge — точка в правом верхнем углу: доступно обновление.
    static func image(for state: SyncState, badge: Bool = false) -> NSImage {
        let key = Key(state: state, badge: badge)
        if let cached = cache[key] {
            return cached
        }
        let image = draw(state, badge: badge)
        cache[key] = image
        return image
    }

    private static func draw(_ state: SyncState, badge: Bool) -> NSImage {
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
            if badge {
                MenuBarIcon.drawBadge(in: rect)
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

    /// Точка «есть обновление»: вокруг неё вырезается кольцо, чтобы она не сливалась с контуром.
    nonisolated private static func drawBadge(in rect: NSRect) {
        let diameter: CGFloat = 6
        let dot = NSRect(x: rect.maxX - diameter, y: rect.maxY - diameter, width: diameter, height: diameter)
        guard let context = NSGraphicsContext.current else { return }
        context.saveGraphicsState()
        context.compositingOperation = .destinationOut
        NSColor.black.setFill()
        NSBezierPath(ovalIn: dot.insetBy(dx: -1.5, dy: -1.5)).fill()
        context.restoreGraphicsState()
        NSColor.black.setFill()
        NSBezierPath(ovalIn: dot).fill()
    }

    nonisolated private static func symbol(_ name: String) -> NSImage? {
        NSImage(systemSymbolName: name, accessibilityDescription: nil)?
            .withSymbolConfiguration(NSImage.SymbolConfiguration(pointSize: pointSize, weight: .regular))
    }
}
