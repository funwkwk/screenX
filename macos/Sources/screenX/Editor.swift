import AppKit
import UniformTypeIdentifiers

/// Keeps a document smaller than the scroll view centred.
final class CenterClipView: NSClipView {
    override func constrainBoundsRect(_ proposedBounds: NSRect) -> NSRect {
        var r = super.constrainBoundsRect(proposedBounds)
        if let doc = documentView {
            if r.width > doc.frame.width { r.origin.x = (doc.frame.width - r.width) / 2 }
            if r.height > doc.frame.height { r.origin.y = (doc.frame.height - r.height) / 2 }
        }
        return r
    }
}

/// Snipping-Tool style window: New / mode / delay on the left, markup tools in the middle,
/// save / copy / share / settings on the right, the snip centred on a dark canvas.
final class EditorController: NSObject, NSWindowDelegate {
    static var all: [EditorController] = []

    let window: NSWindow
    private let canvas = CanvasView()
    private let scroll = NSScrollView()
    private let hint = NSTextField(labelWithString: "Press New or Control-Shift-S to start a snip.")
    private var savedURL: URL?
    private var dirty = false
    private var copyTimer: Timer?

    private var toolButtons: [NSButton] = []
    private let shapes = NSPopUpButton(frame: .zero, pullsDown: true)
    private let modePopup = NSPopUpButton(frame: .zero, pullsDown: false)
    private let delayPopup = NSPopUpButton(frame: .zero, pullsDown: false)
    private let colorWell = NSColorWell()
    private let sizeSlider = NSSlider(value: 4, minValue: 1, maxValue: 24, target: nil, action: nil)
    private var undoButton: NSButton!
    private var redoButton: NSButton!
    private var imageButtons: [NSControl] = []

    var hasImage: Bool { canvas.image != nil }

    init(image: NSImage?, savedURL: URL?) {
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1100, height: 720),
                          styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        super.init()
        window.title = "screenX"
        window.appearance = NSAppearance(named: .darkAqua)
        window.backgroundColor = NSColor(white: 0.125, alpha: 1)
        window.isReleasedWhenClosed = false
        window.minSize = NSSize(width: 900, height: 420)
        window.delegate = self
        buildUI()
        canvas.onChange = { [weak self] in self?.changed() }
        load(image, savedURL: savedURL)
        window.center()
        EditorController.all.append(self)
    }

    func show() {
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
    }

    // =================================================================== UI

    private func symbol(_ name: String, _ tip: String) -> NSImage {
        NSImage(systemSymbolName: name, accessibilityDescription: tip) ?? NSImage(size: NSSize(width: 16, height: 16))
    }

    private func button(_ name: String, _ tip: String, _ action: Selector) -> NSButton {
        let b = NSButton(image: symbol(name, tip), target: self, action: action)
        b.bezelStyle = .texturedRounded
        b.toolTip = tip
        b.setAccessibilityLabel(tip)
        return b
    }

    private func toolButton(_ name: String, _ tip: String, _ tool: Tool) -> NSButton {
        let b = button(name, tip, #selector(pickTool(_:)))
        b.setButtonType(.pushOnPushOff)
        b.tag = tool.rawValue
        toolButtons.append(b)
        return b
    }

    private func separator() -> NSView {
        let v = NSBox()
        v.boxType = .separator
        v.widthAnchor.constraint(equalToConstant: 1).isActive = true
        v.heightAnchor.constraint(equalToConstant: 22).isActive = true
        return v
    }

    private func buildUI() {
        let content = NSView()
        window.contentView = content

        // left
        let newButton = NSButton(title: "New", image: symbol("plus", "New"), target: self, action: #selector(newSnip))
        newButton.bezelStyle = .texturedRounded
        newButton.imagePosition = .imageLeading
        newButton.toolTip = "New snip (Command-N)"
        newButton.keyEquivalent = "n"
        newButton.keyEquivalentModifierMask = [.command]
        let record = button("video", "Record the screen", #selector(newRecording))
        for m in SnipMode.allCases { modePopup.addItem(withTitle: m.title) }
        modePopup.selectItem(at: SnipMode.allCases.firstIndex(of: Prefs.snipMode) ?? 0)
        modePopup.target = self
        modePopup.action = #selector(modeChanged)
        modePopup.toolTip = "Snipping mode"
        let delays = [0, 3, 5, 10]
        for d in delays { delayPopup.addItem(withTitle: d == 0 ? "No delay" : "\(d) seconds") }
        delayPopup.selectItem(at: delays.firstIndex(of: Prefs.snipDelay) ?? 0)
        delayPopup.target = self
        delayPopup.action = #selector(delayChanged)
        delayPopup.toolTip = "Snip delay"
        let left = NSStackView(views: [newButton, record, modePopup, delayPopup])
        left.spacing = 8

        // centre
        shapes.addItem(withTitle: "")
        shapes.item(at: 0)?.image = symbol("square.on.circle", "Shapes")
        for (title, tool) in [("Rectangle", Tool.rectangle), ("Ellipse", .ellipse), ("Line", .line), ("Arrow", .arrow)] {
            shapes.addItem(withTitle: title)
            shapes.lastItem?.tag = tool.rawValue
        }
        shapes.target = self
        shapes.action = #selector(pickShape(_:))
        shapes.toolTip = "Shapes"
        shapes.bezelStyle = .texturedRounded
        colorWell.color = canvas.penColor
        colorWell.target = self
        colorWell.action = #selector(colorChanged)
        colorWell.toolTip = "Colour"
        colorWell.widthAnchor.constraint(equalToConstant: 34).isActive = true
        colorWell.heightAnchor.constraint(equalToConstant: 24).isActive = true
        sizeSlider.target = self
        sizeSlider.action = #selector(sizeChanged)
        sizeSlider.toolTip = "Size"
        sizeSlider.widthAnchor.constraint(equalToConstant: 90).isActive = true
        undoButton = button("arrow.uturn.backward", "Undo (Command-Z)", #selector(undo))
        undoButton.keyEquivalent = "z"
        undoButton.keyEquivalentModifierMask = [.command]
        redoButton = button("arrow.uturn.forward", "Redo (Shift-Command-Z)", #selector(redo))
        redoButton.keyEquivalent = "z"
        redoButton.keyEquivalentModifierMask = [.command, .shift]
        let ocr = button("text.viewfinder", "Text actions: copy the text in this snip", #selector(extractText))
        let eraseAll = button("trash", "Erase all mark-ups", #selector(eraseAllMarks))
        let centre = NSStackView(views: [
            toolButton("pencil.tip", "Pen", .pen), toolButton("highlighter", "Highlighter", .highlighter),
            toolButton("eraser", "Eraser", .eraser), shapes, colorWell, sizeSlider, separator(),
            toolButton("crop", "Image crop: drag the area to keep", .crop), ocr, separator(), undoButton, redoButton, eraseAll,
        ])
        centre.spacing = 6

        // right
        let save = button("square.and.arrow.down", "Save as (Command-S)", #selector(saveAs))
        save.keyEquivalent = "s"
        save.keyEquivalentModifierMask = [.command]
        let copy = button("doc.on.doc", "Copy (Shift-Command-C)", #selector(copyImage))
        copy.keyEquivalent = "c"
        copy.keyEquivalentModifierMask = [.command, .shift]
        let share = button("square.and.arrow.up", "Share", #selector(shareImage(_:)))
        let settings = button("gearshape", "Settings", #selector(openSettings))
        let right = NSStackView(views: [save, copy, share, settings])
        right.spacing = 6
        var controls: [NSControl] = [save, copy, share, ocr, eraseAll, shapes, colorWell, sizeSlider]
        controls.append(contentsOf: toolButtons)
        imageButtons = controls

        let bar = NSStackView(views: [left, centre, right])
        bar.distribution = .equalSpacing
        bar.edgeInsets = NSEdgeInsets(top: 0, left: 12, bottom: 0, right: 12)
        bar.translatesAutoresizingMaskIntoConstraints = false
        content.addSubview(bar)

        scroll.contentView = CenterClipView()
        scroll.documentView = canvas
        scroll.hasVerticalScroller = true
        scroll.hasHorizontalScroller = true
        scroll.autohidesScrollers = true
        scroll.allowsMagnification = true
        scroll.minMagnification = 0.1
        scroll.maxMagnification = 8
        scroll.drawsBackground = true
        scroll.backgroundColor = NSColor(white: 0.125, alpha: 1)
        scroll.translatesAutoresizingMaskIntoConstraints = false
        content.addSubview(scroll)

        hint.textColor = NSColor(white: 0.72, alpha: 1)
        hint.font = .systemFont(ofSize: 15)
        hint.translatesAutoresizingMaskIntoConstraints = false
        content.addSubview(hint)

        NSLayoutConstraint.activate([
            bar.topAnchor.constraint(equalTo: content.topAnchor),
            bar.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            bar.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            bar.heightAnchor.constraint(equalToConstant: 48),
            scroll.topAnchor.constraint(equalTo: bar.bottomAnchor),
            scroll.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            scroll.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            scroll.bottomAnchor.constraint(equalTo: content.bottomAnchor),
            hint.centerXAnchor.constraint(equalTo: scroll.centerXAnchor),
            hint.centerYAnchor.constraint(equalTo: scroll.centerYAnchor),
        ])
        selectTool(.pen)
    }

    // =================================================================== image

    func load(_ image: NSImage?, savedURL: URL?) {
        self.savedURL = savedURL
        dirty = false
        canvas.load(image)
        let has = image != nil
        scroll.isHidden = !has
        hint.isHidden = has
        for c in imageButtons { c.isEnabled = has }
        refreshHistory()
        guard let image = image else { return }
        window.title = savedURL?.lastPathComponent ?? "screenX"

        // size the window to the snip, then zoom out when it still does not fit
        let visible = (window.screen ?? NSScreen.main)?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        let w = min(max(image.size.width + 80, window.minSize.width), visible.width * 0.92)
        let h = min(max(image.size.height + 150, window.minSize.height), visible.height * 0.92)
        window.setContentSize(NSSize(width: w, height: h))
        window.center()
        scroll.magnification = 1
        DispatchQueue.main.async { [weak self] in
            guard let self = self else { return }
            let area = self.scroll.contentSize
            if image.size.width > area.width || image.size.height > area.height {
                self.scroll.magnify(toFit: NSRect(origin: .zero, size: image.size).insetBy(dx: -12, dy: -12))
            }
        }
    }

    private func changed() {
        dirty = true
        refreshHistory()
        guard Prefs.autoCopyChanges else { return }
        copyTimer?.invalidate()
        copyTimer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: false) { [weak self] _ in
            DispatchQueue.main.async {
                if let img = self?.canvas.flattened() { ImageTools.copy(img) }
            }
        }
    }

    private func refreshHistory() {
        undoButton.isEnabled = canvas.canUndo
        redoButton.isEnabled = canvas.canRedo
    }

    // =================================================================== actions

    private func selectTool(_ tool: Tool) {
        canvas.tool = tool
        for b in toolButtons { b.state = b.tag == tool.rawValue ? .on : .off }
        colorWell.color = tool == .highlighter ? canvas.highlighterColor : canvas.penColor
    }

    @objc private func pickTool(_ sender: NSButton) {
        selectTool(Tool(rawValue: sender.tag) ?? .pen)
    }

    @objc private func pickShape(_ sender: NSPopUpButton) {
        guard let item = sender.selectedItem, let tool = Tool(rawValue: item.tag), sender.indexOfSelectedItem > 0 else { return }
        selectTool(tool)
    }

    @objc private func colorChanged() {
        if canvas.tool == .highlighter { canvas.highlighterColor = colorWell.color } else { canvas.penColor = colorWell.color }
    }

    @objc private func sizeChanged() {
        canvas.strokeWidth = CGFloat(sizeSlider.doubleValue.rounded())
    }

    @objc private func undo() { canvas.undo() }
    @objc private func redo() { canvas.redo() }
    @objc private func eraseAllMarks() { canvas.eraseAll() }

    @objc private func modeChanged() {
        Prefs.snipMode = SnipMode.allCases[max(0, modePopup.indexOfSelectedItem)]
    }

    @objc private func delayChanged() {
        Prefs.snipDelay = [0, 3, 5, 10][max(0, min(3, delayPopup.indexOfSelectedItem))]
    }

    @objc private func newSnip() {
        window.orderOut(nil)
        AppDelegate.shared.snip(Prefs.snipMode, delay: Prefs.snipDelay, into: self)
    }

    @objc private func newRecording() {
        window.miniaturize(nil)
        AppDelegate.shared.startRecording()
    }

    @objc private func copyImage() {
        guard let img = canvas.flattened() else { return }
        ImageTools.copy(img)
        Toast.show("Copied to clipboard")
    }

    @objc private func saveAs() {
        guard let img = canvas.flattened() else { return }
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.png, .jpeg]
        panel.nameFieldStringValue = savedURL?.lastPathComponent ?? Prefs.uniqueFile(in: Prefs.screenshotFolder, prefix: "Screenshot", ext: "png").lastPathComponent
        panel.directoryURL = savedURL?.deletingLastPathComponent() ?? Prefs.screenshotFolder
        panel.beginSheetModal(for: window) { [weak self] response in
            guard response == .OK, let url = panel.url else { return }
            do {
                try ImageTools.save(img, to: url)
                self?.savedURL = url
                self?.dirty = false
                self?.window.title = url.lastPathComponent
            } catch {
                NSAlert(error: error).runModal()
            }
        }
    }

    @objc private func shareImage(_ sender: NSButton) {
        guard let img = canvas.flattened() else { return }
        NSSharingServicePicker(items: [img]).show(relativeTo: sender.bounds, of: sender, preferredEdge: .minY)
    }

    @objc private func openSettings() {
        AppDelegate.shared.openSettings()
    }

    @objc private func extractText() {
        guard let img = canvas.image else { return }
        Toast.show("Extracting text…")
        ImageTools.recognizeText(in: img) { text in
            if text.isEmpty { Toast.show("No text detected"); return }
            ImageTools.copy(text: text)
            TextResult.show(text)
        }
    }

    // =================================================================== window delegate

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        guard dirty, hasImage, Prefs.askToSave else { return true }
        let alert = NSAlert()
        alert.messageText = "Do you want to save your changes to this snip?"
        alert.addButton(withTitle: "Save…")
        alert.addButton(withTitle: "Don't Save")
        alert.addButton(withTitle: "Cancel")
        switch alert.runModal() {
        case .alertFirstButtonReturn:
            saveAs()
            return false
        case .alertSecondButtonReturn:
            return true
        default:
            return false
        }
    }

    func windowWillClose(_ notification: Notification) {
        copyTimer?.invalidate()
        EditorController.all.removeAll { $0 === self }
    }
}

/// Window with the recognised text (already copied to the clipboard).
enum TextResult {
    static var windows: [NSWindow] = []

    static func show(_ text: String) {
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 560, height: 380),
                         styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        w.title = "Text copied to clipboard"
        w.isReleasedWhenClosed = false
        let scroll = NSTextView.scrollableTextView()
        if let tv = scroll.documentView as? NSTextView {
            tv.string = text
            tv.font = .systemFont(ofSize: 14)
            tv.textContainerInset = NSSize(width: 10, height: 10)
        }
        w.contentView = scroll
        w.center()
        NSApp.activate(ignoringOtherApps: true)
        w.makeKeyAndOrderFront(nil)
        windows.append(w)
        if windows.count > 8 { windows.removeFirst() }
    }
}
