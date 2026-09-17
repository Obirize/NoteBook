# NoteBook (Notlar)

An Apple Notes–style notebook for Windows: local, dark, and encrypted at rest. Built with C# / WPF on .NET 10. No browser, no account, no internet required.

[![Latest release](https://img.shields.io/github/v/release/Obirize/NoteBook?label=download&color=e7bb62)](https://github.com/Obirize/NoteBook/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/Obirize/NoteBook/total?color=333337)](https://github.com/Obirize/NoteBook/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-333337)](LICENSE)
![Windows 10/11](https://img.shields.io/badge/Windows-10%20%7C%2011-333337)

**[⬇ Download the latest installer](https://github.com/Obirize/NoteBook/releases/latest)** · [Türkçe](README.tr.md)

![Notes](docs/screenshot.png)

## Highlights

- Notes list on the left, title and text on the right. Search, pin, and a 30-day "Recently deleted" folder.
- Autosave 650 ms after you stop typing; also on note switch and on close.
- Everything is encrypted with AES-256-GCM. The content key is protected by Windows DPAPI for the current user, so the app never asks for a password.
- Smooth pixel-based wheel scrolling in both the list and the editor, following the Windows "lines per notch" setting.
- Multi-select: delete, restore or permanently delete many notes at once. Permanent deletion always asks first.
- Import `.txt` files as notes (open, or drag and drop onto the window), export any note as UTF-8 `.txt` named after its title.
- Photos and videos on a note: the paperclip button, drag and drop, or Ctrl+V with a picture on the clipboard. Thumbnails above the text; click one for a full-size viewer with video playback. Every file is stored encrypted with its own key.
- Context menus, keyboard shortcuts, custom dark chrome, themed scrollbars.
- Single instance: opening a file or a new note while the app runs is forwarded to the open window.
- Automatic updates from GitHub Releases (the only network request the app makes).
- 13 languages: English, Türkçe, Español, 中文, हिन्दी, العربية (right-to-left), Português, Русский, 日本語, Deutsch, Français, Bahasa Indonesia, 한국어. The app follows the Windows display language; the globe button in the sidebar switches it.

<p align="center"><img src="docs/screenshot-select.png" width="49%" alt="Multi-select"> <img src="docs/screenshot-arabic.png" width="49%" alt="Arabic, right-to-left"></p>

## Install

Download `NoteBook-Setup-<version>.exe` from [Releases](https://github.com/Obirize/NoteBook/releases). The installer is per-user: no administrator rights, installs to `%LocalAppData%\Programs\Notlar`, keeps your notes in `…\Notlar\data` and never deletes them on uninstall.

Optional integrations offered by the installer:

- **"Open with" for `.txt` and a Default Apps entry.** Windows Notepad is left untouched; NoteBook is added next to it. Windows only lets the *user* pick a file type's default, so the last wizard page offers to open *Settings → Default apps*, where you choose `.txt → Notlar` once.
- **Right-click → "New note (Notlar)"** on the desktop and in folder backgrounds. It opens a new note directly; no file is created. Windows' own "New → Text Document" stays as it is.

The installer is not code-signed yet, so the browser download may show a SmartScreen prompt on first run ("More info → Run anyway"). In-app updates do not show it.

Portable use also works: unzip, keep `Not Defteri.exe` next to the `app` folder, notes live in `data` beside them.

## Shortcuts

| Shortcut | Action |
| --- | --- |
| Ctrl+N | New note |
| Ctrl+O | Open a TXT file as a new note |
| Ctrl+Shift+S | Save the open note as an unencrypted TXT copy |
| Ctrl+Shift+A | Add photos or videos to the open note |
| Ctrl+V (picture or media files on the clipboard) | Attach it to the open note |
| Ctrl+K / Ctrl+F | Search |
| Ctrl+S | Save now / retry a failed save |
| Ctrl+Z / Ctrl+Y | Undo / redo text edits |
| Delete (outside a text box) | Delete the open or checked notes (to Recently deleted); in Recently deleted, permanently delete (asks first) |
| Shift+Delete | Delete permanently (asks first) |
| Ctrl+A (select mode) | Select all |
| Esc (select mode) | Leave select mode |

## How encryption works

Note text, titles, dates and imported legacy files are encrypted with AES-256-GCM. A random 256-bit content key is wrapped with Windows DPAPI (`CurrentUser`) and stored in the vault; it is never written in the clear, and every save uses a fresh nonce. Any modification of the content or the key fails authentication.

Photos and videos are not put into the vault file. Each attachment is written to `data/attachments/<id>.bin`, encrypted in 1 MB chunks with AES-256-GCM under its own random 256-bit key; the key, name, size and hash live only inside the encrypted notebook. Every chunk authenticates the file header, the attachment id, its index and a "last chunk" flag, so chunks cannot be reordered, dropped, truncated or moved between files. Because the file never has to be re-encrypted, a backup (or, later, another device) receives it byte for byte. Playing a video hands the player a decrypted copy in the temp folder that is deleted when the viewer closes.

There is no separate application password, setup screen or idle lock: the normal Windows sign-in protects your notes. The goal is that **someone who copies the vault file alone cannot read it**. A program running under the same Windows account, or a person at your unlocked session, can open the notes. This is not a defense against malware with administrator rights, keyloggers or memory dumps.

References: [DataProtectionScope](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.dataprotectionscope), [AesGcm](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm). No telemetry. Not yet independently audited.

## Files, backup and recovery

- `data/notes.vault` — the encrypted notebook.
- `data/notes.vault.bak` — the previous encrypted save.
- `data/attachments/` — encrypted photos and videos, one file per attachment.

**Backup** (the box icon in the sidebar) → *Create backup…* writes a single `.vault` file protected by a password you choose (PBKDF2 600k + AES-256-GCM, with its own random key). It opens on any PC with that password — it does not depend on your Windows account. *Restore from backup…* merges a backup into your notebook: notes missing locally are added, the newer revision of each note wins, and nothing is ever removed. Restoring also accepts a plain copy of `notes.vault` from the same Windows account. When the notebook has attachments, the backup gets a `<name>.vault.files` folder beside it with the encrypted files; keep the two together. Restoring copies the files it lacks.

If the main file cannot be opened, the app offers to try the previous save; damaged files are kept as `.damaged-…`, never silently replaced with empty notes.

Notes moved to Recently deleted are purged after 30 days (each card shows the remaining time). "Delete permanently" removes a note from the vault, the rolling backup and any imported legacy archive at once, together with its attachment files.

## Opening TXT files

`Not Defteri.exe file.txt` imports the file as a new note; `Not Defteri.exe --new` opens a fresh note. If the app is already running, both are forwarded to the open window. NoteBook is not a file editor: the TXT is imported, edits live in the vault, and the original file is never modified. Use export to write a TXT back out.

## Automatic updates

On startup the app asks the GitHub Releases API for the latest version. If a newer installer exists, a **"Version x.y.z ready · Update"** button appears in the status bar. Clicking it downloads the installer, verifies its SHA-256 against the published checksum, installs silently and relaunches the app. Your notes stay in place.

This is the only network request the application makes; it carries no identifying data. To opt out, create an empty file named `guncelleme-kapali` in the `data` folder.

## Languages

UI strings live in `src/Notlar/Languages/<code>.json` and are embedded at build time; English is the fallback for any missing key. To add a language, copy `en.json`, translate the values (keep the `{0}` placeholders), and add one line to `L10n.Languages` in `src/Notlar/L10n.cs` with the native name and culture. The test run verifies that every language has every key. The installer wizard is localized through Inno Setup's language files (`setup/Notlar.iss`).

## Development

Requires the .NET 10 SDK and Windows. Source is in `src/Notlar/`.

- `build.cmd` — publishes a self-contained x64 build to `app/` and compiles the root launcher.
- `test.cmd` — runs the test program with temporary data: encryption, tamper detection, migration, save failures, autosave, search, trash policy, bulk actions, smooth scrolling and UI layout. Screenshots land in `artifacts/`.
- `build-setup.cmd` — builds the installer with [Inno Setup 6](https://jrsoftware.org/isdl.php) into `dist/`.

### Releasing

Push a version tag and the workflow in `.github/workflows/release.yml` builds the installer on Windows, writes its `.sha256`, and attaches both to a GitHub Release. Running apps pick it up on their next start.

```bash
git tag v1.0.1
git push --tags
```

## Roadmap

- Phone access: first a PWA that decrypts the vault in the browser (WebCrypto), later native iOS/Android apps with two-way sync. The data model (note ids, revisions, deletion timestamps) is already prepared for it.
- Code signing for the installer.

## License

MIT — see [LICENSE](LICENSE).
