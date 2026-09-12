using System.Globalization;

namespace ClipRef;

/// <summary>
/// Typed view over an <see cref="ISettingsStore"/>, mirroring the macOS reference's
/// <c>UserDefaults</c> surface: the log-folder path, the retention window, and the
/// login-item flag, plus the resolved destination folder. Setters write through to the
/// store immediately. All defaulting and parsing live here so they stay unit-testable.
/// </summary>
internal sealed class Settings
{
    private readonly ISettingsStore _store;

    internal Settings(ISettingsStore store)
    {
        _store = store;
    }

    /// <summary>The user-chosen folder to save into; empty when never set (use <see cref="FolderPath"/>).</summary>
    internal string LogFolderPath
    {
        get => _store.Get(Const.LogFolderPathKey) ?? string.Empty;
        set => _store.Set(Const.LogFolderPathKey, value);
    }

    /// <summary>Days to keep saved files; a missing, unparseable, or non-positive value falls back to 7.</summary>
    internal int RetentionDays
    {
        get => int.TryParse(_store.Get(Const.RetentionDaysKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days > 0
            ? days
            : Const.DefaultRetentionDays;
        set => _store.Set(Const.RetentionDaysKey, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The marker put in front of the saved path on the clipboard; a missing, empty, or
    /// whitespace-only value falls back to <c>§</c>. Stored values are trimmed: a prefix carrying
    /// whitespace would produce a reference that <see cref="ClipboardSaver.LooksLikeReference"/>
    /// rejects by design, silently breaking the double-click guard.
    /// </summary>
    internal string ReferencePrefix
    {
        get
        {
            var stored = (_store.Get(Const.ReferencePrefixKey) ?? string.Empty).Trim();
            return stored.Length > 0 ? stored : Const.DefaultReferencePrefix;
        }
        set => _store.Set(Const.ReferencePrefixKey, value);
    }

    /// <summary>Whether the launch-at-login item has been configured; missing or unparseable reads as false.</summary>
    internal bool DidConfigureLoginItem
    {
        get => bool.TryParse(_store.Get(Const.DidConfigureLoginItemKey), out var configured) && configured;
        set => _store.Set(Const.DidConfigureLoginItemKey, value.ToString());
    }

    /// <summary>
    /// The folder files are actually written to: the stored <see cref="LogFolderPath"/> when set,
    /// otherwise the default <c>%USERPROFILE%\Developer\clipboard-logs</c>. Stored paths are taken
    /// as-is (absolute on Windows); no <c>~</c> expansion.
    /// </summary>
    internal string FolderPath
    {
        get
        {
            var stored = LogFolderPath;
            return stored.Length > 0 ? stored : DefaultFolderPath;
        }
    }

    private static string DefaultFolderPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Developer", "clipboard-logs");

    /// <summary>Persistence keys and defaults mirrored from the macOS reference.</summary>
    private static class Const
    {
        internal const string LogFolderPathKey = "logFolderPath";
        internal const string RetentionDaysKey = "retentionDays";
        internal const string DidConfigureLoginItemKey = "didConfigureLoginItem";
        internal const string ReferencePrefixKey = "referencePrefix";
        internal const int DefaultRetentionDays = 7;

        // "§" rather than the macOS reference's "@": in Claude Code and opencode "@" opens the
        // file-mention autocomplete, and a resolved mention pulls the file into context at once —
        // the opposite of what ClipRef is for.
        internal const string DefaultReferencePrefix = "§";
    }
}
