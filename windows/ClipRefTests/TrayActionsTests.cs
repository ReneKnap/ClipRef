using ClipRef;

namespace ClipRefTests;

/// <summary>
/// Tests the WinForms-free tray controller <see cref="TrayActions"/> — the unit-tested boundary of
/// the otherwise integration-only menu host (ADR-0009). It drives the three menu actions over the
/// same in-memory doubles as <see cref="ClipboardSaveServiceTests"/>, plus a fake folder launcher and
/// picker, with no <see cref="System.Windows.Forms.NotifyIcon"/> and no modal dialog. The deep
/// save/prune behavior is pinned in <see cref="ClipboardSaveServiceTests"/>; here we verify the menu
/// wiring: open creates-then-opens, change persists only a real selection (and the shared
/// <see cref="Settings"/> reaches the service), and save-now delegates to <see cref="ClipboardSaveService.Save"/>.
/// </summary>
public class TrayActionsTests
{
    private const string Folder = @"C:\logs";

    /// <summary>The default reference prefix the put-back assertions below expect.</summary>
    private const string Prefix = "§";

    private static readonly DateTime FixedClock = new(2026, 6, 25, 13, 30, 45);

    private sealed record Harness(
        TrayActions Actions,
        ClipboardSaveService Service,
        InMemoryFileSystem FileSystem,
        InMemoryClipboardWriter Writer,
        Settings Settings,
        FakeFolderLauncher Launcher,
        FakeFolderPicker Picker,
        FakeSaveFeedback Feedback);

    /// <summary>
    /// Wires <see cref="TrayActions"/> over one shared <see cref="Settings"/> and
    /// <see cref="InMemoryFileSystem"/> — the same instances the service uses — so a folder change
    /// made through the controller is visible to a subsequent <see cref="ClipboardSaveService.Save"/>.
    /// </summary>
    private static Harness BuildHarness(
        ClipboardSnapshot? clipboard = null,
        InMemoryFileSystem? fileSystem = null,
        FakeFolderPicker? picker = null,
        string folder = Folder)
    {
        fileSystem ??= new InMemoryFileSystem();
        picker ??= new FakeFolderPicker(null);
        var launcher = new FakeFolderLauncher();
        var feedback = new FakeSaveFeedback();
        var settings = new Settings(new InMemorySettingsStore(("logFolderPath", folder)));
        var writer = new InMemoryClipboardWriter();
        var service = new ClipboardSaveService(
            new InMemoryClipboardReader(clipboard ?? new ClipboardSnapshot(null, null, null)),
            // UTC display zone keeps the exact clip-<timestamp> filenames deterministic regardless of
            // the test runner's local timezone (see ClipboardSaveServiceTests).
            writer, fileSystem, new InMemoryFileTagger(), settings, () => FixedClock, TimeZoneInfo.Utc);
        var actions = new TrayActions(service, settings, fileSystem, launcher, picker, feedback);
        return new Harness(actions, service, fileSystem, writer, settings, launcher, picker, feedback);
    }

    [Fact]
    public void OpenFolder_CreatesDestinationFolderThenOpensIt()
    {
        var harness = BuildHarness();

        harness.Actions.OpenFolder();

        Assert.Contains(Folder, harness.FileSystem.CreatedDirectories); // created first (parity with makeDestinationFolder)
        Assert.True(harness.Launcher.WasOpened);
        Assert.Equal(Folder, harness.Launcher.Opened);                  // then opened that same folder
    }

    [Fact]
    public void OpenFolder_StillOpensWhenFolderCreateFails()
    {
        var fileSystem = new InMemoryFileSystem { ThrowOnCreateDirectory = true };
        var harness = BuildHarness(fileSystem: fileSystem);

        var exception = Record.Exception(() => harness.Actions.OpenFolder());

        Assert.Null(exception);                       // best-effort create never escapes
        Assert.True(harness.Launcher.WasOpened);       // and the folder is still opened
        Assert.Equal(Folder, harness.Launcher.Opened);
    }

    [Fact]
    public void ChangeFolder_PersistsChosenFolder()
    {
        const string picked = @"C:\picked";
        var harness = BuildHarness(
            clipboard: new ClipboardSnapshot(null, "hello", null),
            picker: new FakeFolderPicker(picked));

        harness.Actions.ChangeFolder();

        Assert.Equal(Folder, harness.Picker.SeededWith);     // picker seeded with the prior folder
        Assert.Equal(picked, harness.Actions.CurrentFolder); // controller reflects the new folder
        Assert.Equal(picked, harness.Settings.FolderPath);   // persisted to settings

        // The shared Settings reached the service: the next save lands in the new folder.
        var result = harness.Service.Save();
        var expected = Path.Combine(picked, "clip-2026-06-25_13.30.45.txt");
        Assert.Equal<SaveResult>(new SaveResult.Saved(expected), result);
        Assert.Equal(Prefix + expected, harness.Writer.LastText);
    }

    [Fact]
    public void ChangeFolder_Cancelled_LeavesFolderUnchanged()
    {
        var harness = BuildHarness(picker: new FakeFolderPicker(null)); // null = user cancelled

        harness.Actions.ChangeFolder();

        Assert.Equal(Folder, harness.Picker.SeededWith);
        Assert.Equal(Folder, harness.Actions.CurrentFolder); // unchanged
        Assert.Equal(Folder, harness.Settings.FolderPath);   // not overwritten with an empty path
    }

    [Fact]
    public void SaveNow_SavesClipboardIntoConfiguredFolder()
    {
        var harness = BuildHarness(clipboard: new ClipboardSnapshot(null, "hello world", null));

        harness.Actions.SaveNow();

        var expected = Path.Combine(Folder, "clip-2026-06-25_13.30.45.txt");
        Assert.Equal("hello world", harness.FileSystem.TextAt(expected)); // delegated to Save()
        Assert.Equal(Prefix + expected, harness.Writer.LastText);            // @-reference put back
    }

    [Fact]
    public void SaveNow_OnSavedClipboard_PresentsSuccessFeedback()
    {
        var harness = BuildHarness(clipboard: new ClipboardSnapshot(null, "hello world", null));

        harness.Actions.SaveNow();

        var feedback = Assert.Single(harness.Feedback.Presented);
        Assert.Equal(SaveFeedback.IconKind.Success, feedback.Icon);
        Assert.Equal(SaveFeedback.SoundKind.Success, feedback.Sound);
        Assert.Null(feedback.DialogMessage);                           // success raises no dialog
        var expected = Path.Combine(Folder, "clip-2026-06-25_13.30.45.txt");
        Assert.Equal(Prefix + expected, harness.Writer.LastText);         // sanity: the save still happened
    }

    [Fact]
    public void SaveNow_OnEmptyClipboard_PresentsWarningFeedbackNoDialog()
    {
        var harness = BuildHarness(clipboard: new ClipboardSnapshot(null, null, null));

        harness.Actions.SaveNow();

        var feedback = Assert.Single(harness.Feedback.Presented);
        Assert.Equal(SaveFeedback.IconKind.Warning, feedback.Icon);
        Assert.Equal(SaveFeedback.SoundKind.Critical, feedback.Sound); // empty shares the failure sound (macOS parity)
        Assert.Null(feedback.DialogMessage);                          // but no dialog
    }

    [Fact]
    public void SaveNow_OnFailure_PresentsFailureFeedbackWithMessage()
    {
        var fileSystem = new InMemoryFileSystem();
        fileSystem.SetAttributes(@"C:\src\report.pdf", FileAttributes.Directory); // a folder → CopyFile fails
        var harness = BuildHarness(
            clipboard: new ClipboardSnapshot(@"C:\src\report.pdf", null, null),
            fileSystem: fileSystem);

        harness.Actions.SaveNow();

        var feedback = Assert.Single(harness.Feedback.Presented);
        Assert.Equal(SaveFeedback.IconKind.Failure, feedback.Icon);
        Assert.Equal(SaveFeedback.SoundKind.Critical, feedback.Sound);
        Assert.False(string.IsNullOrEmpty(feedback.DialogMessage));    // failure carries the message to a dialog
        Assert.Contains("report.pdf", feedback.DialogMessage);
    }
}
