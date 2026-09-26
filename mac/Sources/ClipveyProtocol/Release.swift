import CryptoKit
import Foundation

// Проверка обновлений (docs/releases.md): версия, ответ releases/latest, SHA256SUMS и его подпись.
// Здесь только разбор и проверки без сети и AppKit — их проверяет clipvey-checks.

/// Версия вида major.minor.patch (тег релиза — с «v» впереди).
public struct ReleaseVersion: Comparable, CustomStringConvertible, Sendable {
    public let major: Int
    public let minor: Int
    public let patch: Int

    /// «1.2.3» или «v1.2.3». Всё остальное (пре-релизы, лишние части, пробелы внутри) — nil.
    public init?(_ text: String) {
        var text = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if text.hasPrefix("v") {
            text.removeFirst()
        }
        let parts = text.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 3 else { return nil }
        var numbers: [Int] = []
        for part in parts {
            guard !part.isEmpty, part.count <= 9, part.allSatisfy({ $0.isASCII && $0.isNumber }), let number = Int(part) else {
                return nil
            }
            numbers.append(number)
        }
        major = numbers[0]
        minor = numbers[1]
        patch = numbers[2]
    }

    public var description: String { "\(major).\(minor).\(patch)" }

    public static func < (a: ReleaseVersion, b: ReleaseVersion) -> Bool {
        (a.major, a.minor, a.patch) < (b.major, b.minor, b.patch)
    }
}

/// Нужное из ответа GitHub releases/latest: тег и файлы релиза.
public struct ReleaseInfo: Sendable {
    public let tag: String
    public let version: ReleaseVersion
    /// Имя файла → адрес для скачивания.
    public let assets: [String: URL]

    public init(tag: String, version: ReleaseVersion, assets: [String: URL]) {
        self.tag = tag
        self.version = version
        self.assets = assets
    }

    public static func parse(_ data: Data) throws -> ReleaseInfo {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw ReleaseError.badResponse("ответ — не объект JSON")
        }
        guard let tag = object["tag_name"] as? String else {
            throw ReleaseError.badResponse("нет tag_name")
        }
        guard let version = ReleaseVersion(tag) else {
            throw ReleaseError.badResponse("тег «\(tag)» — не версия major.minor.patch")
        }
        var assets: [String: URL] = [:]
        for asset in object["assets"] as? [[String: Any]] ?? [] {
            if let name = asset["name"] as? String,
               let link = asset["browser_download_url"] as? String,
               let url = URL(string: link) {
                assets[name] = url
            }
        }
        return ReleaseInfo(tag: tag, version: version, assets: assets)
    }
}

public enum ReleaseError: Error, CustomStringConvertible, Equatable {
    case badResponse(String)
    case badSignature
    case badChecksums(String)
    case missingChecksum(String)
    case hashMismatch(String)

    public var description: String {
        switch self {
        case .badResponse(let detail): "неверный ответ: \(detail)"
        case .badSignature: "подпись SHA256SUMS не совпадает"
        case .badChecksums(let detail): "SHA256SUMS испорчен: \(detail)"
        case .missingChecksum(let name): "в SHA256SUMS нет строки для \(name)"
        case .hashMismatch(let name): "SHA-256 файла \(name) не совпадает с SHA256SUMS"
        }
    }
}

public enum ReleaseVerifier {
    /// Открытый ключ подписи релизов, зашитый в приложение (X9.63, base64).
    public static let publicKeyBase64 = "BH2yxPlYNbki9eTLDttU3YTv5qjUJ8Biz4ChVq7y7OdeOqkmmtYi5Zch3XAfrF0x4qSrNvvNOaFMqMONHERTJJs="

    /// Проверяет подпись SHA256SUMS и возвращает его строки: имя файла → sha256 (hex строчными).
    /// signature — содержимое SHA256SUMS.sig: base64 от 64 байт r‖s (пробелы и перевод строки по краям допустимы).
    public static func verifiedChecksums(sums: Data, signature: Data, publicKeyBase64: String = publicKeyBase64) throws -> [String: String] {
        guard let keyData = Data(base64Encoded: publicKeyBase64.trimmingCharacters(in: .whitespacesAndNewlines)),
              let key = try? P256.Signing.PublicKey(x963Representation: keyData) else {
            throw ReleaseError.badSignature
        }
        let text = String(decoding: signature, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        guard let raw = Data(base64Encoded: text), raw.count == 64,
              let parsed = try? P256.Signing.ECDSASignature(rawRepresentation: raw),
              key.isValidSignature(parsed, for: sums) else {
            throw ReleaseError.badSignature
        }
        return try parseChecksums(sums)
    }

    /// Строки «<sha256 hex строчными>  <имя>\n». Любое отступление от формата — ошибка.
    public static func parseChecksums(_ data: Data) throws -> [String: String] {
        guard let text = String(data: data, encoding: .utf8) else {
            throw ReleaseError.badChecksums("не UTF-8")
        }
        guard text.hasSuffix("\n") else {
            throw ReleaseError.badChecksums("нет перевода строки в конце")
        }
        var result: [String: String] = [:]
        for line in text.dropLast().split(separator: "\n", omittingEmptySubsequences: false) {
            let hash = line.prefix(64)
            let rest = line.dropFirst(64)
            guard hash.count == 64, hash.allSatisfy({ $0.isHexDigit && !$0.isUppercase }),
                  rest.hasPrefix("  "), rest.count > 2 else {
                throw ReleaseError.badChecksums("строка «\(line)»")
            }
            let name = String(rest.dropFirst(2))
            guard result[name] == nil else {
                throw ReleaseError.badChecksums("\(name) дважды")
            }
            result[name] = String(hash)
        }
        return result
    }

    /// SHA-256 файла совпадает со строкой из SHA256SUMS.
    public static func verifyFile(_ fileData: Data, name: String, checksums: [String: String]) throws {
        guard let expected = checksums[name] else {
            throw ReleaseError.missingChecksum(name)
        }
        let actual = SHA256.hash(data: fileData).map { String(format: "%02x", $0) }.joined()
        guard actual == expected else {
            throw ReleaseError.hashMismatch(name)
        }
    }
}
