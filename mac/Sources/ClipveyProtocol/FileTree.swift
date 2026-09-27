import Foundation

/// Что отправлять в описании: элементы и локальные файлы для них (docs/protocol.md, «Файлы» и
/// «Поведение сторон → Файлы в буфере»). Совпадает с FileTree в Clipvey.Core.
/// - Символические ссылки и особые файлы пропускаются, .DS_Store внутри папок — тоже.
/// - Имена — в NFC, «\» и «:» заменяются на «_», совпадения в одной папке получают номер.
/// - Больше 10 000 элементов, больше 10 ГиБ, слишком длинное имя или путь — не отправляется.
public struct FileTree: Sendable {
    public let items: [FileItem]
    /// Локальный файл или папка для каждого элемента, по порядку items.
    public let urls: [URL]
    public let total: Int64

    /// Обойти выбранное. Читает диск — вызывать не на главном потоке.
    public static func build(_ roots: [URL]) -> Result<FileTree, FileOfferFailure> {
        var builder = Builder()
        do {
            var used = Set<String>()
            for root in roots {
                try builder.visit(root.standardizedFileURL, parent: nil, siblings: &used)
            }
        } catch let failure as FileOfferFailure {
            return .failure(failure)
        } catch {
            return .failure(.unreadable)
        }
        guard !builder.items.isEmpty else { return .failure(.empty) }
        return .success(FileTree(items: builder.items, urls: builder.urls, total: builder.total))
    }

    private struct Builder {
        static let keys: [URLResourceKey] = [.isSymbolicLinkKey, .isDirectoryKey, .isRegularFileKey, .fileSizeKey]
        /// Служебный файл Finder: в папках не передаётся.
        static let skippedNames: Set<String> = [".DS_Store"]

        var items: [FileItem] = []
        var urls: [URL] = []
        var total: Int64 = 0

        mutating func visit(_ url: URL, parent: String?, siblings: inout Set<String>) throws {
            let values: URLResourceValues
            do {
                values = try url.resourceValues(forKeys: Set(Self.keys))
            } catch {
                throw FileOfferFailure.unreadable
            }
            if values.isSymbolicLink == true {
                return
            }
            let isDirectory = values.isDirectory == true
            guard isDirectory || values.isRegularFile == true else { return }

            let base = FileNames.wireName(url.lastPathComponent)
            var name = base
            var number = 2
            while siblings.contains(name) {
                name = FileNames.numbered(base, number, isDirectory: isDirectory)
                number += 1
            }
            siblings.insert(name)
            guard FileNames.nameProblem(name) == nil else { throw FileOfferFailure.nameTooLong }
            let path = parent.map { "\($0)/\(name)" } ?? name
            guard path.utf8.count <= ProtocolLimits.maxFilePathBytes else { throw FileOfferFailure.nameTooLong }
            guard items.count < ProtocolLimits.maxFileItems else { throw FileOfferFailure.tooManyItems }

            guard isDirectory else {
                let size = Int64(values.fileSize ?? 0)
                total += size
                guard total <= ProtocolLimits.maxFileTotalBytes else { throw FileOfferFailure.tooLarge }
                items.append(.file(path, size: size))
                urls.append(url)
                return
            }
            items.append(.directory(path))
            urls.append(url)
            let children: [URL]
            do {
                children = try FileManager.default.contentsOfDirectory(at: url, includingPropertiesForKeys: Self.keys)
            } catch {
                throw FileOfferFailure.unreadable
            }
            var used = Set<String>()
            for child in children.sorted(by: { $0.lastPathComponent < $1.lastPathComponent })
            where !Self.skippedNames.contains(child.lastPathComponent) {
                try visit(child, parent: path, siblings: &used)
            }
        }
    }
}
