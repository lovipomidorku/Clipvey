// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "Clipvey",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "Clipvey",
            path: "Sources/Clipvey"
        )
    ]
)
