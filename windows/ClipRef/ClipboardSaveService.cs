namespace ClipRef;

/// <summary>
/// Orchestrates the save action: reads the clipboard, classifies it via
/// <see cref="ClipboardSaver.Decide(ClipboardSnapshot)"/>, writes the payload to a uniquely-named
/// file in the destination folder, stamps it with its NTFS-ADS ownership tag, and puts a
/// <see cref="Settings.ReferencePrefix"/>-prefixed path reference back on the clipboard, then prunes
/// expired tagged files. Ports the
/// write half of the macOS <c>saveClipboard</c> (the <c>write</c> helper) plus its trailing
/// <c>pruneOldFiles</c>. All disk, tag, and clipboard access goes through the injected seams, so the
/// flow is unit-testable; the UTC clock makes the ownership tag and the prune cutoff deterministic,
/// and together with the display timezone the timestamped filename too.
/// </summary>
internal sealed class ClipboardSaveService
{
    private readonly IClipboardReader _reader;
    private readonly IClipboardWriter _clipboardWriter;
    private readonly IFileSystem _fileSystem;
    private readonly IFileTagger _fileTagger;
    private readonly Settings _settings;

    // The absolute save instant, and it MUST be UTC: NtfsFileTagger stores the ownership tag as epoch
    // seconds and prune compares the tag against now − RetentionDays, so both stay on this one basis.
    private readonly Func<DateTime> _clock;

    // The timezone the human-readable clip-<timestamp> filename is rendered in — the machine's local
    // zone in production. Only the cosmetic name uses it; the tag and prune keep the raw UTC _clock
    // instant. Mirrors the macOS original, which formats the filename in the local zone while storing
    // the tag as an absolute epoch (ClipboardSaver.swift timestampFormatter vs tagAsSaved).
    private readonly TimeZoneInfo _displayTimeZone;

    internal ClipboardSaveService(
        IClipboardReader reader,
        IClipboardWriter clipboardWriter,
        IFileSystem fileSystem,
        IFileTagger fileTagger,
        Settings settings,
        Func<DateTime> clock,
        TimeZoneInfo? displayTimeZone = null)
    {
        _reader = reader;
        _clipboardWriter = clipboardWriter;
        _fileSystem = fileSystem;
        _fileTagger = fileTagger;
        _settings = settings;
        _clock = clock;
        _displayTimeZone = displayTimeZone ?? TimeZoneInfo.Local;
    }

    /// <summary>
    /// The current save instant expressed in <see cref="_displayTimeZone"/>, used only to render the
    /// human-readable <c>clip-&lt;timestamp&gt;</c> filename. The ownership tag and prune keep the raw
    /// UTC <see cref="_clock"/> instant, so localizing the name never shifts the absolute basis they
    /// compare on. Requires the clock to be UTC- or unspecified-kind (the documented contract), which
    /// <see cref="TimeZoneInfo.ConvertTimeFromUtc(DateTime, TimeZoneInfo)"/> also enforces.
    /// </summary>
    private DateTime NowInDisplayZone() => TimeZoneInfo.ConvertTimeFromUtc(_clock(), _displayTimeZone);

    /// <summary>
    /// Reads the clipboard once, decides what to save, writes it, and replaces the clipboard with an
    /// <c>@&lt;path&gt;</c> reference. A copied file that is a folder or too large fails and aborts
    /// (no fall-through); an empty or self-referential clipboard is a no-op.
    /// </summary>
    internal SaveResult Save()
    {
        var snapshot = _reader.Read();
        return ClipboardSaver.Decide(snapshot) switch
        {
            SaveDecision.CopyFile copyFile => CopyFile(copyFile.Path),
            SaveDecision.SaveText saveText => Write(
                folder => ClipboardSaver.UniqueGeneratedPath(folder, ClipboardSaver.Const.TextExtension, NowInDisplayZone(), _fileSystem.Exists),
                destination => _fileSystem.WriteAllText(destination, saveText.Text)),
            SaveDecision.SaveImage => SaveImage(snapshot),
            _ => new SaveResult.NothingToSave(),
        };
    }

    private SaveResult CopyFile(string source)
    {
        if (!ClipboardSaver.IsCopyableFile(source, _fileSystem.GetAttributes))
        {
            return new SaveResult.Failure(
                $"ClipRef saves files, not folders — \"{Path.GetFileName(source)}\" is a folder. Copy a file instead.");
        }

        if (!ClipboardSaver.FitsSizeLimit(source, _fileSystem.GetSize))
        {
            return new SaveResult.Failure(
                $"\"{Path.GetFileName(source)}\" is too large to copy (ClipRef's limit is 100 MB). Copy a smaller file.");
        }

        return Write(
            folder => ClipboardSaver.UniquePreferredPath(folder, Path.GetFileName(source), _fileSystem.Exists),
            destination => _fileSystem.CopyFile(source, destination));
    }

    private SaveResult SaveImage(ClipboardSnapshot snapshot)
    {
        if (snapshot.ImagePng is null)
        {
            return new SaveResult.NothingToSave();
        }

        var imagePng = snapshot.ImagePng;
        return Write(
            folder => ClipboardSaver.UniqueGeneratedPath(folder, ClipboardSaver.Const.ImageExtension, NowInDisplayZone(), _fileSystem.Exists),
            destination => _fileSystem.WriteAllBytes(destination, imagePng));
    }

    /// <summary>
    /// The shared write path: create the destination folder, pick the unique destination via
    /// <paramref name="destinationFor"/>, write it via <paramref name="body"/>, stamp it with the
    /// ownership tag, and swap the clipboard for an <c>@&lt;path&gt;</c> reference. The reference is
    /// the path verbatim — no quoting or escaping, even for paths with spaces, matching the macOS
    /// original.
    /// </summary>
    private SaveResult Write(Func<string, string> destinationFor, Action<string> body)
    {
        var folder = _settings.FolderPath;
        try
        {
            _fileSystem.CreateDirectory(folder);
        }
        catch (Exception exception)
        {
            return new SaveResult.Failure("Could not create folder:\n" + exception.Message);
        }

        var destination = destinationFor(folder);
        try
        {
            body(destination);
        }
        catch (Exception exception)
        {
            return new SaveResult.Failure("Could not write file:\n" + exception.Message);
        }

        try
        {
            _fileTagger.TagAsSaved(destination, _clock());
        }
        catch (Exception)
        {
            // Best-effort: a failed ownership tag must not deny the user their reference (parity
            // with the macOS original discarding setxattr's result). Prune simply won't recognise
            // an untagged file as ours and will leave it alone.
        }

        _clipboardWriter.SetText(_settings.ReferencePrefix + destination);

        // Self-cleaning runs on every successful save (macOS saveClipboard line 185): the content is
        // already safely on disk and the reference is back on the clipboard. Best-effort and never
        // throws, so it cannot turn a completed save into a failure.
        PruneOldFiles();
        return new SaveResult.Saved(destination);
    }

    /// <summary>
    /// Deletes saved files older than <see cref="Settings.RetentionDays"/>, trusting the ownership tag
    /// alone: a file goes only when <see cref="IFileTagger.SavedDate"/> returns an instant strictly
    /// older than the cutoff (<c>now − retentionDays</c>). Untagged or unparseable files — anything the
    /// user keeps in the folder — are never touched, and filesystem timestamps are never consulted
    /// (ports macOS <c>pruneOldFiles</c>). Best-effort throughout: a missing/unreadable folder
    /// enumerates empty and a single file that won't delete is skipped, so the sweep always finishes
    /// and never throws.
    /// </summary>
    internal void PruneOldFiles()
    {
        // The cutoff and the tag are compared by absolute instant; NtfsFileTagger stores the tag as
        // UTC, so Phase 3 must inject a UTC clock (() => DateTime.UtcNow) for the two to share a basis.
        var cutoff = _clock() - TimeSpan.FromDays(_settings.RetentionDays);
        foreach (var path in _fileSystem.EnumerateFiles(_settings.FolderPath))
        {
            try
            {
                if (_fileTagger.SavedDate(path) is { } savedAt && savedAt < cutoff)
                {
                    _fileSystem.DeleteFile(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Best-effort per file: a locked or permission-denied file is skipped so the rest of
                // the sweep still runs (parity with macOS try? on removeItem).
            }
        }
    }
}
