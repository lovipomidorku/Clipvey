// Именованный буфер обмена для сквозных проверок (тот же, что у Clipvey --test --pasteboard NAME).
// Настоящий буфер пользователя не трогается, macOS не спрашивает разрешения.
//   pasteboard write NAME TEXT   записать текст (как обычное копирование, без маркера Clipvey)
//   pasteboard write-secret NAME TEXT   записать как менеджер паролей (org.nspasteboard.ConcealedType)
//   pasteboard write-image NAME FILE [TEXT]   записать картинку (тип по расширению: png, jpg/jpeg, tiff/tif, heic)
//                                и, если задан, текст рядом с ней (как браузер кладёт ссылку на картинку)
//   pasteboard read NAME         напечатать текст
//   pasteboard read-data NAME TYPE FILE   сохранить данные типа TYPE (например public.png) в FILE
//   pasteboard types NAME        напечатать типы, по одному в строке
//   pasteboard clear NAME        очистить и освободить буфер
// lib.sh собирает его один раз: swiftc -O pasteboard.swift -o tmp/pasteboard
import AppKit

let arguments = CommandLine.arguments
guard arguments.count >= 3 else {
    FileHandle.standardError.write(Data("использование: pasteboard write|write-secret|write-image|read|read-data|types|clear NAME [...]\n".utf8))
    exit(2)
}
let pasteboard = NSPasteboard(name: NSPasteboard.Name(arguments[2]))

func imageType(for path: String) -> NSPasteboard.PasteboardType {
    switch (path as NSString).pathExtension.lowercased() {
    case "jpg", "jpeg": NSPasteboard.PasteboardType("public.jpeg")
    case "tif", "tiff": .tiff
    case "heic": NSPasteboard.PasteboardType("public.heic")
    default: .png
    }
}

switch arguments[1] {
case "write" where arguments.count >= 4:
    pasteboard.clearContents()
    pasteboard.setString(arguments[3], forType: .string)
case "write-secret" where arguments.count >= 4:
    pasteboard.clearContents()
    pasteboard.setString(arguments[3], forType: .string)
    pasteboard.setString("", forType: NSPasteboard.PasteboardType("org.nspasteboard.ConcealedType"))
case "write-image" where arguments.count >= 4:
    guard let data = FileManager.default.contents(atPath: arguments[3]) else {
        FileHandle.standardError.write(Data("не читается \(arguments[3])\n".utf8))
        exit(1)
    }
    pasteboard.clearContents()
    pasteboard.setData(data, forType: imageType(for: arguments[3]))
    if arguments.count >= 5 {
        pasteboard.setString(arguments[4], forType: .string)
    }
case "read":
    print(pasteboard.string(forType: .string) ?? "", terminator: "")
case "read-data" where arguments.count >= 5:
    guard let data = pasteboard.data(forType: NSPasteboard.PasteboardType(arguments[3])) else {
        FileHandle.standardError.write(Data("в буфере нет \(arguments[3])\n".utf8))
        exit(1)
    }
    FileManager.default.createFile(atPath: arguments[4], contents: data)
case "types":
    for type in pasteboard.types ?? [] {
        print(type.rawValue)
    }
case "clear":
    pasteboard.clearContents()
    pasteboard.releaseGlobally()
default:
    FileHandle.standardError.write(Data("неизвестная команда \(arguments[1])\n".utf8))
    exit(2)
}
