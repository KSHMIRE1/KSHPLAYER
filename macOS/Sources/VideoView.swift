import AppKit

/// Vue vidéo : porte la couche Metal de mpv et lui transmet souris et clavier
/// (en mode intégré, mpv ne reçoit rien tout seul sur Mac).
final class VideoView: NSView {
    let metalLayer = MetalLayer()
    weak var mpv: MpvController?
    /// La souris approche du haut de la fenêtre (affiche les boutons rouge/orange/vert).
    var onHoverTop: ((Bool) -> Void)?

    private var tracking: NSTrackingArea?
    private var leftDownSent = false
    private var scrollAccum: CGFloat = 0

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        metalLayer.backgroundColor = NSColor.black.cgColor
        metalLayer.framebufferOnly = true
        metalLayer.pixelFormat = .bgra8Unorm
    }
    required init?(coder: NSCoder) { fatalError() }

    override func makeBackingLayer() -> CALayer { metalLayer }
    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func layout() {
        super.layout()
        let scale = window?.backingScaleFactor ?? 2
        metalLayer.contentsScale = scale
        metalLayer.frame = bounds
        metalLayer.drawableSize = CGSize(width: bounds.width * scale, height: bounds.height * scale)
    }

    override func viewDidChangeBackingProperties() {
        super.viewDidChangeBackingProperties()
        needsLayout = true
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let tracking { removeTrackingArea(tracking) }
        let t = NSTrackingArea(rect: .zero, options: [.mouseMoved, .mouseEnteredAndExited, .activeAlways, .inVisibleRect],
                               owner: self, userInfo: nil)
        addTrackingArea(t)
        tracking = t
    }

    // MARK: - Souris

    private func sendPosition(_ event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)
        let scale = window?.backingScaleFactor ?? 2
        let x = Int(p.x * scale), y = Int((bounds.height - p.y) * scale)
        mpv?.command("mouse", String(x), String(y))
        onHoverTop?(bounds.height - p.y < 48)
    }

    /// La barre KSHPLAYER est-elle sous la souris ? (publié par kshosc.lua)
    private var overControls: Bool { mpv?.getString("user-data/kshosc/over") == "yes" }

    override func mouseEntered(with event: NSEvent) {
        mpv?.setString("user-data/kshosc/leave", "no")
        sendPosition(event)
    }

    override func mouseExited(with event: NSEvent) {
        mpv?.setString("user-data/kshosc/leave", "yes")
        onHoverTop?(false)
    }

    override func mouseMoved(with event: NSEvent) { sendPosition(event) }
    override func mouseDragged(with event: NSEvent) { sendPosition(event) }

    override func mouseDown(with event: NSEvent) {
        sendPosition(event)
        if overControls {
            mpv?.command("keydown", "MBTN_LEFT")
            leftDownSent = true
        } else if event.clickCount == 2 {
            window?.toggleFullScreen(nil)          // double-clic sur l'image = plein écran
        } else {
            window?.performDrag(with: event)       // glisser l'image = déplacer la fenêtre
        }
    }

    override func mouseUp(with event: NSEvent) {
        if leftDownSent {
            mpv?.command("keyup", "MBTN_LEFT")
            leftDownSent = false
        }
    }

    override func rightMouseDown(with event: NSEvent) { mpv?.command("keypress", "MBTN_RIGHT") }

    override func scrollWheel(with event: NSEvent) {
        sendPosition(event)
        let dy = event.hasPreciseScrollingDeltas ? event.scrollingDeltaY / 12 : event.scrollingDeltaY
        scrollAccum += dy
        while scrollAccum >= 1 { mpv?.command("keypress", "WHEEL_UP"); scrollAccum -= 1 }
        while scrollAccum <= -1 { mpv?.command("keypress", "WHEEL_DOWN"); scrollAccum += 1 }
    }

    // MARK: - Clavier

    override func keyDown(with event: NSEvent) {
        if let name = KeyMap.mpvName(for: event) { mpv?.command("keypress", name) }
    }
}

/// Traduit une touche macOS en nom de touche mpv (« SPACE », « Shift+RIGHT », « Ctrl+i »…).
enum KeyMap {
    private static let special: [UInt16: String] = [
        123: "LEFT", 124: "RIGHT", 125: "DOWN", 126: "UP",
        49: "SPACE", 36: "ENTER", 76: "KP_ENTER", 53: "ESC", 48: "TAB", 51: "BS", 117: "DEL",
        115: "HOME", 119: "END", 116: "PGUP", 121: "PGDWN",
        122: "F1", 120: "F2", 99: "F3", 118: "F4", 96: "F5", 97: "F6", 98: "F7", 100: "F8",
    ]

    static func mpvName(for event: NSEvent) -> String? {
        let f = event.modifierFlags
        var mods = ""
        if f.contains(.control) { mods += "Ctrl+" }
        if f.contains(.option) { mods += "Alt+" }
        if f.contains(.command) { mods += "Meta+" }

        if let s = special[event.keyCode] {
            return mods + (f.contains(.shift) ? "Shift+" : "") + s
        }
        // Avec Ctrl / Alt / Cmd : lettre de base ; sinon le caractère tapé (déjà en majuscule avec Maj, « # », etc.)
        let chars = mods.isEmpty ? event.characters : event.charactersIgnoringModifiers?.lowercased()
        guard let c = chars, let first = c.first, !first.isNewline else { return nil }
        return mods + String(first)
    }
}
