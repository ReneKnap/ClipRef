using ClipRef;

namespace ClipRefTests;

/// <summary>
/// Tests the write side: <see cref="ClipboardSaveService.Save"/> running read → Decide → write →
/// @-put-back over in-memory doubles (no real disk or clipboard). Pins the precedence and
/// guard-abort, the unique-naming map (original name for a copied file, <c>clip-&lt;timestamp&gt;</c>
/// for text/image) including collisions, the verbatim @-path put-back, and the folder-create /
/// write failure mapping. A fixed clock makes the timestamped name deterministic.
/// </summary>
public class ClipboardSaveServiceTests
{
    private const string Folder = @"C:\logs";

    /// <summary>The default reference prefix the put-back assertions below expect.</summary>
    private const string Prefix = "§";

    private static readonly DateTime FixedClock = new(2026, 6, 25, 13, 30, 45);

    private static ClipboardSaveService Service(
        ClipboardSnapshot snapshot,
        InMemoryFileSystem fileSystem,
        InMemoryClipboardWriter writer,
        InMemoryFileTagger? tagger = null,
        string? referencePrefix = null)
    {
        var store = referencePrefix is null
            ? new InMemorySettingsStore(("logFolderPath", Folder))
            : new InMemorySettingsStore(("logFolderPath", Folder), ("referencePrefix", referencePrefix));
        var settings = new Settings(store);
        return new ClipboardSaveService(
            new InMemoryClipboardReader(snapshot), writer, fileSystem, tagger ?? new InMemoryFileTagger(),
            // UTC display zone keeps the exact clip-<timestamp> filenames deterministic regardless of
            // the test runner's local timezone; the local-zone rendering is pinned by its own test.
            settings, () => FixedClock, TimeZoneInfo.Utc);
    }

    [Fact]
    public void TextSaved_WritesTxtAndPutsReferenceBack()
    {
        var fileSystem = new InMemoryFileSystem();
        var writer = new InMemoryClipboardWriter();
        var result = Service(new ClipboardSnapshot(null, "hello world", null), fileSystem, writer).Save();

        var expected = Path.Combine(Folder, "clip-2026-06-25_13.30.45.txt");
        Assert.Equal<SaveResult>(new SaveResult.Saved(expected), result);
        Assert.Equal("hello world", fileSystem.TextAt(expected));
        Assert.Equal(Prefix + expected, writer.LastText);
    }

    [Fact]
    public void ConfiguredPrefix_ReachesTheClipboard()
    {
        var fileSystem = new InMemoryFileSystem();
        var writer = new InMemoryClipboardWriter();
        var result = Service(new ClipboardSnapshot(null, "hello world", null), fileSystem, writer, referencePrefix: "@").Save();

        var expected = Path.Combine(Folder, "clip-2026-06-25_13.30.45.txt");
        Assert.Equal<SaveResult>(new SaveResult.Saved(expected), result);
        Assert.Equal("@" + expected, writer.LastText);   // the stored value wins over the default
    }

    [Fact]
    public void CustomPrefix_ReachesTheClipboardVerbatim()
    {
        var fileSystem = new InMemoryFileSystem();
        var writer = new InMemoryClipboardWriter();
        var result = Service(new ClipboardSnapshot(null, "hello world", null), fileSystem, writer, referencePrefix: "ref:").Save();

        var expected = Path.Combine(Folder, "clip-2026-06-25_13.30.45.txt");
        Assert.Equal<SaveResult>(new SaveResult.Saved(expected), result);
        Assert.Equal("ref:" + expected, writer.LastText);
    }

    [Fact]
    public void ImageSaved_WritesPng()
    {
        var fileSystem = new InMemoryFileSystem();
        var writer = new InMemoryClipboardWriter();
        var image = new byte[] { 1, 2, 3 };
        var result = Service(new ClipboardSnapshot(null, null, image), fileSystem, writer).Save();

        var expected = Path.Combine(Folder, "clip-2026-06-25_13.30.45.png");
        Assert.Equal<SaveResult>(new SaveResult.Saved(expected), result);
        Assert.Equal(image, fileSystem.BytesAt(expected));
        Assert.Equal(Prefix + expected, writer.LastText);
    }

    [Fact]
    public void EmptyClipboard_NothingToSave()
    {
        var fileSystem = new InMemoryFileSystem();
        var writer = new InMemoryClipboardWriter();
        var result = Service(new ClipboardSnapshot(null, null, null), fileSystem, writer).Save();

        Assert.Equal<SaveResult>(new SaveResult.NothingToSave(), result);
        Assert.True(fileSystem.WroteNothing);
        Assert.Null(writer.LastText);
    }

    [Theory]
    [InlineData(@"§C:\logs\clip.txt")]
    // The legacy prefix aborts the save too, even though the configured prefix is now "§" — a
    // reference an older build left on the clipboard must not be saved back as text.
    [InlineData(@"@C:\logs\clip.txt")]
    public void ReferenceText_NothingToSave(string clipboardText)
    {
        var fileSystem = new InMemoryFileSystem();
        var writer = new InMemoryClipboardWriter();
        var result = Service(new ClipboardSnapshot(null, clipboardText, null), fileSystem, writer).Save();

        Assert.Equal<SaveResult>(new SaveResult.NothingToSave(), result);
        Assert.True(fileSystem.WroteNothing);
        Assert.Null(writer.LastText);
    }

    [Fact]
    public void FileCopied_KeepsOriginalNameAndPutsReferenceBack()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.SetAttributes(@"C:\src\report.pdf", FileAttributes.Normal);
        fileSystem.SetSize(@"C:\src\report.pdf", 1000);
        var writer = new InMemoryClipboardWriter();
        var result = Service(new ClipboardSnapshot(@"C:\src\report.pdf", null, null), fileSystem, writer).Save();

        var expected = Path.Combine(Folder, "report.pdf");
        Assert.Equal<SaveResult>(new SaveResult.Saved(expected), result);
        Assert.True(fileSystem.CopiedTo(expected));
        Assert.Equal(Prefix + expected, writer.LastText);
    }

    [Fact]
    public void FileCopy_NonCopyable_FailsAndAborts()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.SetAttributes(@"C:\src\report.pdf", FileAttributes.Directory);
        var writer = new InMemoryClipboardWriter();
        var result = Service(new ClipboardSnapshot(@"C:\src\report.pdf", null, null), fileSystem, writer).Save();

        var failure = Assert.IsType<SaveResult.Failure>(result);
        Assert.Contains("report.pdf", failure.Message);
        Assert.Contains("folder", failure.Message);
        Assert.Contains("Copy a file", failure.Message);
        Assert.True(fileSystem.WroteNothing);
        Assert.Null(writer.LastText);
    }

    [Fact]
    public void FileCopy_TooLarge_FailsAndAborts()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.SetAttributes(@"C:\src\report.pdf", FileAttributes.Normal);
        fileSystem.SetSize(@"C:\src\report.pdf", 200_000_000);
        var writer = new InMemoryClipboardWriter();
        var result = Service(new ClipboardSnapshot(@"C:\src\report.pdf", null, null), fileSystem, writer).Save();

        var failure = Assert.IsType<SaveResult.Failure>(result);
        Assert.Contains("report.pdf", failure.Message);
        Assert.Contains("too large", failure.Message);
        Assert.True(fileSystem.WroteNothing);
        Assert.Null(writer.LastText);
    }

    [Fact]
    public void TextSave_CollisionGetsDashTwoSuffix()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.SeedExisting(Path.Combine(Folder, "clip-2026-06-25_13.30.45.txt"));
        var writer = new InMemoryClipboardWriter();
        var result = Service(new ClipboardSnapshot(null, "x", null), fileSystem, writer).Save();

        var expected = Path.Combine(Folder, "clip-2026-06-25_13.30.45-2.txt");
        Assert.Equal<SaveResult>(new SaveResult.Saved(expected), result);
        Assert.Equal(Prefix + expected, writer.LastText);
    }

    [Fact]
    public void FileCopy_CollisionGetsSpaceTwoSuffix()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.SetAttributes(@"C:\src\report.pdf", FileAttributes.Normal);
        fileSystem.SetSize(@"C:\src\report.pdf", 1000);
        fileSystem.SeedExisting(Path.Combine(Folder, "report.pdf"));
        var writer = new InMemoryClipboardWriter();
        var result = Service(new ClipboardSnapshot(@"C:\src\report.pdf", null, null), fileSystem, writer).Save();

        var expected = Path.Combine(Folder, "report 2.pdf");
        Assert.Equal<SaveResult>(new SaveResult.Saved(expected), result);
        Assert.True(fileSystem.CopiedTo(expected));
    }

    [Fact]
    public void ReferenceWithSpaces_NotQuoted()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.SetAttributes(@"C:\src\my report.pdf", FileAttributes.Normal);
        fileSystem.SetSize(@"C:\src\my report.pdf", 1000);
        var writer = new InMemoryClipboardWriter();
        var result = Service(new ClipboardSnapshot(@"C:\src\my report.pdf", null, null), fileSystem, writer).Save();

        var expected = Path.Combine(Folder, "my report.pdf");
        Assert.Equal<SaveResult>(new SaveResult.Saved(expected), result);
        Assert.Equal(Prefix + expected, writer.LastText); // verbatim, no quoting/escaping
    }

    [Fact]
    public void CreateDirectoryThrows_ReturnsFailure()
    {
        var fileSystem = new InMemoryFileSystem { ThrowOnCreateDirectory = true };
        var writer = new InMemoryClipboardWriter();
        var result = Service(new ClipboardSnapshot(null, "x", null), fileSystem, writer).Save();

        var failure = Assert.IsType<SaveResult.Failure>(result);
        Assert.StartsWith("Could not create folder", failure.Message);
        Assert.True(fileSystem.WroteNothing);
        Assert.Null(writer.LastText);
    }

    [Fact]
    public void WriteThrows_ReturnsFailure()
    {
        var fileSystem = new InMemoryFileSystem { ThrowOnWrite = true };
        var writer = new InMemoryClipboardWriter();
        var result = Service(new ClipboardSnapshot(null, "x", null), fileSystem, writer).Save();

        var failure = Assert.IsType<SaveResult.Failure>(result);
        Assert.StartsWith("Could not write file", failure.Message);
        Assert.Null(writer.LastText);
    }

    [Fact]
    public void FolderCreated_OnSuccessfulSave()
    {
        var fileSystem = new InMemoryFileSystem();
        var writer = new InMemoryClipboardWriter();
        Service(new ClipboardSnapshot(null, "x", null), fileSystem, writer).Save();

        Assert.Contains(Folder, fileSystem.CreatedDirectories);
    }

    [Fact]
    public void TextSave_StampsOwnershipTagWithClockInstant()
    {
        var fileSystem = new InMemoryFileSystem();
        var writer = new InMemoryClipboardWriter();
        var tagger = new InMemoryFileTagger();
        Service(new ClipboardSnapshot(null, "hello", null), fileSystem, writer, tagger).Save();

        var expected = Path.Combine(Folder, "clip-2026-06-25_13.30.45.txt");
        Assert.True(tagger.Tagged(expected));
        Assert.Equal(FixedClock, tagger.TagOf(expected));
    }

    [Fact]
    public void TextSave_FilenameUsesLocalDisplayTime_WhileTagStaysUtc()
    {
        // Regression (timestamp-utc-offset): the filename timestamp must render in the machine's
        // *local* wall-clock — macOS DateFormatter parity — while the ownership tag stays on the
        // absolute UTC instant. The bug fed one UTC clock into both, so the name ran 2 h behind
        // local in CEST. A fixed UTC clock plus a custom UTC+2 display zone pins both halves.
        var utcInstant = new DateTime(2026, 6, 25, 13, 30, 45, DateTimeKind.Utc);
        var plusTwo = TimeZoneInfo.CreateCustomTimeZone(
            "UTC+2 (test)", TimeSpan.FromHours(2), "UTC+2 (test)", "UTC+2 (test)");
        var fileSystem = new InMemoryFileSystem();
        var writer = new InMemoryClipboardWriter();
        var tagger = new InMemoryFileTagger();
        var settings = new Settings(new InMemorySettingsStore(("logFolderPath", Folder)));
        var service = new ClipboardSaveService(
            new InMemoryClipboardReader(new ClipboardSnapshot(null, "hello", null)),
            writer, fileSystem, tagger, settings, () => utcInstant, plusTwo);

        var result = service.Save();

        // 13:30:45 UTC + 2 h → the name (and the @-reference) show 15:30:45 local wall-clock…
        var expected = Path.Combine(Folder, "clip-2026-06-25_15.30.45.txt");
        Assert.Equal<SaveResult>(new SaveResult.Saved(expected), result);
        Assert.Equal(Prefix + expected, writer.LastText);
        // …while the ownership tag keeps the raw, unshifted UTC instant so prune stays correct.
        Assert.Equal(utcInstant, tagger.TagOf(expected));
    }

    [Fact]
    public void ImageSave_StampsOwnershipTag()
    {
        var fileSystem = new InMemoryFileSystem();
        var writer = new InMemoryClipboardWriter();
        var tagger = new InMemoryFileTagger();
        Service(new ClipboardSnapshot(null, null, new byte[] { 1, 2, 3 }), fileSystem, writer, tagger).Save();

        var expected = Path.Combine(Folder, "clip-2026-06-25_13.30.45.png");
        Assert.True(tagger.Tagged(expected));
        Assert.Equal(FixedClock, tagger.TagOf(expected));
    }

    [Fact]
    public void FileCopy_StampsOwnershipTag()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.SetAttributes(@"C:\src\report.pdf", FileAttributes.Normal);
        fileSystem.SetSize(@"C:\src\report.pdf", 1000);
        var writer = new InMemoryClipboardWriter();
        var tagger = new InMemoryFileTagger();
        Service(new ClipboardSnapshot(@"C:\src\report.pdf", null, null), fileSystem, writer, tagger).Save();

        var expected = Path.Combine(Folder, "report.pdf");
        Assert.True(tagger.Tagged(expected));
        Assert.Equal(FixedClock, tagger.TagOf(expected));
    }

    [Fact]
    public void GuardAbort_DoesNotStampTag()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.SetAttributes(@"C:\src\report.pdf", FileAttributes.Directory);
        var writer = new InMemoryClipboardWriter();
        var tagger = new InMemoryFileTagger();
        var result = Service(new ClipboardSnapshot(@"C:\src\report.pdf", null, null), fileSystem, writer, tagger).Save();

        Assert.IsType<SaveResult.Failure>(result);
        Assert.True(tagger.TaggedNothing);
    }

    [Fact]
    public void TooLarge_DoesNotStampTag()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.SetAttributes(@"C:\src\report.pdf", FileAttributes.Normal);
        fileSystem.SetSize(@"C:\src\report.pdf", 200_000_000);
        var writer = new InMemoryClipboardWriter();
        var tagger = new InMemoryFileTagger();
        var result = Service(new ClipboardSnapshot(@"C:\src\report.pdf", null, null), fileSystem, writer, tagger).Save();

        Assert.IsType<SaveResult.Failure>(result);
        Assert.True(tagger.TaggedNothing);
    }

    [Fact]
    public void NothingToSave_DoesNotStampTag()
    {
        var fileSystem = new InMemoryFileSystem();
        var writer = new InMemoryClipboardWriter();
        var tagger = new InMemoryFileTagger();
        var result = Service(new ClipboardSnapshot(null, null, null), fileSystem, writer, tagger).Save();

        Assert.Equal<SaveResult>(new SaveResult.NothingToSave(), result);
        Assert.True(tagger.TaggedNothing);
    }

    [Fact]
    public void WriteFailure_DoesNotStampTag()
    {
        var fileSystem = new InMemoryFileSystem { ThrowOnWrite = true };
        var writer = new InMemoryClipboardWriter();
        var tagger = new InMemoryFileTagger();
        var result = Service(new ClipboardSnapshot(null, "x", null), fileSystem, writer, tagger).Save();

        var failure = Assert.IsType<SaveResult.Failure>(result);
        Assert.StartsWith("Could not write file", failure.Message);
        Assert.True(tagger.TaggedNothing);
    }

    [Fact]
    public void TagFailureStillSucceeds_BestEffort()
    {
        var fileSystem = new InMemoryFileSystem();
        var writer = new InMemoryClipboardWriter();
        var tagger = new InMemoryFileTagger { ThrowOnTag = true };
        var result = Service(new ClipboardSnapshot(null, "hello", null), fileSystem, writer, tagger).Save();

        var expected = Path.Combine(Folder, "clip-2026-06-25_13.30.45.txt");
        Assert.Equal<SaveResult>(new SaveResult.Saved(expected), result);
        Assert.Equal(Prefix + expected, writer.LastText);
    }

    // ---- PruneOldFiles: deletes only our own expired files, by the tag alone ----
    //
    // Mirrors ClipRefTests/ClipboardSaverTests.swift testPruneDeletesOnlyOurExpiredFiles. With the
    // default 7-day retention and the fixed clock, the cutoff is FixedClock - 7 days; a file is
    // pruned only when its tagged save date is strictly older than that. SeedExisting stands in for
    // a file already in the folder; the tagger stamps the (known) save date prune reads back.

    private static InMemoryFileTagger TaggerWith(params (string Path, DateTime SavedAt)[] tags)
    {
        var tagger = new InMemoryFileTagger();
        foreach (var (path, savedAt) in tags)
        {
            tagger.TagAsSaved(path, savedAt);
        }

        return tagger;
    }

    [Fact]
    public void Prune_DeletesExpiredTaggedFile()
    {
        var fileSystem = new InMemoryFileSystem();
        var expired = Path.Combine(Folder, "old.txt");
        fileSystem.SeedExisting(expired);
        var tagger = TaggerWith((expired, FixedClock.AddDays(-10)));
        var service = Service(new ClipboardSnapshot(null, null, null), fileSystem, new InMemoryClipboardWriter(), tagger);

        service.PruneOldFiles();

        Assert.False(fileSystem.Exists(expired));
        Assert.True(fileSystem.Deleted(expired));
    }

    [Fact]
    public void Prune_KeepsRecentTaggedFile()
    {
        var fileSystem = new InMemoryFileSystem();
        var fresh = Path.Combine(Folder, "new.txt");
        fileSystem.SeedExisting(fresh);
        var tagger = TaggerWith((fresh, FixedClock.AddDays(-1)));
        var service = Service(new ClipboardSnapshot(null, null, null), fileSystem, new InMemoryClipboardWriter(), tagger);

        service.PruneOldFiles();

        Assert.True(fileSystem.Exists(fresh));
        Assert.False(fileSystem.Deleted(fresh));
    }

    [Fact]
    public void Prune_KeepsUntaggedForeignFile()
    {
        var fileSystem = new InMemoryFileSystem();
        var foreign = Path.Combine(Folder, "user-keepsake.txt");
        fileSystem.SeedExisting(foreign); // present in the folder but never tagged → not one of ours
        var service = Service(new ClipboardSnapshot(null, null, null), fileSystem, new InMemoryClipboardWriter(), new InMemoryFileTagger());

        service.PruneOldFiles();

        Assert.True(fileSystem.Exists(foreign));
    }

    [Fact]
    public void Prune_BoundaryExactlyAtCutoff_Kept()
    {
        var fileSystem = new InMemoryFileSystem();
        var boundary = Path.Combine(Folder, "boundary.txt");
        fileSystem.SeedExisting(boundary);
        var tagger = TaggerWith((boundary, FixedClock.AddDays(-7))); // == cutoff; strict < keeps it
        var service = Service(new ClipboardSnapshot(null, null, null), fileSystem, new InMemoryClipboardWriter(), tagger);

        service.PruneOldFiles();

        Assert.True(fileSystem.Exists(boundary));
    }

    [Fact]
    public void Prune_EmptyOrMissingFolder_NoOp()
    {
        var fileSystem = new InMemoryFileSystem(); // nothing in the folder
        var service = Service(new ClipboardSnapshot(null, null, null), fileSystem, new InMemoryClipboardWriter(), new InMemoryFileTagger());

        var exception = Record.Exception(() => service.PruneOldFiles());

        Assert.Null(exception);
    }

    [Fact]
    public void Prune_DeleteFailureOnOneFile_StillPrunesOthers()
    {
        var fileSystem = new InMemoryFileSystem();
        var locked = Path.Combine(Folder, "locked.txt");
        var other = Path.Combine(Folder, "other.txt");
        fileSystem.SeedExisting(locked);
        fileSystem.SeedExisting(other);
        fileSystem.FailDeleteOf(locked);
        var tagger = TaggerWith((locked, FixedClock.AddDays(-10)), (other, FixedClock.AddDays(-10)));
        var service = Service(new ClipboardSnapshot(null, null, null), fileSystem, new InMemoryClipboardWriter(), tagger);

        var exception = Record.Exception(() => service.PruneOldFiles());

        Assert.Null(exception);                    // a single delete failure never escapes
        Assert.True(fileSystem.Exists(locked));    // its delete threw and was swallowed
        Assert.False(fileSystem.Exists(other));    // the sweep continued past it
    }

    [Fact]
    public void Save_TriggersPrune()
    {
        var fileSystem = new InMemoryFileSystem();
        var writer = new InMemoryClipboardWriter();
        var expired = Path.Combine(Folder, "old.txt");
        fileSystem.SeedExisting(expired);
        var tagger = TaggerWith((expired, FixedClock.AddDays(-10)));
        var service = Service(new ClipboardSnapshot(null, "hello", null), fileSystem, writer, tagger);

        var result = service.Save();

        var saved = Path.Combine(Folder, "clip-2026-06-25_13.30.45.txt");
        Assert.Equal<SaveResult>(new SaveResult.Saved(saved), result); // the save still completes
        Assert.Equal(Prefix + saved, writer.LastText);                    // and puts its @-ref back
        Assert.False(fileSystem.Exists(expired));                      // prune ran after the save
        Assert.True(fileSystem.Exists(saved));                         // the just-saved file survives
    }
}
