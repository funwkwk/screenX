import AppKit
import AVFoundation

/// Borderless windows cannot become key by default; these need the keyboard (Esc / Return).
final class KeyWindow: NSWindow {
    override var canBecomeKey: Bool { true }
}

/// Full-screen layer for the recording area. Before recording the screen is dimmed outside the area, which can
/// be moved, resized from its corners / edges or redrawn. While recording only a dashed red frame is shown.
final class RegionView: NSView {
    var region: NSRect
    var recording = false { didSet { needsDisplay = true } }
    var paused = false { didSet { needsDisplay = true } }
    var onChange: (() -> Void)?

    private enum Drag { case none, new, move, resize }
    private var drag = Drag.none
    private var edges: (l: Bool, r: Bool, t: Bool, b: Bool) = (false, false, false, false)
    private var startPoint = NSPoint.zero
    private var startRegion = NSRect.zero

    init(frame: NSRect, region: NSRect) {
        self.region = region
        super.init(frame: frame)
    }

    required init?(coder: NSCoder) { fatalError("not used") }

    override func draw(_ dirtyRect: NSRect) {
        if recording {
            (paused ? NSColor.systemOrange : NSColor.systemRed).setStroke()
            let p = NSBezierPath(rect: region.insetBy(dx: -1.5, dy: -1.5))
            p.lineWidth = 1.5
            p.setLineDash([3, 3], count: 2, phase: 0)
            p.stroke()
            return
        }
        NSColor(white: 0, alpha: 0.55).setFill()
        let dim = NSBezierPath(rect: bounds)
        dim.appendRect(region)
        dim.windingRule = .evenOdd
        dim.fill()

        NSColor.white.setStroke()
        let frame = NSBezierPath(rect: region)
        frame.lineWidth = 1.5
        frame.setLineDash([5, 4], count: 2, phase: 0)
        frame.stroke()

        // corner brackets and edge bars
        let c = NSBezierPath()
        c.lineWidth = 5
        c.lineCapStyle = .round
        c.lineJoinStyle = .round
        let k: CGFloat = 18
        for (x, sx) in [(region.minX, CGFloat(1)), (region.maxX, CGFloat(-1))] {
            for (y, sy) in [(region.minY, CGFloat(1)), (region.maxY, CGFloat(-1))] {
                c.move(to: NSPoint(x: x, y: y + k * sy))
                c.line(to: NSPoint(x: x, y: y))
                c.line(to: NSPoint(x: x + k * sx, y: y))
            }
        }
        for x in [region.minX, region.maxX] {
            c.move(to: NSPoint(x: x, y: region.midY - 16))
            c.line(to: NSPoint(x: x, y: region.midY + 16))
        }
        for y in [region.minY, region.maxY] {
            c.move(to: NSPoint(x: region.midX - 16, y: y))
            c.line(to: NSPoint(x: region.midX + 16, y: y))
        }
        c.stroke()
    }

    override func mouseDown(with event: NSEvent) {
        guard !recording else { return }
        let p = convert(event.locationInWindow, from: nil)
        startPoint = p
        startRegion = region
        let s: CGFloat = 12
        let nearL = abs(p.x - region.minX) <= s, nearR = abs(p.x - region.maxX) <= s
        let nearB = abs(p.y - region.minY) <= s, nearT = abs(p.y - region.maxY) <= s
        let within = region.insetBy(dx: -s, dy: -s).contains(p)
        if within && (nearL || nearR || nearT || nearB) {
            drag = .resize
            edges = (nearL, nearR && !nearL, nearT, nearB && !nearT)
        } else if region.contains(p) {
            drag = .move
        } else {
            drag = .new
        }
    }

    override func mouseDragged(with event: NSEvent) {
        guard drag != .none else { return }
        var p = convert(event.locationInWindow, from: nil)
        p.x = min(max(p.x, 0), bounds.width)
        p.y = min(max(p.y, 0), bounds.height)
        switch drag {
        case .new:
            region = NSRect(x: min(p.x, startPoint.x), y: min(p.y, startPoint.y), width: abs(p.x - startPoint.x), height: abs(p.y - startPoint.y))
        case .move:
            var r = startRegion.offsetBy(dx: p.x - startPoint.x, dy: p.y - startPoint.y)
            r.origin.x = min(max(r.origin.x, 0), bounds.width - r.width)
            r.origin.y = min(max(r.origin.y, 0), bounds.height - r.height)
            region = r
        case .resize:
            var l = startRegion.minX, r = startRegion.maxX, b = startRegion.minY, t = startRegion.maxY
            if edges.l { l = p.x }
            if edges.r { r = p.x }
            if edges.t { t = p.y }
            if edges.b { b = p.y }
            let nr = NSRect(x: min(l, r), y: min(b, t), width: abs(r - l), height: abs(t - b))
            if nr.width >= 40 && nr.height >= 40 { region = nr }
        case .none:
            break
        }
        needsDisplay = true
        onChange?()
    }

    override func mouseUp(with event: NSEvent) {
        if drag == .new && (region.width < 40 || region.height < 40) { region = startRegion }
        drag = .none
        needsDisplay = true
        onChange?()
    }
}

/// Region layer + floating control bar + AVFoundation capture session.
/// Ready:      [● Start] 00:00:00 [mic] [✕]
/// Recording:  [❚❚] [■] 00:00:09 [mic] [trash]
@MainActor
final class Recorder: NSObject, AVCaptureFileOutputRecordingDelegate {
    static var current: Recorder?

    private let screen: NSScreen
    private let layer: KeyWindow
    private let view: RegionView
    private let bar: KeyWindow
    private let row = NSStackView()
    private let timeLabel = NSTextField(labelWithString: "00:00:00")
    private var micButton: NSButton!
    private var pauseButton: NSButton?
    private var session: AVCaptureSession?
    private var output: AVCaptureMovieFileOutput?
    private var timer: Timer?
    private var discard = false
    private var keyMonitor: Any?

    static func start() {
        if let r = current {
            r.bar.makeKeyAndOrderFront(nil)
            return
        }
        guard let screen = NSScreen.main else { return }
        let r = Recorder(screen: screen)
        current = r
        r.show()
    }

    private init(screen: NSScreen) {
        self.screen = screen
        let f = screen.frame
        let size = NSSize(width: (f.width * 0.6).rounded(), height: (f.height * 0.6).rounded())
        let region = NSRect(x: ((f.width - size.width) / 2).rounded(), y: ((f.height - size.height) / 2).rounded(), width: size.width, height: size.height)
        view = RegionView(frame: NSRect(origin: .zero, size: f.size), region: region)
        layer = KeyWindow(contentRect: f, styleMask: .borderless, backing: .buffered, defer: false)
        bar = KeyWindow(contentRect: NSRect(x: 0, y: 0, width: 360, height: 52), styleMask: .borderless, backing: .buffered, defer: false)
        super.init()

        layer.isOpaque = false
        layer.backgroundColor = .clear
        layer.hasShadow = false
        layer.level = .statusBar
        layer.sharingType = .none
        layer.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        layer.isReleasedWhenClosed = false
        layer.contentView = view
        view.onChange = { [weak self] in self?.placeBar() }

        bar.isOpaque = false
        bar.backgroundColor = .clear
        bar.hasShadow = true
        bar.level = NSWindow.Level(rawValue: NSWindow.Level.statusBar.rawValue + 1)
        bar.sharingType = .none
        bar.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        bar.isReleasedWhenClosed = false
        bar.isMovableByWindowBackground = true
        bar.appearance = NSAppearance(named: .darkAqua)
        let card = NSView()
        card.wantsLayer = true
        card.layer?.backgroundColor = NSColor(white: 0.17, alpha: 0.97).cgColor
        card.layer?.cornerRadius = 10
        card.layer?.borderColor = NSColor(white: 1, alpha: 0.2).cgColor
        card.layer?.borderWidth = 1
        bar.contentView = card
        row.spacing = 8
        row.edgeInsets = NSEdgeInsets(top: 8, left: 10, bottom: 8, right: 10)
        row.translatesAutoresizingMaskIntoConstraints = false
        card.addSubview(row)
        NSLayoutConstraint.activate([
            row.topAnchor.constraint(equalTo: card.topAnchor), row.bottomAnchor.constraint(equalTo: card.bottomAnchor),
            row.leadingAnchor.constraint(equalTo: card.leadingAnchor), row.trailingAnchor.constraint(equalTo: card.trailingAnchor),
        ])
        timeLabel.font = .monospacedDigitSystemFont(ofSize: 15, weight: .regular)
        timeLabel.textColor = .white
        micButton = icon("mic.slash", "Microphone", #selector(toggleMic))
        showIdle()
    }

    private func icon(_ symbol: String, _ tip: String, _ action: Selector, tint: NSColor? = nil) -> NSButton {
        let img = NSImage(systemSymbolName: symbol, accessibilityDescription: tip) ?? NSImage(size: NSSize(width: 16, height: 16))
        let b = NSButton(image: img, target: self, action: action)
        b.isBordered = false
        b.toolTip = tip
        b.setAccessibilityLabel(tip)
        b.contentTintColor = tint ?? .white
        b.widthAnchor.constraint(equalToConstant: 34).isActive = true
        b.heightAnchor.constraint(equalToConstant: 30).isActive = true
        return b
    }

    private func setRow(_ views: [NSView]) {
        for v in row.arrangedSubviews {
            row.removeArrangedSubview(v)
            v.removeFromSuperview()
        }
        for v in views { row.addArrangedSubview(v) }
        bar.setContentSize(row.fittingSize)
        placeBar()
    }

    private func refreshMic() {
        let on = Prefs.recordMic
        micButton.image = NSImage(systemSymbolName: on ? "mic" : "mic.slash", accessibilityDescription: "Microphone")
        micButton.contentTintColor = on ? .white : NSColor(white: 0.55, alpha: 1)
        micButton.toolTip = on ? "Microphone on" : "Microphone off"
    }

    private func showIdle() {
        let start = NSButton(title: "Start", target: self, action: #selector(startClicked))
        start.image = NSImage(systemSymbolName: "record.circle", accessibilityDescription: "Start")
        start.imagePosition = .imageLeading
        start.bezelStyle = .rounded
        start.bezelColor = NSColor(hex: "#4CC2FF")
        start.keyEquivalent = "\r"
        start.toolTip = "Start recording (Return)"
        timeLabel.stringValue = "00:00:00"
        micButton.isEnabled = true
        refreshMic()
        setRow([start, timeLabel, micButton, icon("xmark", "Close (Esc)", #selector(cancel))])
    }

    private func showRecording() {
        let pause = icon("pause.fill", "Pause", #selector(togglePause))
        pauseButton = pause
        micButton.isEnabled = false
        setRow([pause, icon("stop.fill", "Stop and save", #selector(stopAndSave), tint: .systemRed), timeLabel, micButton,
                icon("trash", "Discard recording", #selector(stopAndDiscard))])
    }

    private func show() {
        layer.setFrame(screen.frame, display: true)
        layer.orderFrontRegardless()
        NSApp.activate(ignoringOtherApps: true)
        bar.makeKeyAndOrderFront(nil)
        placeBar()
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] e in
            guard let self = self, self.session == nil else { return e }
            if e.keyCode == 53 { self.cancel(); return nil } // Esc
            return e
        }
    }

    /// Centre the bar above the area (below or inside it when there is no room).
    private func placeBar() {
        let f = screen.frame, r = view.region, size = bar.frame.size
        var x = f.minX + r.midX - size.width / 2
        var y = f.minY + r.maxY + 8
        if y + size.height > f.maxY { y = f.minY + r.minY - size.height - 8 }
        if y < f.minY { y = f.minY + r.maxY - size.height - 8 }
        x = min(max(x, f.minX + 4), f.maxX - size.width - 4)
        bar.setFrameOrigin(NSPoint(x: x, y: y))
    }

    // =================================================================== actions

    @objc private func toggleMic() {
        Prefs.recordMic.toggle()
        refreshMic()
    }

    @objc private func cancel() {
        if session != nil { stopAndDiscard() } else { close() }
    }

    @objc private func startClicked() {
        guard session == nil else { return }
        if !CGPreflightScreenCaptureAccess() {
            CGRequestScreenCaptureAccess()
            Toast.show("Allow screenX under System Settings > Privacy & Security > Screen Recording, then press Start again.")
            return
        }
        if Prefs.recordMic {
            AVCaptureDevice.requestAccess(for: .audio) { granted in
                DispatchQueue.main.async { self.begin(withMic: granted) }
            }
        } else {
            begin(withMic: false)
        }
    }

    private func begin(withMic: Bool) {
        guard session == nil, Recorder.current === self else { return }
        guard let number = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber else {
            Toast.show("Could not find the display to record.")
            return
        }
        let maybeInput: AVCaptureScreenInput? = AVCaptureScreenInput(displayID: CGDirectDisplayID(number.uint32Value))
        guard let input = maybeInput else {
            Toast.show("Could not start recording this display.")
            return
        }
        // even pixel sizes keep the H.264 encoder happy
        var r = view.region.integral
        r.size.width = max(32, r.width - r.width.truncatingRemainder(dividingBy: 2))
        r.size.height = max(32, r.height - r.height.truncatingRemainder(dividingBy: 2))
        input.cropRect = r
        input.minFrameDuration = CMTime(value: 1, timescale: CMTimeScale(Prefs.fps))
        input.capturesCursor = Prefs.showCursor

        let s = AVCaptureSession()
        guard s.canAddInput(input) else { Toast.show("Could not start recording this display."); return }
        s.addInput(input)
        if withMic, let mic = AVCaptureDevice.default(for: .audio), let audio = try? AVCaptureDeviceInput(device: mic), s.canAddInput(audio) {
            s.addInput(audio)
        } else if Prefs.recordMic {
            Toast.show("Recording without the microphone (no access or no device).")
        }
        let out = AVCaptureMovieFileOutput()
        guard s.canAddOutput(out) else { Toast.show("Could not start recording."); return }
        s.addOutput(out)
        s.startRunning()

        let url = Prefs.uniqueFile(in: Prefs.videoFolder, prefix: "Screen Recording", ext: "mov")
        out.startRecording(to: url, recordingDelegate: self)
        session = s
        output = out
        discard = false

        view.recording = true
        layer.ignoresMouseEvents = true
        showRecording()
        timer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
            DispatchQueue.main.async { self?.tick() }
        }
    }

    private func tick() {
        guard let out = output else { return }
        let t = out.recordedDuration
        let secs = t.isValid && !t.isIndefinite ? Int(t.seconds) : 0
        timeLabel.stringValue = String(format: "%02d:%02d:%02d", secs / 3600, (secs / 60) % 60, secs % 60)
    }

    @objc private func togglePause() {
        guard let out = output else { return }
        if out.isRecordingPaused { out.resumeRecording() } else { out.pauseRecording() }
        let paused = !view.paused
        view.paused = paused
        pauseButton?.image = NSImage(systemSymbolName: paused ? "play.fill" : "pause.fill", accessibilityDescription: paused ? "Resume" : "Pause")
        pauseButton?.toolTip = paused ? "Resume" : "Pause"
    }

    @objc func stopAndSave() {
        discard = false
        finish()
    }

    @objc private func stopAndDiscard() {
        discard = true
        finish()
    }

    private func finish() {
        guard let out = output else { close(); return }
        timer?.invalidate()
        timeLabel.stringValue = "Saving…"
        out.stopRecording()
    }

    nonisolated func fileOutput(_ output: AVCaptureFileOutput, didFinishRecordingTo outputFileURL: URL, from connections: [AVCaptureConnection], error: Error?) {
        let problem = error?.localizedDescription
        DispatchQueue.main.async {
            self.session?.stopRunning()
            let failed = problem != nil && !FileManager.default.fileExists(atPath: outputFileURL.path)
            let discard = self.discard
            self.close()
            if discard {
                try? FileManager.default.removeItem(at: outputFileURL)
            } else if failed {
                Toast.show("Recording failed: \(problem ?? "unknown error")")
            } else {
                let pb = NSPasteboard.general
                pb.clearContents()
                pb.writeObjects([outputFileURL as NSURL])
                Toast.show("Screen recording saved: \(outputFileURL.lastPathComponent)")
                NSWorkspace.shared.activateFileViewerSelecting([outputFileURL])
            }
        }
    }

    private func close() {
        timer?.invalidate()
        if let m = keyMonitor { NSEvent.removeMonitor(m) }
        keyMonitor = nil
        session = nil
        output = nil
        layer.orderOut(nil)
        bar.orderOut(nil)
        if Recorder.current === self { Recorder.current = nil }
    }
}
