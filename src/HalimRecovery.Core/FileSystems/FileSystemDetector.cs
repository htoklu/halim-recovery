using System.Text;
using HalimRecovery.Core.IO;
using HalimRecovery.Core.Models;

namespace HalimRecovery.Core.FileSystems;

/// <summary>Detects the filesystem of a volume from its boot sector signature.</summary>
public static class FileSystemDetector
{
    public static FileSystemKind Detect(RawDiskReader reader)
    {
        var boot = new byte[512];
        if (reader.ReadAt(0, boot, 0, 512) < 512) return FileSystemKind.Unknown;
        return Detect(boot);
    }

    public static FileSystemKind Detect(ReadOnlySpan<byte> bootSector)
    {
        if (bootSector.Length < 512) return FileSystemKind.Unknown;

        string oem = Encoding.ASCII.GetString(bootSector.Slice(3, 8));
        if (oem == "NTFS    ") return FileSystemKind.Ntfs;
        if (oem == "EXFAT   ") return FileSystemKind.ExFat;

        // FAT32: filesystem type string at offset 82; FAT12/16 at offset 54.
        string fat32Type = Encoding.ASCII.GetString(bootSector.Slice(82, 8));
        if (fat32Type.StartsWith("FAT32")) return FileSystemKind.Fat32;

        string fat16Type = Encoding.ASCII.GetString(bootSector.Slice(54, 8));
        if (fat16Type.StartsWith("FAT16") || fat16Type.StartsWith("FAT12")) return FileSystemKind.Fat16;

        return FileSystemKind.Unknown;
    }
}
