First public release of **YDownloader**, a lean, fast download manager for Windows 11 (C# .NET 8, WPF).

![Main window](https://raw.githubusercontent.com/yusa1905/YDownloader/main/assets/screenshots/main.png)

## Highlights

- **Downloads:** multiple connections per file, resume, checksums, queues with schedules and limits, per-host limits, "after completion" actions (shut down, hibernate, sleep) and keep-awake.
- **Link grabber and packages:** the Add dialog detects multi-part archives (`.partN.rar`, `.rar` + `.r00`, `.7z.001`, `.zip.001`, `.z01`), groups them into packages with their own folder and shows missing parts. It offers filters (`ext:`, `size>`, `host:`, `status:`), auto-deselect rules and an online check via AllDebrid.
- **Archive extraction:** automatic after download (can be turned off). Plain ZIP works out of the box; RAR, 7z, multi-part and password-protected archives use an installed 7-Zip. Archives are never deleted, existing files are never overwritten, unsafe paths are rejected.
- **Archive passwords:** a list in Settings (encrypted for your Windows account), a password entered from the row menu, and passwords sent along by Click'n'Load. They are tried in turn and passed to 7-Zip through standard input, never on the command line.
- **Browser extension** for Chrome and Firefox: right-click a link, image, video, audio or selected text. Links land in the Add dialog, nothing starts until you confirm.
- **Click'n'Load** (off by default): listens on `127.0.0.1:9666` only, including AES-encrypted links.
- **Clipboard check** (off by default): shows a notice with the number of matching links only; nothing is downloaded until you confirm.
- **AllDebrid** support (API key stored encrypted).
- **Interface:** light and dark theme, UI scaling 80–200 %, tray icon, autostart, self-drawn speed graph, remaining time and free disk space at a glance. Available in English, German, Spanish, French, Italian, Brazilian Portuguese and Turkish.
- **Portable mode:** put an empty file named `.portable` next to the exe and all data stays in the program folder.

## Performance

Measured on one machine (Windows 11), Release build (ReadyToRun, win-x64), portable copy, empty list, one run per package, three minutes idle: the window is visible after about 320 ms. Private working set is about 60–64 MB; the working set, which includes shared pages such as the graphics driver and runtime, is about 130–136 MB. Other machines, drivers and lists will differ, and running downloads use more.

## Downloads

| File | Size | Notes |
|---|---|---|
| `YDownloader-1.0.0-framework-dependent.zip` | ~1 MB | Needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64) |
| `YDownloader-1.0.0-self-contained.zip` | ~62 MB | Runs without any installed runtime |

Requires Windows 11 (x64). Extract the zip anywhere and run `YDownloader.exe`. There is no installer; data lives in `%AppData%\YDownloader` unless you use portable mode.

> **Note:** `YDownloader.exe` is not code-signed, so Windows SmartScreen may warn on first start ("More info" → "Run anyway"). Compare the SHA-256 of your download with the values below.

## Browser extension

The extension is in the `Extension` folder next to the exe.

1. In YDownloader open Settings (Ctrl+,) → Services → Browser and press *Connect*. This registers the native messaging host for the current user only (`HKCU`).
2. **Chrome / Chromium:** open `chrome://extensions`, enable *Developer mode*, choose *Load unpacked* and select the `Extension` folder.
3. **Firefox:** open `about:debugging`, choose *This Firefox* → *Load Temporary Add-on* and select `Extension\firefox\manifest.json`. Firefox keeps it only until it is closed; a permanent install would need a signature.

If you move the program folder, press *Update* in the same settings page.

## Known limitations

- RAR extraction has not been tested with real archives yet.
- No cookies or referer for downloads, so files behind a login are not supported.
- No DLC support.
- Archive passwords are briefly held in memory by YDownloader and 7-Zip.
- The AllDebrid integration follows the documentation and has not been run against the live service.
- Translations have not been reviewed by native speakers.

## SHA-256

```
767220ff8c1e5f62ac8d5c1d3cacdc43c6649de2d6dbf63c28462e3bf877fe5a  YDownloader-1.0.0-framework-dependent.zip
cc0fb9f1c1c348d149a83db386fd6474357485d0343923e92127b3d780487db1  YDownloader-1.0.0-self-contained.zip
```

Also attached as `SHA256SUMS.txt`.

## Disclaimer

YDownloader is an independent project and is not affiliated with AllDebrid, JDownloader or Apple. Names are used only to describe compatibility. Intended for legal content only; you are responsible for what you download. Licensed under the [MIT License](https://github.com/yusa1905/YDownloader/blob/main/LICENSE).
