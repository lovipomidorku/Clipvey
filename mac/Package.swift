// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "Clipvey",
    platforms: [.macOS(.v14)],
    targets: [
        // Протокол и криптография (docs/protocol.md) — без AppKit, чтобы проверять отдельно.
        .target(
            name: "ClipveyProtocol",
            path: "Sources/ClipveyProtocol"
        ),
        .executableTarget(
            name: "Clipvey",
            dependencies: ["ClipveyProtocol"],
            path: "Sources/Clipvey"
        ),
    ]
)
