// Рисует иконку Clipvey 1024×1024: значок буфера обмена на скруглённом квадрате по шаблону Apple
// (тело 824×824 по центру, поля 100 px, непрерывные скругления радиусом 185,4 px).
//
// Запуск:  swift scripts/make-icon.swift Resources/AppIcon.png [teal|blue|graphite]
// Затем scripts/build.sh сам нарежет из PNG все размеры и соберёт AppIcon.icns.
import AppKit
import SwiftUI

let canvasSize: CGFloat = 1024
let bodySize: CGFloat = 824
let bodyCornerRadius: CGFloat = 185.4

let palettes: [String: (top: Color, bottom: Color)] = [
    "blue": (Color(red: 0.33, green: 0.62, blue: 1.00), Color(red: 0.10, green: 0.31, blue: 0.86)),
    "graphite": (Color(red: 0.56, green: 0.59, blue: 0.64), Color(red: 0.23, green: 0.25, blue: 0.29)),
    "teal": (Color(red: 0.20, green: 0.80, blue: 0.78), Color(red: 0.13, green: 0.36, blue: 0.80)),
]

struct Icon: View {
    let top: Color
    let bottom: Color

    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: bodyCornerRadius, style: .continuous)
                .fill(LinearGradient(colors: [top, bottom], startPoint: .top, endPoint: .bottom))
            // Мягкий блик сверху, как у системных иконок.
            RoundedRectangle(cornerRadius: bodyCornerRadius, style: .continuous)
                .fill(LinearGradient(colors: [.white.opacity(0.18), .clear], startPoint: .top, endPoint: .center))
            Image(systemName: "doc.on.clipboard.fill")
                .resizable()
                .scaledToFit()
                .frame(width: bodySize * 0.58, height: bodySize * 0.58)
                .foregroundStyle(LinearGradient(colors: [.white, Color(white: 0.88)], startPoint: .top, endPoint: .bottom))
                .shadow(color: .black.opacity(0.28), radius: 16, y: 10)
        }
        .frame(width: bodySize, height: bodySize)
        .frame(width: canvasSize, height: canvasSize)
    }
}

let arguments = Array(CommandLine.arguments.dropFirst())
let output = arguments.first ?? "AppIcon.png"
let paletteName = arguments.dropFirst().first ?? "teal"
guard let palette = palettes[paletteName] else {
    print("Неизвестная палитра \(paletteName). Есть: \(palettes.keys.sorted().joined(separator: ", "))")
    exit(1)
}

MainActor.assumeIsolated {
    let renderer = ImageRenderer(content: Icon(top: palette.top, bottom: palette.bottom))
    renderer.scale = 1
    guard let image = renderer.cgImage,
          let png = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]) else {
        print("Не удалось нарисовать иконку")
        exit(1)
    }
    do {
        try png.write(to: URL(fileURLWithPath: output))
        print("\(output): \(image.width)×\(image.height), палитра \(paletteName)")
    } catch {
        print("Не удалось записать \(output): \(error.localizedDescription)")
        exit(1)
    }
}
