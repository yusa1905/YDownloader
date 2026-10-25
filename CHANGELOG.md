# Changelog

## 1.0.1

### Added

- ZIP files that use a compression method .NET cannot read (for example Zstandard) are now extracted with 7-Zip or Windows `tar.exe` as a fallback. The order is .NET, 7-Zip, `tar.exe`.
- Protection against data loss for `downloads.json` and `settings.json`: a file that is locked is retried a few times and then left untouched for the session (nothing is saved over it); a corrupt file is copied to a `.bad` file before the app starts with defaults. The user is told in a notice.
- Settings are written through a single write chain (the newest state wins and is flushed on exit). A write error is shown in the Settings sheet, which stays open.
- Clearer error messages for file problems: missing folder or drive (for example an unplugged disk), path too long, and no access.

### Changed

- More precise messages when extraction fails: unsupported compression method (with the method name), damaged or incomplete archive, password needed, and 7-Zip not responding in time.
- Better file names for direct downloads: `Content-Disposition` first, then the URL path and query (including the final address after redirects), then the `Content-Type` for a missing extension. A guessed name is improved by one extra request when the download starts.
- Notices about data files are shown one after another and never on top of an open sheet.

### Fixed

- A download whose server returns a web page instead of the file is rejected when the target name is an archive, media file or program (zip, rar, 7z, iso, video, audio, images, exe, msi and similar), and for any non-page name when AllDebrid is enabled. If the AllDebrid host list was unavailable, the message says so.
- A locked `settings.json` no longer crashes the start.
- Quitting before the download list has finished loading no longer writes an empty list.
- Moving data between portable mode and `%AppData%` is refused while the list or settings could not be loaded safely.

## 1.0.0

Initial release.
