import AppKit
import SwiftUI
import CoreText
import UniformTypeIdentifiers

final class AppDelegate: NSObject, NSApplicationDelegate {
    private let hw = HardwareProfile.detect()
    private var launcher: NSWindow!
    private var player: PlayerWindowController?
    private var pendingURLs: [URL] = []
    private var ready = false
    private var update: Updater.Info?
    private var status = Edition.isPro ? "Glisse un rush, un film ou un dossier de séquence d'images"
                                       : "Glisse un film ou un épisode ici"

    func applicationDidFinishLaunching(_ notification: Notification) {
        // police KSHFLIX (Unbounded) embarquée
        if let font = Bundle.main.url(forResource: "Unbounded", withExtension: "ttf") {
            CTFontManagerRegisterFontsForURL(font as CFURL, .process, nil)
        }
        buildMenu()
        launcher = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 960, height: 600),
                            styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
                            backing: .buffered, defer: false)
        launcher.titlebarAppearsTransparent = true
        launcher.titleVisibility = .hidden
        launcher.isMovableByWindowBackground = true
        launcher.backgroundColor = NSColor(red: 0x05 / 255, green: 0x05 / 255, blue: 0x06 / 255, alpha: 1)
        launcher.title = Edition.name
        launcher.isReleasedWhenClosed = false
        refreshLauncher()
        launcher.center()

        Updater.check { [weak self] info in
            guard let self, let info else { return }
            self.update = info
            self.refreshLauncher()
        }

        ready = true
        if let url = pendingURLs.first { pendingURLs.removeAll(); play(url) }
        else { launcher.makeKeyAndOrderFront(nil) }
        NSApp.activate(ignoringOtherApps: true)
    }

    /// Fichiers ouverts depuis le Finder (double-clic, « Ouvrir avec », glisser sur l'icône du Dock).
    func application(_ application: NSApplication, open urls: [URL]) {
        guard let url = urls.first else { return }
        if ready { play(url) } else { pendingURLs = urls }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if !flag { launcher.makeKeyAndOrderFront(nil) }
        return true
    }

    // MARK: - Lecture

    private func refreshLauncher() {
        launcher.contentView = NSHostingView(rootView: LauncherView(
            hw: hw, status: status,
            onOpenFile: { [weak self] in self?.openFile() },
            onOpenFolder: { [weak self] in self?.openFolder() },
            onDrop: { [weak self] url in self?.play(url) },
            update: update))
    }

    private static let sequenceExts = ["exr", "dpx", "tif", "tiff", "png", "jpg", "jpeg", "tga"]

    func play(_ url: URL) {
        do {
            var isDir: ObjCBool = false
            FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir)

            let p = try player ?? makePlayer()
            if isDir.boolValue {
                guard Edition.isPro else { return }
                let files = (try? FileManager.default.contentsOfDirectory(atPath: url.path)) ?? []
                let best = Self.sequenceExts
                    .map { ext in (ext, files.filter { $0.lowercased().hasSuffix("." + ext) }.count) }
                    .max { $0.1 < $1.1 }
                guard let best, best.1 >= 2 else { setStatus("Aucune séquence d'images dans ce dossier"); return }
                p.playSequence(folder: url, ext: best.0)
            } else {
                p.play(url)
            }
            NSDocumentController.shared.noteNewRecentDocumentURL(url)
            launcher.orderOut(nil)
        } catch {
            setStatus("Lecture impossible : \(error.localizedDescription)")
            launcher.makeKeyAndOrderFront(nil)
        }
    }

    private func makePlayer() throws -> PlayerWindowController {
        let p = try PlayerWindowController(hw: hw)
        p.onClosed = { [weak self] in
            self?.player = nil
            self?.setStatus(Edition.isPro ? "Glisse un rush, un film ou un dossier de séquence d'images"
                                          : "Glisse un film ou un épisode ici")
            self?.launcher.makeKeyAndOrderFront(nil)
        }
        player = p
        return p
    }

    private func setStatus(_ s: String) { status = s; refreshLauncher() }

    @objc func openFile() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        panel.allowedContentTypes = [.movie, .video, .audiovisualContent, .image, .data]
        if panel.runModal() == .OK, let url = panel.url { play(url) }
    }

    @objc func openFolder() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.prompt = "Lire la séquence"
        if panel.runModal() == .OK, let url = panel.url { play(url) }
    }

    // MARK: - Menu

    private func buildMenu() {
        let main = NSMenu()

        let appItem = NSMenuItem(); main.addItem(appItem)
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "À propos de \(Edition.name)", action: #selector(NSApplication.orderFrontStandardAboutPanel(_:)), keyEquivalent: "")
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "Masquer \(Edition.name)", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "Quitter \(Edition.name)", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu

        let fileItem = NSMenuItem(); main.addItem(fileItem)
        let fileMenu = NSMenu(title: "Fichier")
        let open = fileMenu.addItem(withTitle: "Ouvrir…", action: #selector(openFile), keyEquivalent: "o")
        open.target = self
        if Edition.isPro {
            let seq = fileMenu.addItem(withTitle: "Ouvrir une séquence d'images…", action: #selector(openFolder), keyEquivalent: "O")
            seq.target = self
        }
        fileMenu.addItem(.separator())
        fileMenu.addItem(withTitle: "Fermer", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        fileItem.submenu = fileMenu

        let winItem = NSMenuItem(); main.addItem(winItem)
        let winMenu = NSMenu(title: "Fenêtre")
        winMenu.addItem(withTitle: "Plein écran", action: #selector(NSWindow.toggleFullScreen(_:)), keyEquivalent: "f")
            .keyEquivalentModifierMask = [.command, .control]
        winMenu.addItem(withTitle: "Réduire", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m")
        winItem.submenu = winMenu
        NSApp.windowsMenu = winMenu

        NSApp.mainMenu = main
    }
}
