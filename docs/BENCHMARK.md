# Halim Recovery — Benchmark Results

Version: 0.5.0 · Date: 2026-08-13 · Environment: Windows 11 x64, VHD-based test lab

## Methodology

Fully automated, hash-verified lab (`testlab/Run-TestLab.ps1`, requires admin):

1. Create a fresh 128 MB VHD, format it (NTFS / FAT32 / exFAT).
2. Generate 14 structurally valid test files (JPG, PNG, GIF, PDF, DOCX, XLSX,
   PPTX, ZIP, MP4, MP3, WAV, TXT + 2 files in a subfolder) with random payloads;
   record SHA-256 of each.
3. Flush the volume cache, permanently delete the whole tree (`Remove-Item -Force`,
   no Recycle Bin), flush again.
4. Run Quick Scan recovery and Deep Scan recovery via the CLI on the volume.
5. Compare every recovered file against the original SHA-256:
   **EXACT** (identical hash), **PARTIAL** (same name/size, different hash),
   **FAILED** (not recovered or unusable).

## Results

| Filesystem | Mode | Total | Exact | Partial | Failed |
|---|---|---|---|---|---|
| NTFS | Quick Scan | 14 | **14** | 0 | 0 |
| NTFS | Deep Scan | 14 | 9 | 1 | 4* |
| FAT32 | Quick Scan | 14 | **14** | 0 | 0 |
| FAT32 | Deep Scan | 14 | 12 | 1 | 1* |
| exFAT | Quick Scan | 14 | **14** | 0 | 0 |
| exFAT | Deep Scan | 14 | 12 | 1 | 1* |

**Quick Scan: 42/42 exact, filename + original path + timestamps preserved.**

\* Deep Scan misses are expected limits of signature carving, not bugs:

- **TXT** has no binary signature, so it can never be found by carving
  (Quick Scan recovers it via filesystem metadata).
- On **NTFS**, small files are stored *resident* inside the MFT record itself;
  they do not exist in the data area, so carving cannot see them.
  Quick Scan recovers resident files directly from the MFT.
- ZIP-based formats (DOCX/XLSX/PPTX/ZIP) are carved by central-directory
  analysis; one file per run typically lands as PARTIAL when trailing-size
  heuristics differ by padding.

Deep Scan is a complementary fallback for when filesystem metadata is gone
(formatted volume, damaged directory tree). On these tests the combined
Quick + Deep coverage is 14/14 per filesystem.

## Important caveat found during testing

Re-mounting a volume after deletion (detach/attach cycle) caused Windows to
write `System Volume Information` / indexer files, which **reused the freshly
freed directory clusters and destroyed the deleted entries** on FAT32/exFAT.
This is exactly why the app and README insist: after accidental deletion,
**stop writing to the drive and scan it as-is, as soon as possible.**

## Honest limitations

- These are controlled tests on freshly formatted VHDs — best-case conditions.
  Real-world drives with ongoing writes will recover less.
- SSDs with TRIM may physically erase deleted data; no software can recover that.
- Heavily fragmented files can be recovered incorrectly by carving
  (fragmentation-aware carving is on the roadmap).
