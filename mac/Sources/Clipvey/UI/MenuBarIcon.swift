import AppKit
import SwiftUI

/// Состояние синхронизации для значка в строке меню.
enum SyncState {
    /// Нет подключений (или ещё нет связанных устройств): силуэт с крестиком.
    case idle
    /// Подключено хотя бы одно устройство: силуэт.
    case connected
    /// Все связанные устройства выключены: бледный силуэт с крестиком.
    case disabled
}

/// Значок строки меню: силуэт значка приложения — планшет с зажимом и документ с загнутым углом.
/// Нет подключений — в углу крестик; всё выключено — силуэт бледнее и с крестиком; есть обновление — точка.
/// Все состояния одного размера и рисуются шаблоном: цвет берётся от строки меню.
@MainActor
enum MenuBarIcon {
    /// Размер значка в точках.
    nonisolated private static let side: CGFloat = 18

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
        let image = NSImage(size: NSSize(width: side, height: side), flipped: false) { rect in
            MenuBarIcon.draw(in: rect, state: state, badge: badge)
            return true
        }
        image.isTemplate = true
        image.accessibilityDescription = "Clipvey"
        cache[key] = image
        return image
    }

    /// Рисунок в сетке 18×18 (начало — левый нижний угол).
    nonisolated private static func draw(in rect: NSRect, state: SyncState, badge: Bool) {
        guard let context = NSGraphicsContext.current else { return }
        let unit = rect.width / 18
        func box(_ x: CGFloat, _ y: CGFloat, _ width: CGFloat, _ height: CGFloat) -> NSRect {
            NSRect(x: rect.minX + x * unit, y: rect.minY + y * unit, width: width * unit, height: height * unit)
        }
        func point(_ x: CGFloat, _ y: CGFloat) -> NSPoint {
            NSPoint(x: rect.minX + x * unit, y: rect.minY + y * unit)
        }
        /// Вырезать (стереть уже нарисованное) фигурой.
        func cut(_ body: () -> Void) {
            context.saveGraphicsState()
            context.compositingOperation = .destinationOut
            NSColor.black.set()
            body()
            context.restoreGraphicsState()
        }
        NSColor.black.set()

        // Планшет с прорезью зажима.
        NSBezierPath(roundedRect: box(7, 4.5, 9.5, 12), xRadius: 2.3 * unit, yRadius: 2.3 * unit).fill()
        cut { NSBezierPath(roundedRect: box(9.6, 13.4, 4.3, 1.4), xRadius: 0.7 * unit, yRadius: 0.7 * unit).fill() }

        // Документ с загнутым правым верхним углом; вокруг него — зазор до планшета.
        let x: CGFloat = 1.5, y: CGFloat = 1, width: CGFloat = 9.5, height: CGFloat = 11.5, fold: CGFloat = 3.8, radius: CGFloat = 2
        let document = NSBezierPath()
        document.move(to: point(x + radius, y))
        document.line(to: point(x + width - radius, y))
        document.appendArc(withCenter: point(x + width - radius, y + radius), radius: radius * unit, startAngle: 270, endAngle: 360)
        document.line(to: point(x + width, y + height - fold))
        document.line(to: point(x + width - fold, y + height))
        document.line(to: point(x + radius, y + height))
        document.appendArc(withCenter: point(x + radius, y + height - radius), radius: radius * unit, startAngle: 90, endAngle: 180)
        document.line(to: point(x, y + radius))
        document.appendArc(withCenter: point(x + radius, y + radius), radius: radius * unit, startAngle: 180, endAngle: 270)
        document.close()
        document.lineJoinStyle = .round
        document.lineWidth = 2.6 * unit
        cut { document.stroke() }
        document.fill()
        // Загиб: уголок обведён прорезью.
        let corner = NSBezierPath()
        corner.move(to: point(x + width - fold, y + height - 0.3))
        corner.line(to: point(x + width - fold, y + height - fold + 0.6))
        corner.appendArc(withCenter: point(x + width - fold + 0.6, y + height - fold + 0.6), radius: 0.6 * unit, startAngle: 180, endAngle: 270)
        corner.line(to: point(x + width - 0.3, y + height - fold))
        corner.lineCapStyle = .round
        corner.lineWidth = 1.2 * unit
        cut { corner.stroke() }

        if state == .disabled {
            // Бледнее целиком (а не по частям, иначе документ и планшет просвечивают друг через друга).
            context.saveGraphicsState()
            NSColor.black.withAlphaComponent(0.4).setFill()
            rect.fill(using: .destinationIn)
            context.restoreGraphicsState()
        }
        if state != .connected {
            // Крестик в правом нижнем углу, вокруг — вырезанный круг.
            let center = point(14.4, 3.6)
            let diameter = 6.8 * unit
            cut { NSBezierPath(ovalIn: NSRect(x: center.x - diameter / 2, y: center.y - diameter / 2, width: diameter, height: diameter)).fill() }
            let arm = 1.7 * unit
            let cross = NSBezierPath()
            cross.move(to: NSPoint(x: center.x - arm, y: center.y - arm))
            cross.line(to: NSPoint(x: center.x + arm, y: center.y + arm))
            cross.move(to: NSPoint(x: center.x - arm, y: center.y + arm))
            cross.line(to: NSPoint(x: center.x + arm, y: center.y - arm))
            cross.lineWidth = 1.5 * unit
            cross.lineCapStyle = .round
            NSColor.black.setStroke()
            cross.stroke()
        }
        if badge {
            // Точка «есть обновление»; вокруг неё вырезается кольцо, чтобы она не сливалась с силуэтом.
            let diameter = 4.4 * unit
            let dot = NSRect(x: rect.maxX - diameter, y: rect.maxY - diameter, width: diameter, height: diameter)
            cut { NSBezierPath(ovalIn: dot.insetBy(dx: -1.3 * unit, dy: -1.3 * unit)).fill() }
            NSColor.black.setFill()
            NSBezierPath(ovalIn: dot).fill()
        }
    }
}
