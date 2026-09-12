# ClipRef for Windows

**Turn your clipboard into a file you can hand to [Claude Code](https://claude.com/claude-code).**

Copy a log or a screenshot, click the tray icon, and ClipRef saves it to a file and puts an
`§`-reference on your clipboard. Paste that into Claude Code and it reads the file only when it
actually needs to — so giant logs and images stay out of your context until they matter.

This is the Windows port of [ClipRef](../README.md) — same idea, native to the Windows system tray.

## Install

Requires Windows 10 or 11 (64-bit). ClipRef is a single self-contained `ClipRef.exe` (~68 MB) —
there's no .NET runtime to install, and nothing to set up. Drop the exe anywhere and run it.

There's no prebuilt download yet, so build it once from source (a couple of commands). The result
is a portable exe you can move wherever you like.

<details>
<summary>Build from source</summary>

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (a newer SDK building the
`net8.0` target works too):

```powershell
git clone https://github.com/Manuel-Welsch/ClipRef.git
cd ClipRef

# build and run the test suite
dotnet test windows/ClipRef.sln

# produce the single-file, self-contained exe
dotnet publish windows/ClipRef/ClipRef.csproj -p:PublishProfile=win-x64
```

The published app lands at:

```
windows\ClipRef\bin\Release\net8.0-windows\win-x64\publish\ClipRef.exe
```

Double-click it (or run it from a terminal) to start the tray.

</details>

## Using ClipRef

ClipRef sits in your **system tray** (a clipboard icon, down by the clock) — no window, no taskbar
button.

1. Copy anything — a log, an error message, a screenshot.
2. **Left-click** the icon. ClipRef saves whatever's on the clipboard — text as a `.txt`, an image
   as a `.png`, or **any file you copied** (PDF, zip, …) kept under its original name — and flashes a
   checkmark with a sound. Your clipboard now holds a `§`-path to that file.
3. Switch to Claude Code and press **Ctrl+V**. The pasted `§…` path tells Claude where to look.

### Example

Your app crashes. You select the whole stack trace and **Ctrl+C**, then left-click the ClipRef icon —
a checkmark flashes and your clipboard is now:

```
§C:\Users\you\Developer\clipboard-logs\clip-2026-06-02_14.03.12.txt
```

Over in Claude Code you type **`why is this crashing?`**, press **Ctrl+V**, and hit Enter. Claude
pulls the full trace from the file — without hundreds of lines flooding the conversation.

## Tray menu (right-click)

Right-click the icon for the menu:

- **Saves → C:\Users\you\Developer\clipboard-logs** — a non-clickable header showing where files
  currently go.
- **Save Clipboard Now** — same as a left-click.
- **Open Folder** — opens the destination folder in Explorer (creating it first if needed).
- **Change Folder…** — pick a different destination folder.
- **Launch at Login** — toggle starting ClipRef when you sign in; the checkmark shows the current
  state.
- **Quit** — removes the icon and exits.

## Headless mode (`--save-once`)

Run the exe with `--save-once` to save the clipboard **once and exit immediately**, without starting
the tray:

```powershell
ClipRef.exe --save-once
```

It saves the clipboard exactly as a click would — the file is written and the `§`-reference is placed
on your clipboard — then prints the **saved file's path** to standard output and exits `0`. If there's
nothing to save, it writes a short message to standard error and exits non-zero. It works even while
the tray app is already running, so it's handy for scripts, hotkeys, or other automation. The flag is
exact and case-sensitive — any other spelling just launches the tray as usual.

## Good to know

- **Where files go:** `%USERPROFILE%\Developer\clipboard-logs` by default — change it from the menu
  (**Change Folder…**).
- **One folder to grant, not your whole disk.** Claude Code can only read the folders you give it
  access to — so a single clipboard folder is the one path you ever have to share, instead of opening
  up your real project directories. It only fills when you click, and cleans itself up.
- **Any file works:** copy a file in Explorer (PDF, image, zip, …) and ClipRef copies it into that
  folder under its **original name** (`report.pdf`), with the `§`-path ready to paste. Copy a second
  file of the same name and it becomes `report 2.pdf`. (Folders are skipped — ClipRef saves files,
  not directories.)
- **Big files are skipped:** anything over 100 MB isn't copied, so a giant video or disk image can't
  freeze the menu while it copies.
- **Self-cleaning, and only after itself:** files ClipRef saved are deleted automatically after
  7 days, so the folder never piles up — but it *only* removes files it created (tagged with an NTFS
  Alternate Data Stream), so anything else you keep in that folder is left untouched. Change the
  window by setting `retentionDays` in `%APPDATA%\ClipRef\settings.json`. (The tag is NTFS-only; on
  other filesystems it simply isn't written, and those files are never auto-pruned.)
- **Why `§` and not `@`:** in Claude Code and opencode, `@` opens the file-mention autocomplete, and
  a mention that resolves pulls the whole file into the conversation right away — the opposite of
  what ClipRef is for. A `§`-path is plain text: Claude reads it only when it decides it needs the
  contents. Prefer the old marker? Set `referencePrefix` to `@` in
  `%APPDATA%\ClipRef\settings.json`, then **restart ClipRef** — the file is read at startup. Any
  string works; blank falls back to `§`.
- **Double-clicking is safe:** if your clipboard already holds a `§`- or `@`-reference to a Windows
  path, ClipRef leaves it alone instead of saving the reference into a new file. Both markers are
  recognised, so a reference an older build left on your clipboard is still safe. This guard knows
  those two only — set `referencePrefix` to something else and a double-click will save your own
  reference as a new text file. Harmless, just untidy.
- **Starts with Windows:** ClipRef adds itself to launch-at-login the first time you run it (a
  per-user `Run` registry entry). Turn it off anytime from the menu — it remembers your choice.

## License

[MIT](../LICENSE) © 2026 Manuel Welsch
