import AppKit

/// Couche Metal dans laquelle mpv dessine (via MoltenVK).
/// Contournements repris de MPVKit : scintillement MoltenVK et activation de l'EDR (HDR) sur le fil principal.
final class MetalLayer: CAMetalLayer {
    override var drawableSize: CGSize {
        get { super.drawableSize }
        set {
            if Int(newValue.width) > 1 && Int(newValue.height) > 1 { super.drawableSize = newValue }
        }
    }

    override var wantsExtendedDynamicRangeContent: Bool {
        get { super.wantsExtendedDynamicRangeContent }
        set {
            if Thread.isMainThread { super.wantsExtendedDynamicRangeContent = newValue }
            else { DispatchQueue.main.sync { super.wantsExtendedDynamicRangeContent = newValue } }
        }
    }
}
