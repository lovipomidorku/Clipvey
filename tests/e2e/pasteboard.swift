// Именованный буфер обмена для сквозных проверок (тот же, что у Clipvey --test --pasteboard NAME).
// Настоящий буфер пользователя не трогается, macOS не спрашивает разрешения.
//   pasteboard write NAME TEXT   записать текст (как обычное копирование, без маркера Clipvey)
//   pasteboard write-secret NAME TEXT   записать как менеджер паролей (org.nspasteboard.ConcealedType)
//   pasteboard read NAME         напечатать текст
//   pasteboard types NAME        напечатать типы, по одному в строке
//   pasteboard clear NAME        очистить и освободить буфер
// lib.sh собирает его один раз: swiftc -O pasteboard.swift -o tmp/pasteboard
import AppKit

let arguments = CommandLine.arguments
guard arguments.count >= 3 else {
    FileHandle.standardError.write(Data("использование: pasteboard write|write-secret|read|types|clear NAME [TEXT]\n".utf8))
    exit(2)
}
let pasteboard = NSPasteboard(name: NSPasteboard.Name(arguments[2]))

switch arguments[1] {
case "write" where arguments.count >= 4:
    pasteboard.clearContents()
    pasteboard.setString(arguments[3], forType: .string)
case "write-secret" where arguments.count >= 4:
    pasteboard.clearContents()
    pasteboard.setString(arguments[3], forType: .string)
    pasteboard.setString("", forType: NSPasteboard.PasteboardType("org.nspasteboard.ConcealedType"))
case "read":
    print(pasteboard.string(forType: .string) ?? "", terminator: "")
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
