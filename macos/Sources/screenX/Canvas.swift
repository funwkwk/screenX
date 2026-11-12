import AppKit

enum Tool: Int {
    case pen, highlighter, eraser, rectangle, ellipse, line, arrow, crop
}

/// One mark-up on the snip: a freehand stroke or a two-point shape. Coordinates are image points, top-left origin.
struct Mark {
    enum Kind { case pen, highlighter, rectangle, ellipse, line, arrow }
    var kind: Kind
    var points: [NSPoint]
    var color: NSColor
    var width: CGFloat

    var bounds: NSRect {
        guard let first = points.first else { return .zero }
        var minX = first.x, maxX = first.x, minY = first.y, maxY = first.y
        for p in points {
            minX = min(minX, p.x); maxX = max(maxX, p.x)
            minY = min(minY, p.y); maxY = max(maxY, p.y)
        }
        return NSRect(x: minX, y: minY, width: maxX - minX, height: maxY - minY)
    }

    func draw() {
        guard let a = points.first, let b = points.last else { return }
        let path = NSBezierPath()
        path.lineWidth = width
        path.lineJoinStyle = .round
        path.lineCapStyle = .round
        switch kind {
        case .pen, .highlighter:
            path.move(to: a)
            for p in points.dropFirst() { path.line(to: p) }
            if points.count == 1 { path.line(to: NSPoint(x: a.x + 0.1, y: a.y + 0.1)) }
            if kind == .highlighter {
                path.lineCapStyle = .square
                color.withAlphaComponent(0.4).setStroke()
            } else {
                color.setStroke()
            }
            path.stroke()
        case .rectangle:
            path.appendRect(bounds)
            color.setStroke()
            path.stroke()
        case .ellipse:
            path.appendOval(in: bounds)
            color.setStroke()
            path.stroke()
        case .line:
            path.move(to: a)
            path.line(to: b)
            color.setStroke()
            path.stroke()
        case .arrow:
            let ang = atan2(b.y - a.y, b.x - a.x)
            let len = hypot(b.x - a.x, b.y - a.y)
            let head = min(len, max(12, width * 3.5))
            let base = NSPoint(x: b.x - head * 0.8 * cos(ang), y: b.y - head * 0.8 * sin(ang))
            path.move(to: a)
            path.line(to: base)
            color.setStroke()
            path.stroke()
            let tip = NSBezierPath()
            tip.move(to: b)
            tip.line(to: NSPoint(x: b.x - head * cos(ang - 0.42), y: b.y - head * sin(ang - 0.42)))
            tip.line(to: NSPoint(x: b.x - head * cos(ang + 0.42), y: b.y - head * sin(ang + 0.42)))
            tip.close()
            color.setFill()
            tip.fill()
        }
    }

    /// Rough hit test for the eraser.
    func hit(_ p: NSPoint) -> Bool {
        let slack = max(8, width)
        switch kind {
        case .pen, .highlighter:
            return points.contains { hypot($0.x - p.x, $0.y - p.y) <= slack + 2 }
        default:
            return bounds.insetBy(dx: -slack, dy: -slack).contains(p)
        }
    }
}

/// The snip plus its mark-ups. Draws at image-point size; the scroll view around it handles zooming.
final class CanvasView: NSView {
    struct State {
        var image: NSImage
        var marks: [Mark]
    }

    private(set) var image: NSImage?
    private(set) var marks: [Mark] = []
    private var undoStack: [State] = []
    private var redoStack: [State] = []
    private var current: Mark?
    private var cropStart: NSPoint?
    private var cropRect: NSRect?

    var tool: Tool = .pen
    var penColor: NSColor = NSColor(hex: "#E81123") ?? .red
    var highlighterColor: NSColor = NSColor(hex: "#FCE100") ?? .yellow
    var strokeWidth: CGFloat = 4
    /// Called after every change (edit, undo, redo, crop).
    var onChange: (() -> Void)?

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }

    var canUndo: Bool { !undoStack.isEmpty }
    var canRedo: Bool { !redoStack.isEmpty }
    var hasEdits: Bool { !undoStack.isEmpty }

    func load(_ image: NSImage?) {
        self.image = image
        marks = []
        undoStack = []
        redoStack = []
        current = nil
        cropRect = nil
        setFrameSize(image?.size ?? NSSize(width: 480, height: 280))
        needsDisplay = true
    }

    // ---- drawing

    override func draw(_ dirtyRect: NSRect) {
        guard let image = image else { return }
        drawContent(image: image)
        if let c = cropRect {
            NSColor(white: 0, alpha: 0.55).setFill()
            let outside = NSBezierPath(rect: bounds)
            outside.appendRect(c)
            outside.windingRule = .evenOdd
            outside.fill()
            NSColor.white.setStroke()
            let frame = NSBezierPath(rect: c)
            frame.lineWidth = 1.5
            frame.stroke()
        }
    }

    private func drawContent(image: NSImage) {
        ImageTools.draw(image, in: NSRect(origin: .zero, size: image.size))
        for m in marks { m.draw() }
        current?.draw()
    }

    /// The snip with the mark-ups burnt in, at the original pixel size.
    func flattened() -> NSImage? {
        guard let image = image else { return nil }
        if marks.isEmpty { return image }
        return ImageTools.render(like: image) { _ in
            ImageTools.draw(image, in: NSRect(origin: .zero, size: image.size))
            for m in self.marks { m.draw() }
        }
    }

    // ---- history

    private func pushUndo() {
        guard let image = image else { return }
        undoStack.append(State(image: image, marks: marks))
        redoStack = []
    }

    private func restore(_ s: State) {
        image = s.image
        marks = s.marks
        setFrameSize(s.image.size)
        needsDisplay = true
        onChange?()
    }

    func undo() {
        guard let image = image, let s = undoStack.popLast() else { return }
        redoStack.append(State(image: image, marks: marks))
        restore(s)
    }

    func redo() {
        guard let image = image, let s = redoStack.popLast() else { return }
        undoStack.append(State(image: image, marks: marks))
        restore(s)
    }

    func eraseAll() {
        guard !marks.isEmpty else { return }
        pushUndo()
        marks = []
        needsDisplay = true
        onChange?()
    }

    // ---- mouse

    private func point(_ event: NSEvent) -> NSPoint {
        let p = convert(event.locationInWindow, from: nil)
        return NSPoint(x: min(max(p.x, 0), bounds.width), y: min(max(p.y, 0), bounds.height))
    }

    override func mouseDown(with event: NSEvent) {
        guard image != nil else { return }
        window?.makeFirstResponder(self)
        let p = point(event)
        switch tool {
        case .eraser:
            eraseAt(p)
        case .crop:
            cropStart = p
            cropRect = NSRect(origin: p, size: .zero)
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
        }
        needsDisplay = true
    }

    override func mouseDragged(with event: NSEvent) {
        var p = point(event)
        switch tool {
        case .eraser:
            eraseAt(p)
        case .crop:
            if let s = cropStart {
                cropRect = NSRect(x: min(s.x, p.x), y: min(s.y, p.y), width: abs(p.x - s.x), height: abs(p.y - s.y))
            }
        default:
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
        }
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        if tool == .crop {
            let r = cropRect
            cropStart = nil
            cropRect = nil
            if let r = r, r.width >= 8, r.height >= 8 { applyCrop(r) }
            needsDisplay = true
            return
        }
        if let m = current {
            current = nil
            let shape = !(m.kind == .pen || m.kind == .highlighter)
            if !shape || (m.bounds.width >= 3 || m.bounds.height >= 3) {
                pushUndo()
                marks.append(m)
                onChange?()
            }
        }
        needsDisplay = true
    }

    private func eraseAt(_ p: NSPoint) {
        guard let i = marks.lastIndex(where: { $0.hit(p) }) else { return }
        pushUndo()
        marks.remove(at: i)
        onChange?()
    }

    private func applyCrop(_ r: NSRect) {
        guard let image = image, let flat = flattened() else { return }
        let px = ImageTools.pixelSize(image)
        let kx = CGFloat(px.w) / image.size.width, ky = CGFloat(px.h) / image.size.height
        let pixelRect = CGRect(x: (r.minX * kx).rounded(), y: (r.minY * ky).rounded(), width: (r.width * kx).rounded(), height: (r.height * ky).rounded())
        guard let cg = flat.cgImage(forProposedRect: nil, context: nil, hints: nil)?.cropping(to: pixelRect) else { return }
        let rep = NSBitmapImageRep(cgImage: cg)
        rep.size = NSSize(width: CGFloat(cg.width) / kx, height: CGFloat(cg.height) / ky)
        let cropped = NSImage(size: rep.size)
        cropped.addRepresentation(rep)
        pushUndo()
        self.image = cropped
        marks = []
        setFrameSize(cropped.size)
        onChange?()
    }
}
