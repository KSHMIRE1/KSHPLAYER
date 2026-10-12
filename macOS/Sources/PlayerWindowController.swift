import AppKit

/// Fenêtre de lecture : uniquement la vidéo, sans bandes noires, boutons natifs du Mac visibles au survol du haut.
final class PlayerWindowController: NSWindowController, NSWindowDelegate {
    let videoView: VideoView
    private(set) var mpv: MpvController!
    var onClosed: (() -> Void)?

    private var videoSize: CGSize = .zero
    private var sized = false

    init(hw: HardwareProfile) throws {
        let view = VideoView(frame: NSRect(x: 0, y: 0, width: 1280, height: 720))
        videoView = view
        let win = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1280, height: 720),
                           styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
                           backing: .buffered, defer: false)
        win.titlebarAppearsTransparent = true
        win.titleVisibility = .hidden
        win.backgroundColor = .black
        win.isMovableByWindowBackground = false
        win.collectionBehavior = [.fullScreenPrimary, .managed]
        win.isReleasedWhenClosed = false
        win.contentView = view
        win.title = Edition.name
        super.init(window: win)
        win.delegate = self
        setTrafficLights(visible: false)

        mpv = try MpvController(layer: videoView.metalLayer, hw: hw)
        videoView.mpv = mpv
        videoView.onHoverTop = { [weak self] top in self?.setTrafficLights(visible: top) }
        mpv.onProperty = { [weak self] name, value in self?.handle(name, value) }
        mpv.onShutdown = { [weak self] in
            guard let self else { return }
            if self.window?.styleMask.contains(.fullScreen) == true { self.window?.toggleFullScreen(nil) }
            self.window?.orderOut(nil)
            self.onClosed?()
        }
    }
    required init?(coder: NSCoder) { fatalError() }

    func play(_ url: URL) {
        mpv.load(url)
        showWindow(nil)
        window?.center()
        window?.makeFirstResponder(videoView)
    }

    func playSequence(folder: URL, ext: String) {
        mpv.loadSequence(folder: folder, ext: ext)
        showWindow(nil)
        window?.center()
        window?.makeFirstResponder(videoView)
    }

    // MARK: - Propriétés mpv -> fenêtre

    private func handle(_ name: String, _ value: Any?) {
        guard let win = window else { return }
        switch name {
        case "fullscreen":
            let want = (value as? Bool) ?? false
            if want != win.styleMask.contains(.fullScreen) { win.toggleFullScreen(nil) }
        case "window-minimized":
            if (value as? Bool) == true { win.miniaturize(nil) }
        case "dwidth":
            if let w = value as? Int64, w > 0 { videoSize.width = CGFloat(w); fitToVideo() }
        case "dheight":
            if let h = value as? Int64, h > 0 { videoSize.height = CGFloat(h); fitToVideo() }
        case "media-title":
            if let t = value as? String { win.title = t }
        default: break
        }
    }

    /// Taille de la fenêtre = format exact du film (pas de bandes), 60 % de l'écran au maximum.
    private func fitToVideo() {
        guard let win = window, videoSize.width > 0, videoSize.height > 0 else { return }
        win.contentAspectRatio = videoSize
        guard !sized, let screen = win.screen ?? NSScreen.main else { return }
        sized = true
        let vf = screen.visibleFrame
        let scale = min(vf.width * 0.6 / videoSize.width, vf.height * 0.6 / videoSize.height, 1.0)
        win.setContentSize(NSSize(width: (videoSize.width * scale).rounded(), height: (videoSize.height * scale).rounded()))
        win.center()
    }

    private func setTrafficLights(visible: Bool) {
        for b in [NSWindow.ButtonType.closeButton, .miniaturizeButton, .zoomButton] {
            window?.standardWindowButton(b)?.animator().alphaValue = visible ? 1 : 0
        }
    }

    // MARK: - Fenêtre -> mpv

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        mpv.quit()          // la fenêtre se ferme quand mpv a terminé
        return false
    }

    func windowDidEnterFullScreen(_ notification: Notification) { mpv.setFlag("fullscreen", true) }
    func windowDidExitFullScreen(_ notification: Notification) { mpv.setFlag("fullscreen", false) }
    func windowDidDeminiaturize(_ notification: Notification) { mpv.setFlag("window-minimized", false) }
}
