// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "screenX",
    platforms: [.macOS(.v13)],
    targets: [
        .executableTarget(name: "screenX", path: "Sources/screenX")
    ]
)
