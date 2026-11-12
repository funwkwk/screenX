import AppKit
import SwiftUI

final class AppDelegate: NSObject, NSApplicationDelegate {
    static var shared: AppDelegate { NSApp.delegate as! AppDelegate }

    private var statusItem: NSStatusItem!
    private var settingsWindow: NSWindow?
    private var sampler: NSColorSampler?

    func applicationDidFinishLaunching(_ notification: Notification) {
        Prefs.registerDefaults()
        buildMainMenu()
        buildStatusItem()
        let ok = HotKey.registerControlShiftS { [weak self] in self?.snip(Prefs.snipMode) }
        Toast.show(ok ? "screenX is running. Press Control-Shift-S to snip, or use the menu-bar icon."
                      : "screenX is running. Control-Shift-S is taken by another app; use the menu-bar icon.")
        // same switches as the Windows build: screenX --snip | --fullscreen | --record | --color | --text
        let args = CommandLine.arguments
        if let i = args.firstIndex(of: "--selftest"), i + 1 < args.count { Overlay.selfTest(URL(fileURLWithPath: args[i + 1])) }
        else if args.contains("--snip") { snip(Prefs.snipMode) }
        else if args.contains("--fullscreen") { snip(.fullscreen) }
        else if args.contains("--record") { startRecording() }
        else if args.contains("--color") { pickColour() }
        else if args.contains("--text") { extractText() }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    // =================================================================== menus

    /// Menu-bar apps still need a main menu so Command-C / V / Z / W / Q work inside their windows.
    private func buildMainMenu() {
        let main = NSMenu()
        let appItem = NSMenuItem()
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "Settings…", action: #selector(openSettings), keyEquivalent: ",")
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "Quit screenX", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu
        main.addItem(appItem)

        let editItem = NSMenuItem()
        let edit = NSMenu(title: "Edit")
        edit.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = edit
        main.addItem(editItem)

        let windowItem = NSMenuItem()
        let windowMenu = NSMenu(title: "Window")
        windowMenu.addItem(withTitle: "Close", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        windowMenu.addItem(withTitle: "Minimize", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m")
        windowItem.submenu = windowMenu
        main.addItem(windowItem)
        NSApp.mainMenu = main
    }

    private func buildStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = statusItem.button {
            if let img = NSImage(systemSymbolName: "camera.viewfinder", accessibilityDescription: "screenX") {
                button.image = img
            } else {
                button.title = "screenX"
            }
            button.toolTip = "screenX — Control-Shift-S"
        }
        let m = NSMenu()
        func add(_ title: String, _ action: Selector, key: String = "", mods: NSEvent.ModifierFlags = []) {
            let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
            item.keyEquivalentModifierMask = mods
            item.target = self
            m.addItem(item)
        }
        add("Open screenX", #selector(openEditor))
        add("New Snip", #selector(menuSnip), key: "s", mods: [.control, .shift])
        add("Window Snip", #selector(menuWindowSnip))
        add("Full Screen Snip", #selector(menuFullSnip))
        add("Record Screen", #selector(startRecording))
        add("Colour Picker", #selector(pickColour))
        add("Text Extractor", #selector(extractText))
        m.addItem(.separator())
        add("Open Screenshots Folder", #selector(openScreenshots))
        add("Open Recordings Folder", #selector(openRecordings))
        m.addItem(.separator())
        add("Settings…", #selector(openSettings))
        m.addItem(.separator())
        add("Quit screenX", #selector(quit))
        statusItem.menu = m
    }

    // =================================================================== snipping

    /// Opens the snipping overlay. The finished snip is copied and auto-saved; it goes to `editor` when the
    /// snip was started from an editor window, otherwise a notification offers to open it for editing.
    func snip(_ mode: SnipMode, delay: Int = 0, into editor: EditorController? = nil) {
        if MainActor.assumeIsolated({ Recorder.current != nil }) { return }
        Overlay.start(mode: mode, delay: delay, onCapture: { image in
            ImageTools.copy(image)
            var url: URL?
            if Prefs.autoSave {
                let target = Prefs.uniqueFile(in: Prefs.screenshotFolder, prefix: "Screenshot", ext: "png")
                if (try? ImageTools.save(image, to: target)) != nil { url = target }
            }
            if let editor = editor {
                if Prefs.multipleWindows && editor.hasImage {
                    editor.show()
                    EditorController(image: image, savedURL: url).show()
                } else {
                    editor.load(image, savedURL: url)
                    editor.show()
                }
                return
            }
            let where_ = url.map { " and saved to \($0.deletingLastPathComponent().lastPathComponent)" } ?? ""
            Toast.show("Snip copied\(where_). Click here to edit it.") {
                EditorController(image: image, savedURL: url).show()
            }
        }, onCancel: {
            editor?.show() // cancelled with Esc: bring the editor back
        })
    }

    @objc private func menuSnip() { snip(Prefs.snipMode) }
    @objc private func menuWindowSnip() { snip(.window) }
    @objc private func menuFullSnip() { snip(.fullscreen) }

    @objc private func openEditor() {
        if let existing = EditorController.all.last {
            existing.show()
        } else {
            EditorController(image: nil, savedURL: nil).show()
        }
    }

    @objc func startRecording() {
        MainActor.assumeIsolated { Recorder.start() }
    }

    @objc func pickColour() {
        let s = NSColorSampler()
        sampler = s
        s.show { [weak self] color in
            self?.sampler = nil
            guard let color = color else { return }
            let hex = color.hexString
            ImageTools.copy(text: hex)
            Toast.show("Colour \(hex) copied to clipboard")
        }
    }

    @objc private func extractText() {
        Overlay.start(mode: .rectangle, text: true, onCapture: { _ in })
    }

    @objc private func openScreenshots() { openFolder(Prefs.screenshotFolder) }
    @objc private func openRecordings() { openFolder(Prefs.videoFolder) }

    private func openFolder(_ url: URL) {
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        NSWorkspace.shared.open(url)
    }

    @objc func openSettings() {
        if settingsWindow == nil {
            let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 620, height: 640),
                             styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
            w.title = "screenX Settings"
            w.isReleasedWhenClosed = false
            w.contentViewController = NSHostingController(rootView: SettingsView())
            w.setContentSize(NSSize(width: 620, height: 640))
            w.center()
            settingsWindow = w
        }
        NSApp.activate(ignoringOtherApps: true)
        settingsWindow?.makeKeyAndOrderFront(nil)
    }

    @objc private func quit() {
        if let r = MainActor.assumeIsolated({ Recorder.current }) {
            MainActor.assumeIsolated { r.stopAndSave() }
            DispatchQueue.main.asyncAfter(deadline: .now() + 2.5) { NSApp.terminate(nil) }
        } else {
            NSApp.terminate(nil)
        }
    }
}
