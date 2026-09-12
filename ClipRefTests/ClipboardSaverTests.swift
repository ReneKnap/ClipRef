import XCTest
@testable import ClipRef

final class ClipboardSaverTests: XCTestCase {

    // MARK: - decide(fileURL:text:hasImage:)

    func testTextIsSaved() {
        XCTAssertEqual(ClipboardSaver.decide(fileURL: nil, text: "hello", hasImage: false), .saveText("hello"))
    }

    func testTextWinsOverImage() {
        XCTAssertEqual(ClipboardSaver.decide(fileURL: nil, text: "hello", hasImage: true), .saveText("hello"))
    }

    func testImageSavedWhenNoText() {
        XCTAssertEqual(ClipboardSaver.decide(fileURL: nil, text: nil, hasImage: true), .saveImage)
        XCTAssertEqual(ClipboardSaver.decide(fileURL: nil, text: "", hasImage: true), .saveImage)
    }

    func testEmptyClipboardIgnored() {
        XCTAssertEqual(ClipboardSaver.decide(fileURL: nil, text: nil, hasImage: false), .ignore)
        XCTAssertEqual(ClipboardSaver.decide(fileURL: nil, text: "", hasImage: false), .ignore)
    }

    func testReferenceIgnoredEvenWithImage() {
        XCTAssertEqual(ClipboardSaver.decide(fileURL: nil, text: "§/Users/me/clip.txt", hasImage: false), .ignore)
        XCTAssertEqual(ClipboardSaver.decide(fileURL: nil, text: "§/Users/me/clip.png", hasImage: true), .ignore)
        // decide's ignore branch inherits detection, so the legacy prefix is guarded here too.
        XCTAssertEqual(ClipboardSaver.decide(fileURL: nil, text: "@/Users/me/clip.txt", hasImage: false), .ignore)
        XCTAssertEqual(ClipboardSaver.decide(fileURL: nil, text: "@/Users/me/clip.png", hasImage: true), .ignore)
    }

    // MARK: - decide: files

    func testFileIsCopied() {
        let url = URL(fileURLWithPath: "/tmp/report.pdf")
        XCTAssertEqual(ClipboardSaver.decide(fileURL: url, text: nil, hasImage: false), .copyFile(url))
    }

    func testFileWinsOverTextAndImage() {
        let url = URL(fileURLWithPath: "/tmp/clip.mov")
        XCTAssertEqual(ClipboardSaver.decide(fileURL: url, text: "ignored text", hasImage: true), .copyFile(url))
    }

    func testFoldersAreNotCopyable() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let file = dir.appendingPathComponent("note.txt")
        try Data("hi".utf8).write(to: file)
        XCTAssertTrue(ClipboardSaver.isCopyableFile(file))
        XCTAssertFalse(ClipboardSaver.isCopyableFile(dir))   // a folder/app bundle
    }

    // MARK: - looksLikeReference

    func testReferenceDetection() {
        XCTAssertTrue(ClipboardSaver.looksLikeReference("§/Users/me/clip.txt"))
        XCTAssertTrue(ClipboardSaver.looksLikeReference("  §~/Developer/x.png  "))   // trimmed
        XCTAssertFalse(ClipboardSaver.looksLikeReference("§here standup notes"))     // whitespace
        XCTAssertFalse(ClipboardSaver.looksLikeReference("§username"))               // no path
        XCTAssertFalse(ClipboardSaver.looksLikeReference("plain text"))
        XCTAssertFalse(ClipboardSaver.looksLikeReference(""))
    }

    /// The backward-compatibility guarantee: detection knows every prefix ClipRef has ever
    /// shipped, not just the configured one. A reference left on the clipboard by an older
    /// build must still be recognised, or the double-click guard breaks exactly when the
    /// `referencePrefix` setting changes.
    func testLegacyAtPrefixStaysRecognized() {
        XCTAssertTrue(ClipboardSaver.looksLikeReference("@/Users/me/clip.txt"))
        XCTAssertTrue(ClipboardSaver.looksLikeReference("  @~/Developer/x.png  "))
        XCTAssertFalse(ClipboardSaver.looksLikeReference("@here standup notes"))
        XCTAssertFalse(ClipboardSaver.looksLikeReference("@username"))
    }

    /// Only the known prefixes count. Without this, "simplifying" the guard to accept any
    /// leading character would still pass — and a quoted path pasted from an error message
    /// would be silently ignored instead of saved.
    func testUnknownPrefixIsNotAReference() {
        XCTAssertFalse(ClipboardSaver.looksLikeReference("%/Users/me/clip.txt"))
        XCTAssertFalse(ClipboardSaver.looksLikeReference("\"/Users/me/clip.txt"))
        XCTAssertFalse(ClipboardSaver.looksLikeReference("/Users/me/clip.txt"))   // bare path
    }

    // MARK: - uniqueURL

    func testUniqueURLNamingAndCollision() throws {
        let dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("ClipRefTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }

        let first = ClipboardSaver.uniqueURL(in: dir, extension: "txt")
        XCTAssertEqual(first.pathExtension, "txt")
        let stem = first.deletingPathExtension().lastPathComponent
        XCTAssertNotNil(
            stem.range(of: #"^clip-\d{4}-\d{2}-\d{2}_\d{2}\.\d{2}\.\d{2}$"#, options: .regularExpression),
            "unexpected name: \(stem)"
        )

        // Once that file exists, the next URL must differ (and not already exist).
        try Data("x".utf8).write(to: first)
        let second = ClipboardSaver.uniqueURL(in: dir, extension: "txt")
        XCTAssertNotEqual(first, second)
        XCTAssertFalse(FileManager.default.fileExists(atPath: second.path))
    }

    func testUniqueURLKeepsOriginalNameAndCollidesFinderStyle() throws {
        let dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("ClipRefTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }

        // First save keeps the original name verbatim.
        let first = ClipboardSaver.uniqueURL(in: dir, preferredName: "report.pdf")
        XCTAssertEqual(first.lastPathComponent, "report.pdf")

        // On collision, a Finder-style " 2", " 3" … lands before the extension.
        try Data("a".utf8).write(to: first)
        let second = ClipboardSaver.uniqueURL(in: dir, preferredName: "report.pdf")
        XCTAssertEqual(second.lastPathComponent, "report 2.pdf")

        try Data("b".utf8).write(to: second)
        let third = ClipboardSaver.uniqueURL(in: dir, preferredName: "report.pdf")
        XCTAssertEqual(third.lastPathComponent, "report 3.pdf")
    }

    func testUniqueURLPreferredNameWithoutExtension() throws {
        let dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("ClipRefTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }

        let first = ClipboardSaver.uniqueURL(in: dir, preferredName: "Makefile")
        XCTAssertEqual(first.lastPathComponent, "Makefile")
        try Data("x".utf8).write(to: first)
        let second = ClipboardSaver.uniqueURL(in: dir, preferredName: "Makefile")
        XCTAssertEqual(second.lastPathComponent, "Makefile 2")
    }

    // MARK: - fitsSizeLimit

    func testSizeLimitAcceptsSmallRejectsLarge() throws {
        let dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("ClipRefTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }

        let file = dir.appendingPathComponent("blob.bin")
        try Data(count: 1_000).write(to: file)
        XCTAssertTrue(ClipboardSaver.fitsSizeLimit(file, maxBytes: 2_000))
        XCTAssertFalse(ClipboardSaver.fitsSizeLimit(file, maxBytes: 500))
    }

    // MARK: - xattr ownership tag + prune

    func testSavedDateRoundTripsThroughXattr() throws {
        let dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("ClipRefTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }

        let file = dir.appendingPathComponent("report.pdf")
        try Data("x".utf8).write(to: file)

        // An untagged file is not one of ours.
        XCTAssertNil(ClipboardSaver.savedDate(of: file))

        // After tagging, the stored save date reads back.
        let when = Date(timeIntervalSince1970: 1_700_000_000)
        ClipboardSaver.tagAsSaved(file, at: when)
        let readBack = try XCTUnwrap(ClipboardSaver.savedDate(of: file))
        XCTAssertEqual(readBack.timeIntervalSince1970, when.timeIntervalSince1970, accuracy: 0.0005)
    }

    func testPruneDeletesOnlyOurExpiredFiles() throws {
        let dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("ClipRefTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }

        let fm = FileManager.default
        let now = Date(timeIntervalSince1970: 1_700_000_000)
        let day = 24.0 * 60 * 60

        // Ours, 10 days old → pruned.
        let expired = dir.appendingPathComponent("old.txt")
        try Data("a".utf8).write(to: expired)
        ClipboardSaver.tagAsSaved(expired, at: now.addingTimeInterval(-10 * day))

        // Ours, 1 day old → kept.
        let fresh = dir.appendingPathComponent("new.txt")
        try Data("b".utf8).write(to: fresh)
        ClipboardSaver.tagAsSaved(fresh, at: now.addingTimeInterval(-1 * day))

        // Not ours (no tag), even though it's an old plain file → must never be touched.
        let foreign = dir.appendingPathComponent("user-keepsake.txt")
        try Data("c".utf8).write(to: foreign)

        ClipboardSaver.pruneOldFiles(in: dir, retentionDays: 7, now: now)

        XCTAssertFalse(fm.fileExists(atPath: expired.path), "expired ClipRef file should be pruned")
        XCTAssertTrue(fm.fileExists(atPath: fresh.path), "recent ClipRef file should survive")
        XCTAssertTrue(fm.fileExists(atPath: foreign.path), "untagged user file must never be pruned")
    }
}
