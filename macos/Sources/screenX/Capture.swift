import AppKit
import Vision

enum SnipMode: String, CaseIterable {
    case rectangle, window, fullscreen

    var title: String {
        switch self {
        case .rectangle: return "Rectangle"
        case .window: return "Window"
        case .fullscreen: return "Full screen"
        }
    }
}

/// Screen grabs through the system `screencapture` tool: it brings the native crosshair / window picker
/// and macOS asks for the Screen Recording permission on behalf of screenX the first time.
enum Capture {
    static var busy = false

    /// Takes a snip. `completion` gets nil when the user pressed Esc.
    static func snip(_ mode: SnipMode, delay: Int = 0, completion: @escaping (NSImage?) -> Void) {
        if busy { return }
        busy = true
        DispatchQueue.main.asyncAfter(deadline: .now() + .milliseconds(delay * 1000 + 250)) {
            let tmp = FileManager.default.temporaryDirectory.appendingPathComponent("screenX-\(UUID().uuidString).png")
            var args = ["-x", "-t", "png"]
            switch mode {
            case .rectangle: args += ["-i"]
            case .window: args += ["-i", "-W"]
            case .fullscreen: args += ["-m"]
            }
            args.append(tmp.path)
            let p = Process()
            p.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
            p.arguments = args
            p.terminationHandler = { _ in
                DispatchQueue.main.async {
                    busy = false
                    var image: NSImage?
                    if let data = try? Data(contentsOf: tmp), let rep = NSBitmapImageRep(data: data) {
                        let img = NSImage(size: rep.size)
                        img.addRepresentation(rep)
                        image = img
                    }
                    try? FileManager.default.removeItem(at: tmp)
                    completion(image)
                }
            }
            do { try p.run() } catch {
                busy = false
                completion(nil)
            }
        }
    }
}

enum ImageTools {
    static func pixelSize(_ image: NSImage) -> (w: Int, h: Int) {
        if let rep = image.representations.first, rep.pixelsWide > 0, rep.pixelsHigh > 0 {
            return (rep.pixelsWide, rep.pixelsHigh)
        }
        return (max(1, Int(image.size.width)), max(1, Int(image.size.height)))
    }

    /// Draws into a new bitmap that has the pixel size of `like`, using a flipped (top-left origin) point coordinate space.
    static func render(like image: NSImage, _ draw: (NSRect) -> Void) -> NSImage? {
        let px = pixelSize(image)
        guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px.w, pixelsHigh: px.h, bitsPerSample: 8,
                                         samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                         colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0),
              let base = NSGraphicsContext(bitmapImageRep: rep) else { return nil }
        rep.size = image.size
        let cg = base.cgContext
        cg.translateBy(x: 0, y: image.size.height)
        cg.scaleBy(x: 1, y: -1)
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: cg, flipped: true)
        draw(NSRect(origin: .zero, size: image.size))
        NSGraphicsContext.restoreGraphicsState()
        let out = NSImage(size: image.size)
        out.addRepresentation(rep)
        return out
    }

    static func draw(_ image: NSImage, in rect: NSRect) {
        image.draw(in: rect, from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true, hints: nil)
    }

    static func withBorder(_ image: NSImage) -> NSImage {
        let t = CGFloat(Prefs.borderSize)
        let color = NSColor(hex: Prefs.borderColor) ?? .systemRed
        return render(like: image) { r in
            draw(image, in: r)
            color.setStroke()
            let path = NSBezierPath(rect: r.insetBy(dx: t / 2, dy: t / 2))
            path.lineWidth = t
            path.stroke()
        } ?? image
    }

    static func pngData(_ image: NSImage) -> Data? {
        if let rep = image.representations.first as? NSBitmapImageRep {
            return rep.representation(using: .png, properties: [:])
        }
        guard let tiff = image.tiffRepresentation, let rep = NSBitmapImageRep(data: tiff) else { return nil }
        return rep.representation(using: .png, properties: [:])
    }

    static func save(_ image: NSImage, to url: URL) throws {
        let type: NSBitmapImageRep.FileType = ["jpg", "jpeg"].contains(url.pathExtension.lowercased()) ? .jpeg : .png
        guard let tiff = image.tiffRepresentation, let rep = NSBitmapImageRep(data: tiff),
              let data = rep.representation(using: type, properties: [:]) else {
            throw NSError(domain: "screenX", code: 1, userInfo: [NSLocalizedDescriptionKey: "Could not encode the image."])
        }
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try data.write(to: url)
    }

    static func copy(_ image: NSImage) {
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.writeObjects([image])
    }

    static func copy(text: String) {
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(text, forType: .string)
    }

    /// Text recognition with the Vision framework (on-device).
    static func recognizeText(in image: NSImage, completion: @escaping (String) -> Void) {
        guard let cg = image.cgImage(forProposedRect: nil, context: nil, hints: nil) else { completion(""); return }
        DispatchQueue.global(qos: .userInitiated).async {
            let request = VNRecognizeTextRequest()
            request.recognitionLevel = .accurate
            request.usesLanguageCorrection = true
            let handler = VNImageRequestHandler(cgImage: cg, options: [:])
            try? handler.perform([request])
            let lines = (request.results ?? []).compactMap { $0.topCandidates(1).first?.string }
            DispatchQueue.main.async { completion(lines.joined(separator: "\n")) }
        }
    }
}

extension NSColor {
    convenience init?(hex: String) {
        var s = hex.trimmingCharacters(in: .whitespaces)
        if s.hasPrefix("#") { s.removeFirst() }
        guard s.count == 6, let v = UInt32(s, radix: 16) else { return nil }
        self.init(srgbRed: CGFloat((v >> 16) & 0xFF) / 255, green: CGFloat((v >> 8) & 0xFF) / 255, blue: CGFloat(v & 0xFF) / 255, alpha: 1)
    }

    var hexString: String {
        let c = usingColorSpace(.sRGB) ?? self
        return String(format: "#%02X%02X%02X", Int(round(c.redComponent * 255)), Int(round(c.greenComponent * 255)), Int(round(c.blueComponent * 255)))
    }
}

/// Small message that fades away by itself, near the bottom of the main screen.
enum Toast {
    static var panel: NSPanel?
    static var timer: Timer?

    static func show(_ text: String) {
        panel?.orderOut(nil)
        timer?.invalidate()
        let label = NSTextField(wrappingLabelWithString: text)
        label.textColor = .white
        label.font = .systemFont(ofSize: 13)
        label.alignment = .center
        label.preferredMaxLayoutWidth = 420
        let size = label.fittingSize
        let w = min(max(size.width, 160), 440) + 36, h = size.height + 24
        let p = NSPanel(contentRect: NSRect(x: 0, y: 0, width: w, height: h), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        p.isOpaque = false
        p.backgroundColor = .clear
        p.level = .statusBar
        p.hasShadow = true
        p.sharingType = .none
        let box = NSView(frame: NSRect(x: 0, y: 0, width: w, height: h))
        box.wantsLayer = true
        box.layer?.backgroundColor = NSColor(white: 0.13, alpha: 0.96).cgColor
        box.layer?.cornerRadius = 10
        label.frame = NSRect(x: 18, y: 12, width: w - 36, height: size.height)
        box.addSubview(label)
        p.contentView = box
        if let screen = NSScreen.main {
            let f = screen.visibleFrame
            p.setFrameOrigin(NSPoint(x: f.midX - w / 2, y: f.minY + 80))
        }
        p.orderFrontRegardless()
        panel = p
        timer = Timer.scheduledTimer(withTimeInterval: 3.2, repeats: false) { _ in
            DispatchQueue.main.async {
                Toast.panel?.orderOut(nil)
                Toast.panel = nil
            }
        }
    }
}
