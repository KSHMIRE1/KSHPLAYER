import AppKit
import Libmpv

/// Le moteur de KSHPLAYER sur Mac : une instance libmpv (MPVKit) qui dessine dans une couche Metal.
final class MpvController {
    private var mpv: OpaquePointer?
    private let queue = DispatchQueue(label: "kshplayer.mpv", qos: .userInitiated)

    /// Changement de propriété observée (appelé sur le fil principal).
    var onProperty: ((String, Any?) -> Void)?
    /// mpv s'arrête (fin du film, touche Q, bouton fermer).
    var onShutdown: (() -> Void)?

    init(layer: CAMetalLayer, hw: HardwareProfile) throws {
        guard let h = mpv_create() else { throw NSError(domain: "KSHPLAYER", code: 1,
            userInfo: [NSLocalizedDescriptionKey: "Impossible de créer le moteur mpv."]) }
        mpv = h

        let res = Bundle.main.resourceURL!.appendingPathComponent("mpv")
        var layerRef = layer
        mpv_set_option(h, "wid", MPV_FORMAT_INT64, &layerRef)

        // --- Dossier mpv livré avec l'appli : barre de contrôle, icônes, raccourcis ---
        opt("config", "yes")
        opt("config-dir", res.path)
        opt("load-scripts", "no")
        opt("scripts", res.appendingPathComponent("scripts/kshosc.lua").path)
        var scriptOpts = "kshosc-platform=mac,kshosc-font=\(q("Helvetica Neue"))"
        if Edition.isPro {
            scriptOpts += ",kshosc-pro=yes,kshosc-lutdir=\(q(Edition.lutFolder.path)),kshosc-shotdir=\(q(Edition.captureFolder.path))"
            opt("hr-seek", "yes")
            opt("keep-open", "yes")
            opt("mf-fps", "24")
        } else {
            opt("keep-open", "no")
        }
        opt("script-opts", scriptOpts)
        opt("log-file", Edition.logFolder.appendingPathComponent("mpv.log").path)
        opt("idle", "once")
        opt("osc", "no")
        opt("osd-bar", "no")
        opt("input-default-bindings", "yes")
        opt("input-media-keys", "yes")
        opt("osd-font", "Helvetica Neue")

        // --- Rendu : Metal (via MoltenVK) + décodage matériel VideoToolbox, HDR/EDR ---
        opt("vo", "gpu-next")
        opt("gpu-api", "vulkan")
        opt("gpu-context", "moltenvk")
        opt("hwdec", "videotoolbox")
        opt("target-colorspace-hint", "yes")
        opt("tone-mapping", "auto")
        opt("ytdl", "no")
        switch hw.tier {
        case .max:
            opt("profile", "high-quality")
            opt("video-sync", "display-resample")
            opt("deband", "yes")
        case .balanced:
            opt("video-sync", "display-resample")
        case .light:
            opt("profile", "fast")
            opt("video-sync", "audio")
            opt("hdr-compute-peak", "no")
        }

        // --- Audio / sous-titres ---
        opt("volume-max", "150")
        opt("sub-auto", "fuzzy")
        opt("slang", "fr,fre,fra,en,eng")
        opt("alang", "fr,fre,fra,en,eng")

        guard mpv_initialize(h) >= 0 else {
            throw NSError(domain: "KSHPLAYER", code: 2, userInfo: [NSLocalizedDescriptionKey: "Le moteur mpv n'a pas démarré."])
        }

        mpv_observe_property(h, 0, "fullscreen", MPV_FORMAT_FLAG)
        mpv_observe_property(h, 0, "window-minimized", MPV_FORMAT_FLAG)
        mpv_observe_property(h, 0, "dwidth", MPV_FORMAT_INT64)
        mpv_observe_property(h, 0, "dheight", MPV_FORMAT_INT64)
        mpv_observe_property(h, 0, "media-title", MPV_FORMAT_STRING)

        mpv_set_wakeup_callback(h, { ctx in
            guard let ctx else { return }
            Unmanaged<MpvController>.fromOpaque(ctx).takeUnretainedValue().readEvents()
        }, Unmanaged.passUnretained(self).toOpaque())
    }

    // MARK: - Lecture

    func load(_ url: URL) {
        setString("force-media-title", url.deletingPathExtension().lastPathComponent)
        command("loadfile", url.path, "replace")
    }

    /// PRO : séquence d'images (EXR, DPX, TIFF, PNG…)
    func loadSequence(folder: URL, ext: String) {
        setString("force-media-title", folder.lastPathComponent + "  ·  séquence " + ext.uppercased())
        command("loadfile", "mf://" + folder.appendingPathComponent("*." + ext).path, "replace")
    }

    func quit() { command("quit") }

    // MARK: - Accès bas niveau

    func command(_ args: String...) {
        guard let mpv else { return }
        var cargs: [UnsafePointer<CChar>?] = args.map { UnsafePointer(strdup($0)) }
        cargs.append(nil)
        defer { for p in cargs where p != nil { free(UnsafeMutablePointer(mutating: p)) } }
        mpv_command(mpv, &cargs)
    }

    func setString(_ name: String, _ value: String) {
        guard let mpv else { return }
        mpv_set_property_string(mpv, name, value)
    }

    func setFlag(_ name: String, _ value: Bool) {
        guard let mpv else { return }
        var v: Int32 = value ? 1 : 0
        mpv_set_property(mpv, name, MPV_FORMAT_FLAG, &v)
    }

    func getString(_ name: String) -> String? {
        guard let mpv, let c = mpv_get_property_string(mpv, name) else { return nil }
        defer { mpv_free(c) }
        return String(cString: c)
    }

    private func opt(_ name: String, _ value: String) {
        guard let mpv else { return }
        mpv_set_option_string(mpv, name, value)
    }

    /// Valeur protégée pour les listes d'options mpv (%longueur%texte)
    private func q(_ v: String) -> String { "%\(v.utf8.count)%\(v)" }

    // MARK: - Événements

    private func readEvents() {
        queue.async { [weak self] in
            guard let self else { return }
            while let mpv = self.mpv {
                guard let ev = mpv_wait_event(mpv, 0) else { break }
                let id = ev.pointee.event_id
                if id == MPV_EVENT_NONE { break }

                switch id {
                case MPV_EVENT_PROPERTY_CHANGE:
                    guard let raw = ev.pointee.data else { break }
                    let prop = raw.assumingMemoryBound(to: mpv_event_property.self).pointee
                    let name = String(cString: prop.name)
                    var value: Any? = nil
                    if let data = prop.data {
                        switch prop.format {
                        case MPV_FORMAT_FLAG: value = data.assumingMemoryBound(to: Int32.self).pointee != 0
                        case MPV_FORMAT_INT64: value = data.assumingMemoryBound(to: Int64.self).pointee
                        case MPV_FORMAT_STRING:
                            if let s = data.assumingMemoryBound(to: UnsafePointer<CChar>?.self).pointee { value = String(cString: s) }
                        default: break
                        }
                    }
                    DispatchQueue.main.async { self.onProperty?(name, value) }

                case MPV_EVENT_SHUTDOWN:
                    mpv_set_wakeup_callback(mpv, nil, nil)
                    mpv_terminate_destroy(mpv)
                    self.mpv = nil
                    DispatchQueue.main.async { self.onShutdown?() }
                    return

                default:
                    break
                }
            }
        }
    }
}
