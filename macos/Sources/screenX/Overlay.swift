import AppKit

// Snipping overlay, the macOS twin of the Windows OverlayWindow: a frozen copy of the screen with a floating
// top toolbar and, after a rectangle snip, a markup bar under the selection. The bars use Liquid Glass
// (NSGlassEffectView) on macOS 26 and later, and a blurred HUD material on older systems.

final class FlippedView: NSView {
    override var isFlipped: Bool { true }
}

enum Glass {
    static func symbol(_ name: String, _ tip: String, size: CGFloat = 15) -> NSImage {
        let base = NSImage(systemSymbolName: name, accessibilityDescription: tip)
            ?? NSImage(systemSymbolName: "questionmark", accessibilityDescription: tip)
            ?? NSImage(size: NSSize(width: 16, height: 16))
        return base.withSymbolConfiguration(NSImage.SymbolConfiguration(pointSize: size, weight: .medium)) ?? base
    }

    /// Wraps `content` in a glass capsule sized to fit it.
    static func wrap(_ content: NSView, radius: CGFloat) -> NSView {
        let size = content.fittingSize
        let frame = NSRect(origin: .zero, size: size)
        content.frame = frame
        if #available(macOS 26.0, *) {
            let g = NSGlassEffectView(frame: frame)
            g.cornerRadius = radius
            g.contentView = content
            return g
        }
        let v = NSVisualEffectView(frame: frame)
        v.material = .hudWindow
        v.blendingMode = .withinWindow
        v.state = .active
        v.wantsLayer = true
        v.layer?.cornerRadius = radius
        v.layer?.cornerCurve = .continuous
        v.layer?.masksToBounds = true
        v.layer?.borderWidth = 0.5
        v.layer?.borderColor = NSColor(white: 1, alpha: 0.35).cgColor
        content.autoresizingMask = [.width, .height]
        v.addSubview(content)
        return v
    }

    static func separator() -> NSView {
        let v = NSView()
        v.wantsLayer = true
        v.layer?.backgroundColor = NSColor(white: 1, alpha: 0.28).cgColor
        v.translatesAutoresizingMaskIntoConstraints = false
        v.widthAnchor.constraint(equalToConstant: 1).isActive = true
        v.heightAnchor.constraint(equalToConstant: 18).isActive = true
        return v
    }
}

/// Borderless icon (or icon + title) button for the glass bars.
final class BarButton: NSButton {
    var active = false { didSet { refresh() } }
    var accent = false { didSet { refresh() } }
    private var hovering = false
    private var handler: () -> Void

    init(_ symbol: String, _ tip: String, title: String? = nil, width: CGFloat = 36, size: CGFloat = 15, handler: @escaping () -> Void) {
        // icon buttons are true circles (width == height); titled buttons are capsules of the same height
        let h: CGFloat = title == nil ? width : 36
        self.handler = handler
        super.init(frame: NSRect(x: 0, y: 0, width: width, height: h))
        isBordered = false
        setButtonType(.momentaryChange)
        image = Glass.symbol(symbol, tip, size: size)
        contentTintColor = .white
        var w = width
        if let title = title {
            let attr = NSAttributedString(string: title, attributes: [
                .foregroundColor: NSColor.white, .font: NSFont.systemFont(ofSize: 13, weight: .semibold),
            ])
            attributedTitle = attr
            imagePosition = .imageLeading
            imageHugsTitle = true
            w = ceil(attr.size().width) + 48
        } else {
            self.title = ""
            imagePosition = .imageOnly
        }
        toolTip = tip
        setAccessibilityLabel(tip)
        wantsLayer = true
        layer?.cornerRadius = h / 2
        target = self
        action = #selector(fire)
        translatesAutoresizingMaskIntoConstraints = false
        widthAnchor.constraint(equalToConstant: w).isActive = true
        heightAnchor.constraint(equalToConstant: h).isActive = true
    }

    required init?(coder: NSCoder) { fatalError("not used") }

    // Without this AppKit adds its own bezel margins to the frame, which stretched the round highlight into an oval.
    override var alignmentRectInsets: NSEdgeInsets { NSEdgeInsets() }

    func setSymbol(_ name: String) { image = Glass.symbol(name, toolTip ?? "") }

    override var isEnabled: Bool { didSet { alphaValue = isEnabled ? 1 : 0.35 } }

    @objc private func fire() { handler() }

    private func refresh() {
        let color: NSColor
        if accent { color = NSColor.controlAccentColor.withAlphaComponent(hovering ? 1 : 0.88) }
        else if active { color = NSColor(white: 1, alpha: 0.30) }
        else if hovering { color = NSColor(white: 1, alpha: 0.14) }
        else { color = .clear }
        layer?.backgroundColor = color.cgColor
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        for a in trackingAreas { removeTrackingArea(a) }
        addTrackingArea(NSTrackingArea(rect: bounds, options: [.mouseEnteredAndExited, .activeAlways], owner: self, userInfo: nil))
    }

    override func mouseEntered(with event: NSEvent) { hovering = true; refresh() }
    override func mouseExited(with event: NSEvent) { hovering = false; refresh() }
}

/// Round colour chip in the Colours flyout.
final class Swatch: NSButton {
    let color: NSColor
    private var handler: (NSColor) -> Void
    var chosen = false { didSet { layer?.borderWidth = chosen ? 2.5 : 0.5 } }

    init(_ color: NSColor, handler: @escaping (NSColor) -> Void) {
        self.color = color
        self.handler = handler
        super.init(frame: NSRect(x: 0, y: 0, width: 22, height: 22))
        isBordered = false
        title = ""
        wantsLayer = true
        layer?.backgroundColor = color.cgColor
        layer?.cornerRadius = 11
        layer?.borderColor = NSColor.white.cgColor
        layer?.borderWidth = 0.5
        toolTip = color.hexString
        target = self
        action = #selector(fire)
        translatesAutoresizingMaskIntoConstraints = false
        widthAnchor.constraint(equalToConstant: 22).isActive = true
        heightAnchor.constraint(equalToConstant: 22).isActive = true
    }

    required init?(coder: NSCoder) { fatalError("not used") }
    override var alignmentRectInsets: NSEdgeInsets { NSEdgeInsets() }
    @objc private func fire() { handler(color) }
}

final class ClosureItem: NSMenuItem {
    private var handler: (() -> Void)?

    convenience init(_ title: String, symbol: String? = nil, key: String = "", mods: NSEvent.ModifierFlags = [.command], checked: Bool = false, handler: @escaping () -> Void) {
        self.init(title: title, action: #selector(fire), keyEquivalent: key)
        keyEquivalentModifierMask = mods
        target = self
        self.handler = handler
        if let s = symbol { image = NSImage(systemSymbolName: s, accessibilityDescription: title) }
        state = checked ? .on : .off
    }

    @objc private func fire() { handler?() }
}

// ======================================================================= canvas

/// Dim layer, selection, handles and mark-ups, drawn over the frozen screenshot. Coordinates are screen
/// points with a top-left origin.
final class OverlayCanvas: NSView {
    enum Phase { case picking, markup }
    private enum Drag { case none, select, resize, draw, erase, free }

    weak var owner: Overlay?
    var mode: SnipMode = .rectangle
    var textMode = false
    var phase = Phase.picking
    var selection: NSRect?
    var scale: CGFloat = 2

    var marks: [Mark] = []
    private var undoStack: [[Mark]] = []
    private var redoStack: [[Mark]] = []
    private var current: Mark?
    var tool: Tool = .pen
    var penColor = NSColor(hex: "#E81123") ?? .red
    var highlighterColor = NSColor(hex: "#FCE100") ?? .yellow
    var strokeWidth: CGFloat = 4

    var windows: [NSRect] = []
    private var hoverWindow: NSRect?
    private(set) var freePath: [NSPoint] = []

    private var drag = Drag.none
    private var start = NSPoint.zero
    private var startSel = NSRect.zero
    private var edges: (l: Bool, r: Bool, t: Bool, b: Bool) = (false, false, false, false)

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    var canUndo: Bool { !undoStack.isEmpty }
    var canRedo: Bool { !redoStack.isEmpty }

    func reset() {
        phase = .picking
        selection = nil
        marks = []
        undoStack = []
        redoStack = []
        current = nil
        freePath = []
        hoverWindow = nil
        drag = .none
        needsDisplay = true
    }

    // ---- history

    private func pushUndo() {
        undoStack.append(marks)
        redoStack = []
    }

    func undo() {
        guard let s = undoStack.popLast() else { return }
        redoStack.append(marks)
        marks = s
        needsDisplay = true
        owner?.historyChanged()
    }

    func redo() {
        guard let s = redoStack.popLast() else { return }
        undoStack.append(marks)
        marks = s
        needsDisplay = true
        owner?.historyChanged()
    }

    func eraseAll() {
        guard !marks.isEmpty else { return }
        pushUndo()
        marks = []
        needsDisplay = true
        owner?.historyChanged()
    }

    // ---- drawing

    override func draw(_ dirtyRect: NSRect) {
        let hole = selection ?? (mode == .window && !textMode && phase == .picking ? hoverWindow : nil)
        let dim = NSBezierPath(rect: bounds)
        if let h = hole {
            dim.appendRect(h)
            dim.windingRule = .evenOdd
        }
        NSColor(white: 0, alpha: 0.45).setFill()
        dim.fill()

        if let sel = selection {
            NSGraphicsContext.saveGraphicsState()
            NSBezierPath(rect: sel).addClip()
            for m in marks { m.draw() }
            current?.draw()
            NSGraphicsContext.restoreGraphicsState()

            NSColor.white.setStroke()
            let frame = NSBezierPath(rect: sel)
            frame.lineWidth = 1.5
            frame.stroke()
            if phase == .markup { drawHandles(sel) }
            drawLabel("\(Int((sel.width * scale).rounded())) × \(Int((sel.height * scale).rounded()))", near: sel)
        } else if let h = hole {
            NSColor.controlAccentColor.setStroke()
            let frame = NSBezierPath(rect: h.insetBy(dx: 1.5, dy: 1.5))
            frame.lineWidth = 3
            frame.stroke()
        }

        if freePath.count > 1 {
            let p = NSBezierPath()
            p.move(to: freePath[0])
            for q in freePath.dropFirst() { p.line(to: q) }
            p.lineWidth = 2
            p.lineJoinStyle = .round
            NSColor.white.setStroke()
            p.stroke()
        }

        if selection == nil && drag == .none { drawHint() }
    }

    private func drawHandles(_ r: NSRect) {
        let c = NSBezierPath()
        c.lineWidth = 4
        c.lineCapStyle = .round
        c.lineJoinStyle = .round
        let k: CGFloat = min(16, r.width / 3, r.height / 3)
        for (x, sx) in [(r.minX, CGFloat(1)), (r.maxX, CGFloat(-1))] {
            for (y, sy) in [(r.minY, CGFloat(1)), (r.maxY, CGFloat(-1))] {
                c.move(to: NSPoint(x: x, y: y + k * sy))
                c.line(to: NSPoint(x: x, y: y))
                c.line(to: NSPoint(x: x + k * sx, y: y))
            }
        }
        if r.height > 90 {
            for x in [r.minX, r.maxX] {
                c.move(to: NSPoint(x: x, y: r.midY - 12))
                c.line(to: NSPoint(x: x, y: r.midY + 12))
            }
        }
        if r.width > 90 {
            for y in [r.minY, r.maxY] {
                c.move(to: NSPoint(x: r.midX - 12, y: y))
                c.line(to: NSPoint(x: r.midX + 12, y: y))
            }
        }
        NSColor.white.setStroke()
        c.stroke()
    }

    private func pill(_ text: String, font: NSFont, centre: NSPoint? = nil, origin: NSPoint? = nil) {
        let attr = NSAttributedString(string: text, attributes: [.foregroundColor: NSColor.white, .font: font])
        let s = attr.size()
        let w = s.width + 20, h = s.height + 10
        var o = origin ?? NSPoint(x: (centre?.x ?? 0) - w / 2, y: (centre?.y ?? 0) - h / 2)
        o.x = min(max(o.x, 6), bounds.width - w - 6)
        o.y = min(max(o.y, 6), bounds.height - h - 6)
        let box = NSRect(x: o.x, y: o.y, width: w, height: h)
        NSColor(white: 0.08, alpha: 0.72).setFill()
        NSBezierPath(roundedRect: box, xRadius: h / 2, yRadius: h / 2).fill()
        attr.draw(at: NSPoint(x: box.minX + 10, y: box.minY + 5))
    }

    private func drawLabel(_ text: String, near r: NSRect) {
        let y = r.minY - 30 >= 6 ? r.minY - 30 : r.minY + 8
        pill(text, font: .monospacedDigitSystemFont(ofSize: 11, weight: .medium), origin: NSPoint(x: r.minX + (y < r.minY ? 0 : 8), y: y))
    }

    private func drawHint() {
        let text: String
        if textMode { text = "Drag over the text to copy it  ·  Esc to cancel" }
        else {
            switch mode {
            case .rectangle: text = "Drag to select an area  ·  Esc to cancel"
            case .window: text = "Click a window to capture it  ·  Esc to cancel"
            case .freeform: text = "Draw around the area to capture  ·  Esc to cancel"
            case .fullscreen: text = "Capturing the full screen…"
            }
        }
        pill(text, font: .systemFont(ofSize: 13, weight: .medium), centre: NSPoint(x: bounds.midX, y: bounds.height * 0.5))
    }

    override func resetCursorRects() {
        addCursorRect(bounds, cursor: .crosshair)
    }

    // ---- mouse

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        for a in trackingAreas { removeTrackingArea(a) }
        addTrackingArea(NSTrackingArea(rect: bounds, options: [.mouseMoved, .activeAlways, .inVisibleRect], owner: self, userInfo: nil))
    }

    private func point(_ event: NSEvent) -> NSPoint {
        let p = convert(event.locationInWindow, from: nil)
        return NSPoint(x: min(max(p.x, 0), bounds.width), y: min(max(p.y, 0), bounds.height))
    }

    override func mouseMoved(with event: NSEvent) {
        guard phase == .picking, mode == .window, !textMode else { return }
        let p = point(event)
        let h = windows.first { $0.contains(p) }
        if h != hoverWindow {
            hoverWindow = h
            needsDisplay = true
        }
    }

    override func mouseDown(with event: NSEvent) {
        window?.makeFirstResponder(self)
        owner?.hideFlyout()
        let p = point(event)
        start = p
        if phase == .markup, let sel = selection {
            let s: CGFloat = 9
            let nearL = abs(p.x - sel.minX) <= s, nearR = abs(p.x - sel.maxX) <= s
            let nearT = abs(p.y - sel.minY) <= s, nearB = abs(p.y - sel.maxY) <= s
            if sel.insetBy(dx: -s, dy: -s).contains(p) && (nearL || nearR || nearT || nearB) {
                drag = .resize
                startSel = sel
                edges = (nearL, nearR && !nearL, nearT, nearB && !nearT)
            } else if sel.contains(p) {
                beginMark(p)
            } else if marks.isEmpty {
                // nothing drawn yet: dragging outside starts a new selection
                phase = .picking
                selection = nil
                drag = .select
                owner?.selectionCleared()
            }
            needsDisplay = true
            return
        }
        if textMode || mode == .rectangle || mode == .fullscreen {
            drag = .select
            selection = nil
        } else if mode == .window {
            if let h = hoverWindow ?? windows.first(where: { $0.contains(p) }) {
                selection = h
                needsDisplay = true
                owner?.picked()
            }
        } else {
            drag = .free
            freePath = [p]
        }
        needsDisplay = true
    }

    private func beginMark(_ p: NSPoint) {
        switch tool {
        case .eraser:
            drag = .erase
            eraseAt(p)
        case .pen:
            current = Mark(kind: .pen, points: [p], color: penColor, width: strokeWidth)
        case .highlighter:
            current = Mark(kind: .highlighter, points: [p], color: highlighterColor, width: max(strokeWidth * 4, 14))
        case .rectangle:
            current = Mark(kind: .rectangle, points: [p, p], color: penColor, width: strokeWidth)
        case .ellipse:
            current = Mark(kind: .ellipse, points: [p, p], color: penColor, width: strokeWidth)
        case .line:
            current = Mark(kind: .line, points: [p, p], color: penColor, width: strokeWidth)
        case .arrow:
            current = Mark(kind: .arrow, points: [p, p], color: penColor, width: strokeWidth)
        case .crop:
            break
        }
        if current != nil { drag = .draw }
    }

    override func mouseDragged(with event: NSEvent) {
        var p = point(event)
        switch drag {
        case .select:
            selection = NSRect(x: min(p.x, start.x), y: min(p.y, start.y), width: abs(p.x - start.x), height: abs(p.y - start.y))
        case .resize:
            var x0 = startSel.minX, x1 = startSel.maxX, y0 = startSel.minY, y1 = startSel.maxY
            if edges.l { x0 = p.x }
            if edges.r { x1 = p.x }
            if edges.t { y0 = p.y }
            if edges.b { y1 = p.y }
            let r = NSRect(x: min(x0, x1), y: min(y0, y1), width: abs(x1 - x0), height: abs(y1 - y0))
            if r.width >= 24 && r.height >= 24 {
                selection = r
                owner?.selectionMoved()
            }
        case .draw:
            guard var m = current else { return }
            if m.kind == .pen || m.kind == .highlighter {
                m.points.append(p)
            } else {
                if event.modifierFlags.contains(.shift), m.kind == .rectangle || m.kind == .ellipse, let a = m.points.first {
                    let side = max(abs(p.x - a.x), abs(p.y - a.y))
                    p = NSPoint(x: a.x + (p.x >= a.x ? side : -side), y: a.y + (p.y >= a.y ? side : -side))
                }
                m.points[1] = p
            }
            current = m
        case .erase:
            eraseAt(p)
        case .free:
            freePath.append(p)
        case .none:
            return
        }
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        let was = drag
        drag = .none
        switch was {
        case .select:
            if let s = selection, s.width >= 8, s.height >= 8 {
                selection = s.integral
                owner?.picked()
            } else {
                selection = nil
            }
        case .draw:
            if let m = current {
                current = nil
                let shape = !(m.kind == .pen || m.kind == .highlighter)
                if !shape || m.bounds.width >= 3 || m.bounds.height >= 3 {
                    pushUndo()
                    marks.append(m)
                    owner?.historyChanged()
                }
            }
        case .free:
            let b = Mark(kind: .pen, points: freePath, color: .white, width: 1).bounds
            if freePath.count > 2, b.width >= 8, b.height >= 8 {
                selection = b.integral
                owner?.picked()
            } else {
                freePath = []
            }
        default:
            break
        }
        needsDisplay = true
    }

    private func eraseAt(_ p: NSPoint) {
        guard let i = marks.lastIndex(where: { $0.hit(p) }) else { return }
        pushUndo()
        marks.remove(at: i)
        owner?.historyChanged()
    }
}

// ======================================================================= overlay

final class Overlay: NSObject, NSSharingServicePickerDelegate {
    static var current: Overlay?
    private static var starting = false

    private enum Flyout { case ink, eraser, shapes }

    private let window: KeyWindow
    private let root = FlippedView()
    private let canvas = OverlayCanvas()
    private let shot: CGImage
    private let onCapture: (NSImage) -> Void
    private let onCancel: (() -> Void)?
    private var previousApp: NSRunningApplication?
    private var keyMonitor: Any?
    private var safeTop: CGFloat = 0

    private var topBar: NSView!
    private var markupBar: NSView!
    private var flyout: NSView?
    private var flyoutKind: Flyout?
    private var snipButton: BarButton!
    private var modeButton: BarButton!
    private var markupToggle: BarButton!
    private var textButton: BarButton!
    private var toolButtons: [Tool: BarButton] = [:]
    private var shapesButton: BarButton!
    private var undoButton: BarButton!
    private var redoButton: BarButton!
    private var shareButton: BarButton!
    private var swatches: [Swatch] = []
    private var picker: NSSharingServicePicker?

    /// Freezes the screen under the pointer and opens the overlay on it. `onCapture` gets the finished snip.
    static func start(mode: SnipMode, delay: Int = 0, text: Bool = false,
                      onCapture: @escaping (NSImage) -> Void, onCancel: (() -> Void)? = nil) {
        if current != nil || starting { return }
        if !CGPreflightScreenCaptureAccess() {
            CGRequestScreenCaptureAccess() // adds screenX to the list and shows the system prompt once
            Toast.show("screenX needs the Screen Recording permission. Allow it under System Settings > Privacy & Security > Screen & System Audio Recording, then reopen screenX.")
            onCancel?()
            return
        }
        starting = true
        let previous = NSWorkspace.shared.frontmostApplication
        DispatchQueue.main.asyncAfter(deadline: .now() + .milliseconds(delay * 1000 + 250)) {
            let mouse = NSEvent.mouseLocation
            guard let screen = NSScreen.screens.first(where: { NSMouseInRect(mouse, $0.frame, false) }) ?? NSScreen.main else {
                starting = false
                onCancel?()
                return
            }
            let windows = Capture.windowRects(on: screen)
            Capture.grab(screen) { cg in
                starting = false
                guard let cg = cg else {
                    Toast.show("Could not capture the screen. Allow screenX under System Settings > Privacy & Security > Screen Recording.")
                    onCancel?()
                    return
                }
                let o = Overlay(screen: screen, shot: cg, mode: mode, text: text, onCapture: onCapture, onCancel: onCancel)
                o.previousApp = previous
                o.canvas.windows = windows
                current = o
                if mode == .fullscreen && !text {
                    o.canvas.selection = o.canvas.bounds
                    o.capture()
                } else {
                    o.show()
                }
            }
        }
    }

    /// Renders the overlay over a made-up screen and writes overlay.png (the UI) and snip.png (the output)
    /// to `folder`. Needs no Screen Recording permission: `screenX --selftest <folder>`.
    static func selfTest(_ folder: URL) {
        guard let screen = NSScreen.main else { exit(1) }
        let size = screen.frame.size
        let fake = NSImage(size: size, flipped: false) { r in
            NSGradient(colors: [NSColor.systemTeal, NSColor.systemIndigo, NSColor.systemPink])?.draw(in: r, angle: 35)
            NSColor.white.setFill()
            NSBezierPath(roundedRect: NSRect(x: 260, y: r.height - 560, width: 520, height: 300), xRadius: 14, yRadius: 14).fill()
            ("screenX self-test" as NSString).draw(at: NSPoint(x: 290, y: r.height - 320),
                withAttributes: [.font: NSFont.systemFont(ofSize: 34, weight: .bold), .foregroundColor: NSColor.black])
            return true
        }
        guard let cg = fake.cgImage(forProposedRect: nil, context: nil, hints: nil) else { exit(1) }
        let o = Overlay(screen: screen, shot: cg, mode: .rectangle, text: false, onCapture: { _ in }, onCancel: nil)
        current = o
        o.show()
        o.canvas.selection = NSRect(x: 220, y: 220, width: 640, height: 380)
        Prefs.quickMarkup = true
        o.picked()
        o.canvas.marks = [
            Mark(kind: .arrow, points: [NSPoint(x: 300, y: 520), NSPoint(x: 520, y: 400)], color: o.canvas.penColor, width: 5),
            Mark(kind: .rectangle, points: [NSPoint(x: 560, y: 300), NSPoint(x: 800, y: 420)], color: .systemBlue, width: 4),
            Mark(kind: .highlighter, points: [NSPoint(x: 300, y: 300), NSPoint(x: 520, y: 300)], color: o.canvas.highlighterColor, width: 16),
        ]
        o.toggleFlyout(.shapes)
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) {
            try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            if let rep = o.root.bitmapImageRepForCachingDisplay(in: o.root.bounds) {
                o.root.cacheDisplay(in: o.root.bounds, to: rep)
                try? rep.representation(using: .png, properties: [:])?.write(to: folder.appendingPathComponent("overlay.png"))
            }
            if let out = o.output(), let data = ImageTools.pngData(out) {
                try? data.write(to: folder.appendingPathComponent("snip.png"))
            }
            let report = "topBar \(o.topBar.frame)\nmarkupBar \(o.markupBar.frame)\nflyout \(o.flyout?.frame ?? .zero)\nglass \(type(of: o.topBar!))\nsafeTop \(o.safeTop)\n"
            try? report.write(to: folder.appendingPathComponent("report.txt"), atomically: true, encoding: .utf8)
            exit(0)
        }
    }

    private init(screen: NSScreen, shot: CGImage, mode: SnipMode, text: Bool,
                 onCapture: @escaping (NSImage) -> Void, onCancel: (() -> Void)?) {
        self.shot = shot
        self.onCapture = onCapture
        self.onCancel = onCancel
        let f = screen.frame
        window = KeyWindow(contentRect: f, styleMask: .borderless, backing: .buffered, defer: false)
        super.init()
        safeTop = screen.safeAreaInsets.top

        window.isOpaque = true
        window.backgroundColor = .black
        window.hasShadow = false
        window.level = NSWindow.Level(rawValue: NSWindow.Level.statusBar.rawValue + 1)
        window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        window.isReleasedWhenClosed = false
        window.acceptsMouseMovedEvents = true
        window.appearance = NSAppearance(named: .darkAqua)
        window.animationBehavior = .none

        let size = NSRect(origin: .zero, size: f.size)
        root.frame = size
        window.contentView = root
        let image = NSImage(cgImage: shot, size: f.size)
        let imageView = NSImageView(frame: size)
        imageView.image = image
        imageView.imageScaling = .scaleAxesIndependently
        imageView.autoresizingMask = [.width, .height]
        root.addSubview(imageView)

        canvas.frame = size
        canvas.autoresizingMask = [.width, .height]
        canvas.owner = self
        canvas.mode = mode == .fullscreen ? .rectangle : mode
        canvas.textMode = text
        canvas.scale = CGFloat(shot.width) / max(1, f.width)
        root.addSubview(canvas)

        buildTopBar()
        buildMarkupBar()
    }

    private func show() {
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
        window.makeFirstResponder(canvas)
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] e in
            guard let self = self, e.window === self.window else { return e }
            return self.key(e) ? nil : e
        }
    }

    private func close(reactivate: Bool = true) {
        if let m = keyMonitor { NSEvent.removeMonitor(m) }
        keyMonitor = nil
        window.orderOut(nil)
        if Overlay.current === self { Overlay.current = nil }
        if reactivate, let app = previousApp, app.processIdentifier != ProcessInfo.processInfo.processIdentifier {
            app.activate()
        }
    }

    private func cancel() {
        close()
        onCancel?()
    }

    // =================================================================== bars

    private func bar(_ views: [NSView]) -> NSView {
        let stack = NSStackView(views: views)
        stack.orientation = .horizontal
        stack.spacing = 4
        stack.edgeInsets = NSEdgeInsets(top: 6, left: 6, bottom: 6, right: 6)
        return Glass.wrap(stack, radius: 24)
    }

    private func modeSymbol(_ m: SnipMode) -> String {
        switch m {
        case .rectangle: return "rectangle.dashed"
        case .window: return "macwindow"
        case .fullscreen: return "display"
        case .freeform: return "lasso"
        }
    }

    private func buildTopBar() {
        snipButton = BarButton("camera.viewfinder", "Snip") { [weak self] in self?.setTextMode(false) }
        let record = BarButton("video", "Record the screen") { [weak self] in
            self?.close(reactivate: false)
            AppDelegate.shared.startRecording()
        }
        modeButton = BarButton(modeSymbol(canvas.mode), "Snip mode") { [weak self] in self?.showModeMenu() }
        let modeChevron = BarButton("chevron.down", "Snip mode", width: 22, size: 9) { [weak self] in self?.showModeMenu() }
        markupToggle = BarButton("pencil.and.scribble", "Quick markup: draw on the snip before capturing") { [weak self] in
            guard let self = self else { return }
            Prefs.quickMarkup.toggle()
            self.markupToggle.active = Prefs.quickMarkup
        }
        markupToggle.active = Prefs.quickMarkup
        let colour = BarButton("eyedropper", "Colour picker") { [weak self] in
            self?.close(reactivate: false)
            AppDelegate.shared.pickColour()
        }
        textButton = BarButton("text.viewfinder", "Text extractor") { [weak self] in self?.setTextMode(true) }
        let closeButton = BarButton("xmark", "Close (Esc)") { [weak self] in self?.cancel() }
        topBar = bar([snipButton, record, Glass.separator(), modeButton, modeChevron, markupToggle,
                      Glass.separator(), colour, textButton, Glass.separator(), closeButton])
        root.addSubview(topBar)
        topBar.setFrameOrigin(NSPoint(x: ((root.bounds.width - topBar.frame.width) / 2).rounded(), y: safeTop + 14))
        refreshTopBar()
    }

    private func refreshTopBar() {
        snipButton.active = !canvas.textMode
        textButton.active = canvas.textMode
        modeButton.setSymbol(modeSymbol(canvas.mode))
        modeButton.isEnabled = !canvas.textMode
        markupToggle.isEnabled = !canvas.textMode
    }

    private func setTextMode(_ on: Bool) {
        canvas.textMode = on
        restart()
    }

    private func restart() {
        hideFlyout()
        markupBar.isHidden = true
        canvas.reset()
        refreshTopBar()
    }

    private func showModeMenu() {
        let menu = NSMenu()
        for m in [SnipMode.rectangle, .window, .fullscreen, .freeform] {
            menu.addItem(ClosureItem(m.title, symbol: modeSymbol(m), checked: m == canvas.mode) { [weak self] in self?.pickMode(m) })
        }
        menu.popUp(positioning: nil, at: NSPoint(x: 0, y: modeButton.bounds.height + 10), in: modeButton)
    }

    private func pickMode(_ m: SnipMode) {
        if m == .fullscreen {
            restart()
            canvas.selection = canvas.bounds
            capture()
            return
        }
        Prefs.snipMode = m
        canvas.mode = m
        restart()
    }

    private func buildMarkupBar() {
        let captureButton = BarButton("checkmark", "Capture: copy and save (Command-Return)", title: "Capture") { [weak self] in self?.capture() }
        captureButton.accent = true
        var captureChevron: BarButton!
        captureChevron = BarButton("chevron.down", "More capture options", width: 22, size: 9) { [weak self] in
            self?.showCaptureMenu(from: captureChevron)
        }
        shareButton = BarButton("square.and.arrow.up", "Share") { [weak self] in self?.share() }
        let lens = BarButton("sparkle.magnifyingglass", "Search image with Google Lens") { [weak self] in self?.lens() }

        func toolButton(_ symbol: String, _ tip: String, _ tool: Tool) -> BarButton {
            let b = BarButton(symbol, tip) { [weak self] in self?.toolTapped(tool) }
            toolButtons[tool] = b
            return b
        }
        let pen = toolButton("pencil.tip", "Pen (click again for colours and size)", .pen)
        let hl = toolButton("highlighter", "Highlighter (click again for colours and size)", .highlighter)
        let eraser = toolButton("eraser", "Eraser (click again to erase everything)", .eraser)
        shapesButton = BarButton("square.on.circle", "Shapes") { [weak self] in self?.toggleFlyout(.shapes) }
        undoButton = BarButton("arrow.uturn.backward", "Undo (Command-Z)") { [weak self] in self?.canvas.undo() }
        redoButton = BarButton("arrow.uturn.forward", "Redo (Shift-Command-Z)") { [weak self] in self?.canvas.redo() }
        let discard = BarButton("trash", "Discard snip (Esc)") { [weak self] in self?.cancel() }

        markupBar = bar([captureButton, captureChevron, shareButton, lens, Glass.separator(),
                         pen, hl, eraser, shapesButton, Glass.separator(), undoButton, redoButton, Glass.separator(), discard])
        markupBar.isHidden = true
        root.addSubview(markupBar)
        refreshTools()
        historyChanged()
    }

    private func refreshTools() {
        for (tool, b) in toolButtons { b.active = canvas.tool == tool }
        shapesButton.active = [.rectangle, .ellipse, .line, .arrow].contains(canvas.tool)
        let symbols: [Tool: String] = [.rectangle: "rectangle", .ellipse: "circle", .line: "line.diagonal", .arrow: "arrow.up.right"]
        shapesButton.setSymbol(symbols[canvas.tool] ?? "square.on.circle")
    }

    private func toolTapped(_ tool: Tool) {
        if canvas.tool == tool {
            toggleFlyout(tool == .eraser ? .eraser : .ink)
        } else {
            canvas.tool = tool
            hideFlyout()
            refreshTools()
        }
    }

    func historyChanged() {
        undoButton.isEnabled = canvas.canUndo
        redoButton.isEnabled = canvas.canRedo
    }

    /// Markup bar under the selection, or above / inside it when there is no room.
    private func placeMarkupBar() {
        guard let sel = canvas.selection else { return }
        let b = markupBar.frame.size, area = root.bounds
        var y = sel.maxY + 12
        if y + b.height > area.height - 8 { y = sel.minY - b.height - 12 }
        if y < safeTop + 8 { y = sel.maxY - b.height - 12 }
        let x = min(max(sel.midX - b.width / 2, 8), area.width - b.width - 8)
        markupBar.setFrameOrigin(NSPoint(x: x.rounded(), y: y.rounded()))
    }

    // =================================================================== flyouts

    private static let palette: [String] = [
        "#000000", "#3C3C3C", "#7A7A7A", "#BDBDBD", "#FFFFFF", "#7B1E1E", "#E81123", "#FF5C5C", "#FF8C00", "#FFB900",
        "#FCE100", "#BAD80A", "#107C10", "#00CC6A", "#00B294", "#008272", "#00B7C3", "#0099BC", "#0078D7", "#4A90E2",
        "#002050", "#5C2D91", "#8764B8", "#B4009E", "#E3008C", "#FF69B4", "#8E562E", "#C19A6B", "#567C73", "#486860",
    ]

    func hideFlyout() {
        flyout?.removeFromSuperview()
        flyout = nil
        flyoutKind = nil
        swatches = []
    }

    private func toggleFlyout(_ kind: Flyout) {
        let same = flyoutKind == kind
        hideFlyout()
        if same { return }

        let column = NSStackView()
        column.orientation = .vertical
        column.alignment = .leading
        column.spacing = 10
        column.edgeInsets = NSEdgeInsets(top: 12, left: 14, bottom: 12, right: 14)
        var anchor: NSView = shapesButton

        switch kind {
        case .eraser:
            anchor = toolButtons[.eraser] ?? shapesButton
            column.addArrangedSubview(BarButton("trash", "Erase all mark-ups", title: "Erase all mark-ups") { [weak self] in
                self?.canvas.eraseAll()
                self?.hideFlyout()
            })
        case .shapes:
            let row = NSStackView()
            row.spacing = 4
            for (symbol, tip, tool) in [("rectangle", "Rectangle (Shift for a square)", Tool.rectangle), ("circle", "Ellipse (Shift for a circle)", .ellipse),
                                        ("line.diagonal", "Line", .line), ("arrow.up.right", "Arrow", .arrow)] {
                let b = BarButton(symbol, tip) { [weak self] in
                    self?.canvas.tool = tool
                    self?.refreshTools()
                    self?.hideFlyout()
                }
                b.active = canvas.tool == tool
                row.addArrangedSubview(b)
            }
            column.addArrangedSubview(row)
            addInkControls(to: column)
        case .ink:
            anchor = toolButtons[canvas.tool] ?? shapesButton
            addInkControls(to: column)
        }

        let f = Glass.wrap(column, radius: 20)
        root.addSubview(f)
        let a = anchor.convert(anchor.bounds, to: root), bar = markupBar.frame, area = root.bounds
        var y = bar.maxY + 8
        if y + f.frame.height > area.height - 8 { y = bar.minY - f.frame.height - 8 }
        let x = min(max(a.midX - f.frame.width / 2, 8), area.width - f.frame.width - 8)
        f.setFrameOrigin(NSPoint(x: x.rounded(), y: y.rounded()))
        flyout = f
        flyoutKind = kind
    }

    private func label(_ text: String) -> NSTextField {
        let l = NSTextField(labelWithString: text)
        l.font = .systemFont(ofSize: 11, weight: .semibold)
        l.textColor = NSColor(white: 1, alpha: 0.75)
        return l
    }

    private func addInkControls(to column: NSStackView) {
        column.addArrangedSubview(label("Colours"))
        let chosen = (canvas.tool == .highlighter ? canvas.highlighterColor : canvas.penColor).hexString
        for r in 0..<3 {
            let row = NSStackView()
            row.spacing = 6
            for c in 0..<10 {
                let color = NSColor(hex: Overlay.palette[r * 10 + c]) ?? .white
                let s = Swatch(color) { [weak self] picked in self?.pickColor(picked) }
                s.chosen = color.hexString == chosen
                swatches.append(s)
                row.addArrangedSubview(s)
            }
            column.addArrangedSubview(row)
        }
        column.addArrangedSubview(label("Size"))
        let slider = NSSlider(value: Double(canvas.strokeWidth), minValue: 1, maxValue: 24, target: self, action: #selector(sizeChanged(_:)))
        slider.translatesAutoresizingMaskIntoConstraints = false
        slider.widthAnchor.constraint(equalToConstant: 10 * 22 + 9 * 6).isActive = true
        column.addArrangedSubview(slider)
    }

    private func pickColor(_ color: NSColor) {
        if canvas.tool == .highlighter { canvas.highlighterColor = color } else { canvas.penColor = color }
        for s in swatches { s.chosen = s.color.hexString == color.hexString }
    }

    @objc private func sizeChanged(_ sender: NSSlider) {
        canvas.strokeWidth = CGFloat(sender.doubleValue.rounded())
    }

    // =================================================================== canvas callbacks

    /// The user finished choosing an area.
    func picked() {
        if canvas.textMode { extractText(); return }
        if canvas.mode == .rectangle && Prefs.quickMarkup {
            canvas.phase = .markup
            canvas.needsDisplay = true
            placeMarkupBar()
            markupBar.isHidden = false
        } else {
            capture()
        }
    }

    func selectionMoved() {
        hideFlyout()
        placeMarkupBar()
    }

    func selectionCleared() {
        hideFlyout()
        markupBar.isHidden = true
    }

    // =================================================================== keyboard

    private func key(_ e: NSEvent) -> Bool {
        let cmd = e.modifierFlags.contains(.command)
        let markup = canvas.phase == .markup && canvas.selection != nil
        if e.keyCode == 53 { // Esc
            if flyout != nil { hideFlyout() } else { cancel() }
            return true
        }
        if e.keyCode == 36 || e.keyCode == 76 { // Return / Enter
            if markup { capture() }
            return true
        }
        guard cmd, let ch = e.charactersIgnoringModifiers?.lowercased() else { return false }
        switch ch {
        case "s": if markup { saveTo() }
        case "c": if markup { copyOnly() }
        case "z": if e.modifierFlags.contains(.shift) { canvas.redo() } else { canvas.undo() }
        case "y": canvas.redo()
        default: return false
        }
        return true
    }

    // =================================================================== output

    private func crop(_ r: NSRect) -> NSImage? {
        let k = canvas.scale
        let px = CGRect(x: (r.minX * k).rounded(), y: (r.minY * k).rounded(), width: (r.width * k).rounded(), height: (r.height * k).rounded())
        guard let cg = shot.cropping(to: px) else { return nil }
        let rep = NSBitmapImageRep(cgImage: cg)
        rep.size = NSSize(width: CGFloat(cg.width) / k, height: CGFloat(cg.height) / k)
        let img = NSImage(size: rep.size)
        img.addRepresentation(rep)
        return img
    }

    /// The snip with the mark-ups burnt in (and the freeform shape cut out).
    private func output() -> NSImage? {
        guard let sel = canvas.selection?.intersection(canvas.bounds), !sel.isEmpty, let base = crop(sel) else { return nil }
        let marks = canvas.marks
        let path = canvas.freePath
        let freeform = canvas.mode == .freeform && !canvas.textMode && path.count > 2
        if marks.isEmpty && !freeform {
            return Prefs.addBorder ? ImageTools.withBorder(base) : base
        }
        let img = ImageTools.render(like: base) { rect in
            let cg = NSGraphicsContext.current?.cgContext
            cg?.translateBy(x: -sel.minX, y: -sel.minY)
            if freeform {
                let clip = NSBezierPath()
                clip.move(to: path[0])
                for p in path.dropFirst() { clip.line(to: p) }
                clip.close()
                clip.addClip()
            }
            ImageTools.draw(base, in: sel)
            for m in marks { m.draw() }
        } ?? base
        return Prefs.addBorder && !freeform ? ImageTools.withBorder(img) : img
    }

    private func capture() {
        guard let img = output() else { cancel(); return }
        close()
        onCapture(img)
    }

    private func copyOnly() {
        guard let img = output() else { return }
        close()
        ImageTools.copy(img)
        Toast.show("Snip copied to clipboard")
    }

    private func saveTo() {
        guard let img = output() else { return }
        close(reactivate: false)
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.png, .jpeg]
        panel.nameFieldStringValue = Prefs.uniqueFile(in: Prefs.screenshotFolder, prefix: "Screenshot", ext: "png").lastPathComponent
        panel.directoryURL = Prefs.screenshotFolder
        NSApp.activate(ignoringOtherApps: true)
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            try ImageTools.save(img, to: url)
            Toast.show("Saved \(url.lastPathComponent)")
        } catch {
            NSAlert(error: error).runModal()
        }
    }

    private func showCaptureMenu(from button: NSView) {
        let menu = NSMenu()
        menu.addItem(ClosureItem("Capture", symbol: "checkmark", key: "\r") { [weak self] in self?.capture() })
        menu.addItem(ClosureItem("Save to…", symbol: "square.and.arrow.down", key: "s") { [weak self] in self?.saveTo() })
        menu.addItem(ClosureItem("Copy only", symbol: "doc.on.doc", key: "c") { [weak self] in self?.copyOnly() })
        menu.popUp(positioning: nil, at: NSPoint(x: 0, y: button.bounds.height + 10), in: button)
    }

    /// Shares a PNG file rather than the bitmap: AirDrop, Mail and Messages all take files.
    private func share() {
        guard let img = output(), let data = ImageTools.pngData(img) else { return }
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("screenX-share", isDirectory: true)
        let url = Prefs.uniqueFile(in: dir, prefix: "Screenshot", ext: "png")
        guard (try? data.write(to: url)) != nil else { return }
        hideFlyout()
        let p = NSSharingServicePicker(items: [url])
        p.delegate = self
        picker = p
        p.show(relativeTo: shareButton.bounds, of: shareButton, preferredEdge: .maxY)
    }

    func sharingServicePicker(_ sharingServicePicker: NSSharingServicePicker, didChoose service: NSSharingService?) {
        picker = nil
        // the service opens its own window, which would sit behind the overlay
        if service != nil { close(reactivate: false) }
    }

    private func lens() {
        guard let img = output() else { return }
        close(reactivate: false)
        ImageTools.copy(img)
        if let url = URL(string: "https://www.google.com/?olud") { NSWorkspace.shared.open(url) }
        Toast.show("Snip copied. Press Command-V in the Google Lens box to search with it.")
    }

    private func extractText() {
        guard let sel = canvas.selection, let img = crop(sel) else { cancel(); return }
        close(reactivate: false)
        ImageTools.recognizeText(in: img) { text in
            if text.isEmpty { Toast.show("No text detected. Try a larger or clearer area."); return }
            ImageTools.copy(text: text)
            TextResult.show(text)
        }
    }
}
