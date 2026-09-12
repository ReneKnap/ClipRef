import AppKit

/// Result of attempting to save the clipboard to a file.
enum SaveResult {
    case success(URL)
    case noContent
    case failure(String)
}

/// Reads the system clipboard, writes it to a file inside a user-configurable
/// folder, and then puts a prefixed absolute path back on the clipboard so it
/// can be pasted straight into Claude Code as a file reference.
///
/// A file on the clipboard is copied keeping its original name (`report.pdf`),
/// adding a Finder-style suffix on collision (`report 2.pdf`); plain text is saved
/// as `clip-<timestamp>.txt` and raw image data (e.g. a screenshot) as `.png`.
final class ClipboardSaver {
    static let shared = ClipboardSaver()

    private enum Const {
        static let filePrefix = "clip-"
        // Dashes for the date, dots for the time (mirrors macOS screenshot names) so the
        // two read apart at a glance; `_` between. Shell- and reference-safe, sortable.
        static let timestampFormat = "yyyy-MM-dd'_'HH.mm.ss"
        static let textExtension = "txt"
        static let imageExtension = "png"
        static let defaultRetentionDays = 7
        // "§" rather than "@": in Claude Code and opencode "@" opens the file-mention
        // autocomplete, and a resolved mention pulls the file into context at once — the
        // opposite of what ClipRef is for.
        static let defaultReferencePrefix = "§"
        // Every prefix ClipRef has ever put on the clipboard. Deliberately a fixed set rather
        // than the configured `referencePrefix`: the question `looksLikeReference` answers is
        // historical — "did some ClipRef build put this here?" — about a clipboard that may
        // still hold a reference from an older build or from before the user changed the
        // setting. Binding it to the current setting would break the guard exactly when the
        // prefix changes. A user-configured exotic prefix is therefore unguarded.
        static let knownReferencePrefixes: [Character] = ["§", "@"]
        // Copies run synchronously on the main thread, so cap the size to keep a huge
        // file from freezing the menu while it copies. 100 MB (decimal, matches Finder).
        static let maxCopyableBytes = 100 * 1_000_000
        // Reverse-DNS xattr stamped on every file we save; its value is the save date as a
        // `timeIntervalSince1970` string. Prune deletes only files carrying this key.
        static let ownerXattr = "de.manuelwelsch.ClipRef.savedAt"
    }

    private let defaults = UserDefaults.standard
    private let folderKey = "logFolderPath"

    /// The folder files are written to. Defaults to `~/Developer/clipboard-logs`.
    var folderURL: URL {
        if let path = defaults.string(forKey: folderKey), !path.isEmpty {
            let expanded = (path as NSString).expandingTildeInPath
            return URL(fileURLWithPath: expanded, isDirectory: true)
        }
        return Self.defaultFolderURL
    }

    static var defaultFolderURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Developer/clipboard-logs", isDirectory: true)
    }

    func setFolder(_ url: URL) {
        defaults.set(url.path, forKey: folderKey)
    }

    /// Creates the destination folder if it does not exist and returns it.
    @discardableResult
    func makeDestinationFolder() throws -> URL {
        let folder = folderURL
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder
    }

    /// Number of days to keep saved files. Override with
    /// `defaults write de.manuelwelsch.ClipRef retentionDays <N>`.
    var retentionDays: Int {
        let value = defaults.integer(forKey: "retentionDays")
        return value > 0 ? value : Const.defaultRetentionDays
    }

    /// The marker put in front of the saved path on the clipboard. Override with
    /// `defaults write de.manuelwelsch.ClipRef referencePrefix <x>` — `@` restores the
    /// original behaviour. A missing, empty, or whitespace-only value falls back to `§`;
    /// stored values are trimmed, since a prefix carrying whitespace would produce a reference
    /// `looksLikeReference` rejects by design, silently breaking the double-click guard.
    var referencePrefix: String {
        let value = (defaults.string(forKey: "referencePrefix") ?? "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        return value.isEmpty ? Const.defaultReferencePrefix : value
    }

    /// Saves the clipboard to a new file and replaces the clipboard contents with
    /// a `<prefix><path>` reference. A file wins if present, then text, then an image.
    @discardableResult
    func saveClipboard() -> SaveResult {
        let pasteboard = NSPasteboard.general
        let fileURL = Self.firstFileURL(on: pasteboard)
        let imageData = pngData(from: pasteboard)

        switch Self.decide(fileURL: fileURL, text: pasteboard.string(forType: .string), hasImage: imageData != nil) {
        case .copyFile(let source):
            guard Self.isCopyableFile(source) else {
                return .failure("ClipRef saves files, not folders — “\(source.lastPathComponent)” is a folder or app bundle. Copy a file instead.")
            }
            guard Self.fitsSizeLimit(source) else {
                let limit = ByteCountFormatter.string(fromByteCount: Int64(Const.maxCopyableBytes), countStyle: .file)
                return .failure("“\(source.lastPathComponent)” is too large to copy (ClipRef's limit is \(limit)). Copy a smaller file.")
            }
            return write(named: { Self.uniqueURL(in: $0, preferredName: source.lastPathComponent) }, pasteboard: pasteboard) { destination in
                try FileManager.default.copyItem(at: source, to: destination)
            }
        case .saveText(let text):
            return write(named: { Self.uniqueURL(in: $0, extension: Const.textExtension) }, pasteboard: pasteboard) { url in
                try Data(text.utf8).write(to: url, options: .atomic)
            }
        case .saveImage:
            guard let imageData else { return .noContent }
            return write(named: { Self.uniqueURL(in: $0, extension: Const.imageExtension) }, pasteboard: pasteboard) { url in
                try imageData.write(to: url, options: .atomic)
            }
        case .ignore:
            return .noContent
        }
    }

    /// What `saveClipboard` should do, decided purely from what's on the clipboard:
    /// a real file wins (copied as-is), then text, then an image; our own prefixed-path
    /// references are ignored. Pure and side-effect-free, so it can be unit-tested.
    enum SaveDecision: Equatable {
        case copyFile(URL)
        case saveText(String)
        case saveImage
        case ignore
    }

    static func decide(fileURL: URL?, text: String?, hasImage: Bool) -> SaveDecision {
        if let fileURL {
            return .copyFile(fileURL)
        }
        if let text, !text.isEmpty {
            return looksLikeReference(text) ? .ignore : .saveText(text)
        }
        return hasImage ? .saveImage : .ignore
    }

    /// The first real file on the clipboard (e.g. a file copied in Finder), or nil.
    private static func firstFileURL(on pasteboard: NSPasteboard) -> URL? {
        let options: [NSPasteboard.ReadingOptionKey: Any] = [.urlReadingFileURLsOnly: true]
        let urls = pasteboard.readObjects(forClasses: [NSURL.self], options: options) as? [URL]
        return urls?.first
    }

    /// A clipboard file is only copyable if it's a regular file. Folders and app bundles
    /// (which are directories) are rejected — a reference to a directory isn't useful
    /// to Claude Code, and a real app bundle can be huge.
    static func isCopyableFile(_ url: URL) -> Bool {
        (try? url.resourceValues(forKeys: [.isRegularFileKey]))?.isRegularFile == true
    }

    /// True when `url`'s size is within `maxBytes` — guards against copying a huge file
    /// synchronously on the main thread. If the size can't be read we don't block (return
    /// true); the limit is a best-effort hang guard, not a hard gate.
    static func fitsSizeLimit(_ url: URL, maxBytes: Int = Const.maxCopyableBytes) -> Bool {
        guard let size = (try? url.resourceValues(forKeys: [.fileSizeKey]))?.fileSize else { return true }
        return size <= maxBytes
    }

    /// True when the clipboard already holds one of our references: a single token starting
    /// with a known prefix (see `Const.knownReferencePrefixes`) followed by an absolute (`/`)
    /// or home (`~`) path. Used to skip re-saving a reference that the previous click just put
    /// on the clipboard.
    static func looksLikeReference(_ text: String) -> Bool {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let first = trimmed.first,
              Const.knownReferencePrefixes.contains(first),
              !trimmed.contains(where: { $0.isWhitespace }) else { return false }
        let path = trimmed.dropFirst()
        return path.hasPrefix("/") || path.hasPrefix("~")
    }

    /// Creates the folder, picks the destination via `name`, writes the file via
    /// `body`, swaps the clipboard for a `<prefix><path>` reference, and prunes old files.
    private func write(named name: (URL) -> URL, pasteboard: NSPasteboard, body: (URL) throws -> Void) -> SaveResult {
        let folder: URL
        do {
            folder = try makeDestinationFolder()
        } catch {
            return .failure("Could not create folder:\n\(error.localizedDescription)")
        }

        let fileURL = name(folder)
        do {
            try body(fileURL)
        } catch {
            return .failure("Could not write file:\n\(error.localizedDescription)")
        }

        // Stamp the file as ClipRef-created so prune can target only our own files,
        // never anything else the user keeps in the folder.
        Self.tagAsSaved(fileURL, at: Date())

        // Replace the clipboard with a reference ready to paste into Claude Code.
        // The saved content is already safely on disk.
        pasteboard.clearContents()
        pasteboard.setString("\(referencePrefix)\(fileURL.path)", forType: .string)

        pruneOldFiles()
        return .success(fileURL)
    }

    /// Extracts PNG data from the clipboard, re-encoding from TIFF or another image
    /// representation when a direct PNG isn't present. Returns nil if there is no image.
    private func pngData(from pasteboard: NSPasteboard) -> Data? {
        if let png = pasteboard.data(forType: .png) {
            return png
        }
        guard let image = NSImage(pasteboard: pasteboard),
              let tiff = image.tiffRepresentation,
              let bitmap = NSBitmapImageRep(data: tiff),
              let png = bitmap.representation(using: .png, properties: [:]) else {
            return nil
        }
        return png
    }

    /// Deletes saved files older than `retentionDays`. A file counts as ours only if it
    /// carries our `savedAt` xattr, and the xattr stores the save date itself — so prune
    /// never trusts filesystem timestamps (a copied file keeps the source's mtime, and
    /// "date added" isn't recorded on every volume) and never touches files the user keeps
    /// in the folder. Untagged or unparseable files are always left alone.
    func pruneOldFiles() {
        Self.pruneOldFiles(in: folderURL, retentionDays: retentionDays)
    }

    /// Prune logic isolated for testing: `now` and the folder are injected so a test can
    /// stamp files with known dates and assert what survives.
    static func pruneOldFiles(in folder: URL, retentionDays: Int, now: Date = Date()) {
        let fileManager = FileManager.default
        guard let entries = try? fileManager.contentsOfDirectory(
            at: folder,
            includingPropertiesForKeys: nil,
            options: [.skipsHiddenFiles]
        ) else { return }

        let cutoff = now.addingTimeInterval(-Double(retentionDays) * 24 * 60 * 60)
        for url in entries {
            guard let savedAt = savedDate(of: url) else { continue }   // not one of ours
            if savedAt < cutoff {
                try? fileManager.removeItem(at: url)
            }
        }
    }

    /// Stamps `url` with our `savedAt` xattr, value = `date` as a decimal
    /// `timeIntervalSince1970` string (human-inspectable via `xattr -p`).
    static func tagAsSaved(_ url: URL, at date: Date) {
        let bytes = Array(String(date.timeIntervalSince1970).utf8)
        url.withUnsafeFileSystemRepresentation { path in
            guard let path else { return }
            _ = bytes.withUnsafeBytes { setxattr(path, Const.ownerXattr, $0.baseAddress, $0.count, 0, 0) }
        }
    }

    /// The save date stamped on `url`, or nil if it isn't one of ours (no xattr) or the
    /// value can't be parsed. Reading the date back from the tag is how prune avoids
    /// trusting filesystem dates.
    static func savedDate(of url: URL) -> Date? {
        url.withUnsafeFileSystemRepresentation { path -> Date? in
            guard let path else { return nil }
            let length = getxattr(path, Const.ownerXattr, nil, 0, 0, 0)
            guard length > 0 else { return nil }
            var buffer = [UInt8](repeating: 0, count: length)
            guard getxattr(path, Const.ownerXattr, &buffer, length, 0, 0) == length,
                  let string = String(bytes: buffer, encoding: .utf8),
                  let interval = TimeInterval(string) else { return nil }
            return Date(timeIntervalSince1970: interval)
        }
    }

    /// Formats the timestamp used in `clip-<timestamp>` names. Cached because
    /// `DateFormatter` is expensive to construct and the format never changes.
    private static let timestampFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = Const.timestampFormat
        return formatter
    }()

    /// A non-existing file URL named `clip-<timestamp>.<ext>` for text and image saves,
    /// appending a numeric suffix if a file from the same second already exists.
    static func uniqueURL(in folder: URL, extension ext: String) -> URL {
        let stamp = timestampFormatter.string(from: Date())
        return uniqueURL(in: folder, stem: "\(Const.filePrefix)\(stamp)", separator: "-", extension: ext)
    }

    /// A non-existing file URL inside `folder` that keeps `preferredName` as-is, adding
    /// a Finder-style " 2", " 3" … before the extension on collision (`report 2.pdf`).
    /// Used for copied files so they land under their original name.
    static func uniqueURL(in folder: URL, preferredName: String) -> URL {
        let name = preferredName as NSString
        return uniqueURL(in: folder, stem: name.deletingPathExtension, separator: " ", extension: name.pathExtension)
    }

    /// Returns a non-existing URL for `stem`(+`.ext`) in `folder`, disambiguating a
    /// collision by inserting `separator``counter` before the extension — e.g.
    /// `report 2.pdf` (separator " ") or `clip-…-2.txt` (separator "-").
    private static func uniqueURL(in folder: URL, stem: String, separator: String, extension ext: String) -> URL {
        let suffix = ext.isEmpty ? "" : ".\(ext)"
        var url = folder.appendingPathComponent("\(stem)\(suffix)")
        var counter = 2
        while FileManager.default.fileExists(atPath: url.path) {
            url = folder.appendingPathComponent("\(stem)\(separator)\(counter)\(suffix)")
            counter += 1
        }
        return url
    }
}
