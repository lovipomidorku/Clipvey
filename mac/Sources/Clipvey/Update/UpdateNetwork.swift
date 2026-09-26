import Foundation

/// Запросы обновлений: только HTTPS, в том числе на каждом перенаправлении
/// (файлы релиза GitHub отдаёт через перенаправление на другой адрес).
/// В режиме проверки допускается http://127.0.0.1 — для локального сервера.
final class UpdateNetwork: NSObject, URLSessionTaskDelegate, Sendable {
    enum Failure: Error, CustomStringConvertible {
        case insecure(URL)
        case status(Int, URL)
        case noReleases
        case rateLimited

        var description: String {
            switch self {
            case .insecure(let url): "адрес не HTTPS: \(url.absoluteString)"
            case .status(let code, let url): "HTTP \(code): \(url.absoluteString)"
            case .noReleases: "релизов нет (404)"
            case .rateLimited: "лимит запросов GitHub"
            }
        }
    }

    private let allowLoopbackHTTP: Bool
    private let session: URLSession

    init(allowLoopbackHTTP: Bool) {
        self.allowLoopbackHTTP = allowLoopbackHTTP
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 30
        configuration.timeoutIntervalForResource = 10 * 60
        configuration.waitsForConnectivity = false
        session = URLSession(configuration: configuration)
        super.init()
    }

    func isAllowed(_ url: URL?) -> Bool {
        guard let url, let scheme = url.scheme?.lowercased() else { return false }
        if scheme == "https" {
            return true
        }
        return allowLoopbackHTTP && scheme == "http" && ["127.0.0.1", "localhost", "::1"].contains(url.host ?? "")
    }

    func latestRelease(url: URL, userAgent: String) async throws -> ReleaseInfo {
        var request = URLRequest(url: url)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.setValue(userAgent, forHTTPHeaderField: "User-Agent")
        let (data, response) = try await fetch(request)
        switch response.statusCode {
        case 200:
            return try ReleaseInfo.parse(data)
        case 404:
            throw Failure.noReleases
        case 403 where response.value(forHTTPHeaderField: "X-RateLimit-Remaining") == "0", 429:
            throw Failure.rateLimited
        default:
            throw Failure.status(response.statusCode, url)
        }
    }

    func download(_ url: URL) async throws -> Data {
        let (data, response) = try await fetch(URLRequest(url: url))
        guard response.statusCode == 200 else {
            throw Failure.status(response.statusCode, url)
        }
        return data
    }

    private func fetch(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        guard let url = request.url, isAllowed(url) else {
            throw Failure.insecure(request.url ?? URL(fileURLWithPath: "/"))
        }
        let (data, response) = try await session.data(for: request, delegate: self)
        guard let http = response as? HTTPURLResponse else {
            throw Failure.status(0, url)
        }
        // Перенаправление на не-HTTPS delegate не пропускает; конечный адрес проверяем ещё раз.
        guard isAllowed(http.url) else {
            throw Failure.insecure(http.url ?? url)
        }
        return (data, http)
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest) async -> URLRequest? {
        // nil — не идти по перенаправлению: ответ 3xx вернётся как есть и станет ошибкой.
        isAllowed(request.url) ? request : nil
    }
}
