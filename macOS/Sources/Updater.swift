import AppKit

/// Mises à jour Mac : lit version.json publié par GitHub ; si une version plus récente existe,
/// l'accueil propose de télécharger le nouveau .dmg.
enum Updater {
    static let baseURL = "https://raw.githubusercontent.com/KSHMIRE1/KSHPLAYER/telechargements"

    struct Info { let version: String; let notes: String; let dmgURL: URL }

    static var current: String {
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0.0"
    }

    static func check(_ done: @escaping (Info?) -> Void) {
        guard let url = URL(string: "\(baseURL)/version.json?t=\(Int(Date().timeIntervalSince1970))") else { return done(nil) }
        var req = URLRequest(url: url)
        req.timeoutInterval = 8
        req.cachePolicy = .reloadIgnoringLocalCacheData
        URLSession.shared.dataTask(with: req) { data, _, _ in
            guard let data,
                  let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let v = json["version"] as? String,
                  let files = json["files"] as? [String: String],
                  let dmg = files[Edition.isPro ? "mac_pro" : "mac"],
                  let dmgURL = URL(string: "\(baseURL)/\(dmg.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? dmg)"),
                  isNewer(v, than: current)
            else { return DispatchQueue.main.async { done(nil) } }
            let info = Info(version: v, notes: json["notes"] as? String ?? "", dmgURL: dmgURL)
            DispatchQueue.main.async { done(info) }
        }.resume()
    }

    static func isNewer(_ a: String, than b: String) -> Bool {
        let pa = a.split(separator: ".").map { Int($0) ?? 0 }, pb = b.split(separator: ".").map { Int($0) ?? 0 }
        for i in 0..<max(pa.count, pb.count) {
            let x = i < pa.count ? pa[i] : 0, y = i < pb.count ? pb[i] : 0
            if x != y { return x > y }
        }
        return false
    }
}
