# Icon packs

One icon system serves both the **Dock** and the **Launchpad**. Priority for every app:

1. per-app override (right-click → *Change icon…*, stored in `icon-overrides.json`, file copied to `UserIcons\`)
2. active icon pack
3. the icon Windows itself shows for the app (cached in `Cache\SystemIcons`)

## Where packs live
`%LOCALAPPDATA%\FloatingDock\IconPacks\<Folder>\` — one folder per pack. `Default` and `Custom` always exist and cannot be deleted.
No decorative packs are supplied from version 1.0.12. `Default` is the system-icon fallback; `Custom` is an empty user-managed image folder. Earlier identifiable bundled DockIcons1/2/3 packs are removed on upgrade; individual overrides and independently imported packs remain.

The folder is watched: add, change or delete a file and the Dock / Launchpad refresh within a second.

## Minimal pack = a folder of images
```
MyPack/
  Firefox.png
  Visual Studio Code.png
  Telegram.png
  Firefox_dark.png        # optional dark variant (used when "Prefer dark variants" is on)
```
Supported: `.png`, `.ico`. Sub-folders are scanned two levels deep. `*_mask.png` files are ignored.

## Optional manifest.json
```json
{
  "id": "MyPack",
  "name": "My Pack",
  "version": "1.2.0",
  "description": "Rounded icons",
  "author": "you",
  "url": "https://example.com",
  "icons": {
    "Visual Studio Code": "apps/vscode.png",
    "Telegram": { "file": "apps/tg.png", "aliases": ["Telegram Desktop", "Телеграм"] }
  }
}
```
All fields are optional. Manifest entries win over files found by scanning. Paths must stay inside the pack folder.

## How an app is matched to an icon
The app's names (display name, executable name, shortcut name, aliases) are compared with icon file names, first match wins:

1. exact name (case / punctuation / diacritics ignored)
2. name without noise words (`Microsoft`, `Desktop`, `x64`, version numbers, `(Portable)` …)
3. same pronunciation, including Cyrillic ↔ Latin (`Телеграм` ≈ `Telegram`)
4. one name contains the other (only for names of 3+ letters, generic words like "app" never match)
5. one-letter difference

Start button and Launchpad button are matched by the names `Start` / `Windows` and `Launchpad` / `Apps` / `App Library`.

## ZIP import / export
*Settings → Icon Packs → Import ZIP…* shows the pack name, version and icon count before anything is written.
The importer rejects: paths leaving the pack folder (`..`, absolute paths, drive letters), executable / script file types
(`.exe .dll .bat .cmd .ps1 .vbs .js .msi .lnk …`), more than 10 000 entries, entries over 32 MB or 256 MB total.
A ZIP may contain the pack at its root or inside a single top-level folder. *Export* writes the active pack as a ZIP that can be imported again.

Pack ids are sanitised into folder names; importing a pack with an existing id replaces it, and the reserved ids `Default` / `Custom` get a suffix.

## Limits (local files and packs)
Applied to every local image (override, pack, ZIP entry) *before* decoding, from the file header:
| Limit | Value |
|---|---|
| File size | 20 MB |
| Image size | 4096 × 4096 px and 8 M pixels |
| Images inside one ICO | ≤ 64 (PNG-in-ICO is checked too) |
| Icons per pack / packs | 5000 / 100 |
| Per-app overrides | 1000 |
| Text entries in ZIP (manifest) | 1 MB |
Files over a limit are skipped and listed as warnings (Settings → Icon Packs); the app falls back to the system icon. Replacing an override writes the new file first and only then removes the old one.
