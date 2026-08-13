# HALIM RECOVERY

**Free & Open Source Windows Data Recovery**

Halim Recovery helps you find and recover accidentally deleted files on Windows 10/11 —
free, open source, no paywalls, no subscriptions.

> Made with ❤️ by **Halim Toklu**

**[⬇ Download for Windows](https://github.com/htoklu/halim-recovery/releases/tag/v0.5.1)** — installer or portable EXE (Windows 10/11 x64)

---

## What is Halim Recovery?

When Windows deletes a file, the data usually stays on the disk until something else
overwrites it. Halim Recovery reads the raw volume (read-only — it never writes to the
source drive), analyzes filesystem metadata and raw sectors, and lets you preview and
recover deleted files to a safe destination.

## Features

- **Quick Scan** — parses filesystem metadata (NTFS MFT, FAT32 directory entries, exFAT
  directory entry sets) to find deleted files **with their original names, paths, sizes and dates**
- **Deep Scan** — signature-based file carving over the raw volume: finds files even when
  filesystem metadata is gone (names/paths are not recoverable in this mode)
- **Recovery health** — every file gets an honest GREEN / YELLOW / RED confidence rating
  computed from real evidence: cluster reuse analysis, format structure validation, layout certainty
- **Preview before recovery** — images, text, DOCX/XLSX content, archive listings, PDF info
- **Safe recovery** — source volume is opened read-only; destination on the same volume is
  blocked; same physical disk triggers a warning; recovered names are sanitized
- **Smart search** — offline, rule-based natural-language filtering
  ("tatil fotoğrafları geçen ay", "invoice PDFs from 2025"); works without any cloud or AI service
- **Progress, pause/resume, cancel** on all long operations; UI never freezes
- **Recovery report** — every session writes a text + JSON report with SHA-256 hashes
- **CLI** (`HalimRecoveryCli.exe`) for scripting and automated testing
- Runs on CPU + disk I/O only; **no GPU required**

## Supported filesystems

| Filesystem | Quick Scan (metadata) | Deep Scan (carving) |
|---|---|---|
| NTFS | ✅ deleted MFT records, original paths, overwrite analysis via $Bitmap | ✅ |
| FAT32 | ✅ deleted directory entries incl. long names (contiguity assumed — see limitations) | ✅ |
| exFAT | ✅ deleted entry sets; exact layout when NoFatChain is set | ✅ |
| Other | ❌ shown as unsupported (honestly) | ✅ carving works on any volume |

## Supported file formats (Deep Scan carving)

JPG · PNG · GIF · PDF · DOCX · XLSX · PPTX · ZIP · MP4 · MOV · MP3 · WAV

Carving parses each format's internal structure (headers, chunk layout, terminators,
central directories) rather than just matching headers, so carved files are validated —
not just guessed.

## Installation

**Requirements:** Windows 10/11 x64. Administrator rights (raw disk access needs it).

1. **[Download the latest release](https://github.com/htoklu/halim-recovery/releases/tag/v0.5.1)** —
   either the installer (`HalimRecovery-0.5.1-setup.exe`, includes Start Menu shortcut
   and uninstaller) or the portable `HalimRecovery.exe` — or build from source:

```powershell
git clone https://github.com/htoklu/halim-recovery.git
cd kurtarmak
dotnet publish src/HalimRecovery.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/app
# Portable EXE: publish/app/HalimRecovery.exe
# Installer (requires Inno Setup 6): ISCC.exe installer\HalimRecovery.iss
```

2. Run `HalimRecovery.exe` (UAC prompt is expected — raw volume access requires it).

## Usage

1. **Stop using the affected drive immediately.** Every write can destroy deleted data.
2. Start Halim Recovery, select the drive the files were deleted from.
3. Run **Quick Scan** first (seconds to minutes). If your files aren't found, run **Deep Scan**.
4. Filter/search the results, check the health rating, preview files.
5. Select files → **Recover Selected** → choose a folder **on a different drive**.
6. Check the recovery report written into the destination folder.

CLI examples:

```powershell
HalimRecoveryCli.exe list-disks
HalimRecoveryCli.exe quick-scan E --json results.json
HalimRecoveryCli.exe recover E --dest D:\Recovered --mode quick --all
```

## ⚠️ Recovery warnings

- **Recover files to another drive whenever possible.** Writing recovered files to the
  source drive can overwrite other deleted data.
- Data that has been **overwritten is gone**. No software can recover overwritten sectors.
- Recovery success depends on how much the drive was used after deletion.

## SSD / TRIM limitations — please read

On SSDs, Windows sends **TRIM** commands when files are deleted. The SSD controller may
erase those blocks at any time afterwards. This means:

- Files deleted from an SSD with TRIM enabled are often **physically unrecoverable** —
  by *any* software, free or commercial.
- Halim Recovery shows an SSD warning and will honestly report what it finds.
- **No tool can promise "100% recovery". Anyone who does is misleading you.**

## Screenshots

*(Screenshots will be added here.)*

## Test laboratory & benchmark

The repository includes a controlled test lab (`testlab/Run-TestLab.ps1`, requires admin)
that creates virtual disks (NTFS/FAT32/exFAT), writes real test files, hashes them
(SHA-256), permanently deletes them, runs recovery, and verifies recovered files
hash-for-hash. Results are classified **EXACT / PARTIAL / FAILED** and published to
`testlab/results/benchmark.md`. Measured results for the current version are in
[docs/BENCHMARK.md](docs/BENCHMARK.md).

## Roadmap

- [x] Project architecture
- [x] Disk discovery
- [x] Quick scan (NTFS, FAT32, exFAT)
- [x] Deep scan
- [x] File carving (12 formats)
- [x] File preview
- [x] Recovery health
- [x] Recovery + reports
- [x] Benchmark test laboratory
- [ ] Disk image (create & scan sector images)
- [x] Natural language search (offline, rule-based)
- [x] Installer (Inno Setup)
- [ ] Fragmented file reconstruction (candidate extent scoring)
- [ ] Stable 1.0.0 release

## Third-party licenses

All dependencies are MIT-licensed; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
No GPL code is used; all filesystem/format parsers were written from scratch against
public specifications.

## Support development

Halim Recovery is completely free. If it saved your files, consider supporting development:

- ☕ **Ko-fi:** [ko-fi.com/htoklu](https://ko-fi.com/htoklu)
- 💳 **IBAN:** `TR910006701000000057870794`
- ₿ **USDT (TRC20):** `TPRPEBtS8YbTnETdzFSNQezTNeqKfHtsLY`
- 📧 **Email:** htoklu1453@gmail.com

## Disclaimer

Halim Recovery is provided "as is", without warranty of any kind. Data recovery is
inherently uncertain: success depends on the state of your drive, and no result can be
guaranteed. For critically important data (legal, medical, business-critical), consider
consulting a professional data recovery service before running any software. The authors
are not liable for any data loss. See [LICENSE](LICENSE).

---

## Download

If you just want to install and use the app, you do **not** need to clone this repository.

**[⬇ Download Halim Recovery for Windows](https://github.com/htoklu/halim-recovery/releases/tag/v0.5.1)**

On that page pick one of:

| File | Recommended for |
|---|---|
| `HalimRecovery-0.5.1-setup.exe` | Most users — installer with Start Menu shortcut and uninstaller |
| `HalimRecovery.exe` | Portable — single file, no installation |
| `HalimRecovery-0.5.1-portable.zip` | Portable GUI + CLI + license/docs |

Windows 10/11 x64. Administrator rights are required (UAC prompt). If Windows SmartScreen
warns about an unknown publisher, choose **More info → Run anyway**.

Source code on this page is for developers who want to build or contribute. End users
should download from the [Releases](https://github.com/htoklu/halim-recovery/releases/tag/v0.5.1) page.
