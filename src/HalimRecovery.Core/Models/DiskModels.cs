namespace HalimRecovery.Core.Models;

/// <summary>A physical disk (e.g. \\.\PhysicalDrive0).</summary>
public sealed class DiskInfo
{
    public int Index { get; init; }
    public string DevicePath => $@"\\.\PhysicalDrive{Index}";
    public string Model { get; init; } = "";
    public ulong SizeBytes { get; init; }
    public string MediaType { get; init; } = "";
    public string InterfaceType { get; init; } = "";
    public bool IsSsd { get; init; }
    public List<VolumeInfo> Volumes { get; } = new();
}

/// <summary>A mounted volume (e.g. C:).</summary>
public sealed class VolumeInfo
{
    /// <summary>Drive letter without colon, e.g. "C". Empty if unmounted.</summary>
    public string DriveLetter { get; init; } = "";
    public string DevicePath => $@"\\.\{DriveLetter}:";
    public string Label { get; init; } = "";
    public string FileSystemName { get; init; } = "";
    public ulong SizeBytes { get; init; }
    public ulong FreeBytes { get; init; }
    /// <summary>Index of the physical disk hosting this volume, -1 if unknown.</summary>
    public int PhysicalDiskIndex { get; set; } = -1;
    public bool HostDiskIsSsd { get; set; }

    public override string ToString() => $"{DriveLetter}: {Label} ({FileSystemName})";
}

public enum FileSystemKind { Unknown, Ntfs, Fat32, ExFat, Fat16 }
