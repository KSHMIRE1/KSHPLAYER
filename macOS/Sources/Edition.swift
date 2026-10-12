import Foundation

/// Les deux éditions sortent du même code (cible « KSHPLAYER » et cible « KSHPLAYER PRO », drapeau KSHPRO).
enum Edition {
#if KSHPRO
    static let isPro = true
    static let name = "KSHPLAYER PRO"
#else
    static let isPro = false
    static let name = "KSHPLAYER"
#endif

    /// ~/Documents/KSHPLAYER PRO/LUTs
    static var lutFolder: URL { ensure(FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("KSHPLAYER PRO/LUTs", isDirectory: true)) }

    /// ~/Pictures/KSHPLAYER PRO
    static var captureFolder: URL { ensure(FileManager.default.urls(for: .picturesDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("KSHPLAYER PRO", isDirectory: true)) }

    /// ~/Library/Logs/KSHPLAYER
    static var logFolder: URL { ensure(FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("Logs/KSHPLAYER", isDirectory: true)) }

    private static func ensure(_ url: URL) -> URL {
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }
}
