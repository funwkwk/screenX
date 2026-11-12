import Foundation

/// User settings, stored in UserDefaults. The key names are shared with the SwiftUI settings view (@AppStorage).
enum Prefs {
    enum Key {
        static let autoSave = "autoSave"
        static let autoCopyChanges = "autoCopyChanges"
        static let askToSave = "askToSaveEdited"
        static let multipleWindows = "multipleWindows"
        static let addBorder = "addBorder"
        static let borderColor = "borderColor"
        static let borderSize = "borderSize"
        static let recordMic = "recordMic"
        static let showCursor = "recordCursor"
        static let fps = "recordFps"
        static let screenshotFolder = "screenshotFolder"
        static let videoFolder = "videoFolder"
        static let snipDelay = "snipDelay"
        static let snipMode = "snipMode"
    }

    static let d = UserDefaults.standard

    static func registerDefaults() {
        let home = FileManager.default.homeDirectoryForCurrentUser
        d.register(defaults: [
            Key.autoSave: true,
            Key.autoCopyChanges: true,
            Key.askToSave: false,
            Key.multipleWindows: false,
            Key.addBorder: false,
            Key.borderColor: "#E81123",
            Key.borderSize: 2.0,
            Key.recordMic: false,
            Key.showCursor: true,
            Key.fps: 30,
            Key.screenshotFolder: home.appendingPathComponent("Pictures/screenX").path,
            Key.videoFolder: home.appendingPathComponent("Movies/screenX").path,
            Key.snipDelay: 0,
            Key.snipMode: "rectangle",
        ])
    }

    static var autoSave: Bool { d.bool(forKey: Key.autoSave) }
    static var autoCopyChanges: Bool { d.bool(forKey: Key.autoCopyChanges) }
    static var askToSave: Bool { d.bool(forKey: Key.askToSave) }
    static var multipleWindows: Bool { d.bool(forKey: Key.multipleWindows) }
    static var addBorder: Bool { d.bool(forKey: Key.addBorder) }
    static var borderColor: String { d.string(forKey: Key.borderColor) ?? "#E81123" }
    static var borderSize: Double { max(1, d.double(forKey: Key.borderSize)) }
    static var recordMic: Bool {
        get { d.bool(forKey: Key.recordMic) }
        set { d.set(newValue, forKey: Key.recordMic) }
    }
    static var showCursor: Bool { d.bool(forKey: Key.showCursor) }
    static var fps: Int { max(5, d.integer(forKey: Key.fps)) }
    static var screenshotFolder: URL { URL(fileURLWithPath: d.string(forKey: Key.screenshotFolder) ?? NSTemporaryDirectory()) }
    static var videoFolder: URL { URL(fileURLWithPath: d.string(forKey: Key.videoFolder) ?? NSTemporaryDirectory()) }
    static var snipDelay: Int {
        get { d.integer(forKey: Key.snipDelay) }
        set { d.set(newValue, forKey: Key.snipDelay) }
    }
    static var snipMode: SnipMode {
        get { SnipMode(rawValue: d.string(forKey: Key.snipMode) ?? "") ?? .rectangle }
        set { d.set(newValue.rawValue, forKey: Key.snipMode) }
    }

    /// "Screenshot 2026-01-31 101530.png" style unique file inside a folder (created when missing).
    static func uniqueFile(in folder: URL, prefix: String, ext: String) -> URL {
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HHmmss"
        let base = "\(prefix) \(f.string(from: Date()))"
        var url = folder.appendingPathComponent("\(base).\(ext)")
        var i = 2
        while FileManager.default.fileExists(atPath: url.path) {
            url = folder.appendingPathComponent("\(base) (\(i)).\(ext)")
            i += 1
        }
        return url
    }
}
