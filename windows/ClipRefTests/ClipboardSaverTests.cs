using ClipRef;

namespace ClipRefTests;

/// <summary>
/// Mirrors the macOS reference tests in ClipRefTests/ClipboardSaverTests.swift for the
/// pure decision core: <see cref="ClipboardSaver.Decide"/> and
/// <see cref="ClipboardSaver.LooksLikeReference"/>. Path literals are translated to
/// Windows form, and extra cases pin the Windows-only reference rule (drive root, UNC,
/// rooted backslash; forward-slash and ~ are intentionally not recognized).
/// </summary>
public class ClipboardSaverTests
{
    // Shared fixtures for the uniqueURL tests: a fixed folder/time so the generated
    // name is deterministic, and a predicate built from a set of already-taken paths
    // standing in for the filesystem (no disk access).
    private const string Folder = @"C:\logs";
    private static readonly DateTime FixedTime = new(2026, 6, 24, 14, 30, 5);

    private static Func<string, bool> Taken(params string[] paths)
    {
        var set = new HashSet<string>(paths);
        return p => set.Contains(p);
    }

    // decide(filePath:text:hasImage:)

    [Fact]
    public void TextIsSaved()
    {
        Assert.Equal<SaveDecision>(new SaveDecision.SaveText("hello"), ClipboardSaver.Decide(null, "hello", false));
    }

    [Fact]
    public void TextWinsOverImage()
    {
        Assert.Equal<SaveDecision>(new SaveDecision.SaveText("hello"), ClipboardSaver.Decide(null, "hello", true));
    }

    [Fact]
    public void ImageSavedWhenNoText()
    {
        Assert.Equal<SaveDecision>(new SaveDecision.SaveImage(), ClipboardSaver.Decide(null, null, true));
        Assert.Equal<SaveDecision>(new SaveDecision.SaveImage(), ClipboardSaver.Decide(null, "", true));
    }

    [Fact]
    public void EmptyClipboardIgnored()
    {
        Assert.Equal<SaveDecision>(new SaveDecision.Ignore(), ClipboardSaver.Decide(null, null, false));
        Assert.Equal<SaveDecision>(new SaveDecision.Ignore(), ClipboardSaver.Decide(null, "", false));
    }

    [Fact]
    public void ReferenceIgnoredEvenWithImage()
    {
        Assert.Equal<SaveDecision>(new SaveDecision.Ignore(), ClipboardSaver.Decide(null, @"§C:\Users\me\clip.txt", false));
        Assert.Equal<SaveDecision>(new SaveDecision.Ignore(), ClipboardSaver.Decide(null, @"§C:\Users\me\clip.png", true));
        // Decide's ignore branch inherits detection, so the legacy prefix is guarded here too.
        Assert.Equal<SaveDecision>(new SaveDecision.Ignore(), ClipboardSaver.Decide(null, @"@C:\Users\me\clip.txt", false));
        Assert.Equal<SaveDecision>(new SaveDecision.Ignore(), ClipboardSaver.Decide(null, @"@C:\Users\me\clip.png", true));
    }

    [Fact]
    public void FileIsCopied()
    {
        const string path = @"C:\tmp\report.pdf";
        Assert.Equal<SaveDecision>(new SaveDecision.CopyFile(path), ClipboardSaver.Decide(path, null, false));
    }

    [Fact]
    public void FileWinsOverTextAndImage()
    {
        const string path = @"C:\tmp\clip.mov";
        Assert.Equal<SaveDecision>(new SaveDecision.CopyFile(path), ClipboardSaver.Decide(path, "ignored text", true));
    }

    // looksLikeReference

    [Theory]
    // Our own references with a Windows absolute path → recognized, under either known prefix.
    [InlineData(@"§C:\Users\me\clip.txt", true)]
    [InlineData(@"@C:\Users\me\clip.txt", true)]
    [InlineData(@"  §C:\Users\me\x.png  ", true)]   // surrounding whitespace is trimmed
    [InlineData(@"  @C:\Users\me\x.png  ", true)]
    [InlineData(@"§\\server\share\clip.txt", true)] // UNC
    [InlineData(@"@\\server\share\clip.txt", true)]
    [InlineData(@"§\folder\clip.txt", true)]        // rooted backslash
    [InlineData(@"@\folder\clip.txt", true)]
    [InlineData(@"§c:\x.txt", true)]                // drive letter is case-insensitive
    [InlineData(@"@c:\x.txt", true)]
    // Not references.
    [InlineData("§here standup notes", false)]      // internal whitespace
    [InlineData("@here standup notes", false)]
    [InlineData("§username", false)]                // no path after the prefix
    [InlineData("@username", false)]
    [InlineData("plain text", false)]
    [InlineData("", false)]
    // Only the known prefixes count — an arbitrary leading character is not a reference, or a
    // quoted path pasted from an error message would be silently ignored instead of saved.
    [InlineData(@"%C:\Users\me\clip.txt", false)]
    [InlineData(@"""C:\Users\me\clip.txt", false)]
    [InlineData(@"C:\Users\me\clip.txt", false)]    // a bare path is not a reference
    // macOS cues are intentionally dropped in the Windows port.
    [InlineData("§/Users/me/clip.txt", false)]      // forward-slash not recognized
    [InlineData("@/Users/me/clip.txt", false)]
    [InlineData("§~/x.png", false)]                 // home (~) not recognized
    [InlineData("@~/x.png", false)]
    public void LooksLikeReferenceDetectsWindowsReferencesOnly(string text, bool expected)
    {
        Assert.Equal(expected, ClipboardSaver.LooksLikeReference(text));
    }

    /// <summary>
    /// The backward-compatibility guarantee: detection knows every prefix ClipRef has
    /// ever shipped, not just the configured one. A reference left on the clipboard by an older
    /// build — or by the macOS app before its own update — must still be recognized, otherwise the
    /// double-click guard breaks exactly when the prefix setting changes.
    /// </summary>
    [Fact]
    public void LegacyAtPrefixStaysRecognizedAfterTheDefaultMovedToSectionSign()
    {
        Assert.True(ClipboardSaver.LooksLikeReference(@"@C:\logs\clip-2026-06-25_13.30.45.txt"));
        Assert.True(ClipboardSaver.LooksLikeReference(@"§C:\logs\clip-2026-06-25_13.30.45.txt"));
    }

    // uniqueURL — generated clip-<timestamp>.<ext> names

    [Fact]
    public void GeneratedNameUsesClipPrefixStampAndExtension()
    {
        Assert.Equal(
            @"C:\logs\clip-2026-06-24_14.30.05.txt",
            ClipboardSaver.UniqueGeneratedPath(Folder, "txt", FixedTime, _ => false));
    }

    [Fact]
    public void GeneratedImageNameUsesGivenExtension()
    {
        Assert.Equal(
            @"C:\logs\clip-2026-06-24_14.30.05.png",
            ClipboardSaver.UniqueGeneratedPath(Folder, "png", FixedTime, _ => false));
    }

    [Fact]
    public void GeneratedNameAppendsCounterOnCollision()
    {
        var exists = Taken(@"C:\logs\clip-2026-06-24_14.30.05.txt");
        Assert.Equal(
            @"C:\logs\clip-2026-06-24_14.30.05-2.txt",
            ClipboardSaver.UniqueGeneratedPath(Folder, "txt", FixedTime, exists));
    }

    [Fact]
    public void GeneratedNameSkipsToThirdOnDoubleCollision()
    {
        var exists = Taken(
            @"C:\logs\clip-2026-06-24_14.30.05.txt",
            @"C:\logs\clip-2026-06-24_14.30.05-2.txt");
        Assert.Equal(
            @"C:\logs\clip-2026-06-24_14.30.05-3.txt",
            ClipboardSaver.UniqueGeneratedPath(Folder, "txt", FixedTime, exists));
    }

    // uniqueURL — preferred (original) names with Finder-style " 2" dedup

    [Theory]
    [InlineData(new string[0], @"C:\logs\report.pdf")]                                                 // free → kept as-is
    [InlineData(new[] { @"C:\logs\report.pdf" }, @"C:\logs\report 2.pdf")]                             // one collision → " 2"
    [InlineData(new[] { @"C:\logs\report.pdf", @"C:\logs\report 2.pdf" }, @"C:\logs\report 3.pdf")]    // two → " 3"
    public void PreferredNameDedupesFinderStyle(string[] taken, string expected)
    {
        Assert.Equal(expected, ClipboardSaver.UniquePreferredPath(Folder, "report.pdf", Taken(taken)));
    }

    [Fact]
    public void PreferredNameWithoutExtensionKeptAsIs()
    {
        Assert.Equal(
            @"C:\logs\README",
            ClipboardSaver.UniquePreferredPath(Folder, "README", _ => false));
    }

    [Fact]
    public void PreferredNameWithoutExtensionDedupesWithoutTrailingDot()
    {
        var exists = Taken(@"C:\logs\README");
        Assert.Equal(
            @"C:\logs\README 2",
            ClipboardSaver.UniquePreferredPath(Folder, "README", exists));
    }

    // isCopyableFile — only regular files are copyable; directories are rejected.
    // The filesystem fact is injected (the path itself is irrelevant), so no disk access.

    private const string FilePath = @"C:\logs\report.pdf";

    [Fact]
    public void RegularFileIsCopyable()
    {
        Assert.True(ClipboardSaver.IsCopyableFile(FilePath, _ => FileAttributes.Normal));
    }

    [Fact]
    public void DirectoryIsNotCopyable()
    {
        Assert.False(ClipboardSaver.IsCopyableFile(FilePath, _ => FileAttributes.Directory));
    }

    [Fact]
    public void DirectoryWithExtraFlagsIsNotCopyable()
    {
        Assert.False(ClipboardSaver.IsCopyableFile(FilePath, _ => FileAttributes.Directory | FileAttributes.ReadOnly));
    }

    [Fact]
    public void MissingOrUnreadableFileIsNotCopyable()
    {
        Assert.False(ClipboardSaver.IsCopyableFile(FilePath, _ => (FileAttributes?)null));
    }

    [Fact]
    public void ReparsePointFileIsStillCopyable()
    {
        // Decision (b): only directories are rejected for now, so a symlink/junction
        // pointing at a file is allowed. Reparse-point handling, if ever needed, comes
        // with the Phase 2 real-stat wrapper.
        Assert.True(ClipboardSaver.IsCopyableFile(FilePath, _ => FileAttributes.ReparsePoint));
    }

    // fitsSizeLimit — best-effort guard against copying a huge file synchronously.

    [Theory]
    [InlineData(50L, 100L, true)]    // below the limit
    [InlineData(100L, 100L, true)]   // exactly at the limit (<=)
    [InlineData(101L, 100L, false)]  // above the limit
    public void FitsSizeLimitComparesAgainstMaxBytes(long size, long maxBytes, bool expected)
    {
        Assert.Equal(expected, ClipboardSaver.FitsSizeLimit(FilePath, _ => size, maxBytes));
    }

    [Fact]
    public void UnreadableSizeDoesNotBlock()
    {
        // If the size can't be read we don't block the copy (the guard is best-effort).
        Assert.True(ClipboardSaver.FitsSizeLimit(FilePath, _ => (long?)null, maxBytes: 100));
    }

    [Fact]
    public void DefaultLimitIsHundredMegabytesDecimal()
    {
        // maxBytes omitted → the real Const.MaxCopyableBytes (100 * 1_000_000).
        Assert.True(ClipboardSaver.FitsSizeLimit(FilePath, _ => 100_000_000L));
        Assert.False(ClipboardSaver.FitsSizeLimit(FilePath, _ => 100_000_001L));
    }
}
