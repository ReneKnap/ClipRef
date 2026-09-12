# ClipRef

**Turn your clipboard into a file you can hand to [Claude Code](https://claude.com/claude-code).**

Copy a log or a screenshot, click the menu-bar icon, and ClipRef saves it to a file and
puts a `§`-reference on your clipboard. Paste that into Claude Code and it reads the
file only when it actually needs to — so giant logs and images stay out of your context
until they matter.

**Without ClipRef**, pasting a 2,500-line log straight into Claude Code floods your context:

![A 2,500-line log flooding the Claude Code composer](docs/before.png)

**With ClipRef**, one click turns it into a single `§`-reference Claude reads only when it needs the contents:

![The same paste, now a one-line reference](docs/paste.png)

## Install

> **On Windows?** This is the macOS app — for the Windows port see [`windows/README.md`](windows/README.md).

Requires macOS 14 (Sonoma) or later. Install with [Homebrew](https://brew.sh):

```sh
brew install --cask manuel-welsch/tap/clipref
```

Or download `ClipRef.app` from the [Releases page](https://github.com/Manuel-Welsch/ClipRef/releases) and drag it to Applications.

<details>
<summary>Build from source</summary>

macOS 14+ and Xcode 16+:

```sh
git clone https://github.com/Manuel-Welsch/ClipRef.git
cd ClipRef
xcodebuild -project ClipRef.xcodeproj -scheme ClipRef \
  -configuration Release -derivedDataPath build -allowProvisioningUpdates build
ditto build/Build/Products/Release/ClipRef.app /Applications/ClipRef.app
open /Applications/ClipRef.app
```

If signing fails, open `ClipRef.xcodeproj` in Xcode and pick your own team under
*Signing & Capabilities*.

</details>

## Using ClipRef

ClipRef sits in your **menu bar** (a clipboard icon) — no Dock icon, no window.

1. Copy anything — a log, an error message, a screenshot.
2. **Left-click** the icon. ClipRef saves whatever's on the clipboard — text as `.txt`, an
   image as `.png`, or **any file you copied** (PDF, zip, …) kept under its original name —
   and flashes a checkmark. Your clipboard now holds a `§`-path to that file.
3. Switch to Claude Code and press **⌘V**. The pasted `§…` path tells Claude where to look.

![The pasted reference in Claude Code](docs/paste.png)

### Example

Your app crashes. You select the whole stack trace and ⌘C, then click the ClipRef icon —
a checkmark flashes and your clipboard is now:

```
§/Users/you/Developer/clipboard-logs/clip-2026-06-02_14.03.12.txt
```

Over in Claude Code you type **`why is this crashing?`**, press **⌘V**, and hit return.
Claude pulls the full trace from the file — without 300 lines flooding the conversation.

### Menu (right-click)

![ClipRef's menu-bar menu](docs/menu.png)

## Good to know

- **Where files go:** `~/Developer/clipboard-logs` by default — change it from the menu.
- **One folder to grant, not your whole disk.** Claude Code can only read the folders you give it access to — so a single clipboard folder is the one path you ever have to share, instead of opening up your real project directories. It only fills when you click, and cleans itself up.
- **Any file works:** copy a file in Finder (PDF, image, zip, …) and ClipRef copies it into that folder under its **original name** (`report.pdf`), with the `§`-path ready to paste. Copy a second file of the same name and it becomes `report 2.pdf`, Finder-style. (Folders and `.app` bundles are skipped — ClipRef saves files, not directories.)
- **Big files are skipped:** anything over 100 MB isn't copied (a quick alert says so), so a giant video or disk image can't freeze the menu while it copies.
- **Self-cleaning, and only after itself:** files ClipRef saved are deleted automatically after 7 days, so the folder never piles up — but it *only* removes files it created, so anything else you keep in that folder is left untouched. (`defaults write de.manuelwelsch.ClipRef retentionDays 14` keeps them longer.)
- **Why `§` and not `@`:** in Claude Code and opencode, `@` opens the file-mention autocomplete, and a mention that resolves pulls the whole file into the conversation right away — the opposite of what ClipRef is for. A `§`-path is plain text: Claude reads it only when it decides it needs the contents. Prefer the old marker? `defaults write de.manuelwelsch.ClipRef referencePrefix "@"`, then restart ClipRef so it picks the value up. Any string works; blank falls back to `§`.
- **Double-clicking is safe:** if your clipboard already holds a `§`- or `@`-reference, ClipRef
  leaves it alone instead of saving the reference into a new file. Both markers are recognised, so a
  reference an older build left on your clipboard is still safe. The guard knows those two only — set
  `referencePrefix` to something else and a double-click will save your own reference as a new text
  file. Harmless, just untidy.

## License

[MIT](LICENSE) © 2026 Manuel Welsch
