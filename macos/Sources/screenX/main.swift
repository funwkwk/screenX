import AppKit

// screenX for macOS: a menu-bar app (no Dock icon) for snips, markup, screen recording,
// colour picking and text extraction.
let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
