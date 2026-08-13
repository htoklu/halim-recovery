# Changelog

All notable changes to Halim Recovery are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/); versioning follows [SemVer](https://semver.org/).

## [0.5.0] — 2026-08-13 (MVP)

### Added
- Quick Scan: deleted-file discovery from filesystem metadata
  - NTFS: MFT FILE record parsing (fixup, attributes, data runs), original path
    reconstruction, cluster-reuse analysis via $Bitmap
  - FAT32: directory tree walk incl. deleted (0xE5) entries with long file names,
    deleted-directory harvesting, FAT-based reuse analysis
  - exFAT: directory entry set parsing incl. deleted sets, NoFatChain exact recovery
- Deep Scan: streaming signature carver with structural validation for
  JPG, PNG, GIF, PDF, ZIP/DOCX/XLSX/PPTX, MP4/MOV, MP3, WAV
- Recovery health scoring (GREEN/YELLOW/RED) from measurable evidence only
- Preview before recovery (images, text, DOCX/XLSX content, archive listing, PDF info)
- Safe recovery engine: read-only source, destination validation, same-disk warning,
  filename sanitization, SHA-256 in reports
- Recovery reports (text + JSON)
- Offline natural-language search (Turkish + English)
- WPF UI (dark theme) with progress/ETA/speed, pause/resume, cancel
- CLI for scripting and test automation
- VHD-based test laboratory with hash-verified EXACT/PARTIAL/FAILED classification
- Logging to %LOCALAPPDATA%\HalimRecovery\logs (no personal content logged)

### Known limitations
- FAT32/exFAT recovery assumes contiguous file layout when the cluster chain was cleared
- Fragmented file reconstruction is not yet implemented (planned)
- Disk image creation/scanning is planned
- TXT carving in Deep Scan is disabled by default (high noise)
