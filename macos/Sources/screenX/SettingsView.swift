import SwiftUI
import AppKit
import ServiceManagement

/// Settings laid out like Snipping Tool's: Snipping, Screen recording, Shortcuts, About. Saved as soon as they change.
struct SettingsView: View {
    @AppStorage(Prefs.Key.autoCopyChanges) private var autoCopyChanges = true
    @AppStorage(Prefs.Key.autoSave) private var autoSave = true
    @AppStorage(Prefs.Key.askToSave) private var askToSave = false
    @AppStorage(Prefs.Key.multipleWindows) private var multipleWindows = false
    @AppStorage(Prefs.Key.addBorder) private var addBorder = false
    @AppStorage(Prefs.Key.borderColor) private var borderColor = "#E81123"
    @AppStorage(Prefs.Key.borderSize) private var borderSize = 2.0
    @AppStorage(Prefs.Key.recordMic) private var recordMic = false
    @AppStorage(Prefs.Key.showCursor) private var showCursor = true
    @AppStorage(Prefs.Key.fps) private var fps = 30
    @AppStorage(Prefs.Key.screenshotFolder) private var screenshotFolder = ""
    @AppStorage(Prefs.Key.videoFolder) private var videoFolder = ""
    @State private var launchAtLogin = SMAppService.mainApp.status == .enabled

    private let borderColors = ["#000000", "#FFFFFF", "#7A7A7A", "#E81123", "#F7630C", "#FFB900", "#16C60C", "#00B7C3", "#0078D4", "#4F4BD9", "#B146C2", "#E3008C"]

    var body: some View {
        Form {
            Section("Snipping") {
                Toggle("Automatically copy changes", isOn: $autoCopyChanges)
                Toggle("Automatically save original screenshots", isOn: $autoSave)
                Toggle("Ask to save edited screenshots", isOn: $askToSave)
                Toggle("Multiple windows", isOn: $multipleWindows)
                Toggle("Add border to each screenshot", isOn: $addBorder)
                if addBorder {
                    HStack(spacing: 6) {
                        Text("Colour")
                        Spacer()
                        ForEach(borderColors, id: \.self) { hex in
                            Circle()
                                .fill(Color(nsColor: NSColor(hex: hex) ?? .red))
                                .frame(width: 18, height: 18)
                                .overlay(Circle().stroke(Color.primary, lineWidth: borderColor == hex ? 2 : 0).padding(-3))
                                .onTapGesture { borderColor = hex }
                        }
                    }
                    HStack {
                        Text("Thickness")
                        Slider(value: $borderSize, in: 1...20, step: 1)
                        Text("\(Int(borderSize)) px").frame(width: 44, alignment: .trailing)
                    }
                }
                folderRow("Screenshots are saved to", path: $screenshotFolder)
            }
            Section("Screen recording") {
                Toggle("Include microphone input by default when a screen recording starts", isOn: $recordMic)
                Toggle("Show the mouse pointer in recordings", isOn: $showCursor)
                Picker("Frame rate", selection: $fps) {
                    ForEach([15, 24, 30, 60], id: \.self) { Text("\($0) fps").tag($0) }
                }
                folderRow("Screen recordings are saved to", path: $videoFolder)
            }
            Section("Shortcuts") {
                LabeledContent("Open screenX", value: "Control-Shift-S")
                Toggle("Start screenX when you log in", isOn: $launchAtLogin)
                    .onChange(of: launchAtLogin) { on in
                        do {
                            if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
                        } catch {
                            launchAtLogin = SMAppService.mainApp.status == .enabled
                        }
                    }
            }
            Section("About this app") {
                LabeledContent("screenX", value: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "")
                Link("funwkwk.my.id", destination: URL(string: "https://funwkwk.my.id")!)
                Link("GitHub", destination: URL(string: "https://github.com/funwkwk/screenX")!)
            }
        }
        .formStyle(.grouped)
        .frame(minWidth: 560, minHeight: 480)
    }

    private func folderRow(_ title: String, path: Binding<String>) -> some View {
        HStack {
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                Text(path.wrappedValue).font(.caption).foregroundColor(.secondary).lineLimit(1).truncationMode(.middle)
            }
            Spacer()
            Button("Change") {
                let panel = NSOpenPanel()
                panel.canChooseDirectories = true
                panel.canChooseFiles = false
                panel.canCreateDirectories = true
                panel.directoryURL = URL(fileURLWithPath: path.wrappedValue)
                if panel.runModal() == .OK, let url = panel.url { path.wrappedValue = url.path }
            }
        }
    }
}
