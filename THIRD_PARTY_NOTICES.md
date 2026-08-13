# Third-Party Notices

Halim Recovery is licensed under the MIT License. It depends on the following
third-party packages, all of which are MIT-licensed and compatible:

| Package | Version | License | Purpose |
|---|---|---|---|
| [System.Management](https://www.nuget.org/packages/System.Management) | 10.0.x | MIT (© Microsoft) | WMI queries for physical disk enumeration |
| [CommunityToolkit.Mvvm](https://www.nuget.org/packages/CommunityToolkit.Mvvm) | 8.4.x | MIT (© .NET Foundation) | MVVM helpers for the WPF UI |

The .NET runtime and WPF are distributed under the MIT License (© .NET Foundation and contributors).

## Format specifications

The filesystem and file-format parsers in Halim Recovery were written from
scratch for this project using publicly available format documentation:

- NTFS on-disk structures: Microsoft documentation and the public Linux-NTFS project documentation
- FAT32: Microsoft Extensible Firmware Initiative FAT32 File System Specification
- exFAT: Microsoft exFAT file system specification (published 2019)
- JPEG (ISO/IEC 10918), PNG (RFC 2083), GIF89a specification, PDF (ISO 32000),
  ZIP (PKWARE APPNOTE), ISO Base Media File Format / MP4 (ISO/IEC 14496-12),
  MP3 (ISO/IEC 11172-3), RIFF/WAVE specification

No source code from GPL-licensed tools (e.g. TestDisk/PhotoRec) is used in this project.

## Artwork

The application icon (`assets/icon.png`, `src/HalimRecovery.App/Assets/app.ico`)
is original artwork created specifically for this project. It contains no
third-party or trademarked material and is licensed under the project's MIT License.
