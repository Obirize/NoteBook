# NoteBook (Notlar)

An Apple Notes–style notebook for Windows with a matching iPhone app that syncs over your own Wi‑Fi. Everything is encrypted, nothing leaves your devices, and there is no account, no cloud and nothing to pay for.

[![Latest release](https://img.shields.io/github/v/release/Obirize/NoteBook?label=download&color=e7bb62)](https://github.com/Obirize/NoteBook/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/Obirize/NoteBook/total?color=333337)](https://github.com/Obirize/NoteBook/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-333337)](LICENSE)
![Windows 10/11](https://img.shields.io/badge/Windows-10%20%7C%2011-333337)
![iPhone](https://img.shields.io/badge/iPhone-Safari%20%E2%86%92%20Home%20Screen-333337)

**[⬇ Download the latest installer](https://github.com/Obirize/NoteBook/releases/latest)** · [Türkçe](README.tr.md)

![NoteBook on Windows](docs/screenshot.png)

## What it does

- **Notes** with a title, text, checklists, photos and videos. Search, pin, multi-select, a 30‑day *Recently deleted* folder, TXT import and export, 13 interface languages on both the PC and the phone.
- **Encrypted at rest.** Notes are stored in a vault sealed with AES‑256‑GCM; every photo and video is a separate file encrypted with its own key. On the PC the keys are protected by your Windows sign‑in, so the app never asks for a password.
- **iPhone sync without a server.** The Windows app itself serves a small web app to your phone; you add it to the Home Screen and pair with a six‑digit code. Notes and files then sync directly between the two devices whenever both are on the same Wi‑Fi. The phone app looks like Apple Notes and works offline.
- **Runs in the background.** Closing the window keeps NoteBook in the notification area so the phone can sync; the installer can start it with Windows.
- **Backups you own.** A password‑protected backup opens on any PC; restoring merges instead of overwriting.
- **Updates from GitHub Releases** — the only thing the app ever talks to outside your network.

<p align="center"><img src="docs/screenshot-select.png" width="49%" alt="Multi-select"> <img src="docs/screenshot-phone-sync.png" width="49%" alt="Phone sync window"></p>

## Install

Download `NoteBook-Setup-<version>.exe` from [Releases](https://github.com/Obirize/NoteBook/releases). The installer is per‑user (no administrator rights), installs to `%LocalAppData%\Programs\NoteBook`, keeps your notes in `…\NoteBook\data` and never deletes them on uninstall. It offers a desktop shortcut, *Open with* for `.txt` files, a *New note* entry in the right‑click menu and *Start with Windows*.

The installer is not code‑signed yet, so the first download may show a SmartScreen prompt ("More info → Run anyway"). In‑app updates do not.

Portable use also works: unzip, keep `Not Defteri.exe` next to the `app` folder; notes live in `data` beside them.

## Phone (iPhone)

Open the phone window with the phone button at the bottom left of the sidebar. It walks through three steps, each with a QR code:

1. **Trust certificate.** The PC is its own certificate authority. The first code opens a page that downloads a profile; install it under *Settings → General → VPN & Device Management*, then switch it on under *Settings → General → About → Certificate Trust Settings*. The page has a *Test the connection* button that tells you when this is done. The fingerprint is shown on both sides so you can compare.
2. **Home Screen app.** The second code opens the app in Safari, which explains the two taps: *Share → Add to Home Screen*. From then on, open NoteBook from the Home Screen (iOS gives Home Screen apps their own storage).
3. **Pairing code.** In the Home Screen app tap *Pair* and type the six‑digit code shown on the PC. A new code appears every minute and codes only exist while that window is open.

Afterwards the phone syncs whenever it is on the home Wi‑Fi and the PC app is running (even hidden in the tray). Away from home the phone keeps working offline and merges when it is back. Photos and videos are never re‑encoded; the app asks iOS for the originals. In a note they sit in a grid of tiles; tapping one opens a viewer with *save or share* (the share sheet's *Save Image / Save Video* puts it in Photos) and *remove*, and the note's *…* menu can save all attachments at once. On the PC, *Save all attachments…* (right-click a note card or an attachment) writes every file of a note, or of all checked notes, into a folder.

## Shortcuts (Windows)

| Shortcut | Action |
| --- | --- |
| Ctrl+N | New note |
| Ctrl+O | Open a TXT file as a new note |
| Ctrl+Shift+A | Add photos or videos to the open note |
| Ctrl+Shift+L | Turn the current line (or the selected lines) into checklist items, and back |
| Ctrl+V | Paste a picture or media files from the clipboard as attachments |
| Ctrl+Shift+S | Save the open note as an unencrypted TXT copy |
| Ctrl+K / Ctrl+F | Search |
| Ctrl+S | Save now / retry a failed save |
| Ctrl+Z / Ctrl+Y | Undo / redo text edits |
| Delete (outside a text box) | Move the open or checked notes to Recently deleted; there, delete permanently (asks first) |
| Shift+Delete | Delete permanently (asks first) |
| Ctrl+A (select mode) | Select all |
| Esc | Leave select mode |

## How the encryption works

**On the PC.** Notes, titles, dates and imported legacy files are encrypted with AES‑256‑GCM. A random 256‑bit content key is wrapped with Windows DPAPI (`CurrentUser`) and stored in the vault; every save uses a fresh nonce and any tampering fails authentication. Each attachment is written to `data/attachments/<id>.bin` in 1 MB AES‑256‑GCM chunks under its own random key; the key and metadata live only inside the encrypted notebook, and every chunk authenticates the file header, the attachment id, its index and a "last chunk" flag, so files cannot be truncated, reordered or swapped. Playing a video hands the player a decrypted copy in the temp folder that is deleted when the viewer closes.

There is no separate application password or idle lock: your Windows sign‑in protects the notes. The goal is that **someone who copies the files alone cannot read them**. A program running under the same Windows account, or a person at your unlocked session, can open the notes; this is not a defense against malware with administrator rights, keyloggers or memory dumps.

**Between the devices.** The phone and the PC share a 256‑bit sync key that travels once, over TLS, in exchange for the pairing code. From it both derive an authentication key (a mutual HMAC challenge on every connection) and a content key (AES‑256‑GCM). Every note travels and is stored on the phone as ciphertext under that content key; attachments travel as the already encrypted files, byte for byte. The server listens on TCP 47831 (HTTPS + WebSocket) and 47832 (the plain setup page that hands out the certificate), answers only addresses on the local network and talks only to devices that prove the key. Merging keeps the higher revision of a note; if both devices edited the same revision, the newer one stays and the other is kept as a "conflict copy". Permanent deletions are remembered for 180 days so a phone cannot bring a purged note back.

References: [DataProtectionScope](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.dataprotectionscope), [AesGcm](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm). No telemetry. Not independently audited.

## Checklists

The checklist button (Ctrl+Shift+L on the PC, the list icon on the phone) turns the current line into an item with a round check box; Enter continues the list, Enter on an empty item ends it, Backspace at the start of an item turns it back into text, and tapping the circle marks it done (struck through). Items are stored as plain lines starting with `○` or `●`, so they sync, search and export like any other text.

## Files, backup and recovery

- `data/notes.vault` — the encrypted notebook; `data/notes.vault.bak` — the previous save.
- `data/attachments/` — encrypted photos and videos, one file per attachment.
- `data/sync/` — the certificates and the sync key (DPAPI‑protected) plus the list of paired phones.
- `data/settings.json` — language and small preferences.

**Backup** (the box icon in the sidebar) → *Create backup…* writes a `.vault` file protected by a password you choose (PBKDF2 600k + AES‑256‑GCM); with attachments, a `<name>.vault.files` folder is written beside it — keep the two together. It opens on any PC with that password. *Restore from backup…* merges: notes missing locally are added, the newer revision of each note wins, files that are missing are copied, nothing is removed.

If the main file cannot be opened, the app offers the previous save; damaged files are kept as `.damaged-…`, never silently replaced. Notes in Recently deleted are purged after 30 days together with their attachment files.

## Languages

The Windows app follows the Windows display language and offers 13 languages from the globe button: English, Türkçe, Español, 中文, हिन्दी, العربية (right‑to‑left), Português, Русский, 日本語, Deutsch, Français, Bahasa Indonesia, 한국어. Strings live in `src/Notlar/Languages/<code>.json`; the test run verifies that every language has every key. The phone app follows the phone's language with the same 13 (`src/Notlar/Web/lang.js`).

<p align="center"><img src="docs/screenshot-arabic.png" width="70%" alt="Arabic, right-to-left"></p>

## Development

Windows and the .NET 10 SDK; Node.js for the phone test. No third‑party packages: the HTTPS/WebSocket server, the certificate authority and the QR encoder are part of the source.

- `src/Notlar/` — the WPF app. `Sync/` holds the server, protocol, certificates and QR code; `Web/` holds the phone app (vanilla JavaScript, WebCrypto, IndexedDB).
- `build.cmd` — publishes a self‑contained x64 build to `app/` and compiles the root launcher.
- `test.cmd` — runs the checks with temporary data: encryption and tamper detection, attachments, backups, trash policy, bulk actions, tray behaviour, UI layout, and a Node script that plays the phone (pairing code, sync in both directions, conflicts, byte‑identical files).
- `build-setup.cmd` — builds the installer with [Inno Setup 6](https://jrsoftware.org/isdl.php) into `dist/`.

Releasing: push a version tag and `.github/workflows/release.yml` builds the installer, writes its `.sha256` and attaches both to a GitHub Release; running apps offer it within a few hours.

```bash
git tag v1.6.0
git push --tags
```

## Roadmap

- Android (the same web app in Chrome; the certificate step differs).
- Sync away from home — today the phone syncs on the home Wi‑Fi; the options are your own WireGuard on the PC, or a transport‑only relay that cannot read anything.
- Code signing for the installer.

## License

MIT — see [LICENSE](LICENSE).
