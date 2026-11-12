import AppKit
import Carbon.HIToolbox

/// System-wide hotkey through Carbon's RegisterEventHotKey (needs no Accessibility permission).
enum HotKey {
    static var action: (() -> Void)?
    private static var ref: EventHotKeyRef?
    private static var installed = false

    /// Registers Control-Shift-S. Returns false when the combination is taken.
    @discardableResult
    static func registerControlShiftS(_ handler: @escaping () -> Void) -> Bool {
        action = handler
        if !installed {
            installed = true
            var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
            InstallEventHandler(GetApplicationEventTarget(), { _, _, _ -> OSStatus in
                DispatchQueue.main.async { HotKey.action?() }
                return noErr
            }, 1, &spec, nil, nil)
        }
        let id = EventHotKeyID(signature: OSType(0x53584B31), id: 1) // 'SXK1'
        let status = RegisterEventHotKey(UInt32(kVK_ANSI_S), UInt32(controlKey | shiftKey), id, GetApplicationEventTarget(), 0, &ref)
        return status == noErr
    }
}
