# HALIM RECOVERY — Architecture

## Technology Decisions (PHASE 1)

| Decision | Choice | Rationale |
|---|---|---|
| Runtime | .NET 9 (C#) | Modern, fast, first-class Windows API access via P/Invoke, single-file EXE publish |
| UI | WPF | Mature, stable packaging, no WebView dependency, fast startup |
| MVVM | CommunityToolkit.Mvvm (MIT) | Lightweight, Microsoft-maintained |
| Disk enumeration | WMI via System.Management (MIT) + P/Invoke DeviceIoControl | Reliable physical disk + volume mapping |
| Raw disk access | P/Invoke `CreateFile("\\.\X:")` + sector-aligned reads | Only reliable way to read raw volumes on Windows |
| Filesystem parsers | **Written from scratch** (NTFS MFT, FAT32, exFAT) | DiscUtils (MIT) has no deleted-record access; PhotoRec is GPLv2+ (incompatible, no code reuse — spec-only) |
| License | MIT | All dependencies are MIT; no GPL code used |

### Research notes
- **DiscUtils** (MIT): stable NTFS/FAT read support but exposes only *live* files — no deleted MFT records. Not used.
- **TestDisk/PhotoRec** (GPLv2+): reference for carving *concepts* only. **No code is copied** — file format signatures come from public format specifications (JPEG/ISO 10918, PNG RFC 2083, PDF ISO 32000, ZIP APPNOTE, ISO BMFF for MP4/MOV, RIFF spec, MP3 frame spec).
- NTFS on-disk format: publicly documented (Microsoft docs, ntfs.com, Linux-NTFS project documentation). Deleted file = MFT FILE record with `InUse` flag cleared; data runs usually intact until clusters are overwritten.
- FAT32 deleted entry: first byte of directory entry = `0xE5`; cluster chain in FAT is zeroed → contiguity assumption is the industry-standard heuristic.
- exFAT deleted entry: entry `TypeCode` in-use bit (0x80) cleared; `NoFatChain` flag often allows exact contiguous recovery.
- SSD + TRIM: after TRIM the controller returns zeros for trimmed LBAs — recovery of those clusters is physically impossible. UI must state this honestly.

## Layers (all in HalimRecovery.Core — no UI dependency)

```
Disks/        Disk discovery (physical disks, volumes, disk↔volume mapping)
IO/           RawDiskReader: sector-aligned buffered read-only stream
FileSystems/  Detection (boot sector signatures) + INtfs/Fat/ExFat quick scanners
  Ntfs/       Boot sector, MFT reader, FILE record parser, attribute & data-run decoding
  Fat32/      Boot sector, FAT table, directory tree walker (incl. 0xE5 entries)
  ExFat/      Boot sector, directory entry sets (incl. deleted sets)
Carving/      Signature registry, streaming carver, per-format validators
Health/       Confidence scoring (GREEN/YELLOW/RED) from measurable evidence
Preview/      Image/text/metadata preview extraction (best-effort, safe)
Recovery/     Safe writer: destination validation, same-physical-disk warning, name sanitization
Scanning/     Orchestration: QuickScan / DeepScan engines, progress, cancel, pause
Reporting/    Recovery session report (JSON + text)
Logging/      Rolling file logger (no personal data, no secrets)
```

## Key invariants
1. **Source volume is opened read-only, always.** No writes to source.
2. Every long operation takes a `CancellationToken` and reports `ScanProgress`.
3. Bounded memory: streaming reads with fixed-size buffers (default 4 MiB); never load a disk into RAM.
4. Confidence values are computed from measurable evidence (signature validation, structural checks, cluster allocation status), never invented.
5. Engine (Core) has zero UI references; CLI and WPF App are thin frontends.

## Projects
- `src/HalimRecovery.Core` — engine (netstandard-free, net9.0)
- `src/HalimRecovery.Cli` — command-line frontend (test lab automation + power users)
- `src/HalimRecovery.App` — WPF GUI (net9.0-windows)
- `tests/HalimRecovery.Tests` — xUnit unit tests (parsers, validators, scoring, path safety)
- `testlab/` — PowerShell scripts: VHD-based end-to-end recovery tests with SHA-256 verification
