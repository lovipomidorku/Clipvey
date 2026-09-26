// Собирает Clipvey.ico для Windows из mac/Resources/AppIcon.png (1024×1024).
// Запуск из папки windows:  swift make-ico.swift ../mac/Resources/AppIcon.png Clipvey.Windows/Clipvey.ico
// Внутри .ico — PNG-изображения 16…256 px (формат поддерживается начиная с Windows Vista).
import AppKit

let arguments = CommandLine.arguments
guard arguments.count == 3, let source = NSImage(contentsOfFile: arguments[1]) else {
    print("Использование: swift make-ico.swift AppIcon.png Clipvey.ico")
    exit(1)
}

func png(side: Int) -> Data {
    let bitmap = NSBitmapImageRep(
        bitmapDataPlanes: nil, pixelsWide: side, pixelsHigh: side, bitsPerSample: 8, samplesPerPixel: 4,
        hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
    NSGraphicsContext.current?.imageInterpolation = .high
    source.draw(in: NSRect(x: 0, y: 0, width: side, height: side))
    NSGraphicsContext.restoreGraphicsState()
    return bitmap.representation(using: .png, properties: [:])!
}

func append<T: FixedWidthInteger>(_ value: T, to data: inout Data) {
    withUnsafeBytes(of: value.littleEndian) { data.append(contentsOf: $0) }
}

let sides = [16, 24, 32, 48, 64, 128, 256]
let images = sides.map(png(side:))
var header = Data()
append(UInt16(0), to: &header)            // зарезервировано
append(UInt16(1), to: &header)            // тип: иконка
append(UInt16(sides.count), to: &header)
var offset = 6 + 16 * sides.count
var body = Data()
for (side, image) in zip(sides, images) {
    header.append(UInt8(side == 256 ? 0 : side))  // 0 означает 256
    header.append(UInt8(side == 256 ? 0 : side))
    header.append(0)                              // палитра
    header.append(0)                              // зарезервировано
    append(UInt16(1), to: &header)                // цветовые плоскости
    append(UInt16(32), to: &header)               // бит на пиксель
    append(UInt32(image.count), to: &header)
    append(UInt32(offset), to: &header)
    offset += image.count
    body.append(image)
}
try! (header + body).write(to: URL(fileURLWithPath: arguments[2]))
print("\(arguments[2]): \(sides.map(String.init).joined(separator: ", ")) px")
