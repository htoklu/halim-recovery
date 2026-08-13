using System.Management;
using System.Runtime.InteropServices;
using HalimRecovery.Core.Logging;
using HalimRecovery.Core.Models;
using Microsoft.Win32.SafeHandles;

namespace HalimRecovery.Core.Disks;

/// <summary>Discovers physical disks and volumes, and maps volumes to physical disks.</summary>
public static class DiskEnumerator
{
    private const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize, byte[] lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    public static List<DiskInfo> GetDisks()
    {
        var disks = new List<DiskInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Index, Model, Size, MediaType, InterfaceType FROM Win32_DiskDrive");
            foreach (var mo in searcher.Get())
            {
                var disk = new DiskInfo
                {
                    Index = Convert.ToInt32(mo["Index"] ?? -1),
                    Model = mo["Model"]?.ToString() ?? "Unknown",
                    SizeBytes = Convert.ToUInt64(mo["Size"] ?? 0UL),
                    MediaType = mo["MediaType"]?.ToString() ?? "",
                    InterfaceType = mo["InterfaceType"]?.ToString() ?? "",
                    IsSsd = IsSsdDisk(Convert.ToInt32(mo["Index"] ?? -1))
                };
                disks.Add(disk);
            }
        }
        catch (Exception ex)
        {
            Log.Error("DiskEnumerator", "WMI disk enumeration failed", ex);
        }

        var volumes = GetVolumes();
        foreach (var vol in volumes)
        {
            var host = disks.FirstOrDefault(d => d.Index == vol.PhysicalDiskIndex);
            if (host != null)
            {
                vol.HostDiskIsSsd = host.IsSsd;
                host.Volumes.Add(vol);
            }
        }
        return disks.OrderBy(d => d.Index).ToList();
    }

    public static List<VolumeInfo> GetVolumes()
    {
        var result = new List<VolumeInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is DriveType.CDRom or DriveType.Network or DriveType.NoRootDirectory)
                    continue;
                var letter = drive.Name.TrimEnd('\\', ':');
                var vol = new VolumeInfo
                {
                    DriveLetter = letter,
                    Label = drive.IsReady ? drive.VolumeLabel : "",
                    FileSystemName = drive.IsReady ? drive.DriveFormat : "",
                    SizeBytes = drive.IsReady ? (ulong)drive.TotalSize : 0,
                    FreeBytes = drive.IsReady ? (ulong)drive.AvailableFreeSpace : 0,
                    PhysicalDiskIndex = GetPhysicalDiskIndex(letter)
                };
                result.Add(vol);
            }
            catch (Exception ex)
            {
                Log.Warn("DiskEnumerator", $"Skipping volume {drive.Name}: {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>Physical disk index hosting the volume, or -1. Does not require admin.</summary>
    public static int GetPhysicalDiskIndex(string driveLetter)
    {
        try
        {
            using var h = CreateFile($@"\\.\{driveLetter}:", 0 /* query only */, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) return -1;
            var buf = new byte[8 + 24 * 8];
            if (!DeviceIoControl(h, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, IntPtr.Zero, 0, buf, (uint)buf.Length, out _, IntPtr.Zero))
                return -1;
            int extents = BitConverter.ToInt32(buf, 0);
            return extents >= 1 ? BitConverter.ToInt32(buf, 8) : -1;
        }
        catch { return -1; }
    }

    private static bool IsSsdDisk(int index)
    {
        try
        {
            // MSFT_PhysicalDisk.MediaType: 3=HDD, 4=SSD
            using var searcher = new ManagementObjectSearcher(@"\\.\root\microsoft\windows\storage",
                $"SELECT MediaType FROM MSFT_PhysicalDisk WHERE DeviceId='{index}'");
            foreach (var mo in searcher.Get())
                return Convert.ToInt32(mo["MediaType"] ?? 0) == 4;
        }
        catch { /* storage namespace may be unavailable; assume unknown */ }
        return false;
    }
}
