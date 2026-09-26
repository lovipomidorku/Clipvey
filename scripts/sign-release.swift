#!/usr/bin/env swift
// Подпись файла SHA256SUMS релиза (docs/releases.md): ECDSA P-256 с SHA-256, подпись — base64 от 64 байт r‖s.
//   swift scripts/sign-release.swift sign SHA256SUMS [--key-file FILE] [--out SHA256SUMS.sig]
//       ключ — из переменной CLIPVEY_SIGNING_KEY или из файла: base64 от 32 байт rawRepresentation
//   swift scripts/sign-release.swift verify SHA256SUMS SHA256SUMS.sig [--public-key B64]
//       по умолчанию — открытый ключ, зашитый в приложения (X9.63, 65 байт, base64)
//   swift scripts/sign-release.swift public-key [--key-file FILE]
//       напечатать открытый ключ для закрытого (для проверки, что ключ тот)
//   swift scripts/sign-release.swift generate
//       новый одноразовый ключ для проверок: печатает закрытый и открытый (настоящий ключ так не создают)
import CryptoKit
import Foundation

let releasePublicKey = "BH2yxPlYNbki9eTLDttU3YTv5qjUJ8Biz4ChVq7y7OdeOqkmmtYi5Zch3XAfrF0x4qSrNvvNOaFMqMONHERTJJs="

var arguments = Array(CommandLine.arguments.dropFirst())

func fail(_ message: String) -> Never {
    FileHandle.standardError.write(Data("sign-release: \(message)\n".utf8))
    exit(1)
}

func option(_ name: String) -> String? {
    guard let index = arguments.firstIndex(of: name) else { return nil }
    guard index + 1 < arguments.count else { fail("после \(name) нужно значение") }
    let value = arguments[index + 1]
    arguments.removeSubrange(index...index + 1)
    return value
}

func read(_ path: String) -> Data {
    guard let data = FileManager.default.contents(atPath: path) else { fail("не читается \(path)") }
    return data
}

func base64(_ text: String, _ what: String) -> Data {
    guard let data = Data(base64Encoded: text.trimmingCharacters(in: .whitespacesAndNewlines)) else {
        fail("\(what) — не base64")
    }
    return data
}

func privateKey() -> P256.Signing.PrivateKey {
    let text: String
    if let file = option("--key-file") {
        text = String(decoding: read(file), as: UTF8.self)
    } else if let value = ProcessInfo.processInfo.environment["CLIPVEY_SIGNING_KEY"], !value.isEmpty {
        text = value
    } else {
        fail("нет ключа: задайте CLIPVEY_SIGNING_KEY или --key-file")
    }
    let raw = base64(text, "ключ")
    guard raw.count == 32, let key = try? P256.Signing.PrivateKey(rawRepresentation: raw) else {
        fail("ключ должен быть base64 от 32 байт")
    }
    return key
}

let command = arguments.isEmpty ? "" : arguments.removeFirst()
switch command {
case "sign":
    let output = option("--out")
    let key = privateKey()
    guard arguments.count == 1 else { fail("использование: sign SHA256SUMS [--key-file FILE] [--out FILE]") }
    let input = arguments[0]
    let signature = try key.signature(for: read(input)).rawRepresentation
    let target = output ?? input + ".sig"
    guard FileManager.default.createFile(atPath: target, contents: Data(signature.base64EncodedString().utf8)) else {
        fail("не записывается \(target)")
    }
    print("Подписано: \(target) (ключ \(key.publicKey.x963Representation.base64EncodedString()))")

case "verify":
    let publicText = option("--public-key") ?? releasePublicKey
    guard arguments.count == 2 else { fail("использование: verify SHA256SUMS SHA256SUMS.sig [--public-key B64]") }
    guard let key = try? P256.Signing.PublicKey(x963Representation: base64(publicText, "открытый ключ")) else {
        fail("открытый ключ должен быть X9.63, 65 байт")
    }
    let raw = base64(String(decoding: read(arguments[1]), as: UTF8.self), "подпись")
    guard raw.count == 64, let signature = try? P256.Signing.ECDSASignature(rawRepresentation: raw) else {
        fail("подпись должна быть base64 от 64 байт r‖s")
    }
    guard key.isValidSignature(signature, for: read(arguments[0])) else {
        fail("подпись НЕ совпадает")
    }
    print("Подпись верна")

case "public-key":
    print(privateKey().publicKey.x963Representation.base64EncodedString())

case "generate":
    let key = P256.Signing.PrivateKey()
    print("private \(key.rawRepresentation.base64EncodedString())")
    print("public \(key.publicKey.x963Representation.base64EncodedString())")

default:
    fail("команды: sign, verify, public-key, generate (подробности — в начале файла)")
}
