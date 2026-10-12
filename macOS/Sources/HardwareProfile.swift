import Metal

enum QualityTier { case light, balanced, max }

/// Détecte la puce graphique du Mac pour adapter le rendu (même logique que sur Windows).
struct HardwareProfile {
    let gpuName: String
    let tier: QualityTier

    var label: String {
        switch tier {
        case .max: return "Qualité max"
        case .balanced: return "Équilibré"
        case .light: return "Mode léger"
        }
    }

    static func detect() -> HardwareProfile {
        guard let dev = MTLCreateSystemDefaultDevice() else {
            return HardwareProfile(gpuName: "Puce graphique inconnue", tier: .balanced)
        }
        let gb = Double(dev.recommendedMaxWorkingSetSize) / 1_073_741_824
        let tier: QualityTier
        if dev.hasUnifiedMemory && dev.name.contains("Apple") {
            // Apple Silicon : M1/M2/M3/M4 de base -> Équilibré, versions Pro/Max/Ultra -> Qualité max
            tier = gb >= 20 ? .max : .balanced
        } else if dev.isLowPower {
            tier = .light          // Intel avec puce intégrée
        } else {
            tier = gb >= 6 ? .max : .balanced   // Intel avec carte AMD dédiée
        }
        return HardwareProfile(gpuName: dev.name, tier: tier)
    }
}
