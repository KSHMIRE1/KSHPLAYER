import SwiftUI
import UniformTypeIdentifiers

/// Écran d'accueil, charte KSHFLIX 2.0 « Salle obscure ».
struct LauncherView: View {
    let hw: HardwareProfile
    var status: String
    var onOpenFile: () -> Void
    var onOpenFolder: () -> Void
    var onDrop: (URL) -> Void

    @State private var hovering = false
    @State private var dropTarget = false

    static let bg = Color(red: 0x05 / 255, green: 0x05 / 255, blue: 0x06 / 255)
    static let surface = Color(red: 0x12 / 255, green: 0x12 / 255, blue: 0x17 / 255)
    static let red = Color(red: 0xF0 / 255, green: 0x28 / 255, blue: 0x3A / 255)
    static let redHi = Color(red: 0xFF / 255, green: 0x3A / 255, blue: 0x4B / 255)
    static let pink = Color(red: 0xFF / 255, green: 0x8A / 255, blue: 0x95 / 255)
    static let text2 = Color(red: 0xB9 / 255, green: 0xB9 / 255, blue: 0xC2 / 255)

    var body: some View {
        ZStack {
            Self.bg.ignoresSafeArea()
            VStack(spacing: 26) {
                // Logo : marque néon KSHFLIX + KSH blanc / PLAYER rouge
                HStack(spacing: 16) {
                    if let img = NSImage(named: "logo-mark") {
                        Image(nsImage: img).resizable().frame(width: 64, height: 64)
                    }
                    (Text("KSH").foregroundColor(.white) + Text("PLAYER").foregroundColor(Self.red))
                        .font(.custom("Unbounded", size: 44).weight(.heavy))
                        .kerning(-1)
                    if Edition.isPro {
                        Text("PRO")
                            .font(.custom("Unbounded", size: 16).weight(.heavy))
                            .foregroundColor(.white)
                            .padding(.horizontal, 10).padding(.vertical, 4)
                            .background(RoundedRectangle(cornerRadius: 9).fill(Self.red))
                    }
                }

                // Carte de dépôt
                VStack(spacing: 18) {
                    Text(Edition.isPro ? "LECTEUR VIDÉO  ·  ÉDITION MONTEURS" : "LECTEUR VIDÉO")
                        .font(.system(size: 11, weight: .heavy)).kerning(2).foregroundColor(Self.pink)
                    Text(status)
                        .font(.system(size: 16)).foregroundColor(Self.text2).multilineTextAlignment(.center)
                    Button(action: onOpenFile) {
                        HStack(spacing: 10) {
                            Text("Ouvrir un fichier").font(.system(size: 15, weight: .bold))
                            Image(systemName: "arrow.right").font(.system(size: 13, weight: .bold))
                        }
                        .foregroundColor(.white)
                        .padding(.horizontal, 22).padding(.vertical, 11)
                        .background(RoundedRectangle(cornerRadius: 14).fill(hovering ? Self.redHi : Self.red))
                    }
                    .buttonStyle(.plain)
                    .onHover { hovering = $0 }
                    .keyboardShortcut("o", modifiers: .command)

                    if Edition.isPro {
                        Button(action: onOpenFolder) {
                            Label("Ouvrir une séquence d'images (dossier)", systemImage: "folder")
                                .font(.system(size: 13)).foregroundColor(Self.text2)
                        }
                        .buttonStyle(.plain)
                    }
                }
                .padding(.horizontal, 30).padding(.vertical, 28)
                .frame(width: 480)
                .background(RoundedRectangle(cornerRadius: 22).fill(Self.surface))
                .overlay(RoundedRectangle(cornerRadius: 22)
                    .stroke(dropTarget ? Self.red : Color.white.opacity(0.1), lineWidth: dropTarget ? 2 : 1))
            }

            // Pastille puce graphique / mode
            VStack {
                Spacer()
                HStack(spacing: 8) {
                    Circle().fill(Self.red).frame(width: 7, height: 7)
                    Text("\(hw.gpuName)  ·  \(hw.label)").font(.system(size: 12)).foregroundColor(Self.text2)
                }
                .padding(.horizontal, 12).padding(.vertical, 6)
                .background(RoundedRectangle(cornerRadius: 14).fill(Self.surface))
                .overlay(RoundedRectangle(cornerRadius: 14).stroke(Color.white.opacity(0.1)))
                .padding(.bottom, 22)
            }
        }
        .frame(minWidth: 760, minHeight: 520)
        .onDrop(of: [.fileURL], isTargeted: $dropTarget) { providers in
            guard let p = providers.first else { return false }
            _ = p.loadObject(ofClass: URL.self) { url, _ in
                if let url { DispatchQueue.main.async { onDrop(url) } }
            }
            return true
        }
    }
}
