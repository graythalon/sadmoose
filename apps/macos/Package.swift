// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "DriftMac",
    platforms: [.macOS(.v14)],
    products: [.executable(name: "DriftMac", targets: ["DriftMac"])],
    targets: [.executableTarget(name: "DriftMac", linkerSettings: [.linkedFramework("SwiftUI"), .linkedFramework("AppKit"), .linkedFramework("CoreGraphics"), .linkedFramework("ApplicationServices"), .linkedFramework("Network"), .linkedFramework("CryptoKit"), .linkedFramework("Security")])]
)
